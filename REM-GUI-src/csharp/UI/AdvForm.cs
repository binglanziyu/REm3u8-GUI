// =====================================================================
//  UI\AdvForm.cs — 高级设置：全部下载选项 + 代理 / Header / 附加参数
//  线程、重试、超时、限速已上提到主界面，此处不再重复
//  代理与 Header 默认打码且只读，必须勾选"显示明文"才能编辑
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Rem.Core;
using Rem.Engine;
using Rem.Services;

namespace Rem.UI
{
    public sealed class AdvForm : Form
    {
        // ---- 下载选项（原主界面选项卡片迁移至此） ----
        public bool Smart;
        public bool Mux;
        public bool Concurrent;
        public bool Binary;
        public bool SkipMerge;
        public bool SkipDownload;
        public bool AppendUrlParams;
        public bool DelAfterDone;
        public bool CheckSegments;
        public bool AutoSelect;

        // ---- 网络与扩展 ----
        public string Proxy;
        public string Headers;
        public string ExtraArgs;

        // 系统代理开关（D8）：true = 使用系统代理（勾选，不下发参数，沿用引擎默认 True）；
        // false = 不勾「使用系统代理」（默认）→ 下发 --use-system-proxy false
        public bool UseSystemProxy;

        // ---- 解密（解密方式固定 AES-128 + 手动 KEY / IV）----
        public string HlsMethod;
        public string HlsKey;
        public string HlsIv;

        // ---- 界面 ----
        public int LogLimit;

        private CheckBox chkSmart;
        private CheckBox chkMux;
        private CheckBox chkConcurrent;
        private CheckBox chkBinary;
        private CheckBox chkSkipMerge;
        private CheckBox chkSkipDownload;
        private CheckBox chkAppend;
        private CheckBox chkDel;
        private CheckBox chkCheck;
        private CheckBox chkAuto;

        private TextBox txtProxy;
        private TextBox txtHeaders;
        private TextBox txtExtra;
        private ThinScroll scHeaders;
        private ThinScroll scExtra;
        private CheckBox chkPlain;
        private CheckBox chkSysProxy;
        private TextBox numLogLimit;
        private readonly string plainProxy;
        private readonly string plainHeaders;

        private TextBox txtKey;
        private TextBox txtIv;
        private Label lbKeyShape;
        private readonly string plainKey;
        private readonly string plainIv;

        private ToolTip tips;
        private readonly Dictionary<CheckBox, string> tipText = new Dictionary<CheckBox, string>();
        // 被互斥锁住的开关 → 拦住它的那一项名字（用于提示"先取消谁"）
        private readonly Dictionary<CheckBox, string> lockReason = new Dictionary<CheckBox, string>();

        public AdvForm(AppConfig cfg)
        {
            Smart = cfg.Smart;
            Mux = cfg.Mux;
            Concurrent = cfg.Concurrent;
            Binary = cfg.Binary;
            SkipMerge = cfg.SkipMerge;
            SkipDownload = cfg.SkipDownload;
            AppendUrlParams = cfg.AppendUrlParams;
            DelAfterDone = cfg.DelAfterDone;
            CheckSegments = cfg.CheckSegments;
            AutoSelect = cfg.AutoSelect;

            Proxy = cfg.Proxy;
            Headers = cfg.Headers;
            ExtraArgs = cfg.ExtraArgs;
            UseSystemProxy = cfg.UseSystemProxy;
            LogLimit = cfg.LogLimit;
            plainProxy = cfg.Proxy;
            plainHeaders = cfg.Headers;

            HlsMethod = cfg.HlsMethod;
            HlsKey = cfg.HlsKey;
            HlsIv = cfg.HlsIv;
            plainKey = cfg.HlsKey;
            plainIv = cfg.HlsIv;

            BuildUi();
        }

        /// <summary>
        /// 设计值 → 像素：唯一缩放入口就是 Theme.S（与主窗体 MainForm 同一口径）。
        /// 本窗体内不再出现裸像素，也不另立第二套换算（D7）。
        /// </summary>
        private static int S(int v)
        {
            return Theme.S(v);
        }

