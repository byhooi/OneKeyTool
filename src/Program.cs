// Program.cs — 程序入口

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OneKeyTool
{
    internal static class Program
    {
        private static Mutex _single;

        [STAThread]
        private static void Main(string[] args)
        {
            // 开机自启时由 Run 项带 --silent 调用：不显示主窗口，只留托盘图标
            bool silent = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], AutoStart.SilentArg, StringComparison.OrdinalIgnoreCase))
                    silent = true;
            }

            // 只允许运行一个实例
            bool created;
            _single = new Mutex(true, "OneKeyTool.SingleInstance", out created);
            if (!created)
            {
                // 静默启动途中发现已有实例，不该在登录时弹窗打扰
                if (!silent)
                {
                    MessageBox.Show("键盘自定义键工具已经在运行了。\n请在系统托盘区找到它。",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 界面线程的异常走 ThreadException，便于写日志后再退出（默认行为会直接崩掉）
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            string cfgPath = Config.DefaultPath;

            // 首次运行自动生成配置文件
            if (!File.Exists(cfgPath))
            {
                Config def = new Config();
                try { def.Save(cfgPath); }
                catch (Exception ex)
                {
                    MessageBox.Show("无法写入配置文件：\n" + cfgPath + "\n\n" + ex.Message,
                        "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            Config cfg = Config.Load(cfgPath);
            if (!string.IsNullOrEmpty(Config.LastLoadError))
                MessageBox.Show("读取配置失败，当前将使用默认值：\n" + Config.LastLoadError,
                    "配置读取失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            string logPath = Path.Combine(Path.GetDirectoryName(cfgPath), "onekeytool.log");
            InstallCrashHandlers(logPath);

            Detector detector = new Detector(cfg, logPath);
            if (!detector.Start())
            {
                MessageBox.Show("安装键盘钩子失败，程序无法工作。\n请尝试以管理员身份运行。",
                    "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.Run(new MainForm(cfgPath, cfg, detector, silent));
        }

        /// <summary>
        /// 兜住未处理异常并写进日志。本程序持有键盘钩子，崩溃时至少要留下线索；
        /// 界面线程的异常还会弹窗说明"键盘不受影响"，免得用户以为键盘被搞坏了。
        /// </summary>
        private static void InstallCrashHandlers(string logPath)
        {
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                WriteCrashLog(logPath, "界面线程异常", e.Exception);
                MessageBox.Show(
                    "程序遇到未处理的错误，即将退出。\n\n" +
                    "键盘不会受影响（钩子随进程退出自动卸载，不会残留按键状态）。\n" +
                    "详情已写入日志：\n" + logPath,
                    "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
            };

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                WriteCrashLog(logPath, "未处理异常", e.ExceptionObject as Exception);
            };
        }

        private static readonly object CrashGate = new object();

        private static void WriteCrashLog(string logPath, string what, Exception ex)
        {
            try
            {
                lock (CrashGate)
                {
                    File.AppendAllText(logPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + what + ": " +
                        (ex == null ? "(无异常对象)" : ex.ToString()) + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
