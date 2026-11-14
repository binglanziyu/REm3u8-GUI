// =====================================================================
//  UI\MainForm.cs — 主窗体：布局、状态机、日志渲染、后台任务轮询
//  单定时器 100ms 驱动一切；后台线程只写标志与队列，UI 线程只读
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Rem.Core;
using Rem.Engine;
using Rem.Interop;
using Rem.Services;

namespace Rem.UI
{
    public sealed partial class MainForm : Form
    {
        private enum RunState { Idle, Precheck, Running }

        // ---------- 服务 ----------
        private readonly Logger log = new Logger();
        private AppConfig cfg = new AppConfig();
        private UpdateManifest manifest;
        /// <summary>主程序自身的发布页地址（检查到新版本时才有值，空串表示无可点地址）。</summary>
        private string appReleaseUrl = "";
        /// <summary>主程序 Release 建议下载的文件名，用于状态栏/日志提示。</summary>
        private string appAssetName = "";
        /// <summary>状态栏右键菜单：打开/复制发布页。</summary>
        private ContextMenuStrip appMenu;

        // ---------- 控件 ----------
        private TextBox txtUrl;
        private TextBox txtName;
        private TextBox txtDir;
        private TextBox txtCmd;
        private Button btnEngine;
        private Button btnBrowse;
        private Button btnOpen;
        private Button btnAdv;
        private Button btnStart;
        private Button btnStop;
        private Button btnClear;
        private Label lblProg;
        private Label lblSpd;
        private Label lblStatus;
        private Label lblUpd;
        private LinkLabel lnkUpdate;
        private LinkLabel lnkClean;
        private RichTextBox txtLog;
        private ThinScroll scLog;
        private ThinScroll scCmd;
        private Panel bar;
        // 进度条已成功收口：满格改绿，和"还没下完的绿"区分开
        private bool barDone;
        // 完成/失败横幅：盖在日志顶部的一条整宽色带，几秒后自动收，点击可立即收
        private Panel banner;
        private Label lbBannerTitle;
        private Label lbBannerDetail;
        private Color bannerTone = Theme.Green;
        private DateTime bannerUntil = DateTime.MinValue;
        private Panel cardOpt;
        private TextBox numThreads;
        private TextBox numRetries;
        private TextBox numTimeout;
        private TextBox txtSpeed;
        // 上一次合法的限速文本：输入非法（超位数/多个小数点）时原地回滚，不弹提示
        private string speedGood = "0";
        private System.Windows.Forms.Timer tick;

        // ---------- 运行期状态 ----------
        private int dpi = 96;
        private RunState state = RunState.Idle;
        private JobRunner runner;
        private readonly Progress prog = new Progress();
        private BuiltArgs currentArgs;
        private string currentResumeFile = "";
        private DateTime jobStart = DateTime.MinValue;
        private int ticks = 0;
        private int logLines = 0;
        private int jobIndex = 0;
        private int jobOk = 0;
        private int jobFail = 0;
        // 预检取消与世代号：取消后旧线程返回的结果必须被丢弃，否则会被当成新一次预检的结果消费（D4）
        private volatile bool preCanceled = false;
        private volatile int preSeq = 0;
        // 运行期收到外部链接时，记录已提示过的中转文件时间戳，避免重复刷状态（D12）
        private string dropNotifiedFor = "";
        private List<EngineEntry> engines = new List<EngineEntry>();
        // 当前引擎在 engines 里的下标（0 = 内置），替代原来的下拉框 SelectedIndex
        private int engineIndex = 0;

        // 后台任务标志
        private volatile bool preBusy = false;
        private volatile bool preDone = false;
        private PrecheckResult preResult = null;
        private string preUrl = "";
        private string preDir = "";
        private string preExe = "";
        private string preFF = "";

        private volatile bool updBusy = false;
        private volatile bool updDone = false;
        private UpdateResult updResult = null;
        private UpdateResult pendingUpdate = null;

        private volatile bool engineProbeBusy = false;
        private volatile bool engineProbeDone = false;
        private volatile bool toolsReady = false;
        private ToolTip tip;

        private volatile bool installBusy = false;
        private volatile bool installDone = false;
        private volatile int installPct = -1;
        private bool installOk = false;
        private string installErr = "";
        private string installTag = "";
        private bool deferredUpdate = false;

        public MainForm()
        {
            BuildUi();
        }

        // ========================= 工具方法 =========================
        /// <summary>
        /// 设计值 → 像素。唯一缩放入口是 Theme.S：主窗体是口径基准（PickDpi 把实测 dpi 写进
        /// Theme.Dpi），这里只做转发，杜绝「窗体一套 dpi、Theme 另一套」两套换算漂移（D7）。
        /// </summary>
        private int S(int v)
        {
            return Theme.S(v);
        }