        private void BuildUi()
        {
            Text = "高级设置";
            Icon = Theme.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9F);
            // 关闭 WinForms 自带的字体缩放：与主窗体一致改为手工缩放，
            // 否则 AutoScaleMode.Font 的整体缩放会和 Theme.S 的逐点缩放叠加，高 DPI 下控件互相挤压（D7）
            AutoScaleMode = AutoScaleMode.None;

            const int x = 14;
            const int labelW = 76;
            // 行距常量（D7 压缩行距）：150% 缩放下本窗高度必须 ≤ 1032px（本机工作区高），
            // 折到设计值只有 688 左右，因此行高与行间隙统一收敛：
            //   rowH  单行输入框/下拉的高度（保持 26：内部 TextBox 固定 22px + 上下留白，压小会切字）
            //   step  单行控件占用的纵向步长 = rowH + 2px 行间隙
            //   note  控件下方 8pt 只读说明文字的占位（说明行紧贴控件下沿，不额外留空）
            const int rowH = 26;
            const int step = 28;
            const int note = 44;    // 控件(26) + 说明文字(18) 的整行占位，说明行底边正好压在下一行顶边
            int y = 10;

            // ---------- 选项 ----------
            Label lbOpt = new Label();
            lbOpt.Text = "下载选项";
            Theme.PaintCardTitle(lbOpt);
            lbOpt.Font = Theme.UiBold(9F);
            // 分组标题上移 2px：9pt 标签实际高 20px，与下一行同步起算会压住下方控件 2px（D7 探针判重叠）；
            // 上移 2px 后标签底边正好贴住下一行顶边，既不叠也不留白
            lbOpt.Location = new Point(S(x), S(y - 2));
            Controls.Add(lbOpt);
            y += 18;

            Panel cardOpt = Theme.NewCard();
            cardOpt.Location = new Point(S(x), S(y));
            cardOpt.Size = new Size(S(442), S(137));
            Controls.Add(cardOpt);

            // 选项做成开关按钮：绿底 = 已开启，暗底 = 已关闭，状态一眼可见
            // 行高 24 / 行距 26：10 个开关压成 5 行 137px 的卡片（原 180px），是本窗最大的一处留白压缩
            chkSmart = NewToggle(cardOpt, "智能预检", 12, 5);
            chkMux = NewToggle(cardOpt, "混流为 MP4", 226, 5);
            chkConcurrent = NewToggle(cardOpt, "边下边转 (-mt)", 12, 31);
            chkBinary = NewToggle(cardOpt, "二进制合并", 226, 31);
            chkSkipMerge = NewToggle(cardOpt, "跳过合并", 12, 57);
            chkSkipDownload = NewToggle(cardOpt, "仅解析不下载", 226, 57);
            chkAppend = NewToggle(cardOpt, "追加 URL 参数", 12, 83);
            chkDel = NewToggle(cardOpt, "完成后删分片", 226, 83);
            chkCheck = NewToggle(cardOpt, "校验分片数", 12, 109);
            chkAuto = NewToggle(cardOpt, "自动最高画质", 226, 109);

            y += 137;

            // 三种状态都要有字面解释：只靠颜色区分，用户会以为互斥项是"坏了"
            Label lbLegend = new Label();
            lbLegend.Text = "绿底 = 已开启　　暗底 = 已关闭　　暗橙底 = 与另一个选项互斥，先取消对方才能勾选";
            lbLegend.AutoSize = true;
            lbLegend.ForeColor = Theme.Dim;
            lbLegend.Font = Theme.Ui(8F);
            lbLegend.Location = new Point(S(x), S(y + 2));
            Controls.Add(lbLegend);
            // 图例是 8pt 自动高度的单行标签（18px），步长要给到 22，才不压住下一段标题
            y += 22;

            BuildTips();

            chkSmart.Checked = Smart;
            chkMux.Checked = Mux;
            chkConcurrent.Checked = Concurrent;
            chkBinary.Checked = Binary;
            chkSkipMerge.Checked = SkipMerge;
            chkSkipDownload.Checked = SkipDownload;
            chkAppend.Checked = AppendUrlParams;
            chkDel.Checked = DelAfterDone;
            chkCheck.Checked = CheckSegments;
            chkAuto.Checked = AutoSelect;

