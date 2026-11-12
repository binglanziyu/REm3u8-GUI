// =====================================================================
//  Engine\JobRunner.cs — 子进程生命周期 + 输出行采集 + 进度帧解析
//  后台只有两个纯阻塞读取线程，不碰 UI；行数据走加锁队列，由 UI 定时器取走
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Rem.Interop;
using Rem.Services;

using Rem.Core;
namespace Rem.Engine
{
    public sealed class LineItem
    {
        public string Text = "";
        public bool IsErr = false;
        public LineItem(string text, bool isErr) { Text = text; IsErr = isErr; }
    }

    public sealed class Progress
    {
        public int Done;
        public int Total;
        public double Pct = -1;
        public string Speed = "";

        private static readonly Regex RxCount = new Regex("(\\d+)\\s*/\\s*(\\d+)", RegexOptions.Compiled);
        private static readonly Regex RxPct = new Regex("([0-9]+(?:\\.[0-9]+)?)\\s*%", RegexOptions.Compiled);
        private static readonly Regex RxSpeed = new Regex(
            "([0-9]+(?:\\.[0-9]+)?)\\s*(?:([KMGT])(?:i?B)(?:ps|/s|/sec)?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public void Reset()
        {
            Done = 0; Total = 0; Pct = -1; Speed = "";
        }

        /// <summary>从一行输出里抽取进度；返回 true 表示这行是进度帧（不再进日志）。</summary>
        public bool Feed(string line, out string speedText)
        {
            speedText = "";
            if (string.IsNullOrEmpty(line)) return false;

            Match mc = RxCount.Match(line);
            Match mp = RxPct.Match(line);
            bool hasCount = mc.Success;
            bool hasPct = mp.Success;
            if (!hasCount && !hasPct) return false;

            string spd = "";
            Match ms = RxSpeed.Match(line);
            if (ms.Success)
            {
                spd = ms.Groups[1].Value + " " + ms.Groups[2].Value.ToUpperInvariant() + "B/s";
            }

            int done = 0;
            int all = 0;
            double pct = -1;
            if (hasCount)
            {
                int.TryParse(mc.Groups[1].Value, out done);
                int.TryParse(mc.Groups[2].Value, out all);
            }
            if (hasPct)
            {
                double.TryParse(mp.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out pct);
            }
            if (!hasCount && !hasPct) return false;
            if (all <= 0 && pct < 0) return false;

            // 口径统一：只要这一行给出了「已完成/总数」，就以分片计数为准，
            // 不再采信同一行里出现的百分比（RE 会同时刷出子任务 100.0% 之类），
            // 否则会出现 Done<Total 却显示 100.0% 的自相矛盾。
            if (all > 0)
            {
                Total = all;
                Done = done > all ? all : done;
                Pct = Total > 0 ? Done * 100.0 / Total : 0;
            }
            else if (pct >= 0)
            {
                Pct = pct;
                // 纯百分比帧（合并/后处理阶段只会刷 %）：进度满 100 时分片计数一并拉齐，
                // 避免出现「46/51 但 100.0%」这种自相矛盾的读数。
                if (pct >= 100 && Total > 0 && Done < Total) Done = Total;
            }
            if (spd.Length > 0) Speed = spd;
            speedText = spd;
            return true;
        }

        public int PercentInt
        {
            get
            {
                double p = Pct;
                if (p < 0 && Total > 0) p = Done * 100.0 / Total;
                if (p < 0) return 0;
                if (p > 100) p = 100;
                return (int)Math.Floor(p);
            }
        }

        public string Text
        {
            get
            {
                double p = Pct;
                if (p < 0 && Total > 0) p = Done * 100.0 / Total;
                string pctText = p < 0 ? "--" : p.ToString("0.0");
                string s = Done + "/" + (Total > 0 ? Total.ToString() : "?") + "   " + pctText + "%";
                if (!string.IsNullOrEmpty(Speed)) s += "   ·   " + Speed;
                return s;
            }
        }
    }

