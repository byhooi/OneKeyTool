// MainForm.cs — 可视化配置界面 + 托盘常驻

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
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
        private CheckBox _chkAutoStart;
        private Label _lblStatus;
        private Label _lblStats;
        private Label _lblHint;
        private System.Windows.Forms.Timer _hintTimer;
        private NotifyIcon _tray;
        private System.Windows.Forms.Timer _pump;
        private Icon _appIcon;   // 窗口图标（标题栏 / Alt-Tab）
        private Icon _trayIcon;  // 托盘图标（小尺寸，单独取原生分辨率）

        // 版本与更新
        private Label _lblVersion;
        private LinkLabel _lnkUpdate;
        private ReleaseInfo _pendingRelease;   // 已发现的新版本
        private bool _checking;                // 防止重复点击

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
            AutoStart.RefreshPath();   // 程序若被移动，修正自启项里的旧路径
            BuildUi();
            LoadToUi();
            HookDetector();
            StartAutoUpdateCheck();
        }

        /// <summary>
        /// 启动后延迟自动检查一次更新。检查在后台线程进行，失败静默——
        /// 联网失败不该打扰用户，也不该影响主功能。
        /// </summary>
        private void StartAutoUpdateCheck()
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 5000;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                CheckForUpdates(false);
            };
            t.Start();
        }

        /// <summary>
        /// 取应用图标，从 exe 内嵌的图标资源里按需取 <paramref name="size"/> 原生分辨率。
        /// Icon.ExtractAssociatedIcon 只会返回 32x32，托盘要 16x16 时会被系统缩放糊掉。
        /// 构建时 app.ico 已嵌入 exe，故此处总能成功；万一失败则退回系统默认图标。
        /// </summary>
        private static Icon GetAppIcon(int size)
        {
            try
            {
                IntPtr h = Native.LoadImage(Native.GetModuleHandle(null),
                    (IntPtr)Native.IDI_APP_ICON, Native.IMAGE_ICON, size, size, 0);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        using (Icon tmp = Icon.FromHandle(h))
                        {
                            return (Icon)tmp.Clone();
                        }
                    }
                    finally
                    {
                        Native.DestroyIcon(h);   // FromHandle 不接管所有权，必须显式销毁
                    }
                }
            }
            catch { }

            // 共享静态对象，必须返回副本，否则调用方 Dispose 会破坏全局状态
            return (Icon)SystemIcons.Application.Clone();
        }

        // ==================== 界面 ====================

        private void BuildUi()
        {
            Text = BuildInfo.DisplayName;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            Icon = _appIcon;

            int y = 16;

            _chkEnabled = new CheckBox();
            _chkEnabled.Text = "启用拦截（取消勾选则完全不干预键盘）";
            _chkEnabled.Location = new Point(16, y);
            _chkEnabled.AutoSize = true;
            Controls.Add(_chkEnabled);

            // 版本号放右上角，与"启用拦截"同一行。反馈问题时用户可直接报出，
            // 与 zip 文件名、exe 文件属性一致。
            // 用 LinkLabel 而非 Label：平时是灰色静态文本，发现新版本时变成可点的链接，
            // 这样不额外占用布局空间。
            _lblVersion = new Label();
            _lblVersion.Text = BuildInfo.Version;
            _lblVersion.Size = new Size(120, 20);
            _lblVersion.Location = new Point(530 - 16 - 120, y);
            _lblVersion.TextAlign = ContentAlignment.MiddleRight;
            _lblVersion.ForeColor = Color.Gray;
            Controls.Add(_lblVersion);

            _lnkUpdate = new LinkLabel();
            _lnkUpdate.Text = BuildInfo.Version;
            _lnkUpdate.Size = new Size(120, 20);
            _lnkUpdate.Location = new Point(530 - 16 - 120, y);
            _lnkUpdate.TextAlign = ContentAlignment.MiddleRight;
            _lnkUpdate.LinkBehavior = LinkBehavior.HoverUnderline;
            _lnkUpdate.LinkColor = Color.FromArgb(0, 102, 204);
            _lnkUpdate.Visible = false;
            _lnkUpdate.Click += OnUpdateLinkClick;
            Controls.Add(_lnkUpdate);

            y += 34;

            GroupBox grp = new GroupBox();
            grp.Text = "按下自定义键时执行";
            grp.Location = new Point(16, y);
            // 内容最低到 202（两行提示的底部），加上标题栏与底边距故取 222
            grp.Size = new Size(488, 222);
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

            // 两行提示要完整落在 GroupBox 内，否则底边框会切掉半行
            Label hint = new Label();
            hint.Text = "网址例：https://www.bing.com      快捷键例：Ctrl+Alt+S 或 Win+Shift+S";
            hint.Location = new Point(16, gy + 32);
            hint.Size = new Size(460, 18);
            hint.ForeColor = Color.Gray;
            grp.Controls.Add(hint);

            Label hint2 = new Label();
            hint2.Text = "程序例：C:\\Windows\\System32\\notepad.exe";
            hint2.Location = new Point(16, gy + 50);
            hint2.Size = new Size(460, 18);
            hint2.ForeColor = Color.Gray;
            grp.Controls.Add(hint2);

            y += grp.Height + 14;

            // 拦截范围与启动行为放在 GroupBox 之外：
            // 原先它们在 GroupBox 内 y=230/254，而 GroupBox 只有 200 高，被整个裁掉不可见。
            _chkSuppressWin = new CheckBox();
            _chkSuppressWin.Text = "同时拦截 Win 键（推荐，可避免弹出“运行”对话框）";
            _chkSuppressWin.Location = new Point(16, y);
            _chkSuppressWin.AutoSize = true;
            Controls.Add(_chkSuppressWin);
            y += 26;

            _chkDryRun = new CheckBox();
            _chkDryRun.Text = "观察模式：只识别不拦截（先用它验证识别是否准确）";
            _chkDryRun.Location = new Point(16, y);
            _chkDryRun.AutoSize = true;
            _chkDryRun.ForeColor = Color.FromArgb(180, 95, 6);
            Controls.Add(_chkDryRun);
            y += 26;

            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "开机自动启动（登录后静默运行，只在托盘显示图标）";
            _chkAutoStart.Location = new Point(16, y);
            _chkAutoStart.AutoSize = true;
            Controls.Add(_chkAutoStart);
            y += 30;

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
            _lblHint.Size = new Size(498, 20);   // 撑满到右内边距，避免长提示被截断
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

            // 高度按内容算，避免以后加控件又被裁掉
            ClientSize = new Size(530, y + 30 + 16);

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
            // NotifyIcon.Text 上限 63 字符，超出会抛异常；版本号很短，这里安全
            _tray.Text = BuildInfo.DisplayName;
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem miShow = new ToolStripMenuItem("显示设置", null,
                delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });

            ToolStripMenuItem miEnabled = new ToolStripMenuItem("启用拦截");
            miEnabled.CheckOnClick = true;
            miEnabled.Click += delegate
            {
                _cfg.Enabled = miEnabled.Checked;
                _chkEnabled.Checked = _cfg.Enabled;
                _cfg.Save(_configPath);
                UpdateStatus();
            };

            ToolStripMenuItem miDryRun = new ToolStripMenuItem("观察模式（只识别不拦截）");
            miDryRun.CheckOnClick = true;
            miDryRun.Click += delegate
            {
                _cfg.DryRun = miDryRun.Checked;
                _chkDryRun.Checked = _cfg.DryRun;
                _cfg.Save(_configPath);
                UpdateStatus();
            };

            ToolStripMenuItem miAutoStart = new ToolStripMenuItem("开机自动启动");
            miAutoStart.CheckOnClick = true;
            miAutoStart.Click += delegate
            {
                AutoStart.SetEnabled(miAutoStart.Checked);
                // 以注册表实际结果为准，写入失败时勾选会回弹
                miAutoStart.Checked = AutoStart.IsEnabled();
                _chkAutoStart.Checked = miAutoStart.Checked;
            };

            ToolStripMenuItem miReload = new ToolStripMenuItem("重新加载配置", null, delegate
            {
                Config fresh = Config.Load(_configPath);
                _detector.UpdateConfig(fresh);
                _cfg = fresh;
                LoadToUi();
            });

            ToolStripMenuItem miCheckUpdate = new ToolStripMenuItem("检查更新", null,
                delegate { CheckForUpdates(true); });

            ToolStripMenuItem miQuit = new ToolStripMenuItem("退出", null, delegate { Quit(); });

            menu.Items.Add(miShow);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miEnabled);
            menu.Items.Add(miDryRun);
            menu.Items.Add(miAutoStart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miReload);
            menu.Items.Add(miCheckUpdate);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miQuit);

            // 弹出前同步勾选状态：窗口里改过、或在任务管理器里禁用过自启，
            // 都能反映到菜单上（勾选状态一律以实际状态为准）
            menu.Opening += delegate
            {
                miEnabled.Checked = _cfg.Enabled;
                miDryRun.Checked = _cfg.DryRun;
                miAutoStart.Checked = AutoStart.IsEnabled();
            };

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
        }

        // ==================== 数据绑定 ====================

        private void LoadToUi()
        {
            _chkEnabled.Checked = _cfg.Enabled;
            _chkSuppressWin.Checked = _cfg.SuppressWindowsKey;
            _chkDryRun.Checked = _cfg.DryRun;
            // 自启状态以注册表实际值为准，不存配置文件
            _chkAutoStart.Checked = AutoStart.IsEnabled();
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

        /// <summary>把界面设置写回配置。返回开机自启是否写入成功。</summary>
        private bool SaveFromUi()
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
            return AutoStart.SetEnabled(_chkAutoStart.Checked);
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

        // ==================== 检查更新 ====================

        /// <summary>
        /// 检查更新。interactive 为 true 表示用户主动触发，此时无论结果如何都给反馈；
        /// 为 false 表示启动时的自动检查，只有发现新版本才提示。
        /// </summary>
        private void CheckForUpdates(bool interactive)
        {
            if (_checking) return;
            _checking = true;
            if (interactive) ShowHint("正在检查更新…", Color.Gray);

            ThreadPool.QueueUserWorkItem(delegate
            {
                ReleaseInfo found = null;
                string error = null;
                try
                {
                    found = Updater.Check();
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _checking = false;
                        OnCheckFinished(found, error, interactive);
                    });
                }
                catch { _checking = false; }
            });
        }

        private void OnCheckFinished(ReleaseInfo found, string error, bool interactive)
        {
            if (error != null)
            {
                if (interactive) ShowHint("检查更新失败：" + error, Color.Firebrick);
                return;
            }

            if (found == null)
            {
                if (interactive) ShowHint("已是最新版本（" + BuildInfo.Version + "）", Color.SeaGreen);
                return;
            }

            _pendingRelease = found;

            // 版本号变成可点的链接
            _lblVersion.Visible = false;
            _lnkUpdate.Text = "↑ " + found.Tag;
            _lnkUpdate.Visible = true;

            string tip = "发现新版本 " + found.Tag + "（当前 " + BuildInfo.Version + "）";
            if (!found.CanAutoUpdate) tip += "，该版本需手动下载";
            ShowHint(tip + " —— 点右上角版本号", Color.FromArgb(0, 102, 204));

            if (interactive) _lnkUpdate.Focus();
        }

        /// <summary>点击右上角版本号：能自动更新就下载替换，否则打开 Release 页。</summary>
        private void OnUpdateLinkClick(object sender, EventArgs e)
        {
            ReleaseInfo info = _pendingRelease;
            if (info == null) return;

            if (!info.CanAutoUpdate)
            {
                OpenReleasePage(info);
                return;
            }

            string choice = "发现新版本 " + info.Tag + "\n当前版本 " + BuildInfo.Version +
                "\n\n将从 GitHub 下载新版本并校验，然后重启程序。是否继续？";
            if (MessageBox.Show(this, choice, "更新 OneKeyTool",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            _lnkUpdate.Enabled = false;
            ShowHint("正在下载 " + info.Tag + " …", Color.Gray);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string newExe = null;
                string error = null;
                try
                {
                    newExe = Updater.Download(info, delegate(int percent)
                    {
                        // 进度回调在后台线程，更新界面要切回 UI 线程
                        try
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                ShowHint("正在下载 " + info.Tag + " … " + percent + "%", Color.Gray);
                            });
                        }
                        catch { }
                    });
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (error != null)
                        {
                            _lnkUpdate.Enabled = true;
                            ShowHint("更新失败：" + error, Color.Firebrick);
                            return;
                        }
                        ApplyUpdate(newExe);
                    });
                }
                catch { }
            });
        }

        /// <summary>下载并校验完成，执行替换与重启。</summary>
        private void ApplyUpdate(string newExePath)
        {
            if (MessageBox.Show(this,
                    "下载完成，校验通过。\n\n程序将退出并重启为新版本，是否现在重启？\n" +
                    "（选择\"取消\"则下次手动启动时生效，本次不重启）",
                    "更新就绪", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            {
                ShowHint("新版本已就位，下次启动生效", Color.SeaGreen);
                _lnkUpdate.Enabled = true;
                return;
            }

            ShowHint("正在应用更新…", Color.Gray);
            try
            {
                Updater.Apply(newExePath);
            }
            catch (Exception ex)
            {
                _lnkUpdate.Enabled = true;
                ShowHint("更新失败：" + ex.Message, Color.Firebrick);
                MessageBox.Show(this, ex.Message, "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 新实例已启动，本实例退出（不弹确认框）
            Quit();
        }

        private void OpenReleasePage(ReleaseInfo info)
        {
            string url = !string.IsNullOrEmpty(info.HtmlUrl) ? info.HtmlUrl : Updater.ReleasesPage;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开下载页面失败：" + ex.Message,
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ShowHint(string text, Color color)
        {
            _lblHint.Text = text;
            _lblHint.ForeColor = color;
            _hintTimer.Stop();
            _hintTimer.Start();
        }

        private void OnSave(object sender, EventArgs e)
        {
            bool autoOk = SaveFromUi();
            LoadToUi();   // 自启失败时复选框会回弹为注册表实际状态

            string msg = "已保存。";
            if (!autoOk)
            {
                msg += "\n\n但开机自启设置未能写入注册表，请检查权限后重试。";
                MessageBox.Show(this, msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show(this, msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
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