            // 互斥联动统一由 NewToggle 里挂的回滚/规则处理器承担，这里只需初始化一次状态
            ApplySkipRules();

            // ---------- 解密 ----------
            Label lbDec = new Label();
            lbDec.Text = "解密（固定 AES-128，手动 KEY / IV）";
            Theme.PaintCardTitle(lbDec);
            lbDec.Font = Theme.UiBold(9F);
            lbDec.Location = new Point(S(x), S(y - 2));   // 同上：标题上移 2px，与下方「解密方式」行贴合不压边
            Controls.Add(lbDec);
            y += 18;

            // 解密方式只剩 AES-128：原「加密方式 / 解密引擎」两个下拉与「密钥文件」行已整体移除，
            // 这里只留一行只读说明：填了 KEY/IV 才下发 --custom-hls-method AES_128
            NewLabel("解密方式", x, y + 3, labelW);
            Label lbMethod = new Label();
            lbMethod.Text = "AES_128（填 KEY/IV 时自动启用）";
            lbMethod.AutoSize = false;
            lbMethod.TextAlign = ContentAlignment.MiddleLeft;
            lbMethod.Location = new Point(S(x + labelW), S(y + 3));
            lbMethod.Size = new Size(S(x + labelW + 352) - S(x + labelW), S(20));
            lbMethod.ForeColor = Theme.Green;
            lbMethod.Font = Theme.Mono(9F);
            Controls.Add(lbMethod);
            tips.SetToolTip(lbMethod, "仅当下面填了 KEY 或 IV 时才下发 --custom-hls-method AES_128；"
                + "留空则按清单原样处理（明文流不会被强行解密）。"
                + "解密引擎 / 解密程序路径 / 实时解密 / 密钥文件等能力已移除。");
            y += step;

            NewLabel("KEY", x, y + 3, labelW);
            txtKey = NewSecretField(x + labelW, y, 352, plainKey);
            txtKey.TextChanged += delegate (object s, EventArgs e) { UpdateKeyShape(); };

            lbKeyShape = new Label();
            lbKeyShape.AutoSize = true;
            lbKeyShape.ForeColor = Theme.Dim;
            lbKeyShape.Font = Theme.Ui(8F);
            lbKeyShape.Location = new Point(S(x + labelW), S(y + rowH));
            Controls.Add(lbKeyShape);
            UpdateKeyShape();
            y += note;

            NewLabel("IV", x, y + 3, labelW);
            txtIv = NewSecretField(x + labelW, y, 352, plainIv);
            tips.SetToolTip(txtIv, "IV：HEX 或 Base64。多数源的 IV 已写在 KEY 文件或播放列表里，这里留空即可；"
                + "只有明确要求单独给 IV 时才需要填（对应引擎参数 --custom-hls-iv）。");
            y += step;

            // ---------- 网络 ----------
            TextBox proxyBox;
            y = AddText("代理", x, y, labelW, Mask.Secret(MaskText(plainProxy)),
                        "http://127.0.0.1:7890 或 socks5://127.0.0.1:1080（socks 跳过预检）", out proxyBox);
            txtProxy = proxyBox;
            txtProxy.ReadOnly = true;
            txtProxy.ForeColor = Theme.Dim;

            // 系统代理开关（D8）：复选框正向表述「使用系统代理」，默认不勾。
            // 不勾（默认）= 显式下发 --use-system-proxy false（不走系统代理，直连）；
            // 勾选 = 不下发该参数，沿用引擎默认 True（走系统代理）。
            chkSysProxy = new CheckBox();
            chkSysProxy.Text = "使用系统代理";
            chkSysProxy.Location = new Point(S(x), S(y));
            chkSysProxy.Size = new Size(S(442), S(rowH));
            Theme.StyleToggle(chkSysProxy);
            chkSysProxy.Checked = UseSystemProxy;
            tips.SetToolTip(chkSysProxy, "使用系统代理：勾选后不给引擎下发 --use-system-proxy，"
                + "沿用引擎默认（走系统代理）；默认不勾，此时显式下发 --use-system-proxy false，"
                + "让引擎忽略系统代理直连。上面「代理」填了地址时始终按填的代理走，与本开关无关。");
            Controls.Add(chkSysProxy);
            y += step;

