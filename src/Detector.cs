// Detector.cs — 自定义键识别核心
//
// 背景（由实测抓包得出）：
//   该键由键盘固件模拟出一段宏，序列为
//       Win↓  Win↑  R↑   [约 1.4ms 内完成]
//       h t t p : / / o n e c l i c k . l e n o v o . c o m . c n
//       Enter
//   字符间隔极短（多数 < 1ms，最大约 15ms），远快于人类打字（30~390ms）。
//
// 策略：
//   1. 看到 Win↓ 时先“暂扣”不发给系统，给一个很短的观察窗口。
//   2. 窗口内若出现 Win↑ + R↑ 三连击，判定为宏，转入收集状态。
//   3. 收集到特征串 "oneclick" 后遇到 Enter → 确认命中：
//         丢弃整段宏，执行用户设定的动作。
//   4. 任何一步不符合预期（比如用户真的按了 Win+E）→ 立即把暂扣的按键
//      原样重放给系统，行为与未安装本工具时完全一致。
//
// 因此本工具不会破坏 Win 键、Win+R、方向键等任何正常功能。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OneKeyTool
{
    internal enum DetectorState
    {
        Idle,
        WinDown,      // 已暂扣 Win↓，等待后续
        WinUpSeen,    // 已暂扣 Win↑，等待 R↑
        WinRSeen,     // 已确认 Win+R，等待宏首字符
        Macro         // 正在收集宏字符
    }

    internal class Detector
    {
        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_RWIN = 0x5C;
        private const ushort VK_R = 0x52;
        private const ushort VK_H = 0x48;
        private const ushort VK_ENTER = 0x0D;

        private Config _cfg;
        private readonly object _gate = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private Native.LowLevelKeyboardProc _hookProc;   // 持有引用防止被 GC
        private IntPtr _hook = IntPtr.Zero;

        private DetectorState _state = DetectorState.Idle;
        private readonly List<KeyEvent> _held = new List<KeyEvent>();
        private long _deadline;
        private long _macroStartedAt;

        /// <summary>本次宏收集到的按键事件数（用于界面展示）。</summary>
        public int LastMacroEvents { get { return _lastMacroEvents; } }
        private int _lastMacroEvents;

        // 特征串匹配
        private ushort[] _sig;
        private int _sigPos;
        private bool _sigSeen;
        private int _prefixPos;      // 匹配 "http" 前缀
        private int _macroChars;

        // 统计
        public long TriggerCount;
        public long SuppressedEventCount;

        /// <summary>命中自定义键时触发。参数为 true 表示当前处于观察模式（未拦截、不应执行动作）。</summary>
        public event Action<bool> Triggered;

        /// <summary>可选：调试日志回调。</summary>
        public event Action<string> Trace;

        public Detector(Config cfg)
        {
            _cfg = cfg;
            _sig = cfg.Signature;
        }

        /// <summary>热重载配置。切换时会先把暂扣的按键放行，避免卡键。</summary>
        public void UpdateConfig(Config cfg)
        {
            lock (_gate)
            {
                if (_held.Count > 0) FlushHeld();
                _state = DetectorState.Idle;
                _deadline = 0;
                _cfg = cfg;
                _sig = cfg.Signature;
            }
        }

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
                FlushHeld();
            }
            Log("键盘钩子已卸载");
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

                lock (_gate)
                {
                    return Handle(ev, nCode, wParam, lParam);
                }
            }
            catch (Exception ex)
            {
                Log("钩子异常: " + ex.Message);
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }
        }

        /// <summary>返回 IntPtr.Zero 表示吞掉该事件，否则放行。</summary>
        private IntPtr Handle(KeyEvent ev, int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 观察模式：完全不拦截，只识别与计数
            if (_cfg.DryRun)
            {
                ObserveOnly(ev);
                return Pass(nCode, wParam, lParam);
            }

            switch (_state)
            {
                case DetectorState.Idle:
                    return HandleIdle(ev, nCode, wParam, lParam);
                case DetectorState.WinDown:
                    return HandleWinDown(ev, nCode, wParam, lParam);
                case DetectorState.WinUpSeen:
                    return HandleWinUpSeen(ev, nCode, wParam, lParam);
                case DetectorState.WinRSeen:
                    return HandleWinRSeen(ev, nCode, wParam, lParam);
                case DetectorState.Macro:
                    return HandleMacro(ev, nCode, wParam, lParam);
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // ---------- 观察模式 ----------

        // 观察模式状态机：不吞事件，只统计。
        // 状态：0=空闲  1=已见 Win↓  2=已见 Win↑  3=已见 R  4=宏收集中
        private int _obsState;
        private int _obsSigPos;
        private bool _obsSigSeen;
        private int _obsPrefixPos;
        private long _obsDeadline;

        private void ObserveOnly(KeyEvent ev)
        {
            bool isWin = (ev.Vk == VK_LWIN || ev.Vk == VK_RWIN);

            if (!ev.IsDown)
            {
                if (_obsState == 1 && isWin)
                {
                    _obsState = 2;
                    _obsDeadline = Now + _cfg.WinUpWaitMs;
                }
                else if (_obsState == 2 && ev.Vk == VK_R)
                {
                    // 实测固件只发 R 的抬起，不发按下，故抬起也要接受
                    _obsState = 3;
                    _obsDeadline = Now + _cfg.RWaitMs;
                }
                return;
            }

            switch (_obsState)
            {
                case 0:
                    if (isWin)
                    {
                        _obsState = 1;
                        _obsDeadline = Now + _cfg.WinWaitMs;
                    }
                    break;

                case 1:
                    if (isWin) break;          // Win 键自动重复，继续等
                    // Win 之后直接出现 R（固件可能把 R↓ 发在 Win↑ 之前）
                    if (ev.Vk == VK_R)
                    {
                        _obsState = 3;
                        _obsDeadline = Now + _cfg.RWaitMs;
                    }
                    else _obsState = 0;        // Win 后跟了别的键，不是宏
                    break;

                case 2:
                    if (ev.Vk == VK_R)
                    {
                        _obsState = 3;
                        _obsDeadline = Now + _cfg.RWaitMs;
                    }
                    else _obsState = 0;
                    break;

                case 3:
                    if (ev.Vk == VK_H)
                    {
                        _obsState = 4;
                        _obsPrefixPos = 1;
                        _obsSigPos = 0;
                        _obsSigSeen = false;
                        _obsDeadline = Now + _cfg.MacroTimeoutMs;
                    }
                    else _obsState = 0;
                    break;

                case 4:
                    if (ev.Vk == VK_ENTER && _obsSigSeen && _obsPrefixPos == 4)
                    {
                        TriggerCount++;
                        Log("☆ 观察模式：识别到自定义键（未拦截、未执行动作）");
                        Action<bool> handler = Triggered;
                        if (handler != null) handler(true);
                        _obsState = 0;
                        break;
                    }

                    // 自动重复会污染序列，忽略
                    if ((Native.GetAsyncKeyState(ev.Vk) & 0x4000) != 0) break;

                    if (_obsPrefixPos == 1 && ev.Vk == 0x54) _obsPrefixPos = 2;
                    else if (_obsPrefixPos == 2 && ev.Vk == 0x54) _obsPrefixPos = 3;
                    else if (_obsPrefixPos == 3 && ev.Vk == 0x50) _obsPrefixPos = 4;
                    else if (_obsPrefixPos < 4) _obsPrefixPos = -1;

                    if (_obsSigPos < _sig.Length && ev.Vk == _sig[_obsSigPos])
                    {
                        _obsSigPos++;
                        if (_obsSigPos >= _sig.Length) _obsSigSeen = true;
                    }
                    else
                    {
                        _obsSigPos = (ev.Vk == _sig[0]) ? 1 : 0;
                    }
                    break;
            }
        }

        // ---------- 各状态 ----------

        private IntPtr HandleIdle(KeyEvent ev, int nCode, IntPtr wParam, IntPtr lParam)
        {
            bool isWin = (ev.Vk == VK_LWIN || ev.Vk == VK_RWIN);

            if (isWin && ev.IsDown && _cfg.SuppressWindowsKey)
            {
                _held.Clear();
                _held.Add(ev);
                _state = DetectorState.WinDown;
                _deadline = Now + _cfg.WinWaitMs;
                return Swallow();
            }
            return Pass(nCode, wParam, lParam);
        }

        private IntPtr HandleWinDown(KeyEvent ev, int nCode, IntPtr wParam, IntPtr lParam)
        {
            bool isWin = (ev.Vk == VK_LWIN || ev.Vk == VK_RWIN);

            if (isWin && !ev.IsDown)
            {
                _held.Add(ev);
                _state = DetectorState.WinUpSeen;
                _deadline = Now + _cfg.WinUpWaitMs;
                return Swallow();
            }

            if (isWin && ev.IsDown)
            {
                // Win 键自动重复：继续暂扣，并顺延观察窗口
                if (_held.Count < 64)
                {
                    _held.Add(ev);
                    _deadline = Now + _cfg.WinWaitMs;
                    return Swallow();
                }
                // 暂扣过多（长按），放弃拦截，原样放行
                Log("暂扣事件过多，放弃本次拦截");
                FlushHeld();
                _state = DetectorState.Idle;
                return Pass(nCode, wParam, lParam);
            }

            // Win↓ 之后直接出现 R：
            // 实测固件在这里就发出了 R 的按下（甚至在 Win↑ 之前），
            // 因此只要看到 R，就认为进入宏序列，继续观察。
            if (ev.Vk == VK_R)
            {
                _held.Add(ev);
                _state = DetectorState.WinRSeen;
                _deadline = Now + _cfg.RWaitMs;
                return Swallow();
            }

            // 用户按了 Win + 其他键（如 Win+E）→ 不是我们的宏，原样恢复
            Abort("Win 后跟了其他键: 0x" + ev.Vk.ToString("X2"));
            return Pass(nCode, wParam, lParam);
        }

        private IntPtr HandleWinUpSeen(KeyEvent ev, int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 实测固件在这里发出 R（有的固件只发抬起，有的按下抬起都发），
            // 因此 down/up 都接受，避免漏判。
            if (ev.Vk == VK_R)
            {
                _held.Add(ev);
                _state = DetectorState.WinRSeen;
                _deadline = Now + _cfg.RWaitMs;
                return Swallow();
            }

            // Win↑ 之后不是 R（可能只是单独按 Win，或 Win+其他组合的收尾）
            Abort("Win 后不是 R");
            return Pass(nCode, wParam, lParam);
        }

        private IntPtr HandleWinRSeen(KeyEvent ev, int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 实测固件在这段的顺序是  Win↓ R↓ Win↑ R↑  h...
            // 因此本状态里会出现 Win↑ 和 R↑ 的收尾事件，必须容忍并继续等 'h'。
            // 只接受“抬起”事件；一旦出现其他键的“按下”，才说明不是宏。
            if (!ev.IsDown)
            {
                _held.Add(ev);
                return Swallow();
            }

            if (ev.Vk == VK_H)
            {
                _held.Add(ev);
                _state = DetectorState.Macro;
                _macroChars = 1;
                _macroStartedAt = Now;
                _prefixPos = 1;        // 已收到 'h'
                _sigPos = 0;
                _sigSeen = false;
                _deadline = Now + _cfg.MacroTimeoutMs;
                return Swallow();
            }

            // Win+R 之后按下的不是 'h' → 用户自己在用“运行”对话框，恢复原样
            Abort("Win+R 后首字符不是 h: 0x" + ev.Vk.ToString("X2"));
            return Pass(nCode, wParam, lParam);
        }

        private IntPtr HandleMacro(KeyEvent ev, int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 按住不放产生的自动重复会污染序列，直接丢弃
            if (ev.IsDown && (Native.GetAsyncKeyState(ev.Vk) & 0x4000) != 0)
                return Swallow();

            if (ev.IsDown)
            {
                _macroChars++;

                // 前缀 "http"
                if (_prefixPos == 1 && ev.Vk == 0x54) _prefixPos = 2;       // t
                else if (_prefixPos == 2 && ev.Vk == 0x54) _prefixPos = 3;  // t
                else if (_prefixPos == 3 && ev.Vk == 0x50) _prefixPos = 4;  // p
                else if (_prefixPos < 4) _prefixPos = -1;                   // 前缀不符

                // 前缀已不可能匹配 → 立即放弃，别让用户白等超时。
                // 注意：此时当前事件还没进 _held，所以 Abort 重放旧事件后，
                // 再把它放行，顺序与数量都正确，不会重复。
                if (_prefixPos < 0)
                {
                    Abort("内容不是 http 开头，判定为普通输入");
                    return Pass(nCode, wParam, lParam);
                }

                // 特征串 "oneclick"
                if (_sigPos < _sig.Length && ev.Vk == _sig[_sigPos])
                {
                    _sigPos++;
                    if (_sigPos >= _sig.Length) _sigSeen = true;
                }
                else
                {
                    _sigPos = (ev.Vk == _sig[0]) ? 1 : 0;
                }

                // 命中条件：前缀正确 + 见过特征串 + 按下回车
                if (ev.Vk == VK_ENTER && _sigSeen && _prefixPos == 4)
                {
                    ConfirmMacro();
                    return Swallow();
                }
            }

            _held.Add(ev);
            _deadline = Now + _cfg.MacroTimeoutMs;

            // 真正的宏里 "oneclick" 出现在第 8~15 个字符。
            // 若已输入 20 个字符还没见到它，就不可能是我们的宏（例如用户
            // 在“运行”对话框里手打了 http://...），立即放弃，避免白等超时。
            if (!_sigSeen && _macroChars > 20)
            {
                Abort("未出现特征串，判定为普通输入");
                return Swallow();
            }

            if (_held.Count > 400)
                Abort("宏序列过长");

            return Swallow();
        }

        // ---------- 收尾动作 ----------

        private void ConfirmMacro()
        {
            TriggerCount++;
            SuppressedEventCount += _held.Count;
            int n = _held.Count;
            _lastMacroEvents = n;
            long cost = Now - _macroStartedAt;
            _held.Clear();
            _state = DetectorState.Idle;
            _deadline = 0;

            Log("★ 命中自定义键：拦截 " + n + " 个按键事件，耗时 " + cost + " ms，执行用户动作");

            Action<bool> handler = Triggered;
            if (handler != null) handler(false);
        }

        private void Abort(string reason)
        {
            if (_held.Count > 0) Log("放弃拦截（" + reason + "），重放 " + _held.Count + " 个事件");
            FlushHeld();
            _state = DetectorState.Idle;
            _deadline = 0;
        }

        /// <summary>把暂扣的按键原样重放给系统（批量注入，保证顺序与速度）。</summary>
        private void FlushHeld()
        {
            if (_held.Count == 0) return;
            List<KeyEvent> copy = new List<KeyEvent>(_held);
            _held.Clear();
            try { InputSender.Replay(copy); }
            catch (Exception ex) { Log("重放失败: " + ex.Message); }
        }

        // ==================== 超时泵 ====================

        /// <summary>由后台线程周期性调用，处理“等待超时”的收尾。</summary>
        public void Tick()
        {
            lock (_gate)
            {
                if (_cfg.DryRun)
                {
                    if (_obsState != 0 && _obsDeadline != 0 && Now >= _obsDeadline) _obsState = 0;
                    return;
                }

                if (_state == DetectorState.Idle || _deadline == 0) return;
                if (Now < _deadline) return;

                switch (_state)
                {
                    case DetectorState.WinDown:
                        // 只是单独按了 Win 键 → 恢复，让开始菜单正常弹出
                        Abort("等待超时（单独按 Win）");
                        break;
                    case DetectorState.WinUpSeen:
                        Abort("等待 R 超时");
                        break;
                    case DetectorState.WinRSeen:
                        Abort("等待宏首字符超时");
                        break;
                    case DetectorState.Macro:
                        Abort("宏收集超时");
                        break;
                }
            }
        }

        // ==================== 辅助 ====================

        private long Now { get { return _clock.ElapsedMilliseconds; } }

        private static IntPtr Swallow() { return new IntPtr(1); }

        private IntPtr Pass(int nCode, IntPtr wParam, IntPtr lParam)
        {
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private void Log(string s)
        {
            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s;
                File.AppendAllText(_logPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }

            Action<string> t = Trace;
            if (t != null) t(s);
        }

        private readonly string _logPath;

        public Detector(Config cfg, string logPath)
        {
            _cfg = cfg;
            _sig = cfg.Signature;
            _logPath = logPath;
        }

        /// <summary>热重载配置。切换时会先把暂扣的按键放行，避免卡键。</summary>
    }
}
