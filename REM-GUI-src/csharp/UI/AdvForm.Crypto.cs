// =====================================================================
//  UI\AdvForm.Crypto.cs — 高级设置窗的解密字段与开关联动分区（partial 拆分）
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
        private static string MaskText(string text)
        {
            return Mask.Secret(text);
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

        /// <summary>
        /// 强制开启态的渲染：和互斥锁一样不置灰，只靠颜色 + 后缀说明，
        /// 但底色用开启绿、描边与文字提到亮绿，表达"引擎已经替你开了，点也取消不掉"。
        /// </summary>
        private void SetToggleForced(CheckBox c, string forcedBy)
        {
            string baseText = (string)c.Tag;
            string baseTip = null;
            tipText.TryGetValue(c, out baseTip);

            if (string.IsNullOrEmpty(forcedBy))
            {
                forceReason.Remove(c);
                // 解除强制时，只有没被互斥锁同时占用才复位，避免把互斥态一起擦掉
                if (!lockReason.ContainsKey(c))
                {
                    c.Text = baseText;
                    Theme.ForceToggle(c, false);
                    if (tips != null && baseTip != null) tips.SetToolTip(c, baseTip);
                }
                return;
            }

            forceReason[c] = forcedBy;
            c.Text = baseText + "（强制）";
            Theme.ForceToggle(c, true);
            if (tips != null)
            {
                tips.SetToolTip(c, (baseTip == null ? baseText : baseTip)
                    + "\r\n\r\n已被强制开启：引擎在「" + forcedBy + "」时一定会做二进制合并，"
                    + "要关掉它得先取消「" + forcedBy + "」。");
            }
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
    }
}
