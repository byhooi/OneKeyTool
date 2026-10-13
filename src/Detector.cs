// Detector.cs — 键盘钩子的安装与事件分发
//
// 识别逻辑本身在 MacroEngine.cs（纯逻辑、可单元测试）。本类只负责：
//   1. 在**专用线程**上安装 / 卸载低级键盘钩子，把原始事件翻译成 KeyEvent；
//   2. 把事件喂给 MacroEngine，按结果决定吞掉还是放行；
//   3. 观察模式下把 Swallow 降级为放行、且不做重放；
//   4. 把暂扣事件真正注入回系统（InputSender）；
//   5. 驱动超时收尾（专用线程上的定时器）与钩子自检重装；
//   6. 异步日志。
//
// 为什么钩子必须放在专用线程上：
//   低级键盘钩子的回调在"安装它的那个线程"上执行，而 Windows 对回调有
//   LowLevelHooksTimeout（默认 300ms）限制，超时后会**静默摘掉钩子**。
//   早先钩子装在界面线程上，界面线程上任何耗时操作（写配置、ShellExecute
//   启动程序）都会顺带把钩子一起拖住。专用线程只跑消息循环、不做别的事，
//   从根上切断这条耦合，也让 _gate 这把锁真正有了意义。
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
        /// <summary>钩子线程上驱动超时收尾的定时器间隔（毫秒）。</summary>
        /// <remarks>
        /// 这个值直接决定"单独按 Win 键"多等多久才恢复：实际延迟约为
        /// WinWaitMs + 本值。故取小值，让实际延迟贴近配置值。
        /// </remarks>
        private const int TickIntervalMs = 15;

        /// <summary>钩子自检重装的间隔（毫秒）。见 ReinstallHook 的说明。</summary>
        private const long HookReinstallIntervalMs = 10 * 60 * 1000;

        /// <summary>自检重装失败后的重试间隔（毫秒）。</summary>
        private const long HookReinstallRetryMs = 5 * 60 * 1000;

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
        private volatile IntPtr _hook = IntPtr.Zero;

        // 专用钩子线程
        private Thread _hookThread;
        private readonly ManualResetEvent _hookReady = new ManualResetEvent(false);
        private volatile bool _hookAlive;
        private volatile bool _hookStopRequested;
        private volatile uint _hookThreadId;
        private long _nextReinstallAt;                   // 只在钩子线程上读写

        // 统计
        public long TriggerCount;
        public long SuppressedEventCount;

        /// <summary>钩子当前是否处于已安装状态。</summary>
        public bool HookAlive { get { return _testMode || _hookAlive; } }

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

        /// <summary>
        /// 启动钩子线程并等待它报告安装结果。返回 false 表示钩子没装上。
        /// </summary>
        public bool Start()
        {
            if (_testMode) return true;
            if (_hookThread != null) return true;

            _hookStopRequested = false;
            _hookAlive = false;
            _hookReady.Reset();

            _hookThread = new Thread(HookThreadProc);
            _hookThread.IsBackground = true;
            _hookThread.Name = "OneKeyTool hook";
            _hookThread.Start();

            // 等待安装结果：Program.cs 靠返回值决定是否弹"安装钩子失败"。
            if (!_hookReady.WaitOne(5000))
            {
                Log("钩子线程未在 5 秒内完成安装");
                return false;
            }
            return _hookAlive;
        }

        public void Stop()
        {
            Thread t = _hookThread;
            if (t != null && t.IsAlive)
            {
                _hookStopRequested = true;
                // 唤醒阻塞在 GetMessage 上的钩子线程；线程退出前会自己摘钩子并放行暂扣事件
                Native.PostThreadMessage(_hookThreadId, Native.WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);

                if (!t.Join(3000))
                {
                    // 兜底：线程没退干净也必须把钩子摘掉，否则键盘一直受本程序影响
                    Log("钩子线程未在 3 秒内退出，强制卸载钩子");
                    IntPtr h = _hook;
                    if (h != IntPtr.Zero)
                    {
                        Native.UnhookWindowsHookEx(h);
                        _hook = IntPtr.Zero;
                    }
                }
            }
            _hookThread = null;
            _hookAlive = false;

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

        // ==================== 钩子线程 ====================

        /// <summary>
        /// 钩子线程主体：装钩子 → 跑消息循环（超时收尾靠线程定时器）→ 退出时摘钩子。
        /// 这个线程上除了消息循环什么都不做，这是钩子不被拖超时的前提。
        /// </summary>
        private void HookThreadProc()
        {
            // 先把消息队列建出来，保证 Stop() 的 PostThreadMessage 一定投得进来
            Native.MSG msg;
            Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, Native.PM_NOREMOVE);
            _hookThreadId = Native.GetCurrentThreadId();

            _hookProc = HookCallback;
            IntPtr hmod = Native.GetModuleHandle(null);
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, hmod, 0);
            if (_hook == IntPtr.Zero)
            {
                Log("SetWindowsHookEx 失败，错误码 " + Marshal.GetLastWin32Error());
                _hookAlive = false;
                _hookReady.Set();
                return;
            }

            Log("键盘钩子已安装（线程 " + _hookThreadId + "）");
            _hookAlive = true;
            _nextReinstallAt = _clock.ElapsedMilliseconds + HookReinstallIntervalMs;
            _hookReady.Set();

            // 线程定时器：WM_TIMER 会被投到这个线程的队列上（hwnd 为空）
            IntPtr timer = Native.SetTimer(IntPtr.Zero, IntPtr.Zero, (uint)TickIntervalMs, IntPtr.Zero);

            try
            {
                while (!_hookStopRequested)
                {
                    if (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) <= 0) break;
                    if (msg.message == Native.WM_APP_QUIT) break;

                    if (msg.message == Native.WM_TIMER && msg.hwnd == IntPtr.Zero)
                    {
                        OnTickTimer();
                        continue;
                    }

                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                Log("钩子线程异常退出: " + ex.Message);
            }

            if (timer != IntPtr.Zero) Native.KillTimer(IntPtr.Zero, timer);

            // 卸载前先把暂扣事件放行，避免卡键
            lock (_gate)
            {
                _engine.Reset("钩子卸载");
                FlushReplay();
            }

            IntPtr h = _hook;
            if (h != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(h);
                _hook = IntPtr.Zero;
            }
            _hookAlive = false;
            Log("键盘钩子已卸载");
        }

        /// <summary>线程定时器到点：推进状态机超时，并偶尔自检重装钩子。</summary>
        private void OnTickTimer()
        {
            lock (_gate)
            {
                _engine.Tick(Now);
                FlushReplay();

                if (_clock.ElapsedMilliseconds >= _nextReinstallAt && _engine.HeldCount == 0)
                    ReinstallHook();
            }
        }

        /// <summary>
        /// 自检重装钩子。Windows 会在回调超时后静默摘掉低级钩子，而程序侧看不出任何异常
        /// （_hook 句柄仍非零），用户看到的是"时灵时不灵"。定期在空闲时重装一次可以把这类
        /// 失效自愈掉。
        ///
        /// 两个约束：
        ///   1. 只在没有暂扣事件时做，免得把正在识别的序列打断；
        ///   2. 必须先摘后装——两个钩子同时存在会让每个按键被处理两次。
        /// 摘到装上之间只有几微秒，这段时间里按键会原样通过，可以接受。
        /// </summary>
        private void ReinstallHook()
        {
            IntPtr old = _hook;
            if (old != IntPtr.Zero) Native.UnhookWindowsHookEx(old);

            IntPtr fresh = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc,
                Native.GetModuleHandle(null), 0);
            if (fresh == IntPtr.Zero)
            {
                // 重装失败就真的没有钩子了，缩短重试间隔
                _hook = IntPtr.Zero;
                _hookAlive = false;
                _nextReinstallAt = _clock.ElapsedMilliseconds + HookReinstallRetryMs;
                Log("钩子自检重装失败，错误码 " + Marshal.GetLastWin32Error() + "，稍后重试");
                return;
            }

            _hook = fresh;
            _hookAlive = true;
            _nextReinstallAt = _clock.ElapsedMilliseconds + HookReinstallIntervalMs;
            Log("钩子已自检重装");
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

        /// <summary>推进时间，处理“等待超时”的收尾。由钩子线程的定时器调用。</summary>
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