            Label lbH = new Label();
            lbH.Text = "Header（每行一条 k: v）";
            lbH.AutoSize = true;
            lbH.ForeColor = Theme.Dim;
            lbH.Location = new Point(S(x), S(y));
            Controls.Add(lbH);
            y += 18;

            txtHeaders = Theme.NewFieldMulti(this, x, y, 442, 64, 9F, true);
            txtHeaders.Text = Mask.HeaderBlock(plainHeaders);
            scHeaders = new ThinScroll(txtHeaders, this);
            y += 68;

            Label lbE = new Label();
            lbE.Text = "附加参数（透传给 N_m3u8DL-RE）";
            lbE.AutoSize = true;
            lbE.ForeColor = Theme.Dim;
            lbE.Location = new Point(S(x), S(y));
            Controls.Add(lbE);
            y += 18;

            // 附加参数框 42px（约 2 行 + 细滚动条），Header 框 64px：多行框靠 ThinScroll 滚动，不裁剪内容
            txtExtra = Theme.NewFieldMulti(this, x, y, 442, 42, 9F, true);
            txtExtra.Text = ExtraArgs;
            scExtra = new ThinScroll(txtExtra, this);
            y += 46;

            chkPlain = new CheckBox();
            chkPlain.Text = "显示明文（代理 / Header / KEY / IV）";
            chkPlain.Location = new Point(S(x), S(y));
            chkPlain.Size = new Size(S(300), S(rowH));
            Theme.StyleToggle(chkPlain);
            chkPlain.CheckedChanged += delegate (object s, EventArgs e) { OnTogglePlain(); };
            Controls.Add(chkPlain);
            y += step;

            // ---------- 界面 ----------
            Label lbLog = new Label();
            lbLog.Text = "日志上限";
            lbLog.AutoSize = false;
            lbLog.TextAlign = ContentAlignment.MiddleLeft;
            lbLog.Location = new Point(S(x), S(y + 2));
            lbLog.Size = new Size(S(labelW), S(20));
            lbLog.ForeColor = Theme.Text;
            Controls.Add(lbLog);

            // 数字输入同样用圆角文本框：NumericUpDown 的上下箭头是系统绘制的浅灰块，
            // 深色底上很扎眼，这里不用它（取值在 Accept() 里统一校验夹紧）
            numLogLimit = Theme.NewField(this, x + labelW, y, 90, 26);
            numLogLimit.Text = (LogLimit >= 20000 && LogLimit <= 2000000 ? LogLimit : 200000).ToString();
            numLogLimit.MaxLength = 7;
            numLogLimit.KeyPress += delegate (object s, KeyPressEventArgs e)
            {
                if (char.IsControl(e.KeyChar)) return;
                if (!char.IsDigit(e.KeyChar)) e.Handled = true;
            };

            Label lbLogHint = new Label();
            lbLogHint.Text = "字符数；超出后裁剪最早的日志行（不影响日志文件）";
            lbLogHint.AutoSize = true;
            lbLogHint.ForeColor = Theme.Dim;
            lbLogHint.Font = Theme.Ui(8F);
            lbLogHint.Location = new Point(S(x + labelW), S(y + rowH));
            Controls.Add(lbLogHint);
            y += note;

            Button ok = new Button();
            ok.Text = "确定";
            ok.Location = new Point(S(288), S(y + 8));
            ok.Size = new Size(S(80), S(26));
            Theme.StyleButton(ok, true);
            ok.Click += delegate (object s, EventArgs e) { Accept(); };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Location = new Point(S(374), S(y + 8));
            cancel.Size = new Size(S(82), S(26));
            Theme.StyleButton(cancel, false);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            // 窗体尺寸由内容反推，不再手写固定值：
            // 宽度 = 左 14 + 内容 442 + 右 14；高度 = 底部按钮下沿（y + 8 + 26）+ 10 留白。
            // 全部经 S() 缩放，保证高 DPI 下内容不会被 ClientSize 裁掉（D7）
            // 行距已按 150% 缩放下 ≤ 1032px 收敛（1.0 / 1.25 / 1.5 三档均不超出工作区高）
            ClientSize = new Size(S(x + 442 + 14), S(y + 8 + 26 + 10));
        }

