using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;

namespace PcAudioServer
{
    /// <summary>
    /// 桌面端界面。引擎在 AudioServer 里，这里只做状态显示和参数设置。
    /// 布局全部用容器（TableLayoutPanel / FlowLayoutPanel）而不是绝对坐标 ——
    /// 绝对坐标在 150% 缩放的屏幕上会被等比放大然后溢出、把按钮切掉。
    /// </summary>
    public sealed class MainForm : Form
    {
        readonly AudioServer server = new AudioServer();
        readonly PhoneLink phone = new PhoneLink();
        System.Windows.Forms.Timer phoneTimer;
        Label phoneLabel;
        Button reconnectBtn;

        TextBox logBox;
        Label stateLabel, clientLabel, formatLabel, ipLabel;
        NumericUpDown portBox;
        RadioButton rbOpus, rbPcm;
        ComboBox bitrateBox, pcmRateBox;
        Button startBtn, stopBtn;
        bool syncing;   // 正在用引擎实际值回填界面（此时不要触发"改了要重启"）

        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        bool exitRequested;      // 真正要退出（而不是缩到托盘）
        bool trayHintShown;

        public MainForm(int port, string codec, int rate, int channels, int bitrate, bool startMinimized)
        {
            Text = "电脑声音 → 手机";
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(720, 480);
            ClientSize = new Size(880, 620);
            Font = new Font("微软雅黑", 9.5f);
            BackColor = Color.FromArgb(0x12, 0x14, 0x1A);
            ForeColor = Color.FromArgb(0xE8, 0xEA, 0xED);

            BuildUi();
            SetupTray();

            server.Log += OnServerLog;
            server.StateChanged += OnServerState;
            phone.Log += OnServerLog;
            phone.Changed += OnServerState;

            portBox.Value = (port < 1 || port > 65535) ? AudioServer.DEFAULT_PORT : port;
            if (codec == "pcm") rbPcm.Checked = true; else rbOpus.Checked = true;
            SelectCombo(bitrateBox, bitrate.ToString());
            SelectCombo(pcmRateBox, rate.ToString());
            SyncEnabled();

            Load += (s, e) =>
            {
                Append("本机地址 " + LocalIp());
                Append("");
                StartServer();
                StartPhoneLink();
            };

            // 必须在 Shown 里收，不能放 Load：Load 比窗体真正显示更早，
            // 那时候调 Hide() 会被随后的显示动作覆盖掉，窗口照样弹出来。
            Shown += (s, e) => { if (startMinimized) HideToTray(); };

            FormClosing += (s, e) =>
            {
                // 点右上角的 X 只是缩到托盘继续后台运行；要真退出请用托盘菜单里的"退出"
                if (!exitRequested && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray();
                    return;
                }
                try { server.Stop(); } catch { }
                try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
            };
        }

        // ---------------- 手机侧一键连接（以前是 启动.bat 的活，现在内置并且常驻） ----------------

        bool phoneBusy;

