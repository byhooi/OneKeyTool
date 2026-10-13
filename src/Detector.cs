// Detector.cs — 键盘钩子的安装与事件分发
//
// 识别逻辑本身在 MacroEngine.cs（纯逻辑、可单元测试）。本类只负责：
//   1. 安装 / 卸载低级键盘钩子，把原始事件翻译成 KeyEvent；
//   2. 把事件喂给 MacroEngine，按结果决定吞掉还是放行；
//   3. 观察模式下把 Swallow 降级为放行、且不做重放；
//   4. 把暂扣事件真正注入回系统（InputSender）；
//   5. 异步日志。
//
// 观察模式与拦截模式共用同一个 MacroEngine，因此两种模式的识别结果必然一致——
// 用户正是先靠观察模式确认识别准确，再切到拦截模式的，两者不一致会直接毁掉信任。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OneKeyTool
{
    internal class Detector
    {
        private Config _cfg;
        private readonly MacroEngine _engine;
        private readonly object _gate = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        // 异步日志
        private readonly Queue<string> _logQueue = new Queue<string>();
        private readonly AutoResetEvent _logSignal = new AutoResetEvent(false);
        private readonly Thread _logThread;
        private bool _logStopping;
        private readonly string _logPath;

        // 测试模式：不装钩子、不真正注入，时间由外部喂入
        private readonly bool _testMode;
        private long _testNow;
        private readonly List<KeyEvent> _testReplay = new List<KeyEvent>();
        private readonly List<KeyEvent> _replayBuffer = new List<KeyEvent>();

        private Native.LowLevelKeyboardProc _hookProc;   // 持有引用防止被 GC
        private IntPtr _hook = IntPtr.Zero;

        // 统计
        public long TriggerCount;
        public long SuppressedEventCount;

        /// <summary>命中自定义键时触发。参数为 true 表示当前处于观察模式（未拦截、不应执行动作）。</summary>
        public event Action<bool> Triggered;

        public Detector(Config cfg, string logPath = null, bool testMode = false)
        {
            _cfg = cfg;
            _logPath = logPath ?? "";
            _testMode = testMode;
            _engine = new MacroEngine(cfg, Log);
            if (!string.IsNullOrEmpty(_logPath))
            {
                _logThread = new Thread(WriteLogs) { IsBackground = true, Name = "OneKeyTool logger" };
                _logThread.Start();
            }
        }

        // ==================== 生命周期 ====================

        public bool Start()
        {
            if (_hook != IntPtr.Zero) return true;
            _hookProc = HookCallback;
            IntPtr hmod = Native.GetModuleHandle(null);
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, hmod, 0);
            if (_hook == IntPtr.Zero)
            {
                Log("SetWindowsHookEx 失败，错误码 " + Marshal.GetLastWin32Error());
                return false;
            }
            Log("键盘钩子已安装");
            return true;
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_hook != IntPtr.Zero)
                {
                    Native.UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
                // 卸载时把暂扣事件放行，避免卡键
                _engine.Reset("钩子卸载");
                FlushReplay();
            }
            Log("键盘钩子已卸载");

            lock (_logQueue) _logStopping = true;
            _logSignal.Set();
            if (_logThread != null) _logThread.Join(1000);
        }

        /// <summary>热重载配置。切换时先把暂扣事件放行，避免卡键。</summary>
        public void UpdateConfig(Config cfg)
        {
            lock (_gate)
            {
                _engine.UpdateConfig(cfg);
                FlushReplay();
                _cfg = cfg;
            }
        }

        // ==================== 钩子回调 ====================

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

            try
            {
                Native.KBDLLHOOKSTRUCT hs = (Native.KBDLLHOOKSTRUCT)
                    Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));

                // 忽略本程序自己注入的按键：
                //   - 带我们的魔数标记 → 无条件忽略（重放安全，永不递归）
                //   - 其他程序注入的事件 → 默认也忽略
                //   - 测试模式(AcceptInjected)下放行外部注入，便于自测
                if (hs.dwExtraInfo == InputSender.Magic)
                    return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

                if (!_cfg.AcceptInjected &&
                    ((hs.flags & Native.LLKHF_INJECTED) != 0 ||
                     (hs.flags & Native.LLKHF_LOWER_IL_INJECTED) != 0))
                    return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

                if (!_cfg.Enabled)
                    return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

                int msg = wParam.ToInt32();
                bool isDown = (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN);

                KeyEvent ev = new KeyEvent(
                    (ushort)hs.vkCode,
                    (ushort)hs.scanCode,
                    isDown,
                    (hs.flags & Native.LLKHF_EXTENDED) != 0);

                if (_cfg.DebugKeys)
                    Log(string.Format("RAW vk=0x{0:X2} scan=0x{1:X2} {2} flags=0x{3:X2} extra=0x{4:X}",
                        hs.vkCode, hs.scanCode, isDown ? "DOWN" : "UP", hs.flags, hs.dwExtraInfo.ToInt64()));

                bool swallow;
                lock (_gate)
                {
                    swallow = Feed(ev);
                    FlushReplay();
                }

                if (swallow) return new IntPtr(1);
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }
            catch (Exception ex)
            {
                Log("钩子异常: " + ex.Message);
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }
        }

        /// <summary>由后台线程周期性调用，处理“等待超时”的收尾。</summary>
        public void Tick()
        {
            lock (_gate)
            {
                _engine.Tick(Now);
                FlushReplay();
            }
        }

        // ==================== 事件处理 ====================

        /// <summary>喂一个事件给状态机，返回是否吞掉它。调用方须持有 _gate。</summary>
        private bool Feed(KeyEvent ev)
        {
            EngineResult r = _engine.Feed(ev, Now);

            if (r.Triggered) OnTriggered(r.TriggerHeldCount);

            // 观察模式：只识别不拦截，吞掉的决定降级为放行，且不做重放
            if (_cfg.DryRun) return false;

            return r.Decision == KeyDecision.Swallow;
        }

        private void OnTriggered(int heldCount)
        {
            TriggerCount++;
            if (_cfg.DryRun)
            {
                Log("☆ 观察模式：识别到自定义键（未拦截、未执行动作）");
            }
            else
            {
                SuppressedEventCount += heldCount;
                Log("★ 命中自定义键：拦截 " + heldCount + " 个按键事件，执行用户动作");
            }

            Action<bool> handler = Triggered;
            if (handler != null) handler(_cfg.DryRun);
        }

        /// <summary>把状态机交还的暂扣事件真正注入回系统。调用方须持有 _gate。</summary>
        private void FlushReplay()
        {
            _replayBuffer.Clear();
            _engine.TakeReplay(_replayBuffer);
            if (_replayBuffer.Count == 0) return;

            // 观察模式从不吞事件，因此也不该有重放
            if (_cfg.DryRun) return;

            if (_testMode)
            {
                _testReplay.AddRange(_replayBuffer);
                return;
            }

            List<KeyEvent> copy = new List<KeyEvent>(_replayBuffer);
            try
            {
                InputSender.Replay(copy);
            }
            catch (Exception ex)
            {
                Log("重放失败，部分按键可能未恢复: " + ex.Message);
                if (MainForm.Current != null)
                    MainForm.Current.ReportError(
                        "恢复暂扣按键失败。请检查是否正在操作管理员权限窗口；" +
                        "Windows 可能阻止低权限程序注入按键。\n\n" + ex.Message);
            }
        }

        // ==================== 测试钩子 ====================

        internal bool FeedForTest(KeyEvent ev, long now)
        {
            if (!_testMode) throw new InvalidOperationException("Test mode is required");
            _testNow = now;
            lock (_gate)
            {
                bool swallow = Feed(ev);
                FlushReplay();
                return swallow;
            }
        }

        internal void TickForTest(long now)
        {
            if (!_testMode) throw new InvalidOperationException("Test mode is required");
            _testNow = now;
            Tick();
        }

        internal int ReplayedForTest { get { return _testReplay.Count; } }

        internal List<KeyEvent> ReplayListForTest { get { return _testReplay; } }

        internal int HeldCountForTest { get { return _engine.HeldCount; } }

        // ==================== 日志 ====================

        private long Now { get { return _testMode ? _testNow : _clock.ElapsedMilliseconds; } }

        /// <summary>
        /// 记录一行日志。只在钩子线程上做入队，磁盘写入交给后台线程——
        /// 低级键盘钩子的回调超时会被 Windows 静默摘掉钩子，回调里不能做磁盘 IO。
        /// </summary>
        private void Log(string s)
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            lock (_logQueue)
            {
                if (_logQueue.Count < 2000)
                    _logQueue.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s);
                else
                    return;
            }
            _logSignal.Set();
        }

        private void WriteLogs()
        {
            while (true)
            {
                _logSignal.WaitOne();

                List<string> batch = new List<string>();
                bool stopping;
                lock (_logQueue)
                {
                    while (_logQueue.Count > 0) batch.Add(_logQueue.Dequeue());
                    stopping = _logStopping;
                }

                if (batch.Count > 0)
                {
                    try
                    {
                        // 超过 2MB 就轮转一次，避免长期运行把磁盘写满
                        FileInfo info = new FileInfo(_logPath);
                        if (info.Exists && info.Length > 2 * 1024 * 1024)
                        {
                            string backupPath = _logPath + ".1";
                            if (File.Exists(backupPath)) File.Delete(backupPath);
                            File.Move(_logPath, backupPath);
                        }
                        File.AppendAllLines(_logPath, batch, new UTF8Encoding(false));
                    }
                    catch { }
                }

                if (stopping) return;
            }
        }
    }
}