    public sealed class JobRunner : IDisposable
    {
        private static readonly Regex RxAnsi = new Regex("\\x1B\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

        private Process proc;
        private IntPtr job = IntPtr.Zero;
        private Thread thOut;
        private Thread thErr;
        private readonly object gate = new object();
        private readonly Queue<LineItem> queue = new Queue<LineItem>();
        private volatile bool killed = false;
        // 本次任务累计被丢弃的日志行数（D10）：只在队列爆掉时累加，供任务结束时如实汇报
        private int droppedLines = 0;

        /// <summary>本次任务累计丢弃的行数（队列上限 20000 行时丢弃最旧部分）。</summary>
        public int DroppedLines
        {
            get { lock (gate) { return droppedLines; } }
        }

        public int ExitCode = -1;
        public int Pid = 0;
        public string StartedUtc = "";

        public bool IsRunning
        {
            get
            {
                try { return proc != null && !proc.HasExited; }
                catch (Exception) { return false; }
            }
        }

        public bool HasExited
        {
            get
            {
                if (proc == null) return true;
                try { return proc.HasExited; }
                catch (Exception) { return true; }
            }
        }

        public bool Start(string exe, BuiltArgs b, string workDir, Logger log)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = JoinArgs(b.Args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.WorkingDirectory = workDir;
            // 引擎是控制台程序，stdout/stderr 被重定向后按【系统 OEM 代码页】输出（简中 936）。
            // 早前误用 UTF8 解码，导致日志里中文全成 "����ض���"；日志文件本身是 UTF-8，但管道不是。
            Encoding outEnc = GetConsoleEncoding();
            try { psi.StandardOutputEncoding = outEnc; } catch (Exception __ex) { Note.Swallow("JobRunner.cs:173", __ex); }
            try { psi.StandardErrorEncoding = outEnc; } catch (Exception __ex) { Note.Swallow("JobRunner.cs:174", __ex); }

            proc = new Process();
            proc.StartInfo = psi;
            proc.EnableRaisingEvents = false;

            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                if (log != null) log.Err("启动引擎失败：" + ex.Message);
                try { proc.Dispose(); } catch (Exception __ex) { Note.Swallow("JobRunner.cs:187", __ex); } 
                proc = null;
                ExitCode = -1;
                return false;
            }

            Pid = proc.Id;
            StartedUtc = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 立刻关掉 stdin：GUI 没有任何交互输入通道，引擎若要选轨/确认会直接读到 EOF 报错退出，
            // 而不是无限期等待（D3）
            try { proc.StandardInput.Close(); } catch (Exception __ex) { Note.Swallow("JobRunner.cs:203", __ex); }

            job = Native.CreateKillOnCloseJob();
            if (job != IntPtr.Zero)
            {
                bool ok = Native.AssignProcessToJobObject(job, proc.Handle);
                if (!ok && log != null) log.Diag("AssignProcessToJobObject 失败", "JobRunner");
            }

            thOut = new Thread(delegate () { Pump(proc.StandardOutput, false); });
            thOut.IsBackground = true;
            thOut.Name = "re-stdout";
            thOut.Start();

            thErr = new Thread(delegate () { Pump(proc.StandardError, true); });
            thErr.IsBackground = true;
            thErr.Name = "re-stderr";
            thErr.Start();

            return true;
        }

