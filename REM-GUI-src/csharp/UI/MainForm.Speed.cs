// =====================================================================
//  UI\MainForm.Speed.cs — MainForm 的限速解析与输入过滤分区（partial 拆分）
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
    }
}
