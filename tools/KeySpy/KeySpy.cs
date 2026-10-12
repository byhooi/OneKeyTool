// KeySpy.cs — 键盘按键来源诊断工具（只读监听，不拦截、不修改任何东西）
// 用途：弄清自定义键究竟向系统发出了什么事件（普通按键？厂商 HID 报文？），以及时间间隔特征。
// 编译：csc.exe /platform:x64 /out:KeySpy.exe KeySpy.cs
// 运行：KeySpy.exe [监听秒数]   默认 120 秒

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class KeySpy
{
    // ================= Win32 常量 =================
    private const int WM_INPUT = 0x00FF;

    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;

    private const uint RIDEV_INPUTSINK = 0x00000100;

    private const int RIM_TYPEMOUSE = 0;
    private const int RIM_TYPEKEYBOARD = 1;
    private const int RIM_TYPEHID = 2;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;

    // ================= 结构体 =================
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWHID
    {
        public uint dwSizeHid;
        public uint dwCount;
        // bRawData 紧随其后
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    // ================= P/Invoke =================
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputDeviceList(IntPtr pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, StringBuilder pData, ref uint pcbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(IntPtr h, StringBuilder b, int len);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(IntPtr h, StringBuilder b, int len);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr p);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr pp, ref HIDP_CAPS caps);

    // ================= 状态 =================
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static StreamWriter _log;
    private static WndProcDelegate _proc;   // 防止委托被 GC
    private static IntPtr _hwnd;
    private static readonly Dictionary<IntPtr, string> _devPaths = new Dictionary<IntPtr, string>();
    private static readonly byte[] _keyState = new byte[256];
    private static readonly long[] _recent = new long[8];
    private static int _recentIdx;
    private static long _lastTick;
    private static long _index;
    private static readonly StringBuilder _typed = new StringBuilder();  // 重建出的键入字符串
    private static double _typedStartMs;

    private static void Say(string s)
    {
        Console.WriteLine(s);
        if (_log != null) { _log.WriteLine(s); _log.Flush(); }
    }

    private static void Main(string[] args)
    {
        int seconds = 120;
        if (args.Length > 0) int.TryParse(args[0], out seconds);
        if (seconds <= 0) seconds = 120;

        string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        string logPath = Path.Combine(logDir, "keyspy_" + DateTime.Now.ToString("HHmmss") + ".log");
        _log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };

        Say("############ KeySpy 键盘诊断 ############");
        Say("开始时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Say("");

        DumpHidDevices();

        // 创建隐藏窗口用于接收 WM_INPUT
        _proc = WindowProc;
        var wc = new WNDCLASSEX();
        wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc);
        wc.hInstance = GetModuleHandle(null);
        wc.lpszClassName = "KeySpyWndClass";
        if (RegisterClassEx(ref wc) == 0)
            Say("!! RegisterClassEx 失败: " + Marshal.GetLastWin32Error());

        _hwnd = CreateWindowEx(0, "KeySpyWndClass", "KeySpy", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Say("!! CreateWindowEx 失败: " + Marshal.GetLastWin32Error());
            return;
        }

        var rid = new RAWINPUTDEVICE[4];
        rid[0].usUsagePage = HID_USAGE_PAGE_GENERIC;   // 0x01
        rid[0].usUsage = HID_USAGE_GENERIC_KEYBOARD;   // 0x06 普通键盘
        rid[1].usUsagePage = 0x000C;                   // 消费者控制（多媒体键）
        rid[1].usUsage = 0x0001;
        rid[2].usUsagePage = 0xFFB5;                   // 厂商自定义集合 1
        rid[2].usUsage = 0x0001;
        rid[3].usUsagePage = 0xFFBC;                   // 厂商自定义集合 2
        rid[3].usUsage = 0x0088;
        for (int i = 0; i < rid.Length; i++)
        {
            rid[i].dwFlags = RIDEV_INPUTSINK;
            rid[i].hwndTarget = _hwnd;
            if (!RegisterRawInputDevices(new[] { rid[i] }, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                Say(string.Format("!! 注册 UsagePage=0x{0:X4} Usage=0x{1:X4} 失败: {2}",
                    rid[i].usUsagePage, rid[i].usUsage, Marshal.GetLastWin32Error()));
        }

        Say("");
        Say("=== 开始监听键盘原始输入，共 " + seconds + " 秒 ===");
        Say("=== 请现在去按那个自定义键（可多按几次），按完等它自己结束 ===");
        Say("");
        Say("序号  间隔ms   前5键跨度ms  设备                         VKey  Scan  Flags 消息      字符");
        Say("----  -------  -----------  ---------------------------  ----  ----  -----  --------  ----");

        long deadline = seconds * 1000L;
        MSG msg;
        while (Clock.ElapsedMilliseconds < deadline)
        {
            while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1 /*PM_REMOVE*/))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            System.Threading.Thread.Sleep(2);
        }

        Say("");
        Say("=== 监听结束，日志已保存到: " + logPath + " ===");
        _log.Flush();
        _log.Close();
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { HandleRawInput(lParam); } catch (Exception ex) { Say("!! 处理异常: " + ex.Message); }
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void HandleRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, headerSize) != size) return;
            var header = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));

            if (header.dwType == RIM_TYPEHID)
            {
                IntPtr hidPtr = new IntPtr(buf.ToInt64() + Marshal.SizeOf(typeof(RAWINPUTHEADER)));
                var hid = (RAWHID)Marshal.PtrToStructure(hidPtr, typeof(RAWHID));
                int dataOff = Marshal.SizeOf(typeof(RAWHID));
                var sb = new StringBuilder();
                for (uint i = 0; i < hid.dwSizeHid; i++)
                    sb.Append(Marshal.ReadByte(hidPtr, dataOff + (int)i).ToString("X2")).Append(' ');
                Say(string.Format("{0,4}  {1,7:F3}  {2,11}  {3,-27}  HID 报文(len={4})  {5}",
                    ++_index, 0.0, "", ShortDeviceName(header.hDevice), hid.dwSizeHid, sb.ToString().Trim()));
                return;
            }

            if (header.dwType != RIM_TYPEKEYBOARD) return;

            IntPtr kbPtr = new IntPtr(buf.ToInt64() + Marshal.SizeOf(typeof(RAWINPUTHEADER)));
            var kb = (RAWKEYBOARD)Marshal.PtrToStructure(kbPtr, typeof(RAWKEYBOARD));

            LogKey(header.hDevice, kb);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void LogKey(IntPtr hDevice, RAWKEYBOARD kb)
    {
        long now = Clock.ElapsedTicks;
        double nowMs = now * 1000.0 / Stopwatch.Frequency;
        double deltaMs = _lastTick == 0 ? 0 : (now - _lastTick) * 1000.0 / Stopwatch.Frequency;
        _lastTick = now;

        _recent[_recentIdx % _recent.Length] = now;
        _recentIdx++;
        double span5 = 0;
        if (_recentIdx > 5)
        {
            long old = _recent[(_recentIdx - 5) % _recent.Length];
            if (old > 0) span5 = (now - old) * 1000.0 / Stopwatch.Frequency;
        }

        _index++;

        bool isBreak = (kb.Flags & 0x01) != 0;
        bool isE0 = (kb.Flags & 0x02) != 0;

        // 维护修饰键状态，用于还原字符
        int vk = kb.VKey;
        if (vk == 0x10 || vk == 0xA0 || vk == 0xA1) _keyState[0x10] = (byte)(isBreak ? 0 : 0x80);
        else if (vk == 0x11 || vk == 0xA2 || vk == 0xA3) _keyState[0x11] = (byte)(isBreak ? 0 : 0x80);
        else if (vk == 0x12 || vk == 0xA4 || vk == 0xA5) _keyState[0x12] = (byte)(isBreak ? 0 : 0x80);
        else if (vk == 0x14 && !isBreak) _keyState[0x14] ^= 0x01;

        string ch = "";
        if (!isBreak && vk != 0)
        {
            try
            {
                var sb = new StringBuilder(8);
                int n = ToUnicodeEx((uint)vk, kb.MakeCode, _keyState, sb, 8, 0, GetKeyboardLayout(0));
                if (n > 0) ch = sb.ToString(0, n);
                else if (n < 0) ch = "<死键>";
            }
            catch { }
        }

        string msgName;
        switch (kb.Message)
        {
            case 0x100: msgName = "DOWN"; break;
            case 0x101: msgName = "UP"; break;
            case 0x104: msgName = "SYSDOWN"; break;
            case 0x105: msgName = "SYSUP"; break;
            default: msgName = "0x" + kb.Message.ToString("X"); break;
        }

        string dev = ShortDeviceName(hDevice);
        string extra = kb.ExtraInformation != 0 ? "  extra=0x" + kb.ExtraInformation.ToString("X8") : "";

        // 键序重建：把连续的击键拼成字符串，并显示总耗时
        if (!isBreak && vk != 0 && !isE0)
        {
            if (_typed.Length == 0) _typedStartMs = nowMs;
            _typed.Append(ch == "" ? "<" + vk.ToString("X2") + ">" : ch);
            if (ch == "\r" || ch == "\n")
            {
                Say("");
                Say(">>> 重建键入序列: \"" + _typed.ToString().Replace("\r", "\\r").Replace("\n", "\\n") + "\"");
                Say(string.Format(">>> 共 {0} 个字符, 总耗时 {1:F1} ms, 平均 {2:F3} ms/字符",
                    _typed.Length, nowMs - _typedStartMs, (nowMs - _typedStartMs) / Math.Max(1, _typed.Length)));
                Say("");
                _typed.Clear();
            }
            else if (_typed.Length > 400) _typed.Clear();
        }

        Say(string.Format("{0,4}  {1,7:F3}  {2,11:F3}  {3,-27}  0x{4:X2}  0x{5:X2}  0x{6:X2}  {7,-8}  {8}{9}",
            _index, deltaMs, span5, dev, vk, kb.MakeCode, kb.Flags,
            msgName + (isE0 ? "(E0)" : ""),
            ch == "" ? "" : "'" + ch + "'", extra));
    }

    private static string ShortDeviceName(IntPtr hDevice)
    {
        string path;
        if (!_devPaths.TryGetValue(hDevice, out path))
        {
            path = GetDeviceName(hDevice);
            _devPaths[hDevice] = path;
        }
        if (string.IsNullOrEmpty(path)) return "0x" + hDevice.ToInt64().ToString("X");

        // 从 \\?\HID#VID_1EA7&PID_0066&MI_01&COL01#... 里提取可读片段
        string p = path.Replace("\\\\?\\", "").Replace('#', '\\');
        var parts = p.Split('\\');
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.StartsWith("VID_") || part.StartsWith("MI_") || part.StartsWith("COL") || part.StartsWith("HID"))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(part);
            }
        }
        return sb.Length > 0 ? sb.ToString() : path;
    }

    private static string GetDeviceName(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, (StringBuilder)null, ref size);
        if (size == 0) return null;
        var sb = new StringBuilder((int)size + 1);
        if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, sb, ref size) == 0) return null;
        return sb.ToString();
    }

    private static void DumpHidDevices()
    {
        Say("=== 系统 HID 设备清单 ===");
        uint num = 0;
        uint sz = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
        GetRawInputDeviceList(IntPtr.Zero, ref num, sz);
        if (num == 0) { Say("(枚举失败)"); return; }

        IntPtr buf = Marshal.AllocHGlobal((int)(num * sz));
        try
        {
            if (GetRawInputDeviceList(buf, ref num, sz) == unchecked((uint)-1))
            {
                Say("(枚举失败: " + Marshal.GetLastWin32Error() + ")");
                return;
            }
            for (uint i = 0; i < num; i++)
            {
                IntPtr p = new IntPtr(buf.ToInt64() + i * sz);
                var item = (RAWINPUTDEVICELIST)Marshal.PtrToStructure(p, typeof(RAWINPUTDEVICELIST));
                if (item.dwType != RIM_TYPEHID) continue;

                string name = GetDeviceName(item.hDevice);
                _devPaths[item.hDevice] = name;

                string line = "[" + ShortDeviceName(item.hDevice) + "]";
                Say(line);

                if (string.IsNullOrEmpty(name)) { Say("    (无法取得设备路径)"); continue; }

                IntPtr h = CreateFile(name, GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == IntPtr.Zero || h.ToInt64() == -1)
                {
                    Say("    打开失败(错误 " + Marshal.GetLastWin32Error() + ")");
                    continue;
                }
                try
                {
                    var attr = new HIDD_ATTRIBUTES();
                    attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                    if (HidD_GetAttributes(h, ref attr))
                        Say(string.Format("    VID=0x{0:X4}  PID=0x{1:X4}  版本=0x{2:X4}",
                            attr.VendorID, attr.ProductID, attr.VersionNumber));

                    var ps = new StringBuilder(256);
                    if (HidD_GetProductString(h, ps, ps.Capacity * 2))
                        Say("    产品名: " + ps.ToString().Trim());
                    var ms = new StringBuilder(256);
                    if (HidD_GetManufacturerString(h, ms, ms.Capacity * 2))
                        Say("    厂商:   " + ms.ToString().Trim());

                    IntPtr pp;
                    if (HidD_GetPreparsedData(h, out pp))
                    {
                        try
                        {
                            var caps = new HIDP_CAPS();
                            if (HidP_GetCaps(pp, ref caps) >= 0)
                            {
                                Say(string.Format("    UsagePage=0x{0:X4} Usage=0x{1:X4}  {2}",
                                    caps.UsagePage, caps.Usage, UsageName(caps.UsagePage, caps.Usage)));
                                Say(string.Format("    报文长度: 输入={0} 输出={1} 特性={2}",
                                    caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength));
                            }
                        }
                        finally { HidD_FreePreparsedData(pp); }
                    }
                }
                finally { CloseHandle(h); }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        Say("");
    }

    private static string UsageName(ushort page, ushort usage)
    {
        if (page == 0x01)
        {
            switch (usage)
            {
                case 0x02: return "(鼠标)";
                case 0x06: return "(键盘)";
                case 0x07: return "(小键盘)";
                case 0x08: return "(多点触控)";
                case 0x80: return "(系统控制)";
                case 0x0C: return "(消费者控制)";
            }
            return "(通用桌面)";
        }
        if (page == 0xFF00 || (page >= 0xFF00 && page <= 0xFFFF)) return "(厂商自定义 ★)";
        return "";
    }
}