        private static string JoinArgs(List<string> args)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Rem.Core.ArgQ.Quote(args[i]));
            }
            return sb.ToString();
        }

        /// <summary>子控制台进程重定向输出的实际编码：系统 OEM 代码页（简中 936）。</summary>
        private static Encoding GetConsoleEncoding()
        {
            try
            {
                int cp = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                if (cp <= 0) cp = 936;
                return Encoding.GetEncoding(cp);
            }
            catch (Exception)
            {
                try { return Encoding.Default; }
                catch (Exception) { return new UTF8Encoding(false); }
            }
        }

        private void Pump(TextReader reader, bool isErr)
        {
            char[] buf = new char[4096];
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                int n;
                try { n = reader.Read(buf, 0, buf.Length); }
                catch (Exception) { break; }
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    char c = buf[i];
                    if (c == '\n' || c == '\r')
                    {
                        if (sb.Length > 0)
                        {
                            Push(sb.ToString(), isErr);
                            sb.Length = 0;
                        }
                        continue;
                    }
                    sb.Append(c);
                }
                if (sb.Length > 8192)
                {
                    Push(sb.ToString(), isErr);
                    sb.Length = 0;
                }
            }
            if (sb.Length > 0) Push(sb.ToString(), isErr);
        }

        /// <summary>
        /// 引擎用 Spectre 渲染多行面板时靠 ANSI 光标定位换行，重定向下 ANSI 被禁用，
        /// 两行会粘连成「...HTTP Live Streaming10:19:42.856 INFO : ...」（实测原始字节里确无换行符）。
        /// 这里在行内出现的日志时间戳前补断行，纯展示层修正。
        /// </summary>
        private static readonly Regex RxInlineStamp =
            new Regex(@"(?<=\S)(?=\d{2}:\d{2}:\d{2}\.\d{3} +(?:INFO|WARN|ERRO|ERROR|DBUG|TRCE|FTL))",
                      RegexOptions.Compiled);

        private void Push(string raw, bool isErr)
        {
            string line = raw;
            try
            {
                if (line.IndexOf('\u001B') >= 0) line = RxAnsi.Replace(line, "");
            }
            catch (Exception __ex) { Note.Swallow("JobRunner.cs:292", __ex); }
            line = line.TrimEnd();
            if (line.Length == 0) return;

            try
            {
                if (RxInlineStamp.IsMatch(line))
                {
                    string[] segs = RxInlineStamp.Split(line);
                    for (int i = 0; i < segs.Length; i++) Enqueue(segs[i], isErr);
                    return;
                }
            }
            catch (Exception __ex) { Note.Swallow("JobRunner.cs:305", __ex); }
            Enqueue(line, isErr);
        }

        private void Enqueue(string line, bool isErr)
        {
            if (line == null) return;
            line = line.Trim();
            if (line.Length == 0) return;
            lock (gate)
            {
                // 队列爆掉时丢最旧的一半，并留一条显式提示；
                // 早前是直接 Clear()，等于整段日志凭空消失且无任何痕迹。
                if (queue.Count >= 20000)
                {
                    int drop = queue.Count / 2;
                    for (int i = 0; i < drop; i++) queue.Dequeue();
                    droppedLines += drop;
                    queue.Enqueue(new LineItem("[日志过快] 已丢弃最旧 " + drop + " 行（本次任务累计丢弃 " + droppedLines
                        + " 行，完整内容见日志文件）", true));
                }
                queue.Enqueue(new LineItem(line, isErr));
            }
        }

        public List<LineItem> Drain()
        {
            lock (gate)
            {
                if (queue.Count == 0) return null;
                List<LineItem> list = new List<LineItem>(queue);
                queue.Clear();
                return list;
            }
        }

        public void Stop()
        {
            killed = true;
            try
            {
                if (job != IntPtr.Zero)
                {
                    Native.TerminateJobObject(job, 1);
                }
            }
            catch (Exception __ex) { Note.Swallow("JobRunner.cs:349", __ex); }
            try
            {
                if (proc != null && !proc.HasExited) proc.Kill();
            }
            catch (Exception __ex) { Note.Swallow("JobRunner.cs:354", __ex); }
            try
            {
                // 强杀后给一个短暂的回收窗口，避免后续 HasExited 仍然为假导致收尾逻辑误判
                if (proc != null && !proc.HasExited) proc.WaitForExit(2000);
            }
            catch (Exception __ex) { Note.Swallow("JobRunner.cs:360", __ex); }
        }

        /// <summary>收尾：等待退出并取退出码。UI 线程调用前应确认 HasExited。</summary>
        public int Finish()
        {
            try
            {
                if (proc != null)
                {
                    if (!proc.HasExited)
                    {
                        try { proc.WaitForExit(5000); } catch (Exception __ex) { Note.Swallow("JobRunner.cs:372", __ex); }
                    }
                    // 超时后仍未退出就如实给 -1，不拿“猜的退出码”冒充结果
                    ExitCode = proc.HasExited ? proc.ExitCode : -1;
                }
            }
            catch (Exception) { ExitCode = -1; }
            return ExitCode;
        }

        public bool WasKilled { get { return killed; } }

        public void Dispose()
        {
            try
            {
                if (thOut != null) thOut.Join(500);
                if (thErr != null) thErr.Join(500);
            }
            catch (Exception __ex) { Note.Swallow("JobRunner.cs:391", __ex); }
            try
            {
                if (job != IntPtr.Zero)
                {
                    Native.CloseHandle(job);
                    job = IntPtr.Zero;
                }
            }
            catch (Exception __ex) { Note.Swallow("JobRunner.cs:400", __ex); }
            try
            {
                if (proc != null)
                {
                    proc.Dispose();
                    proc = null;
                }
            }
            catch (Exception __ex) { Note.Swallow("JobRunner.cs:409", __ex); }
        }
    }
}
