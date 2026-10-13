// StateMachineTests.cs — 状态机回归测试
//
// 由 build.ps1 在每次构建时编译并运行，退出码非 0 即视为构建失败。
// 不依赖 Win32 钩子：通过 Detector 的测试钩子喂入模拟事件序列与时间。
//
// 编译：csc /main:OneKeyTool.Tests.TestRunner /out:StateMachineTests.exe src\*.cs tests\StateMachineTests.cs

using System;
using System.Collections.Generic;
using OneKeyTool;

namespace OneKeyTool.Tests
{
    /// <summary>一条带时间戳的模拟按键事件。</summary>
    internal struct Step
    {
        public ushort Vk;
        public bool IsDown;
        public bool IsExtended;
        public long At;

        public Step(ushort vk, bool down, long at, bool extended = false)
        {
            Vk = vk;
            IsDown = down;
            IsExtended = extended;
            At = at;
        }

        public KeyEvent ToEvent()
        {
            // scan 取非 0 值，便于验证重放走扫描码路径
            return new KeyEvent(Vk, (ushort)(Vk + 0x100), IsDown, IsExtended);
        }
    }

    /// <summary>录制一段按键序列，供两种模式回放同一份输入。</summary>
    internal sealed class Recorder
    {
        public readonly List<Step> Steps = new List<Step>();
        public long T;

        public void Down(ushort vk, bool extended = false) { Steps.Add(new Step(vk, true, T++, extended)); }
        public void Up(ushort vk, bool extended = false) { Steps.Add(new Step(vk, false, T++, extended)); }
        public void Tap(ushort vk, bool extended = false) { Down(vk, extended); Up(vk, extended); }

        public void Text(string text)
        {
            foreach (char ch in text) Tap((ushort)char.ToUpperInvariant(ch));
        }

        public void Gap(int ms) { T += ms; }
    }

    internal static class TestRunner
    {
        // 常用虚拟键码
        private const ushort LWIN = 0x5B;
        private const ushort R = 0x52;
        private const ushort H = 0x48;
        private const ushort T = 0x54;
        private const ushort P = 0x50;
        private const ushort U = 0x55;
        private const ushort N = 0x4E;
        private const ushort A = 0x41;
        private const ushort E = 0x45;
        private const ushort LEFT = 0x25;
        private const ushort ENTER = 0x0D;
        private const ushort LSHIFT = 0xA0;
        private const ushort OEM_SEMICOLON = 0xBA;   // ;: 键，加 Shift 才是冒号
        private const ushort OEM_SLASH = 0xBF;       // /? 键
        private const ushort OEM_PERIOD = 0xBE;      // . 键

        private static int _assertions;
        private static int _failures;

        private static void Assert(bool condition, string message)
        {
            _assertions++;
            if (condition) return;
            _failures++;
            Console.WriteLine("  断言失败: " + message);
        }

        private static Detector NewDetector(bool dryRun, out Config cfg)
        {
            cfg = new Config();
            cfg.DryRun = dryRun;
            return new Detector(cfg, null, true);
        }

        // ==================== 序列构造 ====================

        /// <summary>
        /// 真实固件序列，逐键码照抄 tools/MacroTest/MacroTest.cs 的 ReplayOnce()：
        ///   Win↓ R↓ Win↑ R↑  h t t p  Shift+:  / /  oneclick  .lenovo .com .cn  [1.1s] Enter↓ Enter↑
        ///
        /// 标点要按真实键码来：固件发的是 Shift + OEM 分号键(0xBA)、OEM 斜杠键(0xBF)、
        /// OEM 句点键(0xBE)，不是 VK 0x3A / 0x2F / 0x2E。状态机不关心这几个键，
        /// 但用例只有复现真实波形才算验证了真实输入。
        /// </summary>
        private static Recorder RealFirmwareMacro() { return RealFirmwareMacro(true); }

