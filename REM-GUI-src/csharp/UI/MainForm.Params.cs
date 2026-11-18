// =====================================================================
//  UI\MainForm.Params.cs — MainForm 的参数提交闭环与剪贴板分区（partial 拆分）
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
        /// <summary>参数框（线程/重试/超时/限速）挂上统一提交：回车提交并取消关注，失焦提交。</summary>
        private void HookParamCommit(TextBox t)
        {
            t.KeyDown += OnParamKey;
            t.Leave += delegate (object s, EventArgs e) { CommitParams(); };
        }

        private void OnParamKey(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                // 参数框里的回车 = 提交并取消关注，不触发“开始下载”（那是 URL/名称框的回车语义）
                e.SuppressKeyPress = true;
                CommitParams();
                ActiveControl = null;
            }
        }

        /// <summary>
        /// 提交参数：规范化 + 越界夹紧回写文本框 + 落配置，让值立即生效。
        /// 回车 / 失焦 / 点非输入区三个入口统一走这里，避免“改了没生效”与“框里显示 999 实际 128”。
        /// </summary>
        private void CommitParams()
        {
            NormalizeSpeed();
            numThreads.Text = NumOf(numThreads, 16).ToString();
            numRetries.Text = NumOf(numRetries, 3).ToString();
            numTimeout.Text = NumOf(numTimeout, 15).ToString();
            CollectConfig();
            SaveConfig();
        }

        /// <summary>给所有非可聚焦表面（窗体/卡片/标签/横幅等）挂“点一下就取消关注”。</summary>
        private void HookUnfocusClick(Control root)
        {
            for (int i = 0; i < root.Controls.Count; i++)
            {
                Control c = root.Controls[i];
                // FieldFrame 自己会把点击转发给内部 TextBox，不能再挂失焦，否则点框会闪一下丢光标
                if (c is FieldFrame) continue;
                if (c is Label || c is Panel) c.MouseDown += OnBackgroundClick;
                HookUnfocusClick(c);
            }
        }

        /// <summary>点非输入区 = 取消关注：参数框有焦点就先提交，再把焦点交回窗体。</summary>
        private void OnBackgroundClick(object s, EventArgs e)
        {
            if (txtSpeed.ContainsFocus || numThreads.ContainsFocus
                || numRetries.ContainsFocus || numTimeout.ContainsFocus)
            {
                CommitParams();
            }
            ActiveControl = null;
        }

        /// <summary>命令预览打码：KEY/IV 的值只以 * 展示，预览结构与实际下发保持一致，不泄密钥。</summary>
        private static string MaskPreview(string cmd)
        {
            // 打码实现下沉到 Core 的 Mask.Preview（Compiled + matchTimeout + fail-closed），
            // 本处只保留调用点：避免 UI 与 Core 两份正则口径漂移，且异常时不再回显明文（3.6）
            return Mask.Preview(cmd);
        }

        private void PasteClipboard()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string url = Safe.UrlFirst(Clipboard.GetText());
                    if (url.Length > 0)
                    {
                        txtUrl.Text = url;
                        SetStatus("已从剪贴板取到链接");
                    }
                }
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
        }
    }
}
