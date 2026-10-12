// MainForm.cs — 可视化配置界面 + 托盘常驻

using System;
using System.Drawing;
using System.Windows.Forms;

namespace OneKeyTool
{
    internal class MainForm : Form
    {
        /// <summary>当前实例，供动作执行器回报错误。</summary>
        public static MainForm Current;

        private readonly string _configPath;

        private CheckBox _chkEnabled;
        private RadioButton _rbUrl;
        private RadioButton _rbProgram;
        private RadioButton _rbHotkey;
        private RadioButton _rbNone;
        private TextBox _txtValue;
        private Button _btnBrowse;
        private CheckBox _chkSuppressWin;
        private CheckBox _chkDryRun;
        private Label _lblStatus;
        private Label _lblStats;
        private Label _lblHint;
        private System.Windows.Forms.Timer _hintTimer;
        private NotifyIcon _tray;
        private System.Windows.Forms.Timer _pump;
        private Icon _appIcon;   // 窗口图标（标题栏 / Alt-Tab）
        private Icon _trayIcon;  // 托盘图标（小尺寸，单独取原生分辨率）

        private Config _cfg;
        private Detector _detector;

        public MainForm(string configPath, Config cfg, Detector detector)
        {
            _configPath = configPath;
            _cfg = cfg;
            _detector = detector;
            Current = this;
            _appIcon = GetAppIcon(32);
            _trayIcon = GetAppIcon(16);
            BuildUi();
            LoadToUi();
            HookDetector();
        }

        /// <summary>
        /// 取应用图标。优先从 exe 内嵌的图标资源里按需取 <paramref name="size"/> 原生分辨率：
        /// Icon.ExtractAssociatedIcon 只会返回 32x32，托盘要 16x16 时会被系统缩放糊掉。
        /// </summary>
        private static Icon GetAppIcon(int size)
        {
            try
            {
                IntPtr h = Native.LoadImage(Native.GetModuleHandle(null),
                    (IntPtr)Native.IDI_APP_ICON, Native.IMAGE_ICON, size, size, 0);
                if (h != IntPtr.Zero)
                {
                    // Icon.FromHandle 不接管所有权，必须自己 DestroyIcon
                    using (Icon tmp = Icon.FromHandle(h))
                    {
                        Icon copy = (Icon)tmp.Clone();
                        Native.DestroyIcon(h);
                        return copy;
                    }
                }
            }
            catch { }

            try
            {
                string localIco = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (System.IO.File.Exists(localIco))
                {
                    using (Icon tmp = new Icon(localIco, size, size)) return (Icon)tmp.Clone();
                }
            }
            catch { }

            // 共享静态对象，必须返回副本，否则调用方 Dispose 会破坏全局状态
            return (Icon)SystemIcons.Application.Clone();
        }

        // ==================== 界面 ====================

