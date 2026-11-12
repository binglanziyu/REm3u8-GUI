// =====================================================================
//  Services\Logger.cs — 环形日志：内存待刷队列 + 会话日志落盘
//  所有线程只往队列里塞，UI 定时器批量取走，界面刷新与业务线程彻底解耦
// =====================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Rem.Core;
namespace Rem.Services
{
    public sealed class LogItem
    {
        public string Text = "";
        public int Argb = Colors.Default;
        public LogItem(string text, int argb) { Text = text; Argb = argb; }
    }

    public sealed class Logger
    {
        private readonly object gate = new object();
        private readonly List<LogItem> pending = new List<LogItem>();
        private readonly Dictionary<string, DateTime> diagSeen = new Dictionary<string, DateTime>();
        private StreamWriter file;
        private string filePath = "";
        // 落盘整体失效（目录不可写、磁盘满等）时置真：界面据此常驻提示"日志未落盘"（D18）
        private volatile bool diskBroken = false;

        public string FilePath { get { return filePath; } }

        /// <summary>日志文件是否已无法写入（true = 只有界面日志，磁盘上没有会话日志）。</summary>
        public bool DiskBroken { get { return diskBroken; } }

        /// <summary>
        /// 切换到新的会话日志文件。必须与 Add / CloseFile 争同一把锁：
        /// 早前这里不加锁，清理日志时主线程换文件、后台线程仍在写旧句柄，会静默丢行。
        /// </summary>
        public void OpenFile(string path)
        {
            lock (gate)
            {
                try
                {
                    if (file != null) { try { file.Flush(); file.Close(); } catch (Exception __ex) { Note.Swallow("Logger.cs:39", __ex); } file = null; }
                    filePath = path;
                    // 目录被手工删掉（dist\logs）时自己补建，别让整个会话日志静默失效
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 8192);
                    file = new StreamWriter(fs, new UTF8Encoding(false));
                    file.AutoFlush = false;
                    file.WriteLine("===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 会话开始 =====");
                    diskBroken = false;
                }
                catch (Exception ex)
                {
                    file = null;
                    diskBroken = true;
                    Diag("打开会话日志失败：" + ex.Message, "Logger.OpenFile");
                }
            }
        }

        public void CloseFile()
        {
            lock (gate)
            {
                if (file != null)
                {
                    try { file.Flush(); file.Close(); } catch (Exception __ex) { Note.Swallow("Logger.cs:60", __ex); }
                    file = null;
                }
            }
        }

        /// <summary>界面单行最长保留字符数：超长的命令/URL 仍能在日志区看清主要部分。</summary>
        private const int MaxViewChars = 2000;

        public void Add(string text, int argb)
        {
            if (text == null) return;
            // 落盘前统一过一遍凭证掩码（HLS 密钥 / Cookie / Authorization / ?token= 等）。
            // 这里是日志的最后一闸：调用方哪怕漏了打码，也不会有明文密钥写进日志文件。
            string full = Mask.Secret(text);
            string view = full;
            // 只在界面侧收敛长度；落盘保留完整的（已掩码）原文，排障时尾部信息不能丢
            if (view.Length > MaxViewChars) view = view.Substring(0, MaxViewChars - 40) + " …（原文 " + full.Length + " 字符，见日志文件）";
            lock (gate)
            {
                pending.Add(new LogItem(view, argb));
                if (file != null)
                {
                    try { file.WriteLine(full); }
                    catch (Exception ex)
                    {
                        // 写盘失败就把句柄扔掉，别让后续每一行都白抛一次异常
                        try { file.Close(); } catch (Exception __ex) { Note.Swallow("Logger.cs:85", __ex); }
                        file = null;
                        diskBroken = true;
                        Diag("写会话日志失败，已停止落盘：" + ex.Message, "Logger.Add");
                    }
                }
            }
        }

        public void Info(string text) { Add(text, Colors.Default); }
        public void Ok(string text) { Add(text, Colors.Green); }
        public void Warn(string text) { Add(text, Colors.Orange); }
        public void Err(string text) { Add(text, Colors.Red); }
        public void Dim(string text) { Add(text, Colors.Dim); }

        /// <summary>取走全部待刷条目（UI 线程批量渲染）。</summary>
        public List<LogItem> Drain()
        {
            lock (gate)
            {
                if (pending.Count == 0) return null;
                List<LogItem> copy = new List<LogItem>(pending);
                pending.Clear();
                return copy;
            }
        }

        public void FlushFile()
        {
            lock (gate)
            {
                if (file != null)
                {
                    try { file.Flush(); } catch (Exception __ex) { Note.Swallow("Logger.cs:117", __ex); }
                }
            }
        }

        /// <summary>诊断日志：同一条目 5 秒内去重，避免刷盘。</summary>
        public void Diag(string msg, string tag)
        {
            try
            {
                // 诊断日志同样过掩码：历史上的 diag.log 里出现过明文 HLS 密钥串
                msg = Mask.Secret(msg);
                string key = (tag == null ? "" : tag) + "|" + msg;
                DateTime now = DateTime.Now;
                lock (gate)
                {
                    DateTime last;
                    if (diagSeen.TryGetValue(key, out last))
                    {
                        if ((now - last).TotalSeconds < 5) return;
                    }
                    if (diagSeen.Count > 500) diagSeen.Clear();
                    diagSeen[key] = now;
                }
                string target = AppPaths.DiagFile;
                if (string.IsNullOrEmpty(target)) return;   // 目录初始化失败时不要把诊断也一起吞成异常
                string line = now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + (tag == null ? "diag" : tag) + "] " + msg + "\r\n";
                File.AppendAllText(target, line, Encoding.UTF8);
            }
            catch (Exception __ex) { Note.Swallow("Logger.cs:144", __ex); }
        }
    }
}