        private static Recorder RealFirmwareMacro(bool trailingEnterUp)
        {
            Recorder r = new Recorder();

            // Win↓ R↓ Win↑ R↑（实测固件把 R↓ 发在 Win↑ 之前）
            r.Down(LWIN, true);
            r.Down(R);
            r.Up(LWIN, true);
            r.Up(R);
            r.Gap(80);

            // "http"
            r.Tap(H);
            r.Tap(T);
            r.Tap(T);
            r.Tap(P);

            // ":"
            r.Down(LSHIFT);
            r.Tap(OEM_SEMICOLON);
            r.Up(LSHIFT);

            // "//"
            r.Tap(OEM_SLASH);
            r.Tap(OEM_SLASH);

            // "oneclick"——特征串
            string sig = "ONECLICK";
            for (int i = 0; i < sig.Length; i++) r.Tap(sig[i]);

            // ".lenovo.com.cn"
            string rest = ".LENOVO.COM.CN";
            for (int i = 0; i < rest.Length; i++)
                r.Tap(rest[i] == '.' ? OEM_PERIOD : (ushort)rest[i]);

            // 约 1.1 秒后回车：按下、抬起都发
            r.Gap(1100);
            r.Down(ENTER);
            if (trailingEnterUp) r.Up(ENTER);
            return r;
        }

        /// <summary>按录制好的序列喂入检测器，并在末尾推进一次超时。</summary>
        private static void Play(Detector d, Recorder r, bool trailingTimeout)
        {
            foreach (Step s in r.Steps) d.FeedForTest(s.ToEvent(), s.At);
            if (trailingTimeout) d.TickForTest(r.T + 10000);
        }

        // ==================== 用例 ====================

        /// <summary>完整宏在观察模式与拦截模式下都必须识别，且不应重放。</summary>
        private static void TestFullMacroInBothModes()
        {
            foreach (bool dryRun in new bool[] { false, true })
            {
                Config cfg;
                Detector d = NewDetector(dryRun, out cfg);
                int triggers = 0;
                d.Triggered += delegate { triggers++; };

                Play(d, RealFirmwareMacro(), false);

                Assert(triggers == 1, "dryRun=" + dryRun + " 真实固件序列应识别（实际 " + triggers + " 次）");
                Assert(d.TriggerCount == 1, "dryRun=" + dryRun + " 计数应 +1");
                Assert(d.ReplayedForTest == 0, "dryRun=" + dryRun + " 命中不应重放（实际 " + d.ReplayedForTest + "）");
            }
        }

        /// <summary>固件只发 R 抬起（不发按下）时也要能识别。</summary>
        private static void TestRUpOnlyVariant()
        {
            Recorder r = new Recorder();
            r.Down(LWIN, true);
            r.Up(R);                       // 只有 R↑
            r.Text("HTTP://ONECLICK.LENOVO.COM.CN");
            r.Down(ENTER);

            foreach (bool dryRun in new bool[] { false, true })
            {
                Config cfg;
                Detector d = NewDetector(dryRun, out cfg);
                Play(d, r, false);
                Assert(d.TriggerCount == 1, "dryRun=" + dryRun + " 只发 R↑ 的固件也应识别");
            }
        }

        /// <summary>Win + 其他键（如 Win+E）应立即放弃拦截并恢复暂扣的 Win。</summary>
        private static void TestWinShortcutReplay()
        {
            Config cfg;
            Detector d = NewDetector(false, out cfg);

            Assert(d.FeedForTest(new Step(LWIN, true, 0, true).ToEvent(), 0), "Win 按下应暂扣");
            Assert(!d.FeedForTest(new Step(E, true, 1).ToEvent(), 1), "Win+E 的 E 应放行");
            Assert(d.ReplayedForTest == 1, "应重放暂扣的 Win 按下（实际 " + d.ReplayedForTest + "）");
            Assert(d.TriggerCount == 0, "Win+E 不应触发");
        }

