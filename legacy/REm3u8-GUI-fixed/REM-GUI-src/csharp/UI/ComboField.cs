using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Rem.UI
{
    /// <summary>
    /// 自绘下拉框。原生 ComboBox 在 DropDownList 下坚持用系统白底方框，
    /// 深色主题里没法染，只能整个自己画：圆角、暗底、细描边，弹出列表同样暗色。
    /// </summary>
    public sealed class ComboField : Control
    {
        private readonly List<string> items = new List<string>();
        private int idx = -1;
        private bool hot;
        private bool open;
        private DropForm drop;
        private Form hookedForm;   // 下拉展开期间挂事件跟随的父窗（D13）

        public event EventHandler SelectedIndexChanged;

        public ComboField()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Field;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9.5F);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public List<string> Items { get { return items; } }

        public void AddItem(string s)
        {
            items.Add(s == null ? "" : s);
            Invalidate();
        }

        public int Count { get { return items.Count; } }

        public int SelectedIndex
        {
            get { return idx; }
            set
            {
                int v = value;
                if (v < -1) v = -1;
                if (v >= items.Count) v = items.Count - 1;
                if (v == idx) { Invalidate(); return; }
                idx = v;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public object SelectedItem
        {
            get { return (idx >= 0 && idx < items.Count) ? (object)items[idx] : null; }
            set
            {
                if (value == null) { SelectedIndex = -1; return; }
                int i = items.IndexOf(value.ToString());
                if (i >= 0) SelectedIndex = i;
            }
        }

        public override string Text
        {
            get { return (idx >= 0 && idx < items.Count) ? items[idx] : ""; }
            set { SelectedItem = value; }
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (d < 2) d = 2;
            if (d > r.Height) d = r.Height;
            if (d > r.Width) d = r.Width;
            int a = d;
            p.AddArc(r.X, r.Y, a, a, 180, 90);
            p.AddArc(r.Right - a, r.Y, a, a, 270, 90);
            p.AddArc(r.Right - a, r.Bottom - a, a, a, 0, 90);
            p.AddArc(r.X, r.Bottom - a, a, a, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            bool act = Focused || open;
            Color fill = open ? Theme.CardHover : (hot ? Theme.Card : Theme.Field);
            Color edge = act ? Theme.AccentEdge : (hot ? Theme.BorderStrong : Theme.Border);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Round(r, Theme.S(Theme.FieldRadius)))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(edge)) g.DrawPath(pen, p);
            }

            // 右侧折角箭头：两段折线比三角更贴合这套细线条风格
            int cx = Width - Theme.S(13);
            int cy = Height / 2 + Theme.S(1);
            int aw = Theme.S(4);
            float lw = Theme.S(1);
            if (lw < 1.3F) lw = 1.3F;
            using (Pen pen = new Pen(act ? Theme.AccentLight : Theme.Dim, lw))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new Point[] {
                    new Point(cx - aw, cy - aw / 2),
                    new Point(cx, cy + aw / 2),
                    new Point(cx + aw, cy - aw / 2) });
            }

            Rectangle tr = new Rectangle(Theme.S(10), 0, Width - Theme.S(26), Height);
            TextRenderer.DrawText(g, Text, Font, tr, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hot = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            Toggle();
            base.OnMouseDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Right)
            {
                if (open) MoveInDrop(1); else SelectedIndex = idx + 1;
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Left)
            {
                if (open) MoveInDrop(-1); else SelectedIndex = idx - 1;
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                Toggle();
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Escape && open)
            {
                CloseDrop();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private void MoveInDrop(int step)
        {
            if (drop == null) return;
            int n = drop.List.Items.Count;
            if (n == 0) return;
            int v = drop.List.SelectedIndex + step;
            if (v < 0) v = 0;
            if (v > n - 1) v = n - 1;
            drop.List.SelectedIndex = v;
        }

        private void Toggle()
        {
            if (open) CloseDrop();
            else OpenDrop();
        }

        private void OpenDrop()
        {
            if (items.Count == 0) return;
            if (drop == null) drop = new DropForm(this);

            drop.List.BeginUpdate();
            drop.List.Items.Clear();
            for (int i = 0; i < items.Count; i++) drop.List.Items.Add(items[i]);
            drop.List.EndUpdate();
            drop.List.SelectedIndex = idx;

            int rowH = drop.List.ItemHeight;
            int rows = Math.Min(items.Count, 7);
            int listW = Width;
            int listH = rows * rowH + Theme.S(2);
            drop.ClientSize = new Size(listW, listH);
            drop.List.Size = new Size(listW - 2, listH - 2);

            open = true;
            HookReposition(true);   // 展开期间跟随父窗/自身位置变化（D13）
            Reposition();
            Invalidate();
            drop.Show();
            drop.List.Focus();
        }

        /// <summary>按控件当前屏幕位置重算下拉层坐标（含底部/右侧防溢出）。</summary>
        private void Reposition()
        {
            if (drop == null || !open) return;
            int listW = drop.ClientSize.Width;
            int listH = drop.ClientSize.Height;
            Point p = PointToScreen(new Point(0, Height - 1));
            Rectangle scr = Screen.FromControl(this).WorkingArea;
            if (p.Y + listH > scr.Bottom) p.Y = PointToScreen(new Point(0, 0)).Y - listH + 1;
            if (p.X + listW > scr.Right) p.X = scr.Right - listW;
            if (p.X < scr.Left) p.X = scr.Left;
            if (p.Y < scr.Top) p.Y = scr.Top;
            if (drop.Location != p) drop.Location = p;
        }

        // 下拉层是独立无边框窗体，不会自动跟随父窗；展开期间挂上移动/缩放事件重定位，
        // 父窗最小化或失活则直接收起，避免列表悬空在旧坐标（D13）
        private void HookReposition(bool on)
        {
            if (on)
            {
                LocationChanged += OnOwnerLayoutChanged;
                SizeChanged += OnOwnerLayoutChanged;
                hookedForm = FindForm();
                if (hookedForm != null)
                {
                    hookedForm.Move += OnOwnerLayoutChanged;
                    hookedForm.Resize += OnOwnerLayoutChanged;
                }
            }
            else
            {
                LocationChanged -= OnOwnerLayoutChanged;
                SizeChanged -= OnOwnerLayoutChanged;
                if (hookedForm != null)
                {
                    hookedForm.Move -= OnOwnerLayoutChanged;
                    hookedForm.Resize -= OnOwnerLayoutChanged;
                    hookedForm = null;
                }
            }
        }

        private void OnOwnerLayoutChanged(object sender, EventArgs e)
        {
            if (!open) return;
            Form f = FindForm();
            if (f == null || f.WindowState == FormWindowState.Minimized || !Visible)
            {
                CloseDrop();
                return;
            }
            Reposition();
        }

        private void CloseDrop()
        {
            HookReposition(false);
            if (drop != null && drop.Visible) drop.Hide();
            open = false;
            Invalidate();
            Focus();
        }

        internal void CommitFromDrop(int i)
        {
            SelectedIndex = i;
            CloseDrop();
        }

        internal void DropClosed()
        {
            open = false;
            Invalidate();
        }

        /// <summary>下拉弹层：无边框窗体 + 暗色自绘 ListBox，躲开系统 ComboBox 的白底。</summary>
        private sealed class DropForm : Form
        {
            public readonly ListBox List;
            private readonly ComboField owner;

            public DropForm(ComboField ownerField)
            {
                owner = ownerField;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Theme.Border;
                Padding = new Padding(1);

                List = new ListBox();
                List.BorderStyle = BorderStyle.None;
                List.DrawMode = DrawMode.OwnerDrawFixed;
                List.BackColor = Theme.Card;
                List.ForeColor = Theme.Text;
                List.Font = Theme.Ui(9.5F);
                List.ItemHeight = Theme.S(22);
                List.IntegralHeight = false;
                List.Dock = DockStyle.Fill;
                Controls.Add(List);

                List.DrawItem += OnDrawItem;
                List.MouseUp += OnListMouseUp;
                List.KeyDown += OnListKeyDown;
                List.MouseMove += OnListMouseMove;
                Deactivate += delegate (object s, EventArgs e) { Close(); };
            }

            private void OnDrawItem(object sender, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                Graphics g = e.Graphics;
                bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                Color bg = sel ? Theme.CardHover : Theme.Card;
                using (SolidBrush b = new SolidBrush(bg))
                    g.FillRectangle(b, e.Bounds);
                if (sel)
                {
                    using (Pen pen = new Pen(Theme.AccentEdge))
                        g.DrawLine(pen, e.Bounds.Left, e.Bounds.Top, e.Bounds.Left, e.Bounds.Bottom - 1);
                }
                Rectangle tr = new Rectangle(e.Bounds.Left + Theme.S(9), e.Bounds.Top,
                                             e.Bounds.Width - Theme.S(14), e.Bounds.Height);
                TextRenderer.DrawText(g, List.Items[e.Index].ToString(), List.Font, tr,
                    sel ? Theme.Text : Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            private void OnListMouseUp(object sender, MouseEventArgs e)
            {
                int i = List.IndexFromPoint(e.Location);
                if (i >= 0) owner.CommitFromDrop(i);
                else owner.DropClosed();
            }

            private void OnListMouseMove(object sender, MouseEventArgs e)
            {
                int i = List.IndexFromPoint(e.Location);
                if (i >= 0 && i != List.SelectedIndex) List.SelectedIndex = i;
            }

            private void OnListKeyDown(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    int i = List.SelectedIndex;
                    if (i >= 0) owner.CommitFromDrop(i);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                base.OnFormClosed(e);
                owner.DropClosed();
            }
        }
    }
}
