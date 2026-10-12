// MacroTest.cs — 自动回放实测抓到的按键序列，用于验证 OneKeyTool 的识别逻辑
// 序列来源：KeySpy 抓包（固件宏真实波形）
//   Win↓ Win↑ R↑ [约1.4ms] h t t p Shift+: / / oneclick.lenovo.com.cn [1.1s] Enter
//
// 用法：先以测试配置启动 OneKeyTool，再运行本程序。
// 编译：csc /platform:x64 /out:MacroTest.exe MacroTest.cs

using System;
using System.Runtime.InteropServices;
using System.Threading;

internal static class MacroTest
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_ENTER = 0x0D;

    private static void Key(ushort vk, bool down, bool ext)
    {
        INPUT[] a = new INPUT[1];
        a[0].type = INPUT_KEYBOARD;
        a[0].U.ki.wVk = vk;
        a[0].U.ki.wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        uint f = 0;
        if (ext) f |= KEYEVENTF_EXTENDEDKEY;
        if (!down) f |= KEYEVENTF_KEYUP;
        a[0].U.ki.dwFlags = f;
        SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void Tap(ushort vk, bool ext) { Key(vk, true, ext); Key(vk, false, ext); }

    private static void Gap(int ms) { if (ms > 0) Thread.Sleep(ms); }

    private static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "macro";
        int count = args.Length > 1 ? int.Parse(args[1]) : 1;

        if (mode == "negatives")
        {
            Console.WriteLine("MacroTest：反例测试（这些都不应被识别为自定义键）");
            Thread.Sleep(3000);
            NegativeCases();
            Console.WriteLine("反例测试完毕。");
            return;
        }

        Console.WriteLine("MacroTest：将回放实测按键序列 " + count + " 次");
        Console.WriteLine("请在 5 秒内切换到记事本或任意输入框（非必须，仅用于肉眼观察）");
        Thread.Sleep(5000);

        for (int i = 0; i < count; i++)
        {
            Console.WriteLine("--- 第 " + (i + 1) + " 次回放 ---");
            ReplayOnce();
            Thread.Sleep(1500);
        }

        Console.WriteLine("回放完毕。");
    }

    /// <summary>反例：模拟用户正常操作，检验是否误判。</summary>
    private static void NegativeCases()
    {
        // 反例 1：单独按 Win 键（打开开始菜单）
        Console.WriteLine("反例1：单独按 Win 键");
        Key(VK_LWIN, true, true);
        Gap(120);
        Key(VK_LWIN, false, true);
        Gap(1200);

        // 反例 2：Win+E（打开资源管理器）
        Console.WriteLine("反例2：Win+E");
        Key(VK_LWIN, true, true);
        Gap(60);
        Tap(0x45, false);
        Gap(60);
        Key(VK_LWIN, false, true);
        Gap(1200);

        // 反例 3：用户正常打字 "http"
        Console.WriteLine("反例3：正常打字 http（间隔约 200ms）");
        Tap(0x48, false); Gap(200);
        Tap(0x54, false); Gap(200);
        Tap(0x54, false); Gap(200);
        Tap(0x50, false); Gap(1200);

        // 反例 4：用户手动 Win+R 然后输入别的内容（不按回车，避免真的执行命令）
        // 注意：真人按 Win+R 是 R↓ R↑ 都在 Win↑ 之后，与宏的顺序相反
        Console.WriteLine("反例4：手动 Win+R 后输入 xyz（不回车）");
        Key(VK_LWIN, true, true);
        Gap(60);
        Key(VK_LWIN, false, true);
        Gap(20);
        Tap(0x52, false);             // R↓ R↑，都在 Win↑ 之后
        Gap(400);
        Tap(0x58, false); Gap(180);   // x
        Tap(0x59, false); Gap(180);   // y
        Tap(0x5A, false); Gap(180);   // z
        Gap(600);
        Tap(0x1B, false);             // Esc 关闭“运行”对话框
        Gap(1200);

        // 反例 5：方向键连按
        Console.WriteLine("反例5：方向键连按");
        for (int i = 0; i < 4; i++) { Tap(0x27, true); Gap(120); }
        Gap(1200);
    }

    private static void ReplayOnce()
    {
        // 1) Win↓ 之后紧跟 R↓（实测固件把 R↓ 发在 Win↑ 之前），再 Win↑ R↑
        Key(VK_LWIN, true, true);
        Gap(10);
        Key(0x52, false, false);    // R↓（非扩展）
        Gap(2);
        Key(VK_LWIN, false, true);  // Win↑
        Gap(4);
        Key(0x52, false, false);    // R↑
        Gap(80);

        // 2) 宏字符开始
        Tap(0x48, false);           // h
        Gap(1);
        Tap(0x54, false);           // t
        Gap(1);
        Tap(0x54, false);           // t
        Gap(1);
        Tap(0x50, false);           // p
        Gap(1);

        // 4) Shift + ':'  (0xBA 是 OEM 分号，Shift 后为冒号)
        Key(VK_LSHIFT, true, false);
        Gap(1);
        Tap(0xBA, false);
        Gap(1);
        Key(VK_LSHIFT, false, false);
        Gap(1);

        // 5) 两个斜杠
        Tap(0xBF, false);
        Gap(1);
        Tap(0xBF, false);
        Gap(1);

        // 6) oneclick
        ushort[] sig = new ushort[] { 0x4F, 0x4E, 0x45, 0x43, 0x4C, 0x49, 0x43, 0x4B };
        for (int i = 0; i < sig.Length; i++) { Tap(sig[i], false); Gap(1); }

        // 7) .lenovo.com.cn
        ushort[] rest = new ushort[] {
            0xBE, 0x4C, 0x45, 0x4E, 0x4F, 0x56, 0x4F,   // .lenovo
            0xBE, 0x43, 0x4F, 0x4D,                      // .com
            0xBE, 0x43, 0x4E                             // .cn
        };
        for (int i = 0; i < rest.Length; i++) { Tap(rest[i], false); Gap(1); }

        // 8) 约 1.1 秒后回车
        Gap(1100);
        Tap(VK_ENTER, false);
        Gap(1);
    }
}
