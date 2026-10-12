// ActionRunner.cs — 执行用户设定的动作

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace OneKeyTool
{
    internal static class ActionRunner
    {
        public static void Run(Config cfg)
        {
            switch (cfg.Kind)
            {
                case ActionKind.None:
                    return;

                case ActionKind.Url:
                    OpenUrl(cfg.Value);
                    return;

                case ActionKind.Program:
                    RunProgram(cfg.Value);
                    return;

                case ActionKind.Hotkey:
                    SendHotkey(cfg.Value);
                    return;
            }
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Fail("打开网址失败: " + ex.Message);
            }
        }

        private static void RunProgram(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return;
            try
            {
                string file = spec.Trim();
                string args = "";

                // 支持 "C:\路径\程序.exe" 参数  或  C:\路径\程序.exe 参数
                if (file.StartsWith("\""))
                {
                    int end = file.IndexOf('"', 1);
                    if (end > 0)
                    {
                        args = file.Substring(end + 1).Trim();
                        file = file.Substring(1, end - 1);
                    }
                }
                else
                {
                    // 先检查整串路径是否存在（含空格的路径也能识别）
                    // 若不存在则尝试按第一个空格拆分，验证候选路径的合理性
                    if (!File.Exists(file) && !Directory.Exists(file))
                    {
                        int sp = file.IndexOf(' ');
                        if (sp > 0)
                        {
                            string candidate = file.Substring(0, sp);
                            // 候选路径存在，或以 .exe 结尾（允许 PATH 中的命令），则视为可执行文件
                            if (File.Exists(candidate) ||
                                candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                candidate.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                                candidate.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                            {
                                args = file.Substring(sp + 1).Trim();
                                file = candidate;
                            }
                        }
                    }
                }

                ProcessStartInfo psi = new ProcessStartInfo(file);
                psi.UseShellExecute = true;
                if (args.Length > 0) psi.Arguments = args;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Fail("运行程序失败: " + ex.Message);
            }
        }

        private static void SendHotkey(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return;
            try
            {
                ushort[] mods;
                ushort key;
                bool keyExt;
                if (!ParseHotkey(spec, out mods, out key, out keyExt))
                {
                    Fail("无法解析快捷键: " + spec);
                    return;
                }
                InputSender.Chord(mods, key, keyExt);
            }
            catch (Exception ex)
            {
                Fail("发送快捷键失败: " + ex.Message);
            }
        }

        // ==================== 快捷键解析 ====================

        private static readonly Dictionary<string, ushort> Named = BuildNamed();

        private static Dictionary<string, ushort> BuildNamed()
        {
            Dictionary<string, ushort> d = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            d["CTRL"] = 0x11;
            d["CONTROL"] = 0x11;
            d["SHIFT"] = 0x10;
            d["ALT"] = 0x12;
            d["WIN"] = 0x5B;
            d["WINDOWS"] = 0x5B;
            d["ENTER"] = 0x0D;
            d["RETURN"] = 0x0D;
            d["ESC"] = 0x1B;
            d["ESCAPE"] = 0x1B;
            d["TAB"] = 0x09;
            d["SPACE"] = 0x20;
            d["BACKSPACE"] = 0x08;
            d["DELETE"] = 0x2E;
            d["DEL"] = 0x2E;
            d["INSERT"] = 0x2D;
            d["HOME"] = 0x24;
            d["END"] = 0x23;
            d["PAGEUP"] = 0x21;
            d["PAGEDOWN"] = 0x22;
            d["UP"] = 0x26;
            d["DOWN"] = 0x28;
            d["LEFT"] = 0x25;
            d["RIGHT"] = 0x27;
            d["PRINTSCREEN"] = 0x2C;
            d["PRTSC"] = 0x2C;
            for (int i = 1; i <= 24; i++) d["F" + i.ToString(CultureInfo.InvariantCulture)] = (ushort)(0x70 + i - 1);
            for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = (ushort)c;
            for (char c = '0'; c <= '9'; c++) d[c.ToString()] = (ushort)c;
            return d;
        }

        /// <summary>解析形如 "Ctrl+Alt+S" 的字符串。最后一段是主键，其余是修饰键。</summary>
        public static bool ParseHotkey(string spec, out ushort[] mods, out ushort key, out bool keyExt)
        {
            mods = new ushort[0];
            key = 0;
            keyExt = false;

            string[] parts = spec.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            List<ushort> modList = new List<ushort>();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                ushort v;
                if (!Named.TryGetValue(parts[i].Trim(), out v)) return false;
                modList.Add(v);
            }

            ushort mainVk;
            if (!Named.TryGetValue(parts[parts.Length - 1].Trim(), out mainVk)) return false;

            mods = modList.ToArray();
            key = mainVk;
            keyExt = InputSender.IsExtendedVk(mainVk);
            return true;
        }

        private static void Fail(string msg)
        {
            // 钩子线程上不要弹窗，交给主界面显示
            if (MainForm.Current != null) MainForm.Current.ReportError(msg);
        }
    }
}
