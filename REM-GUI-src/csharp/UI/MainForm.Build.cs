// =====================================================================
//  UI\MainForm.Build.cs — MainForm 的界面构建分区（partial 拆分）
//  纯代码搬移，逻辑与行为与拆分前完全一致（重构方案第 2 章）
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

        /// <summary>链接三行与「浏览 / 打开」：窗口最顶部的输入区。</summary>
        private void BuildHeader()
        {
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
        }

        /// <summary>引擎参数卡片：引擎选择、线程、重试、超时、限速，以及参数框提交闭环的挂接。</summary>
        private void BuildEngineCard()
        {
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

            // 参数框统一「提交」闭环：回车 / 失焦 / 点非输入区都走 CommitParams（规范化 + 夹紧回写 + 落配置）
            HookParamCommit(numThreads);
            HookParamCommit(numRetries);
            HookParamCommit(numTimeout);
            HookParamCommit(txtSpeed);
        }

        /// <summary>工具行、进度条、日志区与完成横幅。</summary>
        private void BuildLogCard()
        {
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

            // 「赞助一下~」入口：占据原「诊断日志」的位置与尺寸，AutoSize 自适应，
            // 不移动任何已有控件，也不改窗口尺寸
            lnkSponsor = new LinkLabel();
            lnkSponsor.Text = "赞助一下~";
            lnkSponsor.AutoSize = true;
            lnkSponsor.Location = new Point(S(444), S(250));
            Theme.StyleLink(lnkSponsor);
            lnkSponsor.LinkColor = Theme.Orange;
            lnkSponsor.VisitedLinkColor = Theme.Orange;
            lnkSponsor.LinkClicked += delegate (object s, LinkLabelLinkClickedEventArgs e) { OnSponsor(); };
            Controls.Add(lnkSponsor);

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
            // 3.4：日志区只留一条自绘绿色细条。RichTextBox 的原生竖条藏不干净——ShowScrollBar 藏掉后
            // RichEdit 每次重排版都会把它建回来，白色竖条就贴在 ThinScroll 左侧并排重复。
            // 这里干脆不申请原生条（无 WS_VSCROLL），右侧改由 EM_SETRECT 让位，滚动全部交给 ThinScroll。
            txtLog.ScrollBars = RichTextBoxScrollBars.None;
            txtLog.ReadOnly = true;
            txtLog.DetectUrls = false;
            txtLog.AccessibleName = "txtLog";   // 供 UIA / 自动化脚本稳定定位
            txtLog.AccessibleDescription = "运行日志";
            txtLog.HideSelection = false;
            txtLog.ShortcutsEnabled = true;
            txtLog.TabStop = false;
            txtLog.VScroll += delegate (object s, EventArgs e) { };
            txtLog.HandleCreated += delegate (object s, EventArgs e) { ApplyLogTextInset(); };
            txtLog.SizeChanged += delegate (object s, EventArgs e) { ApplyLogTextInset(); };
            // 撤掉原生条后 RichEdit 自己不再处理滚轮（实测 WM_MOUSEWHEEL 无法移动视图），
            // 滚轮改走与 ThinScroll 同一条 EM_LINESCROLL 通道，每次 3 行，与拖动同一套定位方式。
            txtLog.MouseWheel += delegate (object s, MouseEventArgs e)
            {
                Native.ScrollBy(txtLog.Handle, e.Delta > 0 ? -3 : 3);
                if (scLog != null) { scLog.SyncNow(); }
            };
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
        }

        /// <summary>
        /// 日志控件不申请原生竖条后，文本会一直铺到控件右缘；这里用格式化矩形给自绘 ThinScroll
        /// 留出「条宽 + 2px 余量」，否则每行行尾会被压在细条底下。首次建句柄与尺寸变化各调一次。
        /// </summary>
        private void ApplyLogTextInset()
        {
            if (txtLog == null || !txtLog.IsHandleCreated) { return; }
            int inset = S(ThinScroll.BarWidth) + S(2);
            Native.SetTextRightInset(txtLog.Handle, txtLog.ClientSize.Width, txtLog.ClientSize.Height, inset);
        }

        /// <summary>命令行预览与底部按钮（清屏 / 暂停 / 开始下载）。</summary>
        private void BuildFooter()
        {
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
            btnStop = NewButton("暂停", 364, 695, 60, false);
            // 「暂停」与"打开/高级设置"同底色同描边，只把文字换成红色区分语义；
            // 任务暂停/失败且有续传时按钮翻成绿字「继续」，点它接着下，不靠拖 resume 文件
            btnStop.ForeColor = Theme.Red;
            btnStop.FlatAppearance.BorderColor = Theme.BorderStrong;
            btnStop.Click += delegate (object s, EventArgs e) { OnStop(); };
            btnStop.Enabled = false;
            btnStart = NewButton("开始下载", 428, 695, 68, true);
            btnStart.Click += delegate (object s, EventArgs e) { OnStart(); };
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
            tip.SetToolTip(txtCmd, "实际下发给 N_m3u8DL-RE 的完整命令行（只读；KEY/IV 显示为 * 打码，其余与实际下发一致）");
            tip.SetToolTip(lnkClean, "删除历史日志文件，当前会话日志保留");
            tip.SetToolTip(lnkSponsor, "开源不易，请作者喝杯咖啡（微信 / 支付宝收款码）");
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
    }
}
