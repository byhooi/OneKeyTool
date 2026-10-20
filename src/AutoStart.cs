// AutoStart.cs — 开机自启（写入当前用户的 Run 项，无需管理员权限）

using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OneKeyTool
{
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "OneKeyTool";

        /// <summary>自启动时传给程序的参数，让主窗口不显示、只留托盘图标。</summary>
        public const string SilentArg = "--silent";

        /// <summary>注册表里应写入的完整命令行（路径带引号，参数放在引号外）。</summary>
        private static string BuildCommand()
        {
            return "\"" + Application.ExecutablePath + "\" " + SilentArg;
        }

        /// <summary>查询注册表里是否已设置自启（以注册表实际状态为准）。</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(ValueName);
                    return v != null && v.ToString().Trim().Length > 0;
                }
            }
            catch { return false; }
        }

        /// <summary>写入或移除自启项。返回是否操作成功。</summary>
        public static bool SetEnabled(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return false;

                    if (on)
                    {
                        k.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
                    }
                    else if (k.GetValue(ValueName) != null)
                    {
                        k.DeleteValue(ValueName, false);
                    }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 若已启用自启但记录的路径与当前 exe 不符（程序被移动或改名），
        /// 则更新为新路径。在启动时调用，避免自启项指向失效路径。
        /// </summary>
        public static void RefreshPath()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    object v = k.GetValue(ValueName);
                    if (v == null) return;

                    // 旧版写入的条目没有 --silent，这里一并升级为静默启动
                    string want = BuildCommand();
                    if (!string.Equals(v.ToString().Trim(), want, StringComparison.OrdinalIgnoreCase))
                    {
                        k.SetValue(ValueName, want, RegistryValueKind.String);
                    }
                }
            }
            catch { }
        }
    }
}
