// =====================================================================
//  UI\MainForm.Drop.cs — MainForm 的外部链接接收与拖放分区（partial 拆分）
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
        private void DrainStartupDrop()
        {
            try
            {
                if (!File.Exists(AppPaths.DropFile)) return;
                // 任务运行期间收到的外部链接不静默滞留：明确告知“已收到，任务结束后处理”，
                // 同一份中转文件只提示一次，避免 100ms tick 反复刷状态（D12）
                if (state != RunState.Idle)
                {
                    string stamp = "";
                    try { stamp = File.GetLastWriteTime(AppPaths.DropFile).Ticks.ToString(); }
                    catch (Exception __ex) { Note.Swallow(__ex); }
                    if (stamp != dropNotifiedFor)
                    {
                        dropNotifiedFor = stamp;
                        log.Warn(">> 已收到外部链接，将在当前任务结束后处理");
                        SetStatus("已收到外部链接，任务结束后处理", Theme.Orange);
                    }
                    return;
                }
                string text = File.ReadAllText(AppPaths.DropFile, Encoding.UTF8);
                File.Delete(AppPaths.DropFile);
                dropNotifiedFor = "";
                string url = Safe.UrlFirst(text);
                if (url.Length > 0)
                {
                    txtUrl.Text = url;
                    SetStatus("已接收外部链接");
                }
                else
                {
                    log.Warn(">> 收到外部内容但没解析出链接，已忽略：" + Mask.Secret(Safe.FirstLine(text)));
                    SetStatus("收到的外部内容里没有链接", Theme.Orange);
                }
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files == null || files.Length == 0) return;
                    string first = files[0];
                    // 只认 .resume.json 续传文件（此前的 ".json &&" 是死条件，普通 .json 会掉进读取分支）（D-UI-1）
                    if (first.EndsWith(".resume.json", StringComparison.OrdinalIgnoreCase))
                    {
                        LoadResumeFile(first);
                        return;
                    }
                    if (Directory.Exists(first))
                    {
                        SetStatus("不支持拖入文件夹，请拖入 m3u8 / mpd / txt 文件，或直接粘贴链接");
                        return;
                    }
                    // 限长读取：拖入的可能是视频/日志等大文件，全量读进字符串会冻结界面甚至 OOM（D-UI-1）
                    string text = ReadHeadText(first, 65536);
                    string url = Safe.UrlFirst(text);
                    if (url.Length > 0)
                    {
                        txtUrl.Text = url;
                        SetStatus("已从文件取到链接");
                    }
                    else
                    {
                        SetStatus("文件里没有找到链接");
                    }
                    return;
                }
                if (e.Data.GetDataPresent(DataFormats.Text))
                {
                    string text = (string)e.Data.GetData(DataFormats.Text);
                    string url = Safe.UrlFirst(text);
                    if (url.Length > 0)
                    {
                        txtUrl.Text = url;
                        SetStatus("已接收拖入文本中的链接");
                    }
                    else
                    {
                        SetStatus("拖入的文本里没找到链接", Theme.Orange);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Err("拖放处理失败：" + ex.Message);
            }
        }

        /// <summary>限长读取文本开头（最多 maxChars 字符）：拖入文件取链接只需前缀，
        /// 绝不把大文件全量读进内存（D-UI-1）。</summary>
        private static string ReadHeadText(string path, int maxChars)
        {
            try
            {
                using (StreamReader sr = new StreamReader(path, Encoding.UTF8, true))
                {
                    char[] buf = new char[8192];
                    StringBuilder sb = new StringBuilder();
                    while (sb.Length < maxChars)
                    {
                        int want = buf.Length;
                        if (maxChars - sb.Length < want) want = maxChars - sb.Length;
                        int n = sr.Read(buf, 0, want);
                        if (n <= 0) break;
                        sb.Append(buf, 0, n);
                    }
                    return sb.ToString();
                }
            }
            catch (Exception __ex)
            {
                Note.Swallow(__ex);
                return "";
            }
        }
    }
}
