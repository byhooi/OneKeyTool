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

        private readonly Action<string> _log;
        private Config _cfg;
        private ushort[] _sig;

        private State _state = State.Idle;
        private readonly List<KeyEvent> _held = new List<KeyEvent>();
        private readonly List<KeyEvent> _replay = new List<KeyEvent>();
        private readonly HashSet<ushort> _pressed = new HashSet<ushort>();

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
            Abort(reason);
            r.HasReplay = _replay.Count > 0;
            return r;
        }

        /// <summary>喂入一个按键事件。nowMs 为调用方提供的单调时间（毫秒）。</summary>
        public EngineResult Feed(KeyEvent ev, long nowMs)
        {
            _now = nowMs;

            // 自动重复 = 同一个键在未抬起的情况下再次按下
            bool repeated = ev.IsDown && _pressed.Contains(ev.Vk);
            if (ev.IsDown) _pressed.Add(ev.Vk);
            else _pressed.Remove(ev.Vk);

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
                    Confirm();
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

        private void Confirm()
        {
            _triggeredThisEvent = true;
            _triggerHeldCount = _held.Count;

            if (_log != null)
                _log("命中自定义键：暂扣 " + _held.Count + " 个事件，耗时 " + (_now - _macroStartedAt) + " ms");

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