        void StartPhoneLink()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { phone.Init(AppDomain.CurrentDomain.BaseDirectory); } catch { }
                TickPhone();
            });
            phoneTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            phoneTimer.Tick += (s, e) => TickPhone();
            phoneTimer.Start();
        }

        // adb 是同步阻塞的，绝不能跑在 UI 线程上，否则界面直接卡死
        void TickPhone()
        {
            if (phoneBusy) return;
            phoneBusy = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { phone.Tick(server.Port, FindApk()); }
                catch { }
                finally { phoneBusy = false; }
            });
        }

        static string FindApk()
        {
            string b = AppDomain.CurrentDomain.BaseDirectory;
            string[] cands =
            {
                Path.Combine(b, "PcAudio.apk"),
                Path.Combine(b, "..", "PcAudio.apk"),
                Path.Combine(b, "..", "..", "PcAudio.apk")
            };
            foreach (var c in cands)
            {
                try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
            }
            return null;
        }

        // ---------------- 托盘：后台运行 ----------------

        void SetupTray()
        {
            trayMenu = new ContextMenuStrip();
            var miShow = new ToolStripMenuItem("显示主界面");
            miShow.Click += (s, e) => ShowWindow();
            var miStart = new ToolStripMenuItem("启动服务");
            miStart.Click += (s, e) => { StartServer(); ShowWindow(); };
            var miStop = new ToolStripMenuItem("停止服务");
            miStop.Click += (s, e) => { server.Stop(); SyncEnabled(); };
            var miExit = new ToolStripMenuItem("退出");
            miExit.Click += (s, e) => { exitRequested = true; Close(); };
            trayMenu.Items.Add(miShow);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miStart);
            trayMenu.Items.Add(miStop);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miExit);

            tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "电脑声音 → 手机",
                Visible = true,
                ContextMenuStrip = trayMenu
            };
            tray.DoubleClick += (s, e) => ShowWindow();
        }

        void ShowWindow()
        {
            try
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
            }
            catch { }
        }

        void HideToTray()
        {
            Hide();
            if (!trayHintShown)
            {
                trayHintShown = true;
                try
                {
                    tray.ShowBalloonTip(2500, "电脑声音",
                        "已缩到托盘后台运行，双击图标可以重新打开。", ToolTipIcon.Info);
                }
                catch { }
            }
        }

        // ---------------- 界面 ----------------

        void BuildUi()
        {
            SuspendLayout();

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.FromArgb(0x12, 0x14, 0x1A)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // ---- 上半：标题 + 状态 + 设置 + 按钮 ----
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(16, 12, 16, 6),
                BackColor = Color.FromArgb(0x12, 0x14, 0x1A)
            };

            top.Controls.Add(new Label
            {
                Text = "电脑声音 → 手机",
                Font = new Font("微软雅黑", 16f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8)
            });

            stateLabel = Mk(top, "", Color.FromArgb(0x8A, 0xB4, 0xF8), 12.5f, true);
            clientLabel = Mk(top, "", Color.FromArgb(0x81, 0xC9, 0x95), 11f, true);
            phoneLabel = Mk(top, "", Color.FromArgb(0xFD, 0xD6, 0x63), 10.5f, true);
            formatLabel = Mk(top, "", Color.FromArgb(0x9A, 0xA0, 0xA6), 10f, true);
            ipLabel = Mk(top, "", Color.FromArgb(0x5F, 0x63, 0x68), 9.5f, true);

            // ---- 设置区（表格布局，列宽自适应）----
            var grid = new TableLayoutPanel
            {
                ColumnCount = 4,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 10, 0, 0),
                BackColor = Color.Transparent
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.RowCount = 2;

            // 第一行：端口 / 编码
            grid.Controls.Add(MkCell("端口"), 0, 0);
            portBox = new NumericUpDown
            {
                Minimum = 1, Maximum = 65535, Width = 90,
                BackColor = Color.FromArgb(0x20, 0x23, 0x2B),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(4, 2, 18, 2)
            };
            grid.Controls.Add(portBox, 1, 0);
            grid.Controls.Add(MkCell("编码"), 2, 0);

            var codecRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(4, 0, 0, 0), WrapContents = false };
            rbOpus = new RadioButton { Text = "Opus 压缩", AutoSize = true, ForeColor = Color.FromArgb(0xE8, 0xEA, 0xED), Margin = new Padding(0, 4, 14, 0) };
            rbPcm = new RadioButton { Text = "无损 PCM", AutoSize = true, ForeColor = Color.FromArgb(0xE8, 0xEA, 0xED), Margin = new Padding(0, 4, 0, 0) };
            rbOpus.CheckedChanged += (s, e) => { SyncEnabled(); RestartIfRunning(); };            codecRow.Controls.Add(rbOpus);
            codecRow.Controls.Add(rbPcm);
            grid.Controls.Add(codecRow, 3, 0);

            // 第二行：码率 / 无损采样率
            grid.Controls.Add(MkCell("Opus 码率"), 0, 1);
            bitrateBox = new ComboBox
            {
                Width = 100, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(0x20, 0x23, 0x2B), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 2, 18, 2)
            };
            foreach (var k in new[] { "16", "32", "64", "96", "128", "192", "256", "320" }) bitrateBox.Items.Add(k);
            bitrateBox.SelectedIndexChanged += (s, e) => RestartIfRunning();
            grid.Controls.Add(bitrateBox, 1, 1);
            grid.Controls.Add(MkCell("无损采样率"), 2, 1);
            pcmRateBox = new ComboBox
            {
                Width = 190, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(0x20, 0x23, 0x2B), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 2, 0, 2)
            };
            pcmRateBox.Items.Add("48000  原生不重采样");
            pcmRateBox.Items.Add("44100  对齐蓝牙链路");
            pcmRateBox.SelectedIndexChanged += (s, e) => RestartIfRunning();
            grid.Controls.Add(pcmRateBox, 3, 1);

            top.Controls.Add(grid);

            // ---- 按钮区 ----
            var btns = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 12, 0, 4),
                WrapContents = false
            };
            startBtn = MkBtn("启动服务", Color.FromArgb(0x2A, 0x4A, 0x7A));
            startBtn.Click += (s, e) => StartServer();
            stopBtn = MkBtn("停止服务", Color.FromArgb(0x3A, 0x2A, 0x2A));
            stopBtn.Click += (s, e) => { server.Stop(); SyncEnabled(); };
            var clearBtn = MkBtn("清空日志", Color.FromArgb(0x22, 0x25, 0x2C));
            clearBtn.Click += (s, e) => { lock (pending) pending.Length = 0; logBox.Clear(); };
            reconnectBtn = MkBtn("重新连接手机", Color.FromArgb(0x2A, 0x3A, 0x4A));
            reconnectBtn.Click += (s, e) =>
            {
                Append("[手机] 手动重新连接 ...");
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { phone.ForceReconnect(); } catch { }
                    TickPhone();
                });
            };
            btns.Controls.Add(reconnectBtn);
            btns.Controls.Add(startBtn);
            btns.Controls.Add(stopBtn);
            btns.Controls.Add(clearBtn);
            top.Controls.Add(btns);

            root.Controls.Add(top, 0, 0);

            // ---- 下半：日志 ----
            logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                BackColor = Color.FromArgb(0x0D, 0x0F, 0x14),
                ForeColor = Color.FromArgb(0xC8, 0xCE, 0xD6),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
                WordWrap = false,
                Margin = new Padding(0)
            };
            var logWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 14), BackColor = Color.FromArgb(0x12, 0x14, 0x1A) };
            logWrap.Controls.Add(logBox);
            root.Controls.Add(logWrap, 0, 1);

            Controls.Add(root);
            ResumeLayout(true);
        }

        static Label Mk(Control parent, string text, Color c, float size, bool owning)
        {
            var l = new Label { Text = text, AutoSize = true, ForeColor = c, Font = new Font("微软雅黑", size), Margin = new Padding(0, 0, 0, 4) };
            parent.Controls.Add(l);
            return l;
        }

        static Label MkCell(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = Color.FromArgb(0xE8, 0xEA, 0xED),
                Font = new Font("微软雅黑", 9.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 6, 2)
            };
        }

        static Button MkBtn(string text, Color back)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(104, 32),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = back,
                Margin = new Padding(0, 0, 10, 0)
            };
        }

        static void SelectCombo(ComboBox cb, string value)
        {
            for (int i = 0; i < cb.Items.Count; i++)
            {
                string s = cb.Items[i].ToString();
                if (s == value || s.StartsWith(value + " ")) { cb.SelectedIndex = i; return; }
            }
            if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        }

        void SyncEnabled()
        {
            bool pcm = rbPcm.Checked;
            bitrateBox.Enabled = !pcm;
            pcmRateBox.Enabled = pcm;
            bool run = server.IsRunning;
            startBtn.Enabled = !run;
            stopBtn.Enabled = run;
            portBox.Enabled = !run;
            if (!run)
            {
                stateLabel.Text = "服务已停止";
                clientLabel.Text = "";
                formatLabel.Text = "";
            }
        }

        int SelectedPcmRate()
        {
            string s = pcmRateBox.SelectedItem == null ? "48000" : pcmRateBox.SelectedItem.ToString();
            return s.StartsWith("44100") ? 44100 : 48000;
        }

        int SelectedBitrate()
        {
            int v;
            if (bitrateBox.SelectedItem == null) return 128;
            return int.TryParse(bitrateBox.SelectedItem.ToString(), out v) ? v : 128;
        }

        string LocalIp()
        {
            try
            {
                foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        return ip.ToString();
            }
            catch { }
            return "127.0.0.1";
        }

        void RestartIfRunning()
        {
            // 正在用引擎的实际值回填界面时不触发重启，否则会自己把自己点着
            if (syncing) return;
            if (!server.IsRunning) { SyncEnabled(); return; }
            // 桌面端一改参数就以桌面端为准：清掉手机端留下的临时覆盖，
            // 否则手机上改过的编码/码率会一直压着，桌面端改了半天不生效。
            server.ClearRemotePrefs();
            Append("[界面] 参数已改，重启服务生效 ...");
            server.Stop();
            System.Threading.Thread.Sleep(250);
            StartServer();
        }

        /// <summary>把引擎当前**实际生效**的设置回填到界面上（手机端改过也要跟着变）。</summary>
        void SyncFromServer()
        {
            syncing = true;
            try
            {
                int pc = server.PrefCodec;                 // -1 = 没被手机改过
                if (pc == 0) rbPcm.Checked = true;
                else if (pc == 1) rbOpus.Checked = true;

                int pb = server.PrefBitrateKbps;
                if (pb > 0) SelectCombo(bitrateBox, pb.ToString());

                int pr = server.PrefPcmRate;
                if (pr > 0) SelectCombo(pcmRateBox, pr.ToString());
            }
            finally { syncing = false; }
        }

        void StartServer()
        {
            string codec = rbPcm.Checked ? "pcm" : "opus";
            int rate = rbPcm.Checked ? SelectedPcmRate() : 48000;
            if (!server.Start((int)portBox.Value, codec, rate, 2, SelectedBitrate()))
                MessageBox.Show(this, "启动失败，详见日志。", "电脑声音", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SyncEnabled();
        }

        // ---------------- 引擎回调（工作线程 -> UI 线程） ----------------

        void OnServerLog(string line)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action<string>(Append), line); return; }
                Append(line);
            }
            catch { }
        }

        void OnServerState()
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action(RefreshState)); return; }
                RefreshState();
            }
            catch { }
        }

        void RefreshState()
        {
            SyncFromServer();
            bool run = server.IsRunning;
            int n = server.ClientCount;
            stateLabel.Text = run ? ("服务运行中 · 监听 0.0.0.0:" + server.Port) : "服务已停止";
            clientLabel.Text = n > 0 ? ("手机已连接（" + n + " 台）") : (run ? "等待手机连接 ..." : "");
            formatLabel.Text = string.IsNullOrEmpty(server.CurrentInfo) ? "" : ("当前编码：" + server.CurrentInfo);
            phoneLabel.Text = "手机连接通道：" + (string.IsNullOrEmpty(phone.Transport) ? "未连接" : phone.Transport)
                              + (phone.AdbFound ? "" : "（没找到 adb.exe）");
            ipLabel.Text = run ? ("本机局域网地址 " + LocalIp()) : "";
            SyncEnabled();
            try
            {
                if (tray != null)
                    tray.Text = "电脑声音 → 手机  ·  " + (run ? ("运行中，手机 " + n + " 台") : "已停止");
            }
            catch { }
        }

        readonly StringBuilder pending = new StringBuilder();
        System.Windows.Forms.Timer flushTimer;

        // 日志批量刷新。
        // 原来每来一行就把整个 TextBox.Text 重设一遍：连接风暴时（手机疯狂重连）
        // 一秒钟能刷几十次全量重设，界面就一直闪、还会卡。
        // 现在改成攒起来、每 150ms 追加一次（AppendText 是增量写，代价小得多）。
        void Append(string line)
        {
            lock (pending) pending.Append(line).Append("\r\n");
            EnsureFlushTimer();
        }

        void EnsureFlushTimer()
        {
            if (flushTimer == null)
            {
                flushTimer = new System.Windows.Forms.Timer { Interval = 150 };
                flushTimer.Tick += (s, e) => FlushLog();
                flushTimer.Start();
            }
        }

        void FlushLog()
        {
            string chunk;
            lock (pending)
            {
                if (pending.Length == 0) return;
                chunk = pending.ToString();
                pending.Length = 0;
            }
            if (logBox == null || logBox.IsDisposed) return;
            if (logBox.TextLength > 300000) logBox.Clear();
            logBox.AppendText(chunk);
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }
    }
}
