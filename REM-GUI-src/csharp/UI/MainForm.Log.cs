// =====================================================================
//  UI\MainForm.Log.cs — MainForm 的日志渲染与状态栏分区（partial 拆分）
//  纯代码搬移，逻辑与行为与原 MainForm.cs 完全一致
// =====================================================================
using System;
using System.Collections.Generic;
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
        private void ClearLog()
        {
            txtLog.Clear();
            logLines = 0;
        }

        private void OnCleanLogs()
        {
            try
            {
                string[] all = Directory.GetFiles(AppPaths.LogDir, "*.log");
                // 排除当前会话正在写入的日志文件，避免自删导致当天日志丢失
                string active = log.FilePath;
                List<string> keep = new List<string>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (active.Length > 0 && string.Equals(all[i], active, StringComparison.OrdinalIgnoreCase)) continue;
                    keep.Add(all[i]);
                }
                string[] files = keep.ToArray();
                if (files.Length == 0)
                {
                    SetStatus("没有可清理的日志");
                    return;
                }
                DialogResult r = MessageBox.Show(this,
                    "将删除日志目录中的 " + files.Length + " 个日志文件（不影响下载结果）。\r\n\r\n目录：" + AppPaths.LogDir,
                    "清理日志", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.OK) return;
                int n = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    try { File.Delete(files[i]); n++; }
                    catch (Exception __ex) { Note.Swallow("MainForm.cs:850", __ex); }
                }
                log.Ok(">> 已清理 " + n + " 个日志文件");
                SetStatus("已清理 " + n + " 个日志文件");
                log.OpenFile(Path.Combine(AppPaths.LogDir, "gui_" + DateTime.Now.ToString("yyyyMMdd") + ".log"));
            }
            catch (Exception ex)
            {
                log.Err("清理日志失败：" + ex.Message);
            }
        }

        private void SetStatus(string text)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = Theme.Dim;
        }

        private void SetStatus(string text, Color color)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = color;
        }

        /// <summary>
        /// 判断一行日志是否属于引擎的失败信号。退出码在这些场景全是 0，只能靠文本：
        /// 坏片 = 「分片数量校验不通过 … Failed」；合并/混流失败 = 「Mux failed」「ERROR: Failed」。
        /// </summary>
        private static bool LooksLikeEngineFailure(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            if (t.IndexOf("分片数量校验不通过", StringComparison.Ordinal) >= 0) return true;
            if (t.IndexOf("Mux failed", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("ERROR: Failed", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void HandleLines(List<LineItem> lines)
        {
            bool changed = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string spd;
                if (prog.Feed(lines[i].Text, out spd))
                {
                    changed = true;
                    continue;
                }
                string t = lines[i].Text;
                // 引擎对这些失败一律返回 exit=0，只看退出码会误判成功并删掉续传文件；
                // 这里留标记，交给 CompleteJob 触发自动补片
                if (LooksLikeEngineFailure(t)) sawJobFailed = true;
                int color = Colors.Default;
                if (lines[i].IsErr ||
                    t.IndexOf("[ERR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    color = Colors.Red;
                }
                else if (t.IndexOf("WARN", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    color = Colors.Orange;
                }
                log.Add(t, color);
            }
            if (changed) RefreshProgressUi();
        }

        private void RefreshProgressUi()
        {
            lblProg.Text = prog.Text;
            string speed = prog.Speed;
            lblSpd.Text = speed.Length > 0 ? speed : "";
            bar.Invalidate();
        }

        private void FlushLog()
        {
            if (txtLog == null || txtLog.IsDisposed) return;
            List<LogItem> items = log.Drain();
            if (items == null) return;

            bool nearBottom = IsNearBottom();
            Native.SetRedraw(txtLog.Handle, false);
            txtLog.SuspendLayout();
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    txtLog.SelectionStart = txtLog.TextLength;
                    txtLog.SelectionLength = 0;
                    txtLog.SelectionColor = items[i].Argb == Colors.Default
                        ? Color.FromArgb(214, 216, 220)
                        : Color.FromArgb(items[i].Argb);
                    txtLog.AppendText(items[i].Text + "\n");
                    logLines++;
                }
                TrimLog();
            }
            finally
            {
                txtLog.ResumeLayout();
                Native.SetRedraw(txtLog.Handle, true);
                txtLog.Invalidate();
            }
            if (nearBottom) ScrollToEnd();
            UpdateLogUse();
        }

        /// <summary>日志用量提示（旧版 lblLogUse 的回退项，这里挂在“清理日志”的悬浮提示上，不占布局）。</summary>
        private void UpdateLogUse()
        {
            if (tip == null || lnkClean == null || lnkClean.IsDisposed) return;
            int max = cfg != null && cfg.LogLimit > 0 ? cfg.LogLimit : 200000;
            tip.SetToolTip(lnkClean, "当前显示 " + logLines + " 行 / 上限 " + max + " 字符（超出裁剪最早部分）\r\n点击删除历史日志文件，当前会话保留");
        }

        private void TrimLog()
        {
            // 上限取自配置（LogLimit 为字符数，0 或负数时回落到 20 万字符）
            int max = cfg != null && cfg.LogLimit > 0 ? cfg.LogLimit : 200000;
            int len = txtLog.TextLength;
            if (len <= max + 20000) return;
            int cutAt = len - max;
            int line = txtLog.GetLineFromCharIndex(cutAt);
            int cut = txtLog.GetFirstCharIndexFromLine(line);
            if (cut <= 0) cut = cutAt;
            txtLog.Select(0, cut);
            txtLog.SelectedText = "";
            logLines = txtLog.GetLineFromCharIndex(txtLog.TextLength);
        }

        private bool IsNearBottom()
        {
            try
            {
                Point p = txtLog.GetPositionFromCharIndex(txtLog.TextLength);
                return p.Y <= txtLog.ClientSize.Height + 20;
            }
            catch (Exception) { return true; }
        }

        private void ScrollToEnd()
        {
            try
            {
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.SelectionLength = 0;
                txtLog.ScrollToCaret();
            }
            catch (Exception __ex) { Note.Swallow("MainForm.cs:1321", __ex); }
        }
    }
}
