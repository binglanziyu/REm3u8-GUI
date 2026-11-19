// =====================================================================
//  UI\VariantForm.cs — 主清单清晰度选择
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Rem.Services;

namespace Rem.UI
{
    public sealed class VariantForm : Form
    {
        public int SelectedIndex = 0;
        public bool AutoBest = false;

        private readonly List<VariantInfo> variants;
        private ListBox list;

        public VariantForm(List<VariantInfo> items, string title)
        {
            variants = items;
            BuildUi(title);
        }

        private void BuildUi(string title)
        {
            Text = string.IsNullOrEmpty(title) ? "选择清晰度" : title;
            Icon = Theme.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9F);
            // 与主窗体同一套缩放口径：关掉窗体自动缩放，所有尺寸/坐标统一走 Theme.S()，
            // 否则 AutoScaleMode.Font 的整体缩放会和手工缩放叠加，高 DPI 下控件互相挤压（D19）
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Theme.S(500), Theme.S(300));

            Label lb = new Label();
            lb.Text = "该链接为主清单，共 " + variants.Count + " 条清晰度，选择要下载的一条：";
            lb.AutoSize = false;
            lb.Location = new Point(Theme.S(14), Theme.S(12));
            lb.Size = new Size(Theme.S(472), Theme.S(20));
            lb.ForeColor = Theme.Dim;
            Controls.Add(lb);

            list = new ListBox();
            list.Location = new Point(Theme.S(14), Theme.S(38));
            list.Size = new Size(Theme.S(472), Theme.S(190));
            list.BackColor = Theme.Field;
            list.ForeColor = Theme.Text;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.Font = Theme.Mono(9F);
            list.IntegralHeight = false;
            for (int i = 0; i < variants.Count; i++) list.Items.Add(variants[i].Label);
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            list.DoubleClick += delegate (object s, EventArgs e) { Accept(); };
            Controls.Add(list);

            CheckBox chk = new CheckBox();
            chk.Text = "交给引擎自动选最高画质（推荐）";
            chk.AutoSize = false;
            chk.Location = new Point(Theme.S(14), Theme.S(236));
            chk.Size = new Size(Theme.S(300), Theme.S(26));
            Theme.StyleToggle(chk);
            Controls.Add(chk);

            Button ok = new Button();
            ok.Text = "开始下载";
            ok.Location = new Point(Theme.S(320), Theme.S(258));
            ok.Size = new Size(Theme.S(84), Theme.S(28));
            Theme.StyleButton(ok, true);
            ok.Click += delegate (object s, EventArgs e) { AutoBest = chk.Checked; Accept(); };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Location = new Point(Theme.S(410), Theme.S(258));
            cancel.Size = new Size(Theme.S(76), Theme.S(28));
            Theme.StyleButton(cancel, false);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void Accept()
        {
            SelectedIndex = list.SelectedIndex < 0 ? 0 : list.SelectedIndex;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