        /// <summary>忽略合并时混流无意义；仅解析时混流与边下边转都不生效。</summary>
        private void ApplySkipRules()
        {
            bool skipMerge = chkSkipMerge.Checked;
            bool skipDl = chkSkipDownload.Checked;

            SetToggleLocked(chkMux, skipDl ? "仅解析不下载" : (skipMerge ? "跳过合并" : null));
            SetToggleLocked(chkConcurrent, skipDl ? "仅解析不下载" : null);

            if (lockReason.ContainsKey(chkMux)) chkMux.Checked = false;
            if (lockReason.ContainsKey(chkConcurrent)) chkConcurrent.Checked = false;
        }

        /// <summary>
        /// 互斥项走"暗橙底 + 橙字 + （互斥）后缀"，不再用 Enabled=false：
        /// 系统会用灰字重绘禁用控件，ForeColor 失效，字就糊在底色里看不清。
        /// blockedBy 为 null 表示解除锁定。
        /// </summary>
        private void SetToggleLocked(CheckBox c, string blockedBy)
        {
            string baseText = (string)c.Tag;
            string baseTip = null;
            tipText.TryGetValue(c, out baseTip);

            if (string.IsNullOrEmpty(blockedBy))
            {
                lockReason.Remove(c);
                c.Text = baseText;
                Theme.LockToggle(c, false);
                if (tips != null && baseTip != null) tips.SetToolTip(c, baseTip);
                return;
            }

            lockReason[c] = blockedBy;
            c.Text = baseText + "（互斥）";
            Theme.LockToggle(c, true);
            if (tips != null)
            {
                tips.SetToolTip(c, (baseTip == null ? baseText : baseTip)
                    + "\r\n\r\n当前不可勾选：与「" + blockedBy + "」互斥，先取消它才能勾这一项。");
            }
        }

        private void BuildTips()
        {
            tips = new ToolTip();
            tips.AutoPopDelay = 30000;
            tips.InitialDelay = 350;
            tips.ReshowDelay = 100;
            tips.ShowAlways = true;

            Tip(chkSmart, "智能预检：下载前先取一次媒体播放列表做校验，链接失效、分片缺失能提前暴露，避免白下几十 MB 才报错；代理为 socks 类型时不预检。");
            Tip(chkMux, "混流为 MP4：把分离的视频轨与音频轨合成一个 mp4 文件（-M format=mp4）。关掉就只留分离的轨文件。");
            Tip(chkConcurrent, "边下边转 (-mt)：音视频两条轨同时下载，整体更快；带宽紧张或站点限速严格时可以关掉，避免两条轨互相抢带宽。");
            Tip(chkBinary, "二进制合并：用二进制拼接代替 ffmpeg 混流，速度快、不重新编码；只适合分片本身已是完整 ts/m4s 的源，音画不同步的源别用。");
            Tip(chkSkipMerge, "跳过合并：不合成单文件，只保留分离的轨文件（--skip-merge）。与「混流为 MP4」互斥。");
            Tip(chkSkipDownload, "仅解析不下载：只解析出有哪些轨道和分片，不实际写文件（--skip-download），用来探明链接结构；与混流、边下边转互斥。");
            Tip(chkAppend, "追加 URL 参数：把输入链接 ? 后面的参数附加到每个分片请求上（--append-url-params）；部分站点不带这些参数会直接 403。");
            Tip(chkDel, "完成后删分片：任务成功后删除临时分片与临时目录（--del-after-done），只留成品，省磁盘。");
            Tip(chkCheck, "校验分片数：核对实际下载的分片数与预期数量（--check-segments-count），不一致就报错，避免拿到残缺视频。");
            Tip(chkAuto, "自动最高画质：显式要求引擎在全部视频轨里取最佳（-sv for=best）、音频轨取最佳（-sa for=best），并自动确认选轨（--auto-select）。关掉后引擎会停下来等人工选轨，而 GUI 没有输入通道会一直挂住，除非在附加参数里自己写选轨规则。");
        }

        private void Tip(CheckBox c, string text)
        {
            tipText[c] = text;
            tips.SetToolTip(c, text);
        }

        private static string MaskText(string text)
        {
            return Mask.Secret(text);
        }