        private void BuildUi()
        {
            Text = "键盘自定义键工具";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(530, 420);
            Font = new Font("Microsoft YaHei UI", 9F);
            Icon = _appIcon;

            int y = 16;

            _chkEnabled = new CheckBox();
            _chkEnabled.Text = "启用拦截（取消勾选则完全不干预键盘）";
            _chkEnabled.Location = new Point(16, y);
            _chkEnabled.AutoSize = true;
            Controls.Add(_chkEnabled);
            y += 34;

            GroupBox grp = new GroupBox();
            grp.Text = "按下自定义键时执行";
            grp.Location = new Point(16, y);
            grp.Size = new Size(488, 200);
            Controls.Add(grp);

            int gy = 24;

            _rbUrl = NewRadio("打开网址", 16, gy, grp);
            gy += 26;
            _rbProgram = NewRadio("运行程序 / 打开文件或文件夹", 16, gy, grp);
            gy += 26;
            _rbHotkey = NewRadio("发送快捷键", 16, gy, grp);
            gy += 26;
            _rbNone = NewRadio("什么都不做（仅屏蔽原网址）", 16, gy, grp);
            gy += 32;

            Label lbl = new Label();
            lbl.Text = "参数：";
            lbl.Location = new Point(16, gy + 3);
            lbl.AutoSize = true;
            grp.Controls.Add(lbl);

            _txtValue = new TextBox();
            _txtValue.Location = new Point(62, gy);
            _txtValue.Size = new Size(320, 24);
            grp.Controls.Add(_txtValue);

            _btnBrowse = new Button();
            _btnBrowse.Text = "浏览...";
            _btnBrowse.Location = new Point(390, gy - 1);
            _btnBrowse.Size = new Size(80, 26);
            _btnBrowse.Click += OnBrowse;
            grp.Controls.Add(_btnBrowse);

            Label hint = new Label();
            hint.Text = "网址例：https://www.bing.com      快捷键例：Ctrl+Alt+S 或 Win+Shift+S";
            hint.Location = new Point(16, gy + 34);
            hint.Size = new Size(460, 20);
            hint.ForeColor = Color.Gray;
            grp.Controls.Add(hint);

            Label hint2 = new Label();
            hint2.Text = "程序例：C:\\Windows\\System32\\notepad.exe";
            hint2.Location = new Point(16, gy + 54);
            hint2.Size = new Size(460, 20);
            hint2.ForeColor = Color.Gray;
            grp.Controls.Add(hint2);

            _chkSuppressWin = new CheckBox();
            _chkSuppressWin.Text = "同时拦截 Win 键（推荐，可避免弹出“运行”对话框）";
            _chkSuppressWin.Location = new Point(16, gy + 84);
            _chkSuppressWin.AutoSize = true;
            grp.Controls.Add(_chkSuppressWin);

            _chkDryRun = new CheckBox();
            _chkDryRun.Text = "观察模式：只识别不拦截（先用它验证识别是否准确）";
            _chkDryRun.Location = new Point(16, gy + 108);
            _chkDryRun.AutoSize = true;
            _chkDryRun.ForeColor = Color.FromArgb(180, 95, 6);
            grp.Controls.Add(_chkDryRun);

            y += 212;

            _lblStatus = new Label();
            _lblStatus.Location = new Point(16, y);
            _lblStatus.Size = new Size(300, 20);
            Controls.Add(_lblStatus);

            _lblStats = new Label();
            _lblStats.Location = new Point(320, y);
            _lblStats.Size = new Size(184, 20);
            _lblStats.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_lblStats);

            _lblHint = new Label();
            _lblHint.Location = new Point(16, y + 22);
            _lblHint.Size = new Size(480, 20);
            _lblHint.Text = "";
            Controls.Add(_lblHint);

            _hintTimer = new System.Windows.Forms.Timer();
            _hintTimer.Interval = 3000;
            _hintTimer.Tick += delegate { _hintTimer.Stop(); _lblHint.Text = ""; };

            y += 52;

            Button btnSave = new Button();
            btnSave.Text = "保存并应用";
            btnSave.Location = new Point(16, y);
            btnSave.Size = new Size(110, 30);
            btnSave.Click += OnSave;
            Controls.Add(btnSave);

            Button btnTest = new Button();
            btnTest.Text = "测试动作";
            btnTest.Location = new Point(136, y);
            btnTest.Size = new Size(90, 30);
            btnTest.Click += OnTest;
            Controls.Add(btnTest);

            Button btnHide = new Button();
            btnHide.Text = "最小化到托盘";
            btnHide.Location = new Point(236, y);
            btnHide.Size = new Size(110, 30);
            btnHide.Click += delegate { Hide(); };
            Controls.Add(btnHide);

            Button btnOpenCfg = new Button();
            btnOpenCfg.Text = "打开配置文件";
            btnOpenCfg.Location = new Point(356, y);
            btnOpenCfg.Size = new Size(110, 30);
            btnOpenCfg.Click += OnOpenConfig;
            Controls.Add(btnOpenCfg);

            BuildTray();

            _pump = new System.Windows.Forms.Timer();
            _pump.Interval = 50;
            _pump.Tick += delegate { _detector.Tick(); UpdateStats(); };
            _pump.Start();

            Resize += OnResize;
        }

