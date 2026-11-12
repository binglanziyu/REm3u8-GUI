// =====================================================================
//  UI\Theme.cs — 暗色主题：配色、字体、控件着色、卡片边框
//  配色基线：Pine Green 单色系（#042509 → #BCD5AC 五级明度阶梯）
//  主色 = PINE GREEN #094217；亮端 = JADED LIME #BCD5AC 派生，只用于前景强调
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Rem.UI
{
    public static class Theme
    {
        // ---------- 代码内自绘的基础件 ----------

        /// <summary>
        /// 程序图标：标题栏 / 任务栏 / Alt-Tab 都用它。
        /// 显式从嵌入资源取，不靠"窗口跟随 exe 图标"的默认行为——
        /// Windows 会把 exe 的旧图标缓存住，改了 exe 图标后窗口仍显示老图标。
        /// </summary>
        public static Icon AppIcon()
        {
            try
            {
                Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
                if (s != null)
                {
                    using (s)
                    {
                        return new Icon(s);
                    }
                }
            }
            catch (Exception ex)
            {
                Rem.Core.Note.Swallow("Theme.AppIcon/embed", ex);
            }
            try
            {
                // 资源缺失时退回 exe 自带图标（至少尺寸小的能显示）
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception ex)
            {
                Rem.Core.Note.Swallow("Theme.AppIcon/exe", ex);
                return null;
            }
        }

        // ---------- 中性色阶（明度递进：LogBg < Bg < Field < Card < CardHover） ----------
        public static readonly Color Bg = Color.FromArgb(5, 19, 10);        // #05130A
        public static readonly Color Card = Color.FromArgb(11, 38, 22);     // #0B2616
        public static readonly Color CardHover = Color.FromArgb(17, 53, 32); // #113520
        public static readonly Color Field = Color.FromArgb(8, 32, 15);     // #08200F
        public static readonly Color LogBg = Color.FromArgb(4, 15, 7);      // #040F07
        public static readonly Color CmdBg = Color.FromArgb(7, 27, 14);     // #071B0E

        public static readonly Color Border = Color.FromArgb(29, 78, 49);        // #1D4E31
        public static readonly Color BorderStrong = Color.FromArgb(44, 108, 68); // #2C6C44

        public static readonly Color Text = Color.FromArgb(231, 242, 229);    // #E7F2E5  对卡片 ≈ 13:1
        public static readonly Color TextDim = Color.FromArgb(168, 198, 169); // #A8C6A9  ≈ 7:1
        public static readonly Color Dim = Color.FromArgb(140, 183, 148);     // #8CB794  ≈ 6.6:1

        // ---------- 主色（PINE GREEN 家族：填充用 Accent，前景用 AccentLight） ----------
        public static readonly Color Accent = Color.FromArgb(9, 66, 23);        // #094217 PINE GREEN
        public static readonly Color AccentHover = Color.FromArgb(22, 95, 57);  // #165F39 FUN GREEN
        public static readonly Color AccentDown = Color.FromArgb(6, 48, 15);    // #06300F
        public static readonly Color AccentText = Color.FromArgb(216, 236, 201); // #D8ECC9 主按钮文字
        public static readonly Color AccentLight = Color.FromArgb(143, 217, 164); // #8FD9A4 前景强调
        public static readonly Color AccentEdge = Color.FromArgb(81, 143, 92);   // #518F5C MIDDLE GREEN 描边

        // ---------- 语义色（深绿底上做过明度补偿） ----------
        public static readonly Color Red = Color.FromArgb(240, 100, 92);
        public static readonly Color Green = Color.FromArgb(98, 201, 127);
        public static readonly Color Orange = Color.FromArgb(227, 166, 60);
        public static readonly Color Blue = Color.FromArgb(110, 193, 216);

        // ---------- 中性按钮（绿系灰，避免旧的冷灰黑在绿底上突兀） ----------
        public static readonly Color Btn = Color.FromArgb(16, 50, 31);       // #10321F
        public static readonly Color BtnHover = Color.FromArgb(23, 69, 43);  // #17452B
        public static readonly Color BtnDown = Color.FromArgb(11, 36, 21);   // #0B2415

        // ---------- 输入框描边（禁用态、圆角） ----------
        public const int FieldRadius = 6;                                     // 圆角半径（96dpi 基准）
        public static readonly Color FieldDead = Color.FromArgb(6, 26, 14);   // #061A0E 禁用项底色，沉到卡片下面
        public static readonly Color FieldDeadEdge = Color.FromArgb(20, 50, 31); // #14321F 禁用项描边，几乎不可见
        public static readonly Color FieldLock = Color.FromArgb(18, 40, 26);   // #12281A 互斥项底色，与绿/暗两种状态都不同
        public static readonly Color LockEdge = Color.FromArgb(138, 92, 40);   // #8A5C28 互斥项描边（暖色，一眼区分）
        public static readonly Color LockText = Color.FromArgb(214, 158, 96);  // #D69E60 互斥项文字，对底色约 6:1

        /// <summary>当前 DPI，由窗体在布局前写入，供 Theme 内部控件按 DPI 缩放。</summary>
        public static int Dpi = 96;

        public static int S(int v)
        {
            return (int)Math.Round(v * Dpi / 96.0);
        }

        private const string UiFamily = "Microsoft YaHei UI";
        private const string MonoFamily = "Consolas";

        public static Font Ui(float size)
        {
            return GetFont(UiFamily, size, FontStyle.Regular);
        }

        public static Font UiBold(float size)
        {
            return GetFont(UiFamily, size, FontStyle.Bold);
        }

        public static Font Mono(float size)
        {
            return GetFont(MonoFamily, size, FontStyle.Regular);
        }

        // 字体实例缓存：几十个控件共用几个 Font，省掉重复 GDI 对象与创建开销
        private static readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();

        private static Font GetFont(string family, float size, FontStyle style)
        {
            string key = family + "|" + size.ToString("0.##") + "|" + ((int)style).ToString();
            lock (fontCache)
            {
                Font hit;
                if (fontCache.TryGetValue(key, out hit)) return hit;
                Font made;
                try
                {
                    made = new Font(family, size, style);
                    if (made.Name != family)
                    {
                        made.Dispose();
                        made = new Font(FontFamily.GenericSansSerif, size, style);
                    }
                }
                catch (Exception)
                {
                    made = new Font(FontFamily.GenericSansSerif, size, style);
                }
                fontCache[key] = made;
                return made;
            }
        }

        public static void StyleButton(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = primary ? AccentEdge : Border;
            b.FlatAppearance.MouseOverBackColor = primary ? AccentHover : BtnHover;
            b.FlatAppearance.MouseDownBackColor = primary ? AccentDown : BtnDown;
            b.BackColor = primary ? Accent : Btn;
            b.ForeColor = primary ? AccentText : Text;
            b.Font = primary ? UiBold(9F) : Ui(9F);
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            b.TabStop = false;
        }

        /// <summary>
        /// 开关型选项按钮：把 CheckBox 画成按钮（Appearance=Button）。
        /// 绿底 = 已开启，暗底 = 已关闭；文字颜色随状态切换，勾选状态一眼可见。
        /// </summary>
        public static void StyleToggle(CheckBox c)
        {
            c.Appearance = Appearance.Button;
            c.FlatStyle = FlatStyle.Flat;
            c.AutoSize = false;
            c.TextAlign = ContentAlignment.MiddleCenter;
            c.FlatAppearance.BorderSize = 1;
            c.FlatAppearance.CheckedBackColor = Accent;
            // 悬停/按下不覆盖开启态底色，否则鼠标一移上去就看不出开没开
            c.FlatAppearance.MouseOverBackColor = Color.Empty;
            c.FlatAppearance.MouseDownBackColor = AccentHover;
            c.BackColor = Field;
            c.UseVisualStyleBackColor = false;
            c.Font = Ui(9F);
            c.Cursor = Cursors.Hand;
            c.TabStop = false;
            c.CheckedChanged += delegate (object s, EventArgs e) { PaintToggle((CheckBox)s); };
            // 互斥选项会被置 Enabled=false，不重绘就会停在"绿底=已开启"的样子，
            // 用户看到的是"能点但点不动"，所以禁用态必须单独有一套视觉
            c.EnabledChanged += delegate (object s, EventArgs e) { PaintToggle((CheckBox)s); };
            PaintToggle(c);
        }

        // 互斥锁定的开关：Enabled=false 会被系统用灰字覆盖 ForeColor，字就糊在底色里看不清，
        // 所以锁定态不改 Enabled，改由这里集中记录并按锁定色绘制。
        private static readonly HashSet<CheckBox> lockedToggles = new HashSet<CheckBox>();

        /// <summary>锁定/解锁一个开关。锁定后仍可读可点，但颜色与后缀表明它当前不可勾选。</summary>
        public static void LockToggle(CheckBox c, bool on)
        {
            if (on) lockedToggles.Add(c);
            else lockedToggles.Remove(c);
            PaintToggle(c);
        }

        /// <summary>开关上色：绿底=开启、暗底=关闭、暗橙底+橙字=互斥锁定。</summary>
        public static void PaintToggle(CheckBox c)
        {
            if (lockedToggles.Contains(c))
            {
                c.BackColor = FieldLock;
                c.FlatAppearance.CheckedBackColor = FieldLock;
                c.FlatAppearance.BorderColor = LockEdge;
                c.ForeColor = LockText;
                c.Cursor = Cursors.Default;
                c.Invalidate();
                return;
            }
            if (!c.Enabled)
            {
                c.BackColor = FieldDead;
                c.FlatAppearance.CheckedBackColor = FieldDead;
                c.FlatAppearance.BorderColor = FieldDeadEdge;
                c.ForeColor = TextDim;
                c.Cursor = Cursors.Default;
                c.Invalidate();
                return;
            }
            c.Cursor = Cursors.Hand;
            c.BackColor = Field;
            c.FlatAppearance.CheckedBackColor = Accent;
            if (c.Checked)
            {
                c.ForeColor = AccentText;
                c.FlatAppearance.BorderColor = AccentEdge;
            }
            else
            {
                c.ForeColor = Dim;
                c.FlatAppearance.BorderColor = Border;
            }
            c.Invalidate();
        }

        public static void StyleLink(LinkLabel l)
        {
            l.LinkColor = Blue;
            l.ActiveLinkColor = AccentLight;
            l.VisitedLinkColor = Blue;
            l.LinkBehavior = LinkBehavior.HoverUnderline;
            l.Font = Ui(9F);
            l.BackColor = Color.Transparent;
            l.TabStop = false;
        }

        /// <summary>
        /// 多行圆角输入框：与单行 NewField 同一套 FieldFrame 描边，内层 TextBox 去边框、
        /// 关掉原生滚动条（由 ThinScroll 自绘），右侧留出滚动条位置。
        /// </summary>
        public static TextBox NewFieldMulti(Control parent, int x, int y, int w, int h, float fontSize, bool mono)
        {
            const int padLeft = 10;
            const int padRight = 12;   // 9px 自绘滚动条 + 3px 余量，不压圆角描边
            const int padV = 6;

            TextBox t = new TextBox();
            t.Multiline = true;
            t.WordWrap = false;
            t.ScrollBars = ScrollBars.None;
            t.BorderStyle = BorderStyle.None;
            t.BackColor = Field;
            t.ForeColor = Text;
            t.Font = mono ? Mono(fontSize) : Ui(fontSize);
            t.Location = new Point(S(padLeft), S(padV));
            t.Size = new Size(S(w) - S(padLeft) - S(padRight), S(h) - S(padV) * 2);

            FieldFrame frame = new FieldFrame(t);
            frame.Location = new Point(S(x), S(y));
            frame.Size = new Size(S(w), S(h));
            parent.Controls.Add(frame);
            return t;
        }

        /// <summary>
        /// 圆角输入框：TextBox 去边框塞进 FieldFrame，描边统一用主题色，聚焦转亮。
        /// 坐标按 96dpi 设计值传入，内部按 Theme.Dpi 缩放。
        /// </summary>
        public static TextBox NewField(Control parent, int x, int y, int w, int h)
        {
            return NewField(parent, x, y, w, h, -1);
        }

        /// <summary>
        /// 带框内行首图标的圆角输入框（glyphKind 见 Glyph 常量，-1 表示不放图标）。
        /// 图标放框内而不是框外：左侧要腾出位置给"这个框是干什么的"文字标签。
        /// </summary>
        public static TextBox NewField(Control parent, int x, int y, int w, int h, int glyphKind)
        {
            return NewField(parent, x, y, w, h, glyphKind, null);
        }

        /// <summary>
        /// 带浮动说明文字的圆角输入框：内容为空时，说明文字浮在行首图标的位置上，
        /// 一旦有内容就收起、把位置让回图标。左侧不再单占一列放说明标签。
        /// </summary>
        public static TextBox NewField(Control parent, int x, int y, int w, int h, int glyphKind, string hint)
        {
            const int padLeft = 11;
            TextBox t = new TextBox();
            t.BorderStyle = BorderStyle.None;
            t.BackColor = Field;
            t.ForeColor = Text;
            t.Font = Ui(9.5F);
            t.Location = new Point(S(padLeft), S(3));
            t.Size = new Size(S(w) - S(padLeft) - S(11), S(22));

            FieldFrame frame = new FieldFrame(t);
            frame.Location = new Point(S(x), S(y));
            frame.Size = new Size(S(w), S(h));
            if (!string.IsNullOrEmpty(hint)) frame.SetHint(hint);

            // 图标独立在圆角框外侧：不占框内空间，背景取窗体底色，纵向与框体居中对齐
            if (glyphKind >= 0)
            {
                Glyph g = new Glyph(glyphKind);
                g.BackColor = Bg;
                int size = S(15);
                g.Location = new Point(S(x) - S(22), S(y) + (S(h) - size) / 2);
                g.Size = new Size(size, size);
                parent.Controls.Add(g);
            }

            parent.Controls.Add(frame);
            return t;
        }

        /// <summary>输入框行首小图标（0 链接 / 1 文件 / 2 文件夹）。</summary>
        public static Glyph NewGlyph(Control parent, int kind, int x, int y, int size)
        {
            Glyph g = new Glyph(kind);
            g.Location = new Point(S(x), S(y));
            g.Size = new Size(S(size), S(size));
            parent.Controls.Add(g);
            return g;
        }

        public static void StyleCombo(ComboBox c)
        {
            c.FlatStyle = FlatStyle.Flat;
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.BackColor = Field;
            c.ForeColor = Text;
            c.Font = Ui(9.5F);
        }

        /// <summary>带 1px 边框的卡片面板（替代 GroupBox，避免系统灰底）。</summary>
        public static Panel NewCard()
        {
            Panel p = new Panel();
            p.BackColor = Card;
            p.Paint += delegate (object s, PaintEventArgs e)
            {
                Panel self = (Panel)s;
                using (Pen pen = new Pen(Border))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, self.Width - 1, self.Height - 1);
                }
            };
            p.Resize += delegate (object s, EventArgs e) { ((Control)s).Invalidate(); };
            return p;
        }

        /// <summary>卡片内小标题（比正文更小、更灰，不抢视觉层级）。</summary>
        public static void PaintCardTitle(Label lb)
        {
            lb.AutoSize = true;
            lb.ForeColor = Dim;
            lb.Font = Ui(8F);
            lb.BackColor = Color.Transparent;
        }

        public static void PaintHairline(Panel p)
        {
            p.Paint += delegate (object s, PaintEventArgs e)
            {
                Panel self = (Panel)s;
                using (Pen pen = new Pen(Border))
                {
                    e.Graphics.DrawLine(pen, 0, self.Height / 2, self.Width, self.Height / 2);
                }
            };
            p.Resize += delegate (object s, EventArgs e) { ((Control)s).Invalidate(); };
        }
    }
}
