// MacroEngine.cs — 自定义键识别状态机（纯逻辑，可测试）
//
// 背景（由实测抓包得出）：
//   该键由键盘固件模拟出一段宏，序列为
//       Win↓  R↓  Win↑  R↑        [约 12ms 内完成]
//       h t t p : / / o n e c l i c k . l e n o v o . c o m . c n
//       Enter
//   字符间隔极短（多数 < 1ms，最大约 15ms），远快于人类打字（30~390ms）。
//
// 策略：
//   1. 看到 Win↓ 时先“暂扣”不发给系统，给一个很短的观察窗口。
//   2. 窗口内出现 R → 判定为宏，转入收集状态。
//   3. 收集到特征串后遇到 Enter → 确认命中：丢弃整段宏，执行用户设定的动作。
//   4. 任何一步不符合预期 → 把暂扣的按键原样重放，行为与未装本工具时一致。
//
// 本类不碰 Win32、不做按键注入：时间由调用方注入，暂扣事件通过 TakeReplay 交还。
// 观察模式与拦截模式共用这一份实现——观察模式只是把 Swallow 降级为 Pass 且不重放，
// 因此两种模式的识别行为必然一致（这是本项目最需要保证的性质：
// 用户正是先靠观察模式确认识别准确，再切到拦截模式的）。

using System;
using System.Collections.Generic;

namespace OneKeyTool
{
    /// <summary>状态机对单个事件的处置。</summary>
    internal enum KeyDecision
    {
        /// <summary>放行给系统。</summary>
        Pass = 0,
        /// <summary>吞掉，不发给系统（观察模式下由调用方降级为放行）。</summary>
        Swallow = 1
    }

    /// <summary>状态机处理一个事件后的结果。</summary>
    internal struct EngineResult
    {
        /// <summary>对该事件的处置。</summary>
        public KeyDecision Decision;

        /// <summary>本次是否确认命中自定义键。</summary>
        public bool Triggered;

        /// <summary>命中时被丢弃的暂扣事件数（供拦截统计）。</summary>
        public int TriggerHeldCount;

        /// <summary>是否有暂扣事件交还重放（观察模式下调用方会忽略）。</summary>
        public bool HasReplay;
    }

    internal sealed class MacroEngine
    {
        private enum State
        {
            Idle,
            WinDown,      // 已暂扣 Win↓，等待后续
            WinUpSeen,    // 已暂扣 Win↑，等待 R
            WinRSeen,     // 已确认 Win+R，等待宏首字符
            Macro         // 正在收集宏字符
        }

        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_RWIN = 0x5C;
        private const ushort VK_R = 0x52;
        private const ushort VK_H = 0x48;
        private const ushort VK_T = 0x54;
        private const ushort VK_P = 0x50;
        private const ushort VK_ENTER = 0x0D;

        /// <summary>暂扣事件数的硬上限，超过即放弃拦截。</summary>
        internal const int MaxHeld = 400;

        /// <summary>宏字符数上限：真正的宏里特征串出现在第 8~15 个字符。</summary>
        internal const int MaxMacroChars = 20;

        /// <summary>
        /// 命中后，等待被丢弃序列里"按下但未抬起"的键补发抬起事件的时长（毫秒）。
        /// 实测固件在回车之后还会补一个 Enter↑，见 Confirm()。
        /// </summary>
        internal const int DanglingUpWaitMs = 500;

        /// <summary>
        /// 自动重复的判定窗口（毫秒）：同一键的两次 DOWN 间隔在此之内、且中间没有
        /// 收到 UP，才算按住不放产生的自动重复。
        ///
        /// 只看 _pressed 不够：只要某个 UP 丢失（钩子被系统短暂摘掉、被钩子链上
        /// 其他程序吞掉），_pressed 就会永久残留该键的“按下”状态，之后每一次
        /// 正常按下都会被误判成重复，从而跳过特征串匹配，整次宏识别失败
        /// （线上日志表现为“未出现特征串，判定为普通输入”）。
        /// 真自动重复的间隔由系统重复速率决定（最快约 30ms），远在窗口内；
        /// 残留状态的距离通常以秒计，会自然失效。
        /// </summary>
        internal const int AutoRepeatWindowMs = 500;