        private static RadioButton NewRadio(string text, int x, int y, Control parent)
        {
            RadioButton rb = new RadioButton();
            rb.Text = text;
            rb.Location = new Point(x, y);
            rb.AutoSize = true;
            parent.Controls.Add(rb);
            return rb;
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = _trayIcon;
            _tray.Text = "键盘自定义键工具";
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示设置", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add("启用/停用", null, delegate
            {
                _cfg.Enabled = !_cfg.Enabled;
                _chkEnabled.Checked = _cfg.Enabled;
                _cfg.Save(_configPath);
                UpdateStatus();
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("重新加载配置", null, delegate
            {
                Config fresh = Config.Load(_configPath);
                _detector.UpdateConfig(fresh);
                _cfg = fresh;
                LoadToUi();
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Quit(); });

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
        }

        // ==================== 数据绑定 ====================

        private void LoadToUi()
        {
            _chkEnabled.Checked = _cfg.Enabled;
            _chkSuppressWin.Checked = _cfg.SuppressWindowsKey;
            _chkDryRun.Checked = _cfg.DryRun;
            _txtValue.Text = _cfg.Value;

            switch (_cfg.Kind)
            {
                case ActionKind.None: _rbNone.Checked = true; break;
                case ActionKind.Program: _rbProgram.Checked = true; break;
                case ActionKind.Hotkey: _rbHotkey.Checked = true; break;
                default: _rbUrl.Checked = true; break;
            }
            UpdateStatus();
        }

        private void SaveFromUi()
        {
            _cfg.Enabled = _chkEnabled.Checked;
            _cfg.SuppressWindowsKey = _chkSuppressWin.Checked;
            _cfg.DryRun = _chkDryRun.Checked;
            _cfg.Value = _txtValue.Text.Trim();

            if (_rbNone.Checked) _cfg.Kind = ActionKind.None;
            else if (_rbProgram.Checked) _cfg.Kind = ActionKind.Program;
            else if (_rbHotkey.Checked) _cfg.Kind = ActionKind.Hotkey;
            else _cfg.Kind = ActionKind.Url;

            _cfg.Save(_configPath);
        }

        private void UpdateStatus()
        {
            if (!_cfg.Enabled)
            {
                _lblStatus.Text = "状态：已停用（不干预键盘）";
                _lblStatus.ForeColor = Color.Gray;
            }
            else if (_cfg.DryRun)
            {
                _lblStatus.Text = "状态：观察模式（只识别，不拦截）";
                _lblStatus.ForeColor = Color.FromArgb(180, 95, 6);
            }
            else
            {
                _lblStatus.Text = "状态：拦截已启用";
                _lblStatus.ForeColor = Color.SeaGreen;
            }
        }

        private void UpdateStats()
        {
            _lblStats.Text = string.Format("已触发 {0} 次 / 拦截 {1} 键",
                _detector.TriggerCount, _detector.SuppressedEventCount);
        }

        // ==================== 事件 ====================

        private void HookDetector()
        {
            _detector.Triggered += delegate(bool dryRun)
            {
                // 钩子线程上不能直接操作界面
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        UpdateStats();
                        if (dryRun)
                        {
                            // 观察模式：只提示识别成功，不执行动作（原网址仍会照常打开）
                            _lblHint.Text = "✓ 已识别到自定义键（观察模式未拦截）";
                            _lblHint.ForeColor = Color.SeaGreen;
                            _hintTimer.Stop();
                            _hintTimer.Start();
                        }
                        else
                        {
                            ActionRunner.Run(_cfg);
                        }
                    });
                }
                catch { }
            };
        }

        private void OnSave(object sender, EventArgs e)
        {
            SaveFromUi();
            LoadToUi();
            MessageBox.Show(this, "已保存。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnTest(object sender, EventArgs e)
        {
            SaveFromUi();
            ActionRunner.Run(_cfg);
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "所有文件|*.*";
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _txtValue.Text = dlg.FileName;
                _rbProgram.Checked = true;
            }
        }

        private void OnOpenConfig(object sender, EventArgs e)
        {
            try { System.Diagnostics.Process.Start("notepad.exe", "\"" + _configPath + "\""); }
            catch { }
        }

        private void OnResize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized) Hide();
        }

        public void ReportError(string msg)
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    MessageBox.Show(this, msg, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            }
            catch { }
        }

        private void Quit()
        {
            _pump.Stop();
            _detector.Stop();
            _tray.Visible = false;
            _tray.Icon = null;   // 先解绑，避免释放时托盘仍在引用
            _trayIcon.Dispose();
            _appIcon.Dispose();
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
