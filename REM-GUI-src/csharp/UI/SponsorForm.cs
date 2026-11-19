// =====================================================================
//  UI\SponsorForm.cs — 「赞助一下~」窗口
//  两张收款码以清单资源内嵌在 exe 里（sponsor_wechat.png / sponsor_alipay.jpg），
//  运行时不读任何外部文件，移动 exe 到别处也能正常显示。
//  资源缺失时退化为文字提示，绝不抛异常（缺图不该让窗口打不开）。
// =====================================================================
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Rem.Core;

namespace Rem.UI
{
    public sealed class SponsorForm : Form
    {
        private const string ResWechat = "sponsor_wechat.png";
        private const string ResAlipay = "sponsor_alipay.jpg";

        public SponsorForm()
        {
            BuildUi();
        }

        private static int S(int v)
        {
            return Theme.S(v);
        }

        private void BuildUi()
        {
            Text = "赞助一下~";
            Icon = Theme.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9F);
            // 与主窗体同一套缩放口径：关掉自动缩放，所有尺寸走 Theme.S()
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(S(600), S(516));

            Label lbTitle = new Label();
            lbTitle.Text = "赞助一下~";
            lbTitle.AutoSize = false;
            lbTitle.TextAlign = ContentAlignment.MiddleLeft;
            lbTitle.ForeColor = Theme.AccentLight;
            lbTitle.Font = Theme.UiBold(12F);
            lbTitle.Location = new Point(S(14), S(12));
            lbTitle.Size = new Size(S(572), S(26));
            Controls.Add(lbTitle);

            Label lbDesc = new Label();
            lbDesc.Text = "REM-GUI 一直免费、无广告，也不采集任何使用数据。如果它帮你省了点时间，欢迎扫码支持一下~";
            lbDesc.AutoSize = false;
            lbDesc.TextAlign = ContentAlignment.MiddleLeft;
            lbDesc.ForeColor = Theme.Dim;
            lbDesc.Font = Theme.Ui(8.5F);
            lbDesc.Location = new Point(S(14), S(42));
            lbDesc.Size = new Size(S(572), S(20));
            Controls.Add(lbDesc);

            BuildCodeCard("微信", ResWechat, 14);
            BuildCodeCard("支付宝", ResAlipay, 312);

            Label lbHint = new Label();
            lbHint.Text = "提醒频率可在「高级设置」里改为每日一次 / 每周一次；点关闭即可继续使用，不影响任何功能。";
            lbHint.AutoSize = false;
            lbHint.TextAlign = ContentAlignment.MiddleLeft;
            lbHint.ForeColor = Theme.Dim;
            lbHint.Font = Theme.Ui(8F);
            lbHint.Location = new Point(S(14), S(430));
            lbHint.Size = new Size(S(572), S(20));
            Controls.Add(lbHint);

            Button close = new Button();
            close.Text = "关闭";
            close.Location = new Point(S(250), S(456));
            close.Size = new Size(S(100), S(28));
            Theme.StyleButton(close, true);
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
        }

        /// <summary>一张收款码卡片：标题 + 居中显示的图片（等比缩放，不裁切）。</summary>
        private void BuildCodeCard(string caption, string resName, int px)
        {
            Panel card = Theme.NewCard();
            card.Location = new Point(S(px), S(70));
            card.Size = new Size(S(274), S(352));
            Controls.Add(card);

            Label lbCap = new Label();
            lbCap.Text = caption;
            Theme.PaintCardTitle(lbCap);
            lbCap.Location = new Point(S(10), S(8));
            card.Controls.Add(lbCap);

            string hint;
            Image img = LoadEmbedded(resName, out hint);
            if (img == null)
            {
                Label lbMiss = new Label();
                lbMiss.Text = "收款码资源缺失";
                lbMiss.AutoSize = false;
                lbMiss.TextAlign = ContentAlignment.MiddleCenter;
                lbMiss.ForeColor = Theme.Red;
                lbMiss.Font = Theme.Ui(9F);
                lbMiss.Location = new Point(S(10), S(140));
                lbMiss.Size = new Size(S(254), S(20));
                card.Controls.Add(lbMiss);
                Rem.Core.Note.Swallow("SponsorForm/embed/" + resName, new Exception(hint));
                return;
            }

            PictureBox pic = new PictureBox();
            pic.Image = img;
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.BackColor = Theme.Field;
            pic.BorderStyle = BorderStyle.FixedSingle;
            pic.Location = new Point(S(10), S(32));
            pic.Size = new Size(S(254), S(308));
            card.Controls.Add(pic);
        }

        /// <summary>
        /// 从清单资源读出图片：先按流解码再克隆成独立 Bitmap，
        /// 避免 Image.FromStream 依赖流生命周期（流一关图就废）。
        /// </summary>
        private static Image LoadEmbedded(string resName, out string hint)
        {
            hint = "";
            try
            {
                Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName);
                if (s == null)
                {
                    hint = "内嵌资源不存在：" + resName;
                    return null;
                }
                using (s)
                {
                    using (Image raw = Image.FromStream(s))
                    {
                        return new Bitmap(raw);
                    }
                }
            }
            catch (Exception ex)
            {
                hint = ex.Message;
                return null;
            }
        }
    }
}
