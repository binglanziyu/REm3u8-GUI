// =====================================================================
//  UI\AdvForm.Layout.cs — 高级设置窗的布局构建与控件工厂分区（partial 拆分）
//  纯代码搬移，逻辑与行为与拆分前完全一致（重构方案第 2 章）
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
    public sealed partial class AdvForm : Form
    {

        /// <summary>下载选项卡片：智能预检 / 混流 / 边下边转 / 二进制合并 / 跳过合并 / 仅解析不下载等全部开关。</summary>
        private int BuildOptionCard(int y)
        {
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
            lbLegend.Text = "绿底 = 已开启　　暗底 = 已关闭　　暗橙底 = 互斥　　亮绿 = 引擎强制开启（点不掉）";
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

            return y;
        }

        /// <summary>自动补片上限：坏片补片允许的最大分片缺失率。</summary>
        private int BuildPatchCard(int y)
        {
            // ---------- 自动补片上限 ----------
            // 与主界面「限速」同排版：标签 + 数字框 + 行内单位与说明，不额外占说明行高度（D7 行距预算）
            NewLabel("补片上限", x, y + 3, labelW);
            numPatch = Theme.NewField(this, x + labelW, y, 60, 26);
            numPatch.Text = (PatchPercent >= AppConfig.PatchPercentMin && PatchPercent <= AppConfig.PatchPercentMax
                ? PatchPercent : 50).ToString();
            numPatch.MaxLength = 3;
            numPatch.KeyPress += delegate (object s, KeyPressEventArgs e)
            {
                if (char.IsControl(e.KeyChar)) return;
                if (!char.IsDigit(e.KeyChar)) e.Handled = true;
            };

            Label lbPatchUnit = new Label();
            lbPatchUnit.Text = "%";
            lbPatchUnit.AutoSize = true;
            lbPatchUnit.ForeColor = Theme.Dim;
            lbPatchUnit.Location = new Point(S(x + labelW + 64), S(y + 5));
            Controls.Add(lbPatchUnit);

            Label lbPatchHint = new Label();
            lbPatchHint.Text = "缺失率超过该比例才放弃补片（1–70，默认 50）";
            lbPatchHint.AutoSize = true;
            lbPatchHint.ForeColor = Theme.Dim;
            lbPatchHint.Font = Theme.Ui(8F);
            lbPatchHint.Location = new Point(S(x + labelW + 82), S(y + 5));
            Controls.Add(lbPatchHint);

            tips.SetToolTip(numPatch, "自动补片上限：坏片补片允许的最大分片缺失率，"
                + "缺失率不超过该比例就用相邻分片顶替补齐，超过就整组放弃并如实报错。范围 1–70，默认 50。");
            y += step;

            return y;
        }

        /// <summary>解密卡片：固定 AES-128，只保留手动填 KEY / IV。</summary>
        private int BuildCryptoCard(int y)
        {
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
            txtKey.TextChanged += delegate (object s, EventArgs e)
            {
                if (chkPlain != null && chkPlain.Checked) plainKey = txtKey.Text;
                UpdateKeyShape();
            };

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
            txtIv.TextChanged += delegate (object s, EventArgs e)
            { if (chkPlain != null && chkPlain.Checked) plainIv = txtIv.Text; };
            tips.SetToolTip(txtIv, "IV：HEX 或 Base64。多数源的 IV 已写在 KEY 文件或播放列表里，这里留空即可；"
                + "只有明确要求单独给 IV 时才需要填（对应引擎参数 --custom-hls-iv）。");
            y += step;

            return y;
        }

        /// <summary>网络与扩展卡片：代理、系统代理、请求头、附加参数。</summary>
        private int BuildNetworkCard(int y)
        {
            // ---------- 网络 ----------
            TextBox proxyBox;
            y = AddText("代理", x, y, labelW, Mask.Secret(MaskText(plainProxy)),
                        "http://127.0.0.1:7890 或 socks5://127.0.0.1:1080（socks 跳过预检）", out proxyBox);
            txtProxy = proxyBox;
            txtProxy.ReadOnly = true;
            txtProxy.ForeColor = Theme.Dim;
            // 明文态编辑即时回写：取消勾选后 Accept 读的是 plain*，不同步会把改动静默丢掉
            txtProxy.TextChanged += delegate (object s, EventArgs e)
            { if (chkPlain != null && chkPlain.Checked) plainProxy = txtProxy.Text; };

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
            txtHeaders.TextChanged += delegate (object s, EventArgs e)
            { if (chkPlain != null && chkPlain.Checked) plainHeaders = txtHeaders.Text; };
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

            return y;
        }

        /// <summary>界面卡片（日志上限）与底部按钮，返回底部按钮所在行的 y。</summary>
        private int BuildFooterCard(int y)
        {
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

            // ---------- 赞助提醒频率（二选一，占按钮行左侧空档，不额外长高）----------
            Label lbSp = new Label();
            lbSp.Text = "赞助提醒";
            lbSp.AutoSize = false;
            lbSp.TextAlign = ContentAlignment.MiddleLeft;
            lbSp.Location = new Point(S(x), S(y + 9));
            lbSp.Size = new Size(S(62), S(22));
            lbSp.ForeColor = Theme.Text;
            Controls.Add(lbSp);

            // 两个开关互斥：勾一个自动放开另一个；不允许两个都关掉（否则周期无从判断）
            chkFreqWeek = NewToggle(this, "每周一次", x + 62, y + 8);
            chkFreqWeek.Size = new Size(S(96), S(26));
            chkFreqDay = NewToggle(this, "每日一次", x + 158, y + 8);
            chkFreqDay.Size = new Size(S(96), S(26));
            // 先定初值再挂互斥：反过来会让初始化时的「取消勾选」被自己拉回去
            bool week = Sponsor.NormalizeFreq(SponsorFreq) == Sponsor.FreqWeek;
            chkFreqWeek.Checked = week;
            chkFreqDay.Checked = !week;
            chkFreqWeek.CheckedChanged += delegate (object s, EventArgs e)
            {
                if (chkFreqWeek.Checked) chkFreqDay.Checked = false;
                else if (!chkFreqDay.Checked) chkFreqWeek.Checked = true;
            };
            chkFreqDay.CheckedChanged += delegate (object s, EventArgs e)
            {
                if (chkFreqDay.Checked) chkFreqWeek.Checked = false;
                else if (!chkFreqWeek.Checked) chkFreqDay.Checked = true;
            };

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

            return y;
        }

        /// <summary>
        /// 设计值 → 像素：唯一缩放入口就是 Theme.S（与主窗体 MainForm 同一口径）。
        /// 本窗体内不再出现裸像素，也不另立第二套换算（D7）。
        /// </summary>
        private static int S(int v)
        {
            return Theme.S(v);
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
            Tip(chkBinary, "二进制合并：用二进制拼接代替 ffmpeg 混流，速度快、不重新编码；只适合分片本身已是完整 ts/m4s 的源，音画不同步的源别用。\r\n\r\n注意：勾了「混流为 MP4」时，引擎自己就会打开二进制合并，此时本项会被标成「强制」且点不掉，要关记得先取消混流。");
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
                // 被引擎强制开启的项不允许取消：勾回去并说明要先关掉谁
                if (!box.Checked && forceReason.TryGetValue(box, out why))
                {
                    box.Checked = true;
                    if (tips != null)
                    {
                        tips.Show("已被强制开启：引擎在「" + why + "」时一定会做二进制合并，先取消它才能关",
                            box, 0, box.Height + 4, 3200);
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
    }
}