        private readonly Action<string> _log;
        private Config _cfg;
        private ushort[] _sig;

        private State _state = State.Idle;
        private readonly List<KeyEvent> _held = new List<KeyEvent>();
        private readonly List<KeyEvent> _replay = new List<KeyEvent>();
        private readonly HashSet<ushort> _pressed = new HashSet<ushort>();

        /// <summary>各键最近一次 DOWN 的时间，与 _pressed 配对，用于区分真正的自动重复。</summary>
        private readonly Dictionary<ushort, long> _lastDownAt = new Dictionary<ushort, long>();

        /// <summary>
        /// 命中时被丢弃、但尚未见到抬起事件的键。它们的抬起事件要一并吞掉，
        /// 否则系统会收到一个没有对应按下的 KEYUP。
        /// </summary>
        private readonly HashSet<ushort> _dangling = new HashSet<ushort>();
        private long _danglingDeadline;

        private long _now;
        private long _deadline;
        private long _macroStartedAt;

        private int _sigPos;
        private bool _sigSeen;
        private int _prefixPos;
        private int _macroChars;

        private bool _triggeredThisEvent;
        private int _triggerHeldCount;

        public MacroEngine(Config cfg, Action<string> log = null)
        {
            _cfg = cfg;
            _sig = SafeSignature(cfg.Signature);
            _log = log;
        }

        /// <summary>当前暂扣的事件数（诊断与测试用）。</summary>
        public int HeldCount { get { return _held.Count; } }

        /// <summary>热重载配置。切换前先把暂扣事件交还，避免卡键。</summary>
        public EngineResult UpdateConfig(Config cfg)
        {
            EngineResult r = Reset("配置已更新");
            _cfg = cfg;
            _sig = SafeSignature(cfg.Signature);
            return r;
        }

        /// <summary>立即清空状态并交还暂扣事件。</summary>
        public EngineResult Reset(string reason)
        {
            EngineResult r = new EngineResult();
            r.Decision = KeyDecision.Pass;
            _dangling.Clear();
            Abort(reason);
            r.HasReplay = _replay.Count > 0;
            return r;
        }

        /// <summary>喂入一个按键事件。nowMs 为调用方提供的单调时间（毫秒）。</summary>
        public EngineResult Feed(KeyEvent ev, long nowMs)
        {
            _now = nowMs;

            // 自动重复 = 同一个键在未抬起的情况下再次按下，且两次按下间隔足够短。
            // 时间窗口不可省：UP 丢失会让 _pressed 永久残留，若只看 _pressed，
            // 很久之后正常按下的同一个键也会被当成重复并跳过特征串匹配。
            long lastDown;
            bool repeated = ev.IsDown && _pressed.Contains(ev.Vk)
                && _lastDownAt.TryGetValue(ev.Vk, out lastDown)
                && nowMs - lastDown <= AutoRepeatWindowMs;

            if (ev.IsDown)
            {
                _pressed.Add(ev.Vk);
                _lastDownAt[ev.Vk] = nowMs;
            }
            else
            {
                _pressed.Remove(ev.Vk);
                _lastDownAt.Remove(ev.Vk);
            }

            _triggeredThisEvent = false;
            _triggerHeldCount = 0;

            EngineResult r = new EngineResult();
            r.Decision = Dispatch(ev, repeated);
            r.Triggered = _triggeredThisEvent;
            r.TriggerHeldCount = _triggerHeldCount;
            r.HasReplay = _replay.Count > 0;
            return r;
        }

        /// <summary>推进时间，处理等待超时。nowMs 为单调时间（毫秒）。</summary>
        public EngineResult Tick(long nowMs)
        {
            _now = nowMs;

            EngineResult r = new EngineResult();
            r.Decision = KeyDecision.Pass;

            if (_state == State.Idle || _deadline == 0) return r;
            if (nowMs < _deadline) return r;

            switch (_state)
            {
                case State.WinDown:   Abort("等待超时（单独按 Win）"); break;
                case State.WinUpSeen: Abort("等待 R 超时"); break;
                case State.WinRSeen:  Abort("等待宏首字符超时"); break;
                case State.Macro:     Abort("宏收集超时"); break;
            }

            r.HasReplay = _replay.Count > 0;
            return r;
        }