        /// <summary>单独按 Win：超时后恢复，开始菜单才能正常弹出。</summary>
        private static void TestSingleWinTimeoutReplay()
        {
            Config cfg;
            Detector d = NewDetector(false, out cfg);

            d.FeedForTest(new Step(LWIN, true, 0, true).ToEvent(), 0);
            d.TickForTest(cfg.WinWaitMs + 1);

            Assert(d.ReplayedForTest == 1, "单独按 Win 超时后应恢复（实际 " + d.ReplayedForTest + "）");
            Assert(d.TriggerCount == 0, "单独按 Win 不应触发");
        }

        /// <summary>长按 Win（自动重复）应立即放弃，不再顺延观察窗口。</summary>
        private static void TestWinRepeatDoesNotExtendWait()
        {
            Config cfg;
            Detector d = NewDetector(false, out cfg);

            d.FeedForTest(new Step(LWIN, true, 0, true).ToEvent(), 0);
            Assert(!d.FeedForTest(new Step(LWIN, true, 10, true).ToEvent(), 10), "Win 自动重复应立即放行");
            Assert(d.ReplayedForTest == 1, "自动重复时应恢复首次 Win 按下（实际 " + d.ReplayedForTest + "）");
        }

        /// <summary>
        /// 回归（曾出过事故）：宏收集状态下按住某个键不放（自动重复），
        /// 暂扣事件必须在 MaxHeld 处放弃，不能无界增长、也不能永不超时。
        /// </summary>
        private static void TestMacroHoldDoesNotGrowUnbounded()
        {
            Config cfg;
            Detector d = NewDetector(false, out cfg);

            Recorder r = new Recorder();
            r.Down(LWIN, true);
            r.Down(R);
            r.Text("HTTP");                 // 进入宏收集状态
            r.Down(T);                      // 按下 't' 后按住不放
            for (int i = 0; i < 2000; i++) { r.Gap(33); r.Down(T); }

            Play(d, r, false);

            Assert(d.HeldCountForTest <= MacroEngine.MaxHeld + 1,
                "宏状态下按住键不应让暂扣无界增长（实际 " + d.HeldCountForTest + "）");
            Assert(d.ReplayedForTest > 0, "放弃拦截后应把暂扣事件交还重放");
            Assert(d.TriggerCount == 0, "长按不应误触发");
        }

        /// <summary>宏收集中途超时，应把暂扣的全部事件按序重放。</summary>
        private static void TestMacroTimeoutReplay()
        {
            Config cfg;
            Detector d = NewDetector(false, out cfg);

            Recorder r = new Recorder();
            r.Down(LWIN, true);
            r.Down(R);
            r.Text("HTTP");

            Play(d, r, false);
            int heldAtTimeout = d.HeldCountForTest;
            Assert(heldAtTimeout == r.Steps.Count, "进入宏状态后全部事件应被暂扣");

            d.TickForTest(r.T + cfg.MacroTimeoutMs + 10);

            Assert(d.TriggerCount == 0, "宏超时不应触发");
            Assert(d.ReplayedForTest == heldAtTimeout,
                "宏超时应重放全部暂扣事件（期望 " + heldAtTimeout + "，实际 " + d.ReplayedForTest + "）");
            Assert(d.HeldCountForTest == 0, "超时后暂扣应清空");
        }

        /// <summary>重放必须保持原始顺序，且带原始扫描码。</summary>
        private static void TestReplayPreservesOrderAndScan()
        {
            Config cfg;
            Detector d = NewDetector(false, out cfg);

            long t = 0;
            d.FeedForTest(new Step(LWIN, true, t++, true).ToEvent(), t);
            d.FeedForTest(new Step(LWIN, false, t++, true).ToEvent(), t);
            d.FeedForTest(new Step(R, true, t++).ToEvent(), t);
            d.FeedForTest(new Step(R, false, t++).ToEvent(), t);
            d.FeedForTest(new Step(N, true, t++).ToEvent(), t);   // 'N'，触发放弃

            List<KeyEvent> replayed = d.ReplayListForTest;
            Assert(replayed.Count == 4, "应重放 4 个事件（实际 " + replayed.Count + "）");
            if (replayed.Count != 4) return;

            Assert(replayed[0].Vk == LWIN && replayed[0].IsDown && replayed[0].IsExtended, "r[0] 应为 Win 按下（扩展键）");
            Assert(replayed[1].Vk == LWIN && !replayed[1].IsDown, "r[1] 应为 Win 抬起");
            Assert(replayed[2].Vk == R && replayed[2].IsDown, "r[2] 应为 R 按下");
            Assert(replayed[3].Vk == R && !replayed[3].IsDown, "r[3] 应为 R 抬起");
            Assert(replayed[0].Scan != 0, "重放事件应保留原始扫描码");
        }

