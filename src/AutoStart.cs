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
                        // 带引号，避免路径含空格时被截断
                        k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"",
                            RegistryValueKind.String);
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

                    string want = "\"" + Application.ExecutablePath + "\"";
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