        /// <summary>取走待重放的暂扣事件（调用方负责真正注入）。</summary>
        public void TakeReplay(List<KeyEvent> into)
        {
            if (_replay.Count == 0) return;
            into.AddRange(_replay);
            _replay.Clear();
        }

        // ==================== 状态转移 ====================

        private KeyDecision Dispatch(KeyEvent ev, bool repeated)
        {
            bool isWin = (ev.Vk == VK_LWIN || ev.Vk == VK_RWIN);

            switch (_state)
            {
                case State.Idle:
                    // 命中后固件补发的抬起事件：吞掉，避免系统收到孤立的 KEYUP。
                    // 只吞"被丢弃序列里确实按下过、且还没见抬起"的键，并带过期时间，
                    // 因此不会误伤用户自己的按键。两种模式下这里的行为完全一致。
                    if (_dangling.Count > 0)
                    {
                        if (_now > _danglingDeadline) _dangling.Clear();
                        else if (!ev.IsDown && _dangling.Remove(ev.Vk)) return KeyDecision.Swallow;
                    }

                    // 观察模式下即使不拦 Win 键也要识别，否则没法用它验证识别是否准确
                    if (isWin && ev.IsDown && (_cfg.SuppressWindowsKey || _cfg.DryRun))
                    {
                        _held.Clear();
                        _held.Add(ev);
                        _state = State.WinDown;
                        _deadline = _now + _cfg.WinWaitMs;
                        return KeyDecision.Swallow;
                    }
                    return KeyDecision.Pass;

                case State.WinDown:
                    if (isWin && !ev.IsDown)
                    {
                        _held.Add(ev);
                        _state = State.WinUpSeen;
                        _deadline = _now + _cfg.WinUpWaitMs;
                        return KeyDecision.Swallow;
                    }

                    // Win 键自动重复：立即放弃，不再顺延观察窗口。
                    // 否则长按 Win 时开始菜单迟迟不弹，这期间的按键全被吞掉。
                    if (isWin && ev.IsDown)
                    {
                        Abort("Win 键自动重复");
                        return KeyDecision.Pass;
                    }

                    // 实测固件在 Win↑ 之前就发出了 R↓，故此处即接受 R
                    if (ev.Vk == VK_R)
                    {
                        _held.Add(ev);
                        _state = State.WinRSeen;
                        _deadline = _now + _cfg.RWaitMs;
                        return KeyDecision.Swallow;
                    }

                    Abort("Win 后跟了其他键: 0x" + ev.Vk.ToString("X2"));
                    return KeyDecision.Pass;

                case State.WinUpSeen:
                    // 有的固件只发 R↑，有的按下抬起都发，故 down/up 都接受，避免漏判
                    if (ev.Vk == VK_R)
                    {
                        _held.Add(ev);
                        _state = State.WinRSeen;
                        _deadline = _now + _cfg.RWaitMs;
                        return KeyDecision.Swallow;
                    }
                    Abort("Win 后不是 R");
                    return KeyDecision.Pass;

                case State.WinRSeen:
                    // 实测顺序为 Win↓ R↓ Win↑ R↑ h...，本状态会出现 Win↑/R↑ 的收尾事件，必须容忍
                    if (!ev.IsDown)
                    {
                        _held.Add(ev);
                        return KeyDecision.Swallow;
                    }
                    if (ev.Vk == VK_H)
                    {
                        _held.Add(ev);
                        _state = State.Macro;
                        _macroChars = 1;
                        _macroStartedAt = _now;
                        _prefixPos = 1;        // 已收到 'h'
                        _sigPos = 0;
                        _sigSeen = false;
                        _deadline = _now + _cfg.MacroTimeoutMs;
                        return KeyDecision.Swallow;
                    }
                    Abort("Win+R 后首字符不是 h: 0x" + ev.Vk.ToString("X2"));
                    return KeyDecision.Pass;

                case State.Macro:
                    return HandleMacro(ev, repeated);
            }

            return KeyDecision.Pass;
        }