        /// <summary>手动 Win+R 后在"运行"对话框里输入其他内容，应放行。</summary>
        private static void TestManualRunDialogPasses()
        {
            Config cfg;
            Detector d = NewDetector(false, out cfg);

            long t = 0;
            d.FeedForTest(new Step(LWIN, true, t++, true).ToEvent(), t);
            d.FeedForTest(new Step(LWIN, false, t++, true).ToEvent(), t);
            d.FeedForTest(new Step(R, true, t++).ToEvent(), t);
            d.FeedForTest(new Step(R, false, t++).ToEvent(), t);

            Assert(!d.FeedForTest(new Step(N, true, t++).ToEvent(), t), "手动 Win+R 后输入内容应放行");
            Assert(d.ReplayedForTest == 4, "应按序恢复暂扣事件（实际 " + d.ReplayedForTest + "）");
            Assert(d.TriggerCount == 0, "手动 Win+R 不应触发");
        }

        /// <summary>错误的 http 前缀应立即放弃，不必白等超时。</summary>
        private static void TestWrongPrefixReplays()
        {
            Config cfg;
            Detector d = NewDetector(false, out cfg);

            long t = 0;
            d.FeedForTest(new Step(LWIN, true, t++, true).ToEvent(), t);
            d.FeedForTest(new Step(R, true, t++).ToEvent(), t);
            d.FeedForTest(new Step(LWIN, false, t++, true).ToEvent(), t);
            d.FeedForTest(new Step(R, false, t++).ToEvent(), t);
            d.FeedForTest(new Step(H, true, t++).ToEvent(), t);

            Assert(!d.FeedForTest(new Step(U, true, t++).ToEvent(), t), "错误的 http 前缀当前键应放行");
            Assert(d.ReplayedForTest == 5, "错误前缀应恢复此前暂扣的按键（实际 " + d.ReplayedForTest + "）");
            Assert(d.TriggerCount == 0, "错误前缀不应触发");
        }

        /// <summary>特征串缺失（用户手打 http://...）应在字符数上限内放弃。</summary>
        private static void TestMissingSignatureAbortsEarly()
        {
            Config cfg;
            Detector d = NewDetector(false, out cfg);

            Recorder r = new Recorder();
            r.Down(LWIN, true);
            r.Down(R);
            r.Text("HTTP://EXAMPLE.COM/PATH/THAT/IS/LONG");

            Play(d, r, false);

            Assert(d.TriggerCount == 0, "无特征串不应触发");
            Assert(d.ReplayedForTest > 0, "放弃后应恢复暂扣事件");
            Assert(d.HeldCountForTest == 0, "放弃后暂扣应清空");
        }

        /// <summary>普通打字与方向键不应被吞，也不应误触发。</summary>
        private static void TestOrdinaryTypingPasses()
        {
            Config cfg;
            Detector d = NewDetector(false, out cfg);

            Assert(!d.FeedForTest(new Step(A, true, 0).ToEvent(), 0), "普通按键按下应放行");
            Assert(!d.FeedForTest(new Step(A, false, 80).ToEvent(), 80), "普通按键抬起应放行");
            Assert(!d.FeedForTest(new Step(LEFT, true, 100, true).ToEvent(), 100), "方向键按下应放行");
            Assert(!d.FeedForTest(new Step(LEFT, false, 101, true).ToEvent(), 101), "方向键抬起应放行");
            Assert(d.TriggerCount == 0, "普通打字不应误触发");
            Assert(d.ReplayedForTest == 0, "普通打字不应产生重放");
        }