        private void PickDpi()
        {
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    dpi = (int)Math.Round(g.DpiX);
                }
            }
            catch (Exception) { dpi = 96; }
            if (dpi < 96) dpi = 96;
            Theme.Dpi = dpi;   // Theme 内部控件（圆角框 / 图标）按同一 DPI 缩放
        }

        // ========================= 布局 =========================
        private void BuildUi()
        {
            SuspendLayout();
            PickDpi();

            Text = "REM-GUI " + Program.Version;
            Icon = Theme.AppIcon();
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9F);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            AllowDrop = true;
            ClientSize = new Size(S(504), S(732));

            // ---- 链接 ----
            // 三行输入统一成"框外行首图标 + 圆角描边框 + 框内右对齐浮动说明"：
            // 图标独立在框左侧，说明文字贴框右侧，输入后自动收起
            txtUrl = Theme.NewField(this, 30, 10, 466, 28, Glyph.Link, "m3u8 链接");
            txtUrl.Font = Theme.Ui(9.5F);
            txtUrl.DoubleClick += delegate (object s, EventArgs e) { PasteClipboard(); };
            txtUrl.KeyDown += OnUrlKey;

            txtName = Theme.NewField(this, 30, 42, 466, 28, Glyph.File, "保存名称");
            txtName.KeyDown += OnUrlKey;

            txtDir = Theme.NewField(this, 30, 74, 362, 28, Glyph.Folder, "保存目录");
            btnBrowse = NewButton("…", 396, 74, 44, false);
            btnBrowse.Click += delegate (object s, EventArgs e) { BrowseDir(); };
            btnOpen = NewButton("打开", 444, 74, 52, false);
            btnOpen.Click += delegate (object s, EventArgs e) { OpenDir(); };

            // ---- 引擎参数卡片（引擎 / 线程 / 重试 / 超时 / 限速）----
            // 引擎不再单独占一行：并进卡片，按钮上直接显示当前版本，点开才出菜单
            cardOpt = Theme.NewCard();
            cardOpt.Location = new Point(S(8), S(118));
            cardOpt.Size = new Size(S(488), S(104));
            Controls.Add(cardOpt);

            Label lbOpt = new Label();
            lbOpt.Text = "引擎参数";
            Theme.PaintCardTitle(lbOpt);
            lbOpt.Location = new Point(S(8), S(6));
            cardOpt.Controls.Add(lbOpt);

            btnEngine = new Button();
            btnEngine.Text = "未选择  ▾";
            btnEngine.TextAlign = ContentAlignment.MiddleLeft;
            btnEngine.Padding = new Padding(S(8), 0, 0, 0);
            btnEngine.Location = new Point(S(8), S(26));
            btnEngine.Size = new Size(S(160), S(26));
            Theme.StyleButton(btnEngine, false);
            btnEngine.Click += delegate (object s, EventArgs e) { ShowEngineMenu(); };
            cardOpt.Controls.Add(btnEngine);

            numThreads = NewNumber(cardOpt, "线程数量", 176, 26, 62, 70, 1, 128);
            numRetries = NewNumber(cardOpt, "重试次数", 330, 26, 62, 70, 0, 100);
            numTimeout = NewNumber(cardOpt, "超时(秒)", 8, 64, 62, 70, 1, 600);

            // 限速：单位固定 M/s，整数与小数各留 3 位（0.001M = 1KB/s），0 表示不限速
            Label lbSpd = new Label();
            lbSpd.Text = "限速";
            lbSpd.AutoSize = false;
            lbSpd.TextAlign = ContentAlignment.MiddleLeft;
            lbSpd.ForeColor = Theme.Text;
            lbSpd.Font = Theme.Ui(9F);
            lbSpd.Location = new Point(S(176), S(67));
            lbSpd.Size = new Size(S(62), S(20));
            cardOpt.Controls.Add(lbSpd);

            // 限速用文本框而非 NumericUpDown：M/s 固定单位，整数最多 3 位、小数最多 3 位
            // （0.001M = 1KB/s），这样填 0.5 就显示 0.5，不会被补成 0.500
            txtSpeed = Theme.NewField(cardOpt, 236, 62, 84, 28);
            txtSpeed.TextAlign = HorizontalAlignment.Left;
            txtSpeed.TextChanged += delegate (object s, EventArgs e) { FilterSpeed(); };
            txtSpeed.Leave += delegate (object s, EventArgs e) { NormalizeSpeed(); };

            Label lbSpdUnit = new Label();
            lbSpdUnit.Text = "M/s";
            lbSpdUnit.AutoSize = false;
            lbSpdUnit.TextAlign = ContentAlignment.MiddleLeft;
            lbSpdUnit.ForeColor = Theme.TextDim;
            lbSpdUnit.Font = Theme.Ui(8.5F);
            lbSpdUnit.Location = new Point(S(324), S(67));
            lbSpdUnit.Size = new Size(S(34), S(20));
            cardOpt.Controls.Add(lbSpdUnit);

            Label lbSpdHint = new Label();
            lbSpdHint.Text = "0 = 不限速";
            lbSpdHint.AutoSize = false;
            lbSpdHint.TextAlign = ContentAlignment.MiddleLeft;
            lbSpdHint.ForeColor = Theme.Dim;
            lbSpdHint.Font = Theme.Ui(8F);
            lbSpdHint.Location = new Point(S(360), S(67));
            lbSpdHint.Size = new Size(S(96), S(20));
            cardOpt.Controls.Add(lbSpdHint);

            // ---- 工具行 ----
            btnAdv = NewButton("高级设置…", 8, 244, 96, false);
            btnAdv.Click += delegate (object s, EventArgs e) { OnAdv(); };

            lnkUpdate = new LinkLabel();
            lnkUpdate.Text = "检查更新";
            lnkUpdate.AutoSize = true;
            lnkUpdate.Location = new Point(S(112), S(250));
            Theme.StyleLink(lnkUpdate);
            lnkUpdate.LinkClicked += delegate (object s, LinkLabelLinkClickedEventArgs e) { OnUpdateLink(); };
            Controls.Add(lnkUpdate);

            lblUpd = new Label();
            lblUpd.Text = "引擎状态未知";
            lblUpd.AutoSize = false;
            lblUpd.TextAlign = ContentAlignment.MiddleLeft;
            lblUpd.ForeColor = Theme.Dim;
            lblUpd.Font = Theme.Ui(8.5F);
            lblUpd.Location = new Point(S(184), S(249));
            lblUpd.Size = new Size(S(196), S(20));
            Controls.Add(lblUpd);

            lnkClean = new LinkLabel();
            lnkClean.Text = "清理日志";
            lnkClean.AutoSize = true;
            lnkClean.Location = new Point(S(390), S(250));
            Theme.StyleLink(lnkClean);
            lnkClean.LinkClicked += delegate (object s, LinkLabelLinkClickedEventArgs e) { OnCleanLogs(); };
            Controls.Add(lnkClean);

            // ---- 进度 ----
            lblProg = new Label();
            lblProg.Text = "0/0   --";
            lblProg.AutoSize = false;
            lblProg.ForeColor = Theme.AccentLight;
            lblProg.Font = Theme.Mono(9.5F);
            lblProg.TextAlign = ContentAlignment.MiddleLeft;
            lblProg.Location = new Point(S(8), S(274));
            lblProg.Size = new Size(S(300), S(20));
            Controls.Add(lblProg);

            lblSpd = new Label();
            lblSpd.Text = "";
            lblSpd.AutoSize = false;
            lblSpd.ForeColor = Theme.Dim;
            lblSpd.Font = Theme.Mono(9F);
            lblSpd.TextAlign = ContentAlignment.MiddleRight;
            lblSpd.Location = new Point(S(312), S(274));
            lblSpd.Size = new Size(S(184), S(20));
            Controls.Add(lblSpd);

            bar = new Panel();
            bar.Location = new Point(S(8), S(296));
            bar.Size = new Size(S(488), S(4));
            bar.BackColor = Theme.Field;
            bar.Paint += OnPaintBar;
            Controls.Add(bar);

            // ---- 日志 ----
            txtLog = new RichTextBox();
            txtLog.Location = new Point(S(8), S(306));
            txtLog.Size = new Size(S(488), S(348));
            txtLog.BackColor = Theme.LogBg;
            txtLog.ForeColor = Theme.Text;
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.Font = Theme.Mono(9.5F);
            txtLog.WordWrap = true;
            txtLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            txtLog.ReadOnly = true;
            txtLog.DetectUrls = false;
            txtLog.AccessibleName = "txtLog";   // 供 UIA / 自动化脚本稳定定位
            txtLog.AccessibleDescription = "运行日志";
            txtLog.HideSelection = false;
            txtLog.ShortcutsEnabled = true;
            txtLog.TabStop = false;
            txtLog.VScroll += delegate (object s, EventArgs e) { };
            Controls.Add(txtLog);
            scLog = new ThinScroll(txtLog, this);

            // ---- 完成横幅 ----
            // 完成瞬间的信号必须够重：日志里一行绿字、状态栏三个字都太轻，切到别的窗口再回来极容易漏看。
            // 这里盖在日志顶部做一条整宽色带（成功绿 / 失败红 / 停止橙），6 秒后自动收起，点击可立即收起。
            banner = new Panel();
            banner.Location = new Point(S(8), S(306));
            banner.Size = new Size(S(488), S(58));
            banner.BackColor = Theme.Card;
            banner.Visible = false;
            banner.Cursor = Cursors.Hand;
            banner.Paint += OnPaintBanner;
            banner.Click += delegate (object s, EventArgs e) { HideBanner(); };

            lbBannerTitle = new Label();
            lbBannerTitle.AutoSize = false;
            lbBannerTitle.AutoEllipsis = true;   // 文件名再长也不撑破横幅
            lbBannerTitle.BackColor = Theme.Card;
            lbBannerTitle.Font = Theme.UiBold(12F);
            lbBannerTitle.TextAlign = ContentAlignment.MiddleLeft;
            lbBannerTitle.Location = new Point(S(16), S(6));
            lbBannerTitle.Size = new Size(S(462), S(26));
            lbBannerTitle.Click += delegate (object s, EventArgs e) { HideBanner(); };
            banner.Controls.Add(lbBannerTitle);

            lbBannerDetail = new Label();
            lbBannerDetail.AutoSize = false;
            lbBannerDetail.AutoEllipsis = true;
            lbBannerDetail.BackColor = Theme.Card;
            lbBannerDetail.Font = Theme.Ui(9F);
            lbBannerDetail.ForeColor = Theme.TextDim;
            lbBannerDetail.TextAlign = ContentAlignment.MiddleLeft;
            lbBannerDetail.Location = new Point(S(16), S(32));
            lbBannerDetail.Size = new Size(S(462), S(20));
            lbBannerDetail.Click += delegate (object s, EventArgs e) { HideBanner(); };
            banner.Controls.Add(lbBannerDetail);

            Controls.Add(banner);

            // ---- 命令行预览 ----
            txtCmd = new TextBox();
            txtCmd.Location = new Point(S(8), S(658));
            txtCmd.Size = new Size(S(488), S(30));
            txtCmd.Multiline = true;
            txtCmd.ReadOnly = true;
            txtCmd.ScrollBars = ScrollBars.Vertical;
            txtCmd.BackColor = Theme.CmdBg;
            txtCmd.ForeColor = Theme.Dim;
            txtCmd.BorderStyle = BorderStyle.None;
            txtCmd.Font = Theme.Mono(8F);
            txtCmd.TabStop = false;
            Controls.Add(txtCmd);
            scCmd = new ThinScroll(txtCmd, this);

            // ---- 底部 ----
            lblStatus = new Label();
            lblStatus.Text = "就绪";
            lblStatus.AutoSize = false;
            lblStatus.ForeColor = Theme.Dim;
            lblStatus.Font = Theme.Ui(9F);
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.Location = new Point(S(8), S(697));
            lblStatus.Size = new Size(S(292), S(26));
            // 主程序自身有新版本时，状态栏文案可点击直接打开发布页，右键可复制地址。
            // 只做提示 + 跳转：运行中的 exe 无法自我替换，自动更新不在本程序职责内。
            lblStatus.Click += delegate (object s, EventArgs e) { OnOpenAppRelease(); };
            appMenu = new ContextMenuStrip();
            appMenu.Items.Add("打开程序发布页", null,
                delegate (object s, EventArgs e) { OnOpenAppRelease(); });
            appMenu.Items.Add("复制发布页地址", null,
                delegate (object s, EventArgs e) { OnCopyAppRelease(); });
            Controls.Add(lblStatus);

            btnClear = NewButton("清屏", 306, 695, 54, false);
            btnClear.Click += delegate (object s, EventArgs e) { ClearLog(); };
            btnStop = NewButton("停止", 364, 695, 60, false);
            // 停止与"打开/高级设置"同底色同描边，只把文字换成红色区分语义，
            // 避免原先更黑一档的底色在绿底上显得突兀
            btnStop.ForeColor = Theme.Red;
            btnStop.FlatAppearance.BorderColor = Theme.BorderStrong;
            btnStop.Click += delegate (object s, EventArgs e) { OnStop(); };
            btnStop.Enabled = false;
            btnStart = NewButton("开始下载", 428, 695, 68, true);
            btnStart.Click += delegate (object s, EventArgs e) { OnStart(); };

            ResumeLayout(false);

            ApplyToolTips();

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 100;
            tick.Tick += OnTick;

            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            Shown += OnShown;
            FormClosing += OnFormClosing;
        }

        /// <summary>
        /// 卡片内一行「标签 + 数字输入」。数字用圆角文本框而不是 NumericUpDown：
        /// 系统自带的上下箭头是浅灰实心小方块，深色底上既丑又改不动配色，直接不用它。
        /// 输入只吃数字键，取值统一走 NumOf()，空/非法/越界自动回落到默认并夹紧。
        /// </summary>
        private TextBox NewNumber(Control parent, string text, int x, int y, int labelW, int numW, int min, int max)
        {
            Label lb = new Label();
            lb.Text = text;
            lb.AutoSize = false;
            lb.TextAlign = ContentAlignment.MiddleLeft;
            lb.ForeColor = Theme.Text;
            lb.Font = Theme.Ui(9F);
            lb.Location = new Point(S(x), S(y + 3));
            lb.Size = new Size(S(labelW), S(20));
            parent.Controls.Add(lb);

            TextBox t = Theme.NewField(parent, x + labelW + 4, y, numW, 26);
            t.Tag = min + "," + max;
            t.MaxLength = 7;
            t.KeyPress += NumberOnly;
            return t;
        }

        /// <summary>数字框只吃数字键，粘贴进来的脏字符由 NumOf() 兜底。</summary>
        private static void NumberOnly(object s, KeyPressEventArgs e)
        {
            if (char.IsControl(e.KeyChar)) return;
            if (!char.IsDigit(e.KeyChar)) e.Handled = true;
        }

        /// <summary>读取数字框的值：空/非法用默认值，越界按 Tag 里记的 min,max 夹住。</summary>
        private static int NumOf(TextBox t, int def)
        {
            int v;
            if (t == null || !int.TryParse(t.Text.Trim(), out v)) v = def;
            if (t != null && t.Tag != null)
            {
                string[] parts = t.Tag.ToString().Split(',');
                int lo, hi;
                if (parts.Length == 2 && int.TryParse(parts[0], out lo) && int.TryParse(parts[1], out hi))
                {
                    if (v < lo) v = lo;
                    if (v > hi) v = hi;
                }
            }
            return v;
        }

        /// <summary>控件级提示（旧版 $tip 的回退项）：新布局参数多了，鼠标停留时给出口径。</summary>
        private void ApplyToolTips()
        {
            tip = new ToolTip();
            tip.AutoPopDelay = 20000;
            tip.InitialDelay = 400;
            tip.ReshowDelay = 100;
            tip.ShowAlways = true;

            tip.SetToolTip(numThreads, "分片下载并发线程数，默认 16；家用带宽 8~16 足够，过高反而易被限速");
            tip.SetToolTip(numRetries, "单个分片失败后的重试次数，默认 3");
            tip.SetToolTip(numTimeout, "单次 HTTP 请求超时（秒），默认 15；慢源/直播可调大");
            tip.SetToolTip(txtSpeed, "限速（M/s）：整数最多 3 位、小数最多 3 位，相当于可填到 1KB/s 精度；填 0 表示不限速");
            tip.SetToolTip(btnEngine, "当前下载引擎；点开可切换版本、选外部 exe、打开引擎目录");
            tip.SetToolTip(btnAdv, "开关类选项、代理、请求头与额外参数");
            tip.SetToolTip(txtCmd, "实际下发给 N_m3u8DL-RE 的完整命令行（只读）");
            tip.SetToolTip(lnkClean, "删除历史日志文件，当前会话日志保留");
            tip.SetToolTip(lnkUpdate, "检查 N_m3u8DL-RE 是否有新版本");
            tip.SetToolTip(txtLog, "实时运行日志，错误为红色、警告为橙色");
            tip.SetToolTip(txtUrl, "视频页 / 播放列表页链接；双击输入框可直接粘贴剪贴板");
            tip.SetToolTip(txtName, "输出文件名（不含扩展名）");
            tip.SetToolTip(txtDir, "保存目录；也可直接把文件夹拖进窗口");
        }

        private Button NewButton(string text, int x, int y, int w, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(S(x), S(y));
            b.Size = new Size(S(w), S(26));
            Theme.StyleButton(b, primary);
            b.TabStop = false;
            Controls.Add(b);
            return b;
        }

        private void OnPaintBar(object sender, PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Field);
            // 成功收口后整条满格并换成更亮的绿：一眼就能和"下到一半"区分开
            int pct = barDone ? 100 : prog.PercentInt;
            int w = bar.Width * pct / 100;
            if (w <= 0) return;
            // 渐变而非纯色：进度条在深底上更有层次，也避免和主按钮抢同一块色
            Color from = barDone ? Theme.Green : Theme.AccentHover;
            using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(w, 1), Math.Max(bar.Height, 1)), from, Theme.AccentLight, 0F))
            {
                e.Graphics.FillRectangle(b, 0, 0, w, bar.Height);
            }
        }

        /// <summary>标题栏带上状态：最小化到任务栏、Alt+Tab 缩略图里也能立刻看出跑完没有。</summary>
        private void SetTitleState(string prefix)
        {
            Text = string.IsNullOrEmpty(prefix)
                ? "REM-GUI " + Program.Version
                : prefix + " · REM-GUI " + Program.Version;
        }

        private void OnPaintBanner(object sender, PaintEventArgs e)
        {
            // 左侧粗色条 + 一圈同色描边：颜色本身就是结论（绿=成功、红=失败、橙=停止）
            using (SolidBrush b = new SolidBrush(bannerTone))
            {
                e.Graphics.FillRectangle(b, 0, 0, S(6), banner.Height);
            }
            using (Pen p = new Pen(bannerTone))
            {
                e.Graphics.DrawRectangle(p, 0, 0, banner.Width - 1, banner.Height - 1);
            }
        }

        /// <summary>弹出完成/失败横幅。seconds 取 0 表示常驻——要显眼的事（下完了 / 失败了）不该自己消失，
        /// 只由“点一下”或“开始下一次任务”收起；正数则由 OnTick 到点自动收。</summary>
        private void ShowBanner(string title, string detail, Color tone, int seconds)
        {
            if (banner == null || banner.IsDisposed) return;
            bannerTone = tone;
            lbBannerTitle.Text = title;
            lbBannerTitle.ForeColor = tone;
            lbBannerDetail.Text = detail;
            banner.Visible = true;
            banner.BringToFront();
            banner.Invalidate();
            bannerUntil = seconds > 0 ? DateTime.Now.AddSeconds(seconds) : DateTime.MaxValue;
        }

        private void HideBanner()
        {
            bannerUntil = DateTime.MinValue;
            if (banner != null && !banner.IsDisposed) banner.Visible = false;
        }

        // ========================= 启动 =========================
        private void OnShown(object sender, EventArgs e)
        {
            try
            {
                Native.SetDarkTitleBar(Handle, true);
                if (scLog != null) { scLog.Attach(); }
                if (scCmd != null) { scCmd.Attach(); }
            }
            catch (Exception __ex) { Note.Swallow("MainForm.cs:461", __ex); }

            manifest = UpdateManifest.Load(AppPaths.AppDir, log);
            cfg = ConfigStore.Load(AppPaths.ConfigPath, log);
            ApplyConfig();

            log.OpenFile(Path.Combine(AppPaths.LogDir, "gui_" + DateTime.Now.ToString("yyyyMMdd") + ".log"));
            log.Info("REM-GUI " + Program.Version + "  (C# WinForms / .NET Framework 4.8)");
            log.Dim("程序目录 " + AppPaths.AppDir);
            log.Dim("数据目录 " + AppPaths.DataDir);

            RefreshEngineList();
            StartEngineProbe();
            ReportResumeTasks();
            DrainStartupDrop();

            txtUrl.Focus();
            tick.Start();
            deferredUpdate = true;
            SetStatus("就绪");

            // 解密失败的加密字段在 Load 时只能被清空，但不能静默（D15）：明确列出字段名并提示重填
            if (ConfigStore.DecryptFailedFields.Count > 0)
            {
                string fields = string.Join("、", ConfigStore.DecryptFailedFields.ToArray());
                log.Warn(">> 部分加密配置无法解密，已清空：" + fields);
                SetStatus("部分加密配置无法解密（" + fields + "），已清空，请重新填写", Theme.Orange);
            }

            // 冷启动耗时落盘（窗体可见时刻 - 进程入口时刻）
            try
            {
                log.Diag("冷启动 " + (Environment.TickCount - Program.StartTick) + " ms", "Startup");
            }
            catch (Exception __ex) { Note.Swallow("MainForm.cs:487", __ex); }

            // 工具自愈放后台：只有真缺失时才解压，绝不阻塞窗体显示
            ThreadPool.QueueUserWorkItem(delegate (object st)
            {
                try { AppPaths.EnsureTools(false, log); }
                catch (Exception ex) { log.Diag("工具自愈失败：" + ex.Message, "EnsureTools"); }
                // 自愈可能刚刚释放出引擎，通知 UI 线程重新枚举，避免停在“未找到引擎”
                toolsReady = true;
            });
        }

        private void ApplyConfig()
        {
            if (string.IsNullOrEmpty(cfg.SaveDir)) cfg.SaveDir = DefaultSaveDir();
            txtDir.Text = cfg.SaveDir;
            ApplyEngineParams();
        }

        /// <summary>把配置里的引擎参数灌进主界面控件（限速拆成数值 + 单位）。</summary>
        private void ApplyEngineParams()
        {
            numThreads.Text = cfg.Threads.ToString();
            numRetries.Text = cfg.Retries.ToString();
            numTimeout.Text = cfg.Timeout.ToString();
            speedGood = FormatSpeed(cfg.SpeedMB);
            txtSpeed.Text = speedGood;
        }

        /// <summary>限速值夹到 [0, 999.999]，四舍五入到 3 位小数（1KB/s 精度）。</summary>
        private static double ClampSpeed(double v)
        {
            if (v < 0) v = 0;
            if (v > AppConfig.SpeedMaxMB) v = AppConfig.SpeedMaxMB;
            return Math.Round(v, 3, MidpointRounding.AwayFromZero);
        }

        /// <summary>0.5 → "0.5"，5.000 → "5"；不带多余的尾随零。</summary>
        private static string FormatSpeed(double v)
        {
            double c = ClampSpeed(v);
            return c.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>"0.5" / "12." / "" → 数值；解析不了就当 0。</summary>
        private static double ParseSpeed(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            string t = text.Trim();
            if (t.EndsWith(".")) t = t.Substring(0, t.Length - 1);
            double v;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        /// <summary>输入过滤：只放行"最多 3 位整数 + 最多 3 位小数"，非法输入直接回滚。</summary>
        private void FilterSpeed()
        {
            string t = txtSpeed.Text;
            bool ok = true;
            int dot = -1;
            int intDigits = 0;
            int fracDigits = 0;
            for (int i = 0; i < t.Length; i++)
            {
                char ch = t[i];
                if (ch == '.')
                {
                    if (dot >= 0) { ok = false; break; }
                    dot = i;
                }
                else if (ch >= '0' && ch <= '9')
                {
                    if (dot < 0) intDigits++; else fracDigits++;
                }
                else { ok = false; break; }
            }
            if (intDigits > 3 || fracDigits > 3) ok = false;
            if (!ok)
            {
                int caret = txtSpeed.SelectionStart;
                txtSpeed.Text = speedGood;
                txtSpeed.SelectionStart = caret > speedGood.Length ? speedGood.Length : caret;
                return;
            }
            speedGood = t;
        }

        /// <summary>失焦时把 "12." / "007" 规范成 "12" / "7"，空值按 0 处理。</summary>
        private void NormalizeSpeed()
        {
            speedGood = FormatSpeed(ParseSpeed(txtSpeed.Text));
            if (txtSpeed.Text != speedGood)
            {
                txtSpeed.Text = speedGood;
            }
        }

        private void CollectConfig()
        {
            cfg.SaveDir = txtDir.Text.Trim();
            cfg.Threads = NumOf(numThreads, 16);
            cfg.Retries = NumOf(numRetries, 3);
            cfg.Timeout = NumOf(numTimeout, 15);
            cfg.SpeedMB = ClampSpeed(ParseSpeed(txtSpeed.Text));
            if (engineIndex > 0 && engineIndex < engines.Count)
            {
                EngineEntry en = engines[engineIndex];
                cfg.EngineSel = string.IsNullOrEmpty(en.Dir) ? "" : Path.GetFileName(en.Dir);
            }
            else
            {
                cfg.EngineSel = "";
            }
            cfg.Clamp();
        }

        /// <summary>
        /// 默认保存目录 = 软件所在目录（REM-GUI.exe 同目录），下载产物跟着程序走，
        /// 不再默认落到「我的视频」或 %LOCALAPPDATA%。程序目录取不到时才逐级退回。
        /// </summary>
        private string DefaultSaveDir()
        {
            try
            {
                if (!string.IsNullOrEmpty(AppPaths.AppDir) && Directory.Exists(AppPaths.AppDir))
                    return AppPaths.AppDir;
            }
            catch (Exception) { }
            try
            {
                string v = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(v)) return AppPaths.DataDir;
                return v;
            }
            catch (Exception)
            {
                return AppPaths.DataDir;
            }
        }

        // ========================= 引擎 =========================
        // ========================= 交互 =========================
        private void OnUrlKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                OnStart();
            }
        }

        private void PasteClipboard()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string url = Safe.UrlFirst(Clipboard.GetText());
                    if (url.Length > 0)
                    {
                        txtUrl.Text = url;
                        SetStatus("已从剪贴板取到链接");
                    }
                }
            }
            catch (Exception __ex) { Note.Swallow("MainForm.cs:758", __ex); }
        }

        private void BrowseDir()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择保存目录";
                dlg.SelectedPath = txtDir.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    txtDir.Text = dlg.SelectedPath;
                }
            }
        }

        private void OpenDir()
        {
            try
            {
                string dir = txtDir.Text.Trim();
                if (dir.Length == 0) dir = DefaultSaveDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // using 只为及时释放进程对象句柄：explorer 是独立进程，Dispose 不会影响它的运行
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start("explorer.exe", dir)) { }
            }
            catch (Exception ex)
            {
                log.Err("打开目录失败：" + ex.Message);
            }
        }

        private void OnAdv()
        {
            CollectConfig();
            using (AdvForm f = new AdvForm(cfg))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    cfg.Smart = f.Smart;
                    cfg.Mux = f.Mux;
                    cfg.Concurrent = f.Concurrent;
                    cfg.Binary = f.Binary;
                    cfg.SkipMerge = f.SkipMerge;
                    cfg.SkipDownload = f.SkipDownload;
                    cfg.AppendUrlParams = f.AppendUrlParams;
                    cfg.DelAfterDone = f.DelAfterDone;
                    cfg.CheckSegments = f.CheckSegments;
                    cfg.AutoSelect = f.AutoSelect;
                    cfg.UseSystemProxy = f.UseSystemProxy;
                    cfg.Proxy = f.Proxy;
                    cfg.Headers = f.Headers;
                    cfg.ExtraArgs = f.ExtraArgs;
                    // 解密方式固定 AES-128：只需回写 KEY / IV（AdvForm 里已把 HlsMethod 定格为常量）
                    cfg.HlsMethod = f.HlsMethod;
                    cfg.HlsKey = f.HlsKey;
                    cfg.HlsIv = f.HlsIv;
                    cfg.LogLimit = f.LogLimit;
                    cfg.Clamp();
                    SaveConfig();
                    SetStatus("高级设置已保存");
                }
            }
        }

        // ========================= 开始 / 停止 =========================
        // ========================= 定时器 =========================
        // ========================= 续传 =========================
        private void DrainStartupDrop()
        {
            try
            {
                if (!File.Exists(AppPaths.DropFile)) return;
                // 任务运行期间收到的外部链接不静默滞留：明确告知“已收到，任务结束后处理”，
                // 同一份中转文件只提示一次，避免 100ms tick 反复刷状态（D12）
                if (state != RunState.Idle)
                {
                    string stamp = "";
                    try { stamp = File.GetLastWriteTime(AppPaths.DropFile).Ticks.ToString(); }
                    catch (Exception __ex) { Note.Swallow("MainForm.cs:1418", __ex); }
                    if (stamp != dropNotifiedFor)
                    {
                        dropNotifiedFor = stamp;
                        log.Warn(">> 已收到外部链接，将在当前任务结束后处理");
                        SetStatus("已收到外部链接，任务结束后处理", Theme.Orange);
                    }
                    return;
                }
                string text = File.ReadAllText(AppPaths.DropFile, Encoding.UTF8);
                File.Delete(AppPaths.DropFile);
                dropNotifiedFor = "";
                string url = Safe.UrlFirst(text);
                if (url.Length > 0)
                {
                    txtUrl.Text = url;
                    SetStatus("已接收外部链接");
                }
            }
            catch (Exception __ex) { Note.Swallow("MainForm.cs:1414", __ex); }
        }

        // ========================= 拖放 =========================
        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files == null || files.Length == 0) return;
                    string first = files[0];
                    if (first.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                        first.EndsWith(".resume.json", StringComparison.OrdinalIgnoreCase))
                    {
                        LoadResumeFile(first);
                        return;
                    }
                    string text = "";
                    try { text = File.ReadAllText(first, Encoding.UTF8); } catch (Exception __ex) { Note.Swallow("MainForm.cs:1442", __ex); }
                    string url = Safe.UrlFirst(text);
                    if (url.Length > 0)
                    {
                        txtUrl.Text = url;
                        SetStatus("已从文件取到链接");
                    }
                    return;
                }
                if (e.Data.GetDataPresent(DataFormats.Text))
                {
                    string text = (string)e.Data.GetData(DataFormats.Text);
                    string url = Safe.UrlFirst(text);
                    if (url.Length > 0)
                    {
                        txtUrl.Text = url;
                        SetStatus("已接收拖入文本中的链接");
                    }
                }
            }
            catch (Exception ex)
            {
                log.Err("拖放处理失败：" + ex.Message);
            }
        }

        // ========================= 更新 =========================
        // ========================= 收尾 =========================
        private void SaveConfig()
        {
            try
            {
                // 传 log：DPAPI 加密失败、配置写盘失败都会被看见，不再静默丢配置
                if (!ConfigStore.Save(AppPaths.ConfigPath, cfg, log))
                {
                    log.Diag("配置未能原子写入：" + AppPaths.ConfigPath, "Config");
                }
            }
            catch (Exception ex)
            {
                log.Diag("保存配置异常：" + ex.Message, "Config");
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (runner != null)
            {
                DialogResult r = MessageBox.Show(this,
                    "任务仍在进行，关闭窗口会终止下载。\r\n\r\n确定关闭吗？",
                    "确认关闭", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
                try { runner.Stop(); } catch (Exception __ex) { Note.Swallow("MainForm.cs:1696", __ex); }
            }
            try { tick.Stop(); } catch (Exception __ex) { Note.Swallow("MainForm.cs:1698", __ex); }
            CollectConfig();
            SaveConfig();
            log.FlushFile();
            log.CloseFile();
        }
    }
}