        /// <summary>卡片内的左标签，统一宽度与垂直居中，避免每处手写 AutoSize 后对不齐。</summary>
        private Label NewLabel(string text, int px, int py, int w)
        {
            Label lb = new Label();
            lb.Text = text;
            lb.AutoSize = false;
            lb.TextAlign = ContentAlignment.MiddleLeft;
            lb.Location = new Point(S(px), S(py));
            lb.Size = new Size(S(px + w) - S(px), S(20));
            lb.ForeColor = Theme.Text;
            Controls.Add(lb);
            return lb;
        }

        /// <summary>只读的敏感输入框：默认显示打码值，勾"显示明文"后才可编辑。</summary>
        private TextBox NewSecretField(int px, int py, int w, string plain)
        {
            TextBox t = Theme.NewField(this, px, py, w, 26);
            t.Font = Theme.Mono(9F);
            t.Text = MaskKey(plain);
            t.ReadOnly = true;
            t.ForeColor = Theme.Dim;
            // 注意：Theme.NewField 已把 TextBox 放进 FieldFrame，这里不能再 Controls.Add(t)，
            // 否则会把它从 FieldFrame 抢到窗体上、坐标退回 (11,3)，视觉上完全消失（D7）
            return t;
        }

        private void OnTogglePlain()
        {
            if (chkPlain.Checked)
            {
                txtProxy.ReadOnly = false;
                txtProxy.ForeColor = Theme.Text;
                txtProxy.Text = plainProxy;
                txtHeaders.ReadOnly = false;
                txtHeaders.ForeColor = Theme.Text;
                txtHeaders.Text = plainHeaders;
                txtKey.ReadOnly = false;
                txtKey.ForeColor = Theme.Text;
                txtKey.Text = plainKey;
                txtIv.ReadOnly = false;
                txtIv.ForeColor = Theme.Text;
                txtIv.Text = plainIv;
            }
            else
            {
                txtProxy.ReadOnly = true;
                txtProxy.ForeColor = Theme.Dim;
                txtProxy.Text = Mask.Secret(plainProxy);
                txtHeaders.ReadOnly = true;
                txtHeaders.ForeColor = Theme.Dim;
                txtHeaders.Text = Mask.HeaderBlock(plainHeaders);
                txtKey.ReadOnly = true;
                txtKey.ForeColor = Theme.Dim;
                txtKey.Text = MaskKey(plainKey);
                txtIv.ReadOnly = true;
                txtIv.ForeColor = Theme.Dim;
                txtIv.Text = MaskKey(plainIv);
            }
            UpdateKeyShape();
        }