        /// <summary>
        /// 核心性质：同一事件序列下，观察模式与拦截模式的识别结果必须完全一致。
        /// 用户正是先靠观察模式确认识别准确、再切到拦截模式的，两者不一致会直接毁掉信任。
        /// </summary>
        private static void TestDryRunMatchesIntercept()
        {
            List<KeyValuePair<string, Recorder>> cases = new List<KeyValuePair<string, Recorder>>();

            cases.Add(new KeyValuePair<string, Recorder>("完整宏", RealFirmwareMacro()));

            Recorder rUpOnly = new Recorder();
            rUpOnly.Down(LWIN, true);
            rUpOnly.Up(R);
            rUpOnly.Text("HTTP://ONECLICK.LENOVO.COM.CN");
            rUpOnly.Down(ENTER);
            cases.Add(new KeyValuePair<string, Recorder>("只发 R↑", rUpOnly));

            Recorder winRepeat = new Recorder();
            winRepeat.Down(LWIN, true);
            winRepeat.Down(LWIN, true);          // 自动重复
            winRepeat.Down(R);
            winRepeat.Text("HTTP://ONECLICK.LENOVO.COM.CN");
            winRepeat.Down(ENTER);
            cases.Add(new KeyValuePair<string, Recorder>("Win 自动重复", winRepeat));

            Recorder macroTimeout = new Recorder();
            macroTimeout.Down(LWIN, true);
            macroTimeout.Down(R);
            macroTimeout.Text("HTTP");
            cases.Add(new KeyValuePair<string, Recorder>("宏中途超时", macroTimeout));

            Recorder holdKey = new Recorder();
            holdKey.Down(LWIN, true);
            holdKey.Down(R);
            holdKey.Text("HTTP");
            holdKey.Down(T);
            for (int i = 0; i < 50; i++) { holdKey.Gap(33); holdKey.Down(T); }
            cases.Add(new KeyValuePair<string, Recorder>("宏中长按", holdKey));

            Recorder wrongPrefix = new Recorder();
            wrongPrefix.Down(LWIN, true);
            wrongPrefix.Down(R);
            wrongPrefix.Tap(H);
            wrongPrefix.Tap(U);
            cases.Add(new KeyValuePair<string, Recorder>("错误前缀", wrongPrefix));

            Recorder winShortcut = new Recorder();
            winShortcut.Down(LWIN, true);
            winShortcut.Tap(E);
            cases.Add(new KeyValuePair<string, Recorder>("Win+E", winShortcut));

            Recorder soloWin = new Recorder();
            soloWin.Down(LWIN, true);
            cases.Add(new KeyValuePair<string, Recorder>("单独按 Win", soloWin));

            foreach (KeyValuePair<string, Recorder> c in cases)
            {
                int intercept = CountTriggers(c.Value, false);
                int dryRun = CountTriggers(c.Value, true);
                Assert(intercept == dryRun,
                    "[" + c.Key + "] 两种模式识别结果应一致（拦截 " + intercept + " / 观察 " + dryRun + "）");

                // 同时校验：观察模式绝不吞事件，也绝不产生重放
                Assert(DryRunSwallowsNothing(c.Value), "[" + c.Key + "] 观察模式不应吞掉任何事件");
                Assert(DryRunReplaysNothing(c.Value), "[" + c.Key + "] 观察模式不应产生重放");
            }
        }

        private static Detector PlayFresh(Recorder r, bool dryRun, bool trailingTimeout)
        {
            Config cfg;
            Detector d = NewDetector(dryRun, out cfg);
            Play(d, r, trailingTimeout);
            return d;
        }

        private static int CountTriggers(Recorder r, bool dryRun)
        {
            return (int)PlayFresh(r, dryRun, true).TriggerCount;
        }

