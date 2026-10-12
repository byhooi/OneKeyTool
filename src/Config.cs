// Config.cs — 配置读写（简单的 key=value 文本格式，便于用户手改）

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace OneKeyTool
{
    internal enum ActionKind
    {
        None,      // 什么都不做（纯屏蔽）
        Url,       // 打开网址
        Program,   // 运行程序 / 打开文件 / 打开文件夹
        Hotkey     // 发送一组快捷键
    }

    internal class Config
    {
        /// <summary>总开关。关掉后本工具完全不干预键盘。</summary>
        public bool Enabled = true;

        /// <summary>按下自定义键时执行的动作类型。</summary>
        public ActionKind Kind = ActionKind.Url;

        /// <summary>动作参数：网址 / 程序路径(可带参数) / 快捷键字符串。</summary>
        public string Value = "https://www.bing.com";

        /// <summary>是否同时拦截 Win 键。保持 true 可避免弹出“运行”对话框。</summary>
        public bool SuppressWindowsKey = true;

        /// <summary>检测到 Win↓ 后，等待 Win↑ 出现的时长（毫秒）。</summary>
        public int WinWaitMs = 300;

        /// <summary>检测到 Win↑ 后，等待 R↑ 出现的时长（毫秒）。宏里这一跳约 1.2ms，故取小值以免拖慢正常按 Win 键。</summary>
        public int WinUpWaitMs = 80;

        /// <summary>检测到 Win+R 后，等待首个宏字符出现的时长（毫秒）。</summary>
        public int RWaitMs = 250;

        /// <summary>整个宏序列的最长允许时长（毫秒），超时即放弃拦截。</summary>
        public int MacroTimeoutMs = 4000;

        /// <summary>观察模式：只识别、记录、提示，但不拦截任何按键。用于先验证识别是否准确。</summary>
        public bool DryRun = false;

        /// <summary>测试用：是否处理本程序注入的按键。正常运行必须为 false，否则会自我触发。</summary>
        public bool AcceptInjected = false;

        /// <summary>调试用：把钩子收到的每个原始事件写入日志。</summary>
        public bool DebugKeys = false;

        /// <summary>识别宏内容用的特征按键序列（默认 "oneclick" 的虚拟键码）。</summary>
        public ushort[] Signature = new ushort[] { 0x4F, 0x4E, 0x45, 0x43, 0x4C, 0x49, 0x43, 0x4B };

        public static string DefaultPath
        {
            get
            {
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir, "config.ini");
            }
        }

        public static Config Load(string path)
        {
            Config c = new Config();
            if (!File.Exists(path)) return c;

            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch { return c; }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                switch (key.ToLowerInvariant())
                {
                    case "enabled":
                        c.Enabled = ParseBool(val, c.Enabled);
                        break;
                    case "actiontype":
                        c.Kind = ParseKind(val);
                        break;
                    case "actionvalue":
                        c.Value = val;
                        break;
                    case "suppresswindowskey":
                        c.SuppressWindowsKey = ParseBool(val, c.SuppressWindowsKey);
                        break;
                    case "winwaitms":
                        c.WinWaitMs = ParseInt(val, c.WinWaitMs);
                        break;
                    case "winupwaitms":
                        c.WinUpWaitMs = ParseInt(val, c.WinUpWaitMs);
                        break;
                    case "dryrun":
                        c.DryRun = ParseBool(val, c.DryRun);
                        break;
                    case "acceptinjected":
                        c.AcceptInjected = ParseBool(val, c.AcceptInjected);
                        break;
                    case "debugkeys":
                        c.DebugKeys = ParseBool(val, c.DebugKeys);
                        break;
                    case "rwaitms":
                        c.RWaitMs = ParseInt(val, c.RWaitMs);
                        break;
                    case "macrotimems":
                        c.MacroTimeoutMs = ParseInt(val, c.MacroTimeoutMs);
                        break;
                }
            }
            return c;
        }

        public void Save(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# OneKeyTool 配置文件");
            sb.AppendLine("# 修改后需重启本程序，或在托盘菜单里选择“重新加载配置”");
            sb.AppendLine();
            sb.AppendLine("# 总开关：true / false");
            sb.AppendLine("Enabled=" + (Enabled ? "true" : "false"));
            sb.AppendLine();
            sb.AppendLine("# 动作类型：none(纯屏蔽) / url(打开网址) / program(运行程序) / hotkey(发送快捷键)");
            sb.AppendLine("ActionType=" + KindToString(Kind));
            sb.AppendLine();
            sb.AppendLine("# 动作参数，随 ActionType 而定：");
            sb.AppendLine("#   url     例：https://www.bing.com");
            sb.AppendLine("#   program 例：C:\\Windows\\System32\\notepad.exe");
            sb.AppendLine("#   hotkey  例：Ctrl+Alt+S   或  Win+Shift+S");
            sb.AppendLine("ActionValue=" + Value);
            sb.AppendLine();
            sb.AppendLine("# 是否同时拦截 Win 键（true 可避免弹出“运行”对话框）");
            sb.AppendLine("SuppressWindowsKey=" + (SuppressWindowsKey ? "true" : "false"));
            sb.AppendLine();
            sb.AppendLine("# 观察模式：true 时只识别并计数，不拦截任何按键。");
            sb.AppendLine("# 建议首次使用时先设为 true，验证识别是否准确，再改回 false 启用拦截。");
            sb.AppendLine("DryRun=" + (DryRun ? "true" : "false"));
            sb.AppendLine();
            sb.AppendLine("# 测试用：是否处理本程序注入的按键（正常运行必须为 false）");
            sb.AppendLine("AcceptInjected=" + (AcceptInjected ? "true" : "false"));
            sb.AppendLine();
            sb.AppendLine("# 高级时序参数（毫秒），一般无需修改");
            sb.AppendLine("WinWaitMs=" + WinWaitMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("WinUpWaitMs=" + WinUpWaitMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("RWaitMs=" + RWaitMs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("MacroTimeoutMs=" + MacroTimeoutMs.ToString(CultureInfo.InvariantCulture));

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static string KindToString(ActionKind k)
        {
            switch (k)
            {
                case ActionKind.None: return "none";
                case ActionKind.Program: return "program";
                case ActionKind.Hotkey: return "hotkey";
                default: return "url";
            }
        }

        private static ActionKind ParseKind(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "none": return ActionKind.None;
                case "program": return ActionKind.Program;
                case "hotkey": return ActionKind.Hotkey;
                case "url": return ActionKind.Url;
                default: return ActionKind.Url;
            }
        }

        private static bool ParseBool(string s, bool fallback)
        {
            string t = s.Trim().ToLowerInvariant();
            if (t == "true" || t == "1" || t == "yes" || t == "on") return true;
            if (t == "false" || t == "0" || t == "no" || t == "off") return false;
            return fallback;
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0)
                return v;
            return fallback;
        }
    }
}