        /// <summary>
        /// KEY / IV 打码：留头尾各 4 位便于确认"填的是哪一条"，中间全星号。
        /// 不能用 Mask.Secret——它只认 Header 和 URL 查询串，裸 HEX 串原样返回。
        /// </summary>
        private static string MaskKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Trim();
            if (s.Length <= 8) return new string('*', 8);
            return s.Substring(0, 4) + "********" + s.Substring(s.Length - 4);
        }

        /// <summary>KEY 形态即时反馈：填进来的到底是不是 HEX/Base64，一眼能看出来，不用等引擎报错。</summary>
        private void UpdateKeyShape()
        {
            if (lbKeyShape == null || txtKey == null) return;
            string v = (chkPlain != null && chkPlain.Checked) ? txtKey.Text.Trim() : plainKey;
            if (string.IsNullOrEmpty(v))
            {
                lbKeyShape.Text = "可填 HEX 或 Base64，建议与 IV 一起填";
                lbKeyShape.ForeColor = Theme.Dim;
                return;
            }
            string shape = ArgBuilder.KeyShape(v);
            if (shape == "HEX")
            {
                lbKeyShape.Text = "已识别为 HEX（" + (v.Length / 2).ToString() + " 字节）";
                lbKeyShape.ForeColor = Theme.Green;
            }
            else if (shape == "BASE64")
            {
                lbKeyShape.Text = "已识别为 Base64";
                lbKeyShape.ForeColor = Theme.Green;
            }
            else
            {
                lbKeyShape.Text = "无法识别：既不是 HEX 也不是 Base64，引擎会报错";
                lbKeyShape.ForeColor = Theme.Orange;
            }
        }

        private CheckBox NewToggle(Control parent, string text, int x, int y)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Tag = text;   // 原文留底：互斥时在原文字后加后缀，解除后按原文还原
            c.AutoSize = false;
            // 卡片内的开关也要走 S()：早先这里写裸像素，高 DPI 下卡片放大了、开关没放大，
            // 100% 与 150% 的疏密完全不是一回事（D7）
            c.Location = new Point(S(x), S(y));
            c.Size = new Size(S(202), S(24));
            Theme.StyleToggle(c);
            // 锁定项不置灰也不置 Enabled=false，点击后立刻回滚并说明原因
            c.CheckedChanged += delegate (object s, EventArgs e)
            {
                CheckBox box = (CheckBox)s;
                string why;
                if (box.Checked && lockReason.TryGetValue(box, out why))
                {
                    box.Checked = false;
                    if (tips != null)
                    {
                        tips.Show("与「" + why + "」互斥，先取消它才能勾这一项",
                            box, 0, box.Height + 4, 2600);
                    }
                    return;
                }
                ApplySkipRules();
            };
            parent.Controls.Add(c);
            return c;
        }

        private int AddText(string label, int x, int y, int labelW, string value, string hint, out TextBox box)
        {
            Label lb = new Label();
            lb.Text = label;
            lb.AutoSize = false;
            // 宽度用「右端点 - 左端点」的缩放差，而不是 S(宽度)：
            // S(14)+S(76)=18+95=113 会越过输入框左边界 S(90)=112，1.25 倍下压住输入框 1px
            lb.Location = new Point(S(x), S(y + 3));
            lb.Size = new Size(S(x + labelW) - S(x), S(20));
            lb.ForeColor = Theme.Text;
            Controls.Add(lb);

            box = Theme.NewField(this, x + labelW, y, 366, 26);
            box.Text = value;

            if (!string.IsNullOrEmpty(hint))
            {
                Label h = new Label();
                h.Text = hint;
                h.AutoSize = true;
                h.ForeColor = Theme.Dim;
                h.Font = Theme.Ui(8F);
                h.Location = new Point(S(x + labelW), S(y + 26));
                Controls.Add(h);
                return y + 44;   // 输入框 26 + 说明行 18，正好贴合，不留空也不叠
            }
            return y + 32;
        }

        private void Accept()
        {
            Smart = chkSmart.Checked;
            Mux = chkMux.Checked;
            Concurrent = chkConcurrent.Checked;
            Binary = chkBinary.Checked;
            SkipMerge = chkSkipMerge.Checked;
            SkipDownload = chkSkipDownload.Checked;
            AppendUrlParams = chkAppend.Checked;
            DelAfterDone = chkDel.Checked;
            CheckSegments = chkCheck.Checked;
            AutoSelect = chkAuto.Checked;
            UseSystemProxy = chkSysProxy.Checked;

            if (chkPlain.Checked)
            {
                Proxy = txtProxy.Text.Trim();
                Headers = txtHeaders.Text;
            }
            else
            {
                Proxy = plainProxy;
                Headers = plainHeaders;
            }
            ExtraArgs = txtExtra.Text.Trim();

            // 解密：方式固定 AES-128（不再是下拉项，直接取常量）；KEY/IV 只在勾了"显示明文"时才认输入框内容
            // （未勾选时输入框里是打码串，读出来会把星号当成密钥写进配置）
            HlsMethod = AppConfig.HlsMethodAes128;
            HlsKey = (chkPlain.Checked ? txtKey.Text : plainKey).Trim();
            HlsIv = (chkPlain.Checked ? txtIv.Text : plainIv).Trim();
            // 空/非法回落默认值，越界按统一边界夹紧，避免手滑把上限设成个位数后看不到日志
            int logLimit;
            if (!int.TryParse(numLogLimit.Text.Trim(), out logLimit)) logLimit = 200000;
            if (logLimit < AppConfig.LogLimitMin) logLimit = AppConfig.LogLimitMin;
            if (logLimit > AppConfig.LogLimitMax) logLimit = AppConfig.LogLimitMax;
            LogLimit = logLimit;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