        private static bool DryRunSwallowsNothing(Recorder r)
        {
            Config cfg;
            Detector d = NewDetector(true, out cfg);
            foreach (Step s in r.Steps)
                if (d.FeedForTest(s.ToEvent(), s.At)) return false;
            return true;
        }

        private static bool DryRunReplaysNothing(Recorder r)
        {
            return PlayFresh(r, true, true).ReplayedForTest == 0;
        }

        /// <summary>
        /// 命中后固件还会补一个 Enter↑。它必须被吞掉，否则系统会收到一个
        /// 没有对应按下的 KEYUP；而紧接着的无关按键抬起不能被误吞。
        /// 超过等待窗口后也要停止吞，免得把用户自己的回车抬起吃掉。
        /// </summary>
        private static void TestTrailingKeyUpAfterTrigger()
        {
            foreach (bool dryRun in new bool[] { false, true })
            {
                Config cfg;
                Detector d = NewDetector(dryRun, out cfg);

                Recorder r = RealFirmwareMacro(false);   // 不含尾部抬起
                Play(d, r, false);
                Assert(d.TriggerCount == 1, "dryRun=" + dryRun + " 应识别");

                long t = r.T + 1;
                bool swallowed = d.FeedForTest(new Step(ENTER, false, t).ToEvent(), t);
                Assert(swallowed == !dryRun,
                    "dryRun=" + dryRun + " 尾部 Enter↑ 应" + (!dryRun ? "吞掉" : "放行") +
                    "（实际" + (swallowed ? "吞掉" : "放行") + "）");

                bool stray = d.FeedForTest(new Step(A, false, t + 1).ToEvent(), t + 1);
                Assert(!stray, "dryRun=" + dryRun + " 无关按键的抬起不应被吞");
                Assert(d.ReplayedForTest == 0, "dryRun=" + dryRun + " 不应产生重放");
            }

            // 过了等待窗口就不该再吞，否则会永久吞掉用户自己的回车抬起
            {
                Config cfg;
                Detector d = NewDetector(false, out cfg);
                Recorder r = RealFirmwareMacro(false);
                Play(d, r, false);

                long late = r.T + MacroEngine.DanglingUpWaitMs + 100;
                Assert(!d.FeedForTest(new Step(ENTER, false, late).ToEvent(), late),
                    "超过等待窗口后不应再吞抬起事件");
            }
        }

        // ==================== 入口 ====================

        public static int Main()
        {
            Console.WriteLine("状态机回归测试");

            Run("完整宏（两种模式）", TestFullMacroInBothModes);
            Run("命中后尾部抬起事件", TestTrailingKeyUpAfterTrigger);
            Run("只发 R↑ 的固件变体", TestRUpOnlyVariant);
            Run("Win+E 立即恢复", TestWinShortcutReplay);
            Run("单独按 Win 超时恢复", TestSingleWinTimeoutReplay);
            Run("长按 Win 不顺延窗口", TestWinRepeatDoesNotExtendWait);
            Run("宏中长按不无界增长", TestMacroHoldDoesNotGrowUnbounded);
            Run("宏超时重放", TestMacroTimeoutReplay);
            Run("重放保持顺序与扫描码", TestReplayPreservesOrderAndScan);
            Run("手动 Win+R 放行", TestManualRunDialogPasses);
            Run("错误前缀立即放弃", TestWrongPrefixReplays);
            Run("特征串缺失提前放弃", TestMissingSignatureAbortsEarly);
            Run("普通打字放行", TestOrdinaryTypingPasses);
            Run("观察模式与拦截模式一致", TestDryRunMatchesIntercept);

            Console.WriteLine();
            if (_failures == 0)
            {
                Console.WriteLine("全部通过：" + _assertions + " 项断言");
                return 0;
            }
            Console.WriteLine("失败 " + _failures + " 项 / 共 " + _assertions + " 项断言");
            return 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("  [通过] " + name);
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine("  [异常] " + name + " → " + ex.Message);
            }
        }
    }
}