        private KeyDecision HandleMacro(KeyEvent ev, bool repeated)
        {
            // 自动重复不参与匹配（否则长按会污染序列），但仍然计入暂扣与长度上限，
            // 这样“按住不放”也会在 MaxHeld 处放弃，且两种模式下行为一致。
            if (ev.IsDown && !repeated)
            {
                _macroChars++;

                // 前缀 "http"
                if (_prefixPos == 1 && ev.Vk == VK_T) _prefixPos = 2;
                else if (_prefixPos == 2 && ev.Vk == VK_T) _prefixPos = 3;
                else if (_prefixPos == 3 && ev.Vk == VK_P) _prefixPos = 4;
                else if (_prefixPos < 4) _prefixPos = -1;      // 前缀不符

                if (_prefixPos < 0)
                {
                    // 此时当前事件还没进 _held：Abort 重放旧事件后再放行它，
                    // 顺序与数量都正确，不会重复。
                    Abort("内容不是 http 开头，判定为普通输入");
                    return KeyDecision.Pass;
                }

                // 特征串
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
                    Confirm(ev);
                    return KeyDecision.Swallow;
                }
            }

            _held.Add(ev);
            // 自动重复不刷新超时窗口，否则长按会让宏状态永远不超时
            if (!repeated) _deadline = _now + _cfg.MacroTimeoutMs;

            // 真正的宏里特征串出现在第 8~15 个字符。若已输入 20 个字符还没见到它，
            // 就不可能是我们的宏（例如用户在“运行”对话框里手打了 http://...），
            // 立即放弃，避免白等超时。
            if (!_sigSeen && _macroChars > MaxMacroChars)
            {
                Abort("未出现特征串，判定为普通输入");
                return KeyDecision.Swallow;
            }

            // 硬上限：注意这个判断必须对自动重复同样生效，否则长按会让 _held 无界增长。
            if (_held.Count > MaxHeld)
            {
                Abort("宏序列过长");
                return KeyDecision.Swallow;
            }

            return KeyDecision.Swallow;
        }

        // ==================== 收尾 ====================

        /// <summary>确认命中：丢弃整段宏，并记下还没有抬起事件的键。</summary>
        private void Confirm(KeyEvent trigger)
        {
            _triggeredThisEvent = true;
            _triggerHeldCount = _held.Count;

            if (_log != null)
                _log("命中自定义键：暂扣 " + _held.Count + " 个事件，耗时 " + (_now - _macroStartedAt) + " ms");

            // 被丢弃的序列里可能有"按下但还没抬起"的键——触发它的那个回车就不在 _held 里，
            // 实测固件在回车之后还会补发一个 Enter↑。把它们记下来，稍后连抬起事件一起吞掉，
            // 否则系统会收到一个没有对应按下的 KEYUP。
            _dangling.Clear();
            for (int i = 0; i < _held.Count; i++)
            {
                if (_held[i].IsDown) _dangling.Add(_held[i].Vk);
                else _dangling.Remove(_held[i].Vk);
            }
            if (trigger.IsDown) _dangling.Add(trigger.Vk);
            else _dangling.Remove(trigger.Vk);
            _danglingDeadline = _now + DanglingUpWaitMs;

            _held.Clear();
            _state = State.Idle;
            _deadline = 0;
        }

        /// <summary>放弃本次拦截，把暂扣事件交还调用方重放。</summary>
        private void Abort(string reason)
        {
            if (_held.Count > 0)
            {
                if (_log != null) _log("放弃拦截（" + reason + "），重放 " + _held.Count + " 个事件");
                _replay.AddRange(_held);
            }
            _held.Clear();
            _state = State.Idle;
            _deadline = 0;
        }

        private static ushort[] SafeSignature(ushort[] sig)
        {
            if (sig != null && sig.Length > 0) return sig;
            return new ushort[] { 0x4F };   // 'O'，兜底，避免 _sig[0] 越界
        }
    }
}
