// =====================================================================
//  UI\ThinScroll.cs — 自绘细滚动条：替换系统原生灰色滚动条
//  用法：先让目标控件隐藏原生条（Native.HideScrollBar），再挂本控件
//  ThinScroll 贴在目标控件右缘，跟随滚轮 / 拖动 / 键盘同步
// =====================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

using Rem.Core;
using Rem.Interop;

namespace Rem.UI
{
    /// <summary>自绘细滚动条，替代 TextBox / RichTextBox 的系统原生滚动条。</summary>
    public sealed class ThinScroll : Control
    {
        private const int BarWidth = 9;

        private readonly Control target;
        private readonly Timer poll;

        private int total = 1;
        private int first = 0;
        private int visible = 1;

        private bool hotThumb = false;
        private bool dragging = false;
        private int dragStartY = 0;
        private int dragStartFirst = 0;
        private int dragStartThumbH = 0;

        public ThinScroll(Control target, Control parent)
        {
            if (target == null) { throw new ArgumentNullException("target"); }
            this.target = target;

            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = false;

            Width = Theme.S(BarWidth);
            Height = target.Height;

            if (parent != null)
            {
                parent.Controls.Add(this);
                parent.Controls.SetChildIndex(this, 0);   // 置顶，压住目标控件右缘
            }

            ReAlign();

            poll = new Timer();
            poll.Interval = 80;
            poll.Tick += delegate (object s, EventArgs e) { if (Visible) { Sync(); } };
            poll.Start();

            target.HandleCreated += delegate (object s, EventArgs e) { Attach(); };
            target.TextChanged += delegate (object s, EventArgs e) { Sync(); };
            target.SizeChanged += delegate (object s, EventArgs e) { ReAlign(); Sync(); };
            target.LocationChanged += delegate (object s, EventArgs e) { ReAlign(); };
            target.FontChanged += delegate (object s, EventArgs e) { Sync(); };
            target.KeyUp += delegate (object s, KeyEventArgs e) { Sync(); };

            if (target.IsHandleCreated) { Attach(); }
        }

        /// <summary>藏掉原生滚动条并立刻同步一次。</summary>
        public void Attach()
        {
            try
            {
                Native.HideScrollBar(target.Handle);
                Sync();
                Invalidate();
            }
            catch (Exception __ex) { Note.Swallow("ThinScroll.cs:Attach", __ex); }
        }

        private void ReAlign()
        {
            try
            {
                if (Parent == null || target.Parent == null) { return; }
                if (target.Parent != Parent) { Parent = target.Parent; }
                int x = target.Right - Width;
                int y = target.Top;
                if (Location.X != x || Location.Y != y) { Location = new Point(x, y); }
                if (Height != target.Height) { Height = target.Height; }
            }
            catch (Exception __ex) { Note.Swallow("ThinScroll.cs:ReAlign", __ex); }
        }

        // ---------------- 滚动位置同步 ----------------

        private int LineHeight()
        {
            int h = 16;
            try
            {
                h = TextRenderer.MeasureText("Xg", target.Font).Height;
            }
            catch (Exception __ex) { Note.Swallow("ThinScroll.cs:LineHeight", __ex); }
            if (h < 6) { h = 6; }
            return h;
        }

        private void Sync()
        {
            IntPtr h = target.Handle;
            if (h == IntPtr.Zero) { return; }

            // 原生条被重建（换字体 / 重设 ScrollBars）时自动再藏一次
            if (Native.HasVertScrollStyle(h)) { Native.HideScrollBar(h); }

            int t = Native.LineCount(h);
            if (t < 1) { t = 1; }
            int f = Native.FirstVisibleLine(h);
            if (f < 0) { f = 0; }
            int lh = LineHeight();
            int v = target.ClientSize.Height / lh;
            if (v < 1) { v = 1; }

            if (t != total || f != first || v != visible)
            {
                total = t;
                first = f;
                visible = v;
                Invalidate();
            }
        }

        // ---------------- 几何 ----------------

        private void ThumbMetrics(out int thumbY, out int thumbH)
        {
            int pad = Theme.S(1);
            int trackH = Height - pad * 2;
            if (trackH < 1) { trackH = 1; }

            long hh = (long)trackH * visible / (total < 1 ? 1 : total);
            if (hh < Theme.S(18)) { hh = Theme.S(18); }
            if (hh > trackH) { hh = trackH; }
            thumbH = (int)hh;

            int span = total - visible;
            thumbY = pad;
            if (span > 0 && trackH > thumbH)
            {
                int f = first > span ? span : first;
                thumbY += (int)((long)(trackH - thumbH) * f / span);
            }
        }

        private bool HitThumb(int y)
        {
            int ty, th;
            ThumbMetrics(out ty, out th);
            return y >= ty && y <= ty + th;
        }

        // ---------------- 绘制 ----------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int pad = Theme.S(1);
            int w = Width - pad * 2;
            if (w < 2) { return; }

            int ty, th;
            ThumbMetrics(out ty, out th);
            if (th < 2) { return; }

            bool show = total > visible;
            Color c;
            if (dragging || hotThumb) { c = Theme.Accent; }
            else if (show) { c = Theme.BorderStrong; }
            else { return; }   // 内容没溢出就不画

            int r = Theme.S(4);
            using (GraphicsPath path = RoundRect(new Rectangle(pad, ty, w, th), r))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillPath(b, path);
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius < 1) { radius = 1; }
            int d = radius * 2;
            if (d > r.Height) { d = r.Height; }
            if (d > r.Width) { d = r.Width; }
            if (d < 2) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------------- 交互 ----------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Sync();
            int ty, th;
            ThumbMetrics(out ty, out th);

            if (e.Y >= ty && e.Y <= ty + th)
            {
                dragging = true;
                dragStartY = e.Y;
                dragStartFirst = first;
                dragStartThumbH = th;
                Capture = true;
            }
            else
            {
                Native.ScrollBy(target.Handle, e.Y < ty ? -visible : visible);
                Sync();
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            bool hot = HitThumb(e.Y);
            if (hot != hotThumb) { hotThumb = hot; Invalidate(); }

            if (!dragging) { return; }

            int pad = Theme.S(1);
            int trackH = Height - pad * 2;
            int range = trackH - dragStartThumbH;
            if (range < 1) { return; }
            int span = total - visible;
            if (span <= 0) { return; }

            int dy = e.Y - dragStartY;
            long want = dragStartFirst + (long)Math.Round((double)dy * span / range);
            if (want < 0) { want = 0; }
            if (want > span) { want = span; }
            if ((int)want != first)
            {
                Native.ScrollToLine(target.Handle, (int)want);
                Sync();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!dragging) { hotThumb = false; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            // 鼠标在滚动条上时滚轮要能滚内容：把焦点交给目标控件
            try
            {
                if (!dragging && target.CanFocus && !target.Focused) { target.Focus(); }
            }
            catch (Exception __ex) { Note.Swallow("ThinScroll.cs:OnMouseEnter", __ex); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int lines = e.Delta > 0 ? -3 : 3;
            Native.ScrollBy(target.Handle, lines);
            Sync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (poll != null) { poll.Stop(); poll.Dispose(); }
                }
                catch (Exception __ex) { Note.Swallow("ThinScroll.cs:Dispose", __ex); }
            }
            base.Dispose(disposing);
        }
    }
}
