// =====================================================================
//  UI\FieldFrame.cs — 输入框美化：圆角描边容器 + 行首 GDI 小图标
//  系统 TextBox 的边框是 WinForms 硬绘的方角灰线，无法直接改；
//  这里把 TextBox 的边框去掉塞进自绘 Panel，描边、圆角、聚焦高亮全部自管。
//  图标同样纯 GDI 绘制，不引入任何图片资源（保持单 exe、体积不变）。
// =====================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Rem.UI
{
    /// <summary>圆角输入框容器：1px 圆角描边，聚焦时描边转亮。</summary>
    public sealed class FieldFrame : Panel
    {
        private readonly TextBox inner;
        private bool focused;
        private Label hintLabel;     // 浮动说明：空内容时浮在原行首图标的位置上，用子控件承载才能盖在 TextBox 之上

        public FieldFrame(TextBox box)
        {
            inner = box;
            BackColor = Theme.Field;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint, true);
            Controls.Add(inner);
            inner.GotFocus += delegate (object s, EventArgs e) { focused = true; Sync(); };
            inner.LostFocus += delegate (object s, EventArgs e) { focused = false; Sync(); };
        }

        /// <summary>设置浮动说明文字（空内容时浮在原图标位置，聚焦或输入后自动让位）。</summary>
        public void SetHint(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (hintLabel == null)
            {
                hintLabel = new Label();
                hintLabel.AutoSize = true;
                hintLabel.BackColor = Theme.Field;
                hintLabel.ForeColor = Theme.Dim;
                hintLabel.Font = inner.Font;
                hintLabel.Text = text;
                hintLabel.Cursor = Cursors.IBeam;
                hintLabel.Click += delegate (object s, EventArgs e) { inner.Focus(); };
                Controls.Add(hintLabel);
                Controls.SetChildIndex(hintLabel, 0);   // 置顶，避免被 TextBox 背景擦掉
            }
            else
            {
                hintLabel.Text = text;
            }
            inner.TextChanged -= OnInnerTextChanged;
            inner.TextChanged += OnInnerTextChanged;
            Sync();
        }


        private void OnInnerTextChanged(object sender, EventArgs e)
        {
            Sync();
        }

        private void Sync()
        {
            // 只在"空内容且未聚焦"时显示浮动说明；一旦点进来就把位置让回图标，
            // 光标落在图标右侧，输入的文字与说明不会互相压叠
            bool showHint = hintLabel != null && inner.TextLength == 0 && !focused;
            if (hintLabel != null)
            {
                hintLabel.Visible = showHint;
                if (showHint)
                {
                    // 说明文字贴框内侧右边显示（图标已在框外，左侧不再占用）
                    int pad = Theme.S(11);
                    hintLabel.AutoSize = false;
                    hintLabel.Left = pad;
                    hintLabel.Width = Math.Max(0, Width - pad * 2);
                    hintLabel.Top = inner.Top;
                    hintLabel.Height = inner.Height;
                    hintLabel.TextAlign = ContentAlignment.MiddleRight;
                }
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int dia = Theme.S(Theme.FieldRadius);
            if (dia < 2) dia = 2;
            int cap = Math.Min(Width, Height) / 2;
            if (dia > cap) dia = cap;
            using (GraphicsPath path = Rounded(r, dia))
            using (Pen pen = new Pen(focused ? Theme.AccentEdge : Theme.Border))
            {
                g.DrawPath(pen, path);
            }
        }

        // 点框内空白处也要能落光标，否则框比文字大时会"点不进去"
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            inner.Focus();
        }

        /// <summary>构造圆角矩形路径（半径按像素直径传入）。</summary>
        public static GraphicsPath Rounded(Rectangle r, int diameter)
        {
            GraphicsPath p = new GraphicsPath();
            if (diameter <= 1)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, diameter, diameter, 180, 90);
            p.AddArc(r.Right - diameter, r.Y, diameter, diameter, 270, 90);
            p.AddArc(r.Right - diameter, r.Bottom - diameter, diameter, diameter, 0, 90);
            p.AddArc(r.X, r.Bottom - diameter, diameter, diameter, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>行首小图标：链接 / 文件 / 文件夹，纯 GDI 线条绘制。</summary>
    public sealed class Glyph : Panel
    {
        public const int Link = 0;
        public const int File = 1;
        public const int Folder = 2;

        private readonly int kind;

        public Glyph(int kind)
        {
            this.kind = kind;
            BackColor = Color.Transparent;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 以 20x20 为基准，按实际尺寸等比缩放，DPI 变化时不变形
            float k = Math.Min(Width, Height) / 20F;
            g.ScaleTransform(k, k);

            using (Pen pen = new Pen(Theme.AccentLight, 1.6F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                if (kind == Link)
                {
                    // 两个交叠圆环 = 链条
                    g.DrawEllipse(pen, 1.5F, 5F, 10F, 10F);
                    g.DrawEllipse(pen, 8.5F, 5F, 10F, 10F);
                }
                else if (kind == File)
                {
                    using (GraphicsPath p = FieldFrame.Rounded(new Rectangle(3, 1, 14, 18), 6))
                    {
                        g.DrawPath(pen, p);
                    }
                    g.DrawLine(pen, 6.5F, 7F, 13.5F, 7F);
                    g.DrawLine(pen, 6.5F, 10.5F, 13.5F, 10.5F);
                    g.DrawLine(pen, 6.5F, 14F, 11F, 14F);
                }
                else
                {
                    // 文件夹：底部矩形 + 左上角标
                    g.DrawLines(pen, new PointF[]
                    {
                        new PointF(1.5F, 17F), new PointF(1.5F, 5.5F), new PointF(7.5F, 5.5F),
                        new PointF(9.5F, 3F), new PointF(18.5F, 3F), new PointF(18.5F, 17F),
                        new PointF(1.5F, 17F)
                    });
                    g.DrawLine(pen, 1.5F, 8F, 18.5F, 8F);
                }
            }
        }
    }
}
