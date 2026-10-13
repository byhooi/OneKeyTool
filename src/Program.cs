// Program.cs — 程序入口

using System;
using System.IO;
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
            // 只允许运行一个实例
            bool created;
            _single = new Mutex(true, "OneKeyTool.SingleInstance", out created);
            if (!created)
            {
                MessageBox.Show("键盘自定义键工具已经在运行了。\n请在系统托盘区找到它。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

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

            Detector detector = new Detector(cfg, logPath);
            if (!detector.Start())
            {
                MessageBox.Show("安装键盘钩子失败，程序无法工作。\n请尝试以管理员身份运行。",
                    "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.Run(new MainForm(cfgPath, cfg, detector));
        }
    }
}
