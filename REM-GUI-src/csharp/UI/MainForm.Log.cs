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
        // 日志渲染积压：每拍最多渲染 LogFlushPerTick 行，余量留到下一拍继续，
        // 防止 UI 卡顿恢复后一次性灌入上万行把 RTF 解析打满、反而继续恶化积压（D-LOG-1）
        private const int LogFlushPerTick = 300;
        private readonly List<LogItem> logBacklog = new List<LogItem>();

        private void ClearLog()
        {
            logBacklog.Clear();
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
                    catch (Exception __ex) { Note.Swallow(__ex); }
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
        /// 坏片 = 「分片数量校验不通过 … Failed」；合并/混流失败 = 「Mux failed」「ERROR: Failed」；
        /// 解密失败 = 源站分片尾部被截断后的一串 CryptographicException（见 LooksLikeDecryptFailure）。
        /// </summary>
        private static bool LooksLikeEngineFailure(string t)
        {
            // 判定逻辑下沉到 Services.FailSignals：UI 里的私有静态方法测不到，
            // 而这类误判（403 当残片）代价是白跑一轮补片 + 给用户错误结论
            return FailSignals.LooksLikeEngineFailure(t);
        }

        /// <summary>
        /// 鉴权/网络失效：token 过期、Cookie/Header 缺失时引擎重试 10 次后抛 401/403。
        /// 这是链接问题，不是分片问题，补片救不了，必须走自己的提示分支。
        /// </summary>
        private static bool LooksLikeAuthFailure(string t)
        {
            return FailSignals.LooksLikeAuthFailure(t);
        }

        /// <summary>
        /// 分片数量校验失败 / 合并混流失败（原有三条，口径不动）。
        /// </summary>
        private static bool LooksLikePartFailure(string t)
        {
            return FailSignals.LooksLikePartFailure(t);
        }

        /// <summary>
        /// 解密失败特征：源站把分片尾部写截断后，密文长度不再是 16 的整数倍，
        /// AES-128-CBC 解密必然抛 CryptographicException（not a complete block / Padding is invalid）。
        /// 只认这几条专属词：「Unhandled exception」是所有异常的共同前缀，
        /// 把它当特征会把 403 重试耗尽也算成残片（3.1.1 的误报根因）。
        /// </summary>
        private static bool LooksLikeDecryptFailure(string t)
        {
            return FailSignals.LooksLikeDecryptFailure(t);
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
                // 鉴权/网络失效必须先判：token 过期时引擎同样把 403 包成 Unhandled exception，
                // 排在补片判定之前才能保证它既不打「残片」文案、也不进补片状态机
                bool authFail = LooksLikeAuthFailure(t);
                if (authFail) sawAuthFailed = true;
                // 引擎对这些失败一律返回 exit=0，只看退出码会误判成功并删掉续传文件；
                // 这里留标记，交给 CompleteJob 触发自动补片
                if (!authFail && LooksLikeEngineFailure(t)) sawJobFailed = true;
                bool decryptFail = !authFail && LooksLikeDecryptFailure(t);
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
                // 解密失败要单独点明成因：引擎只会吐一串 CryptographicException，
                // 不说明白的话用户看不出是「源站分片残缺」而不是链接或密钥的问题
                if (decryptFail && !decryptHintShown)
                {
                    decryptHintShown = true;
                    log.Add(">> 分片解密失败，源站分片残缺：密文长度不是 16 的整数倍，已安排自动补片", Colors.Red);
                }
                // 鉴权失效也只提示一次：重试耗尽会把同一句 403 连打 10 行。
                // 文案与预检口径保持一致，并明确说清不进自动补片——否则用户会盯着
                // “已安排自动补片”等一个永远不会成功的重试
                if (authFail && !authHintShown)
                {
                    authHintShown = true;
                    log.Add(">> 链接鉴权失效或已过期（预检口径：链接需要鉴权或已失效，请刷新 Cookie/Header 后重试）：本次不进入自动补片，请重新获取链接后再开始", Colors.Red);
                }
            }
            if (changed) RefreshProgressUi();
        }

        private void RefreshProgressUi()
        {
            // 主行只放「进度 + 总大小」，速率单独占右侧一格：
            // 早先两者挤在一行，总量 1.95GB 会被读成速度，用户根本分不清哪个是哪个
            lblProg.Text = prog.MainText;
            lblSpd.Text = prog.Speed;
            lblProg.ForeColor = barDone ? Theme.Green : Theme.AccentLight;
            bar.Invalidate();
        }

        private void FlushLog()
        {
            if (txtLog == null || txtLog.IsDisposed) return;
            if (logBacklog.Count == 0)
            {
                List<LogItem> items = log.Drain();
                if (items == null) return;
                logBacklog.AddRange(items);
            }
            if (logBacklog.Count == 0) return;

            int take = logBacklog.Count;
            if (take > LogFlushPerTick) take = LogFlushPerTick;

            bool nearBottom = IsNearBottom();
            Native.SetRedraw(txtLog.Handle, false);
            txtLog.SuspendLayout();
            try
            {
                for (int i = 0; i < take; i++)
                {
                    txtLog.SelectionStart = txtLog.TextLength;
                    txtLog.SelectionLength = 0;
                    txtLog.SelectionColor = logBacklog[i].Argb == Colors.Default
                        ? Color.FromArgb(214, 216, 220)
                        : Color.FromArgb(logBacklog[i].Argb);
                    txtLog.AppendText(logBacklog[i].Text + "\n");
                    logLines++;
                }
                logBacklog.RemoveRange(0, take);
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

        /// <summary>打开诊断日志（重构方案第 4 章 E3）：让"程序吞了个异常"变成用户够得着的一份文件。</summary>
        private void OnOpenDiag()
        {
            string path = AppPaths.DiagFile;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                SetStatus("本次会话没有内部异常记录");
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(path);
                SetStatus("已打开诊断日志：" + path);
            }
            catch (Exception ex)
            {
                // 没有 .log 关联程序时退化为打开所在目录，保证入口始终有效
                Note.Swallow(ex);
                try
                {
                    using (System.Diagnostics.Process p = System.Diagnostics.Process.Start("explorer.exe", AppPaths.LogDir)) { }
                }
                catch (Exception ex2) { Note.Swallow(ex2); }
                SetStatus("已打开日志目录：" + AppPaths.LogDir);
            }
        }

        /// <summary>诊断入口的条数提示（不占布局，挂在悬浮提示上）</summary>
        private void UpdateDiagUse()
        {
            if (tip == null || lnkDiag == null || lnkDiag.IsDisposed) return;
            int n = Logger.DiagCount;
            if (n <= 0) tip.SetToolTip(lnkDiag, "本次会话没有内部异常记录\r\n点击查看诊断日志（diag.log）");
            else tip.SetToolTip(lnkDiag, "本次会话已记录 " + n + " 条内部异常（已写入 diag.log）\r\n点击查看诊断日志");
        }

        /// <summary>日志用量提示（旧版 lblLogUse 的回退项，这里挂在“清理日志”的悬浮提示上，不占布局）。</summary>
        private void UpdateLogUse()
        {
            if (tip == null || lnkClean == null || lnkClean.IsDisposed) return;
            int max = cfg != null && cfg.LogLimit > 0 ? cfg.LogLimit : 200000;
            tip.SetToolTip(lnkClean, "当前显示 " + logLines + " 行 / 上限 " + max + " 字符（超出裁剪最早部分）\r\n点击删除历史日志文件，当前会话保留");
            UpdateDiagUse();
        }

        private void TrimLog()
        {
            // 上限取自配置（LogLimit 为字符数，0 或负数时回落到 20 万字符）
            // 触发阈值放宽到 max + max/2：裁剪是 O(全文) 的 RTF 重排，
            // 按 2 万一裁频率太高（每 2 万新字符就重排一次全量缓冲）（D-LOG-1）
            int max = cfg != null && cfg.LogLimit > 0 ? cfg.LogLimit : 200000;
            int len = txtLog.TextLength;
            if (len <= max + max / 2) return;
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
            catch (Exception __ex) { Note.Swallow(__ex); return true; }
        }

        private void ScrollToEnd()
        {
            try
            {
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.SelectionLength = 0;
                txtLog.ScrollToCaret();
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
        }
    }
}
