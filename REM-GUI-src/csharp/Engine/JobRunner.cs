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
        // 引擎在收口帧里给出的整片总大小（如 "1.95GB"）：这是容量，绝不能再当速率
        public string Size = "";

        private static readonly Regex RxCount = new Regex("(\\d+)\\s*/\\s*(\\d+)", RegexOptions.Compiled);
        private static readonly Regex RxPct = new Regex("([0-9]+(?:\\.[0-9]+)?)\\s*%", RegexOptions.Compiled);
        // 速率：单位后面必须真的带速率后缀（Bps / B/s / B/sec）。
        // 早先前缀后缀写成可选，于是 "2153/2153 100.00% 1.95GB - 00:00:00" 里的总量被当成 1.95 GB/s（D12）
        private static readonly Regex RxSpeed = new Regex(
            "([0-9]+(?:\\.[0-9]+)?)\\s*([KMGT])?i?B(?:ps|/s|/sec)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // 容量：同一套单位、但后面不跟速率后缀的才是总大小；负向前瞻挡住 "MBps" 这类速率
        private static readonly Regex RxSize = new Regex(
            "([0-9]+(?:\\.[0-9]+)?)\\s*([KMGT])i?B(?![A-Za-z])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public void Reset()
        {
            Done = 0; Total = 0; Pct = -1; Speed = ""; Size = "";
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

            // 速率只认带 Bps / B/s / B/sec 后缀的字段；"1.95GB" 这种容量单独走 RxSize
            string spd = "";
            Match ms = RxSpeed.Match(line);
            if (ms.Success)
            {
                // 0.00Bps 是引擎"刚建连还没收到数据"时的占位，显示出来只是噪声
                double val;
                double.TryParse(ms.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out val);
                string unit = ms.Groups[2].Success ? ms.Groups[2].Value.ToUpperInvariant() + "B" : "B";
                if (val > 0) spd = ms.Groups[1].Value + " " + unit + "/s";
            }

            string size = "";
            Match mz = RxSize.Match(line);
            if (mz.Success)
            {
                size = mz.Groups[1].Value + " " + mz.Groups[2].Value.ToUpperInvariant() + "B";
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
            if (size.Length > 0) Size = size;
            // 收口帧只给总量、没有速率；不清空的话上一帧的速度会一直挂在"完成"上
            if (Pct >= 100) Speed = "";
            speedText = Speed;
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
                string s = MainText;
                if (!string.IsNullOrEmpty(Speed)) s += "   ·   " + Speed;
                return s;
            }
        }

        /// <summary>
        /// 界面主行只放「进度 + 总大小」，速率交给右侧独立的速度标签：
        /// 早先两者挤在同一行，总量和速率混排，用户分不清 1.95GB 是速度还是体积。
        /// </summary>
        public string MainText
        {
            get
            {
                double p = Pct;
                if (p < 0 && Total > 0) p = Done * 100.0 / Total;
                string pctText = p < 0 ? "--" : p.ToString("0.0");
                string s = Done + "/" + (Total > 0 ? Total.ToString() : "?") + "   " + pctText + "%";
                if (!string.IsNullOrEmpty(Size)) s += "   ·   共 " + Size;
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
        // Start() 传入的日志器：Stop() 也要写一行「已连带终止 N 个子进程」，故提升为字段
        private Logger log;
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
                catch (Exception __ex) { Note.Swallow(__ex); return false; }
            }
        }

        public bool HasExited
        {
            get
            {
                if (proc == null) return true;
                try { return proc.HasExited; }
                catch (Exception __ex) { Note.Swallow(__ex); return true; }
            }
        }

        public bool Start(string exe, BuiltArgs b, string workDir, Logger log)
        {
            // 参数名与字段同名：显式 this. 赋值，供 Stop() 记录子进程连带终止条数
            this.log = log;
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = JoinArgs(b.Args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.WorkingDirectory = workDir;
            // 引擎是控制台程序，stdout/stderr 被重定向后一般按【系统 OEM 代码页】输出（简中 936），
            // 但不同版本/不同构建也可能直接吐 UTF-8。任何「预先指定一种编码」的做法都会在其中
            // 一种情况下把中文解成乱码：早前固定 UTF8 → "����ض���"，固定 GBK → "鐜板湪"。
            // 所以这里不再给 StreamReader 指定编码，而是直接读原始字节流，由 Enc.DecodeLine
            // 逐行判定「是不是合法 UTF-8」，不是再按 ANSI 解（见 Core\Enc.cs）。
            // 注意：必须读 .BaseStream —— StandardOutputEncoding 只影响 StreamReader，
            // 而按行判定的前提是拿到未解码的原始字节。

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
                try { proc.Dispose(); } catch (Exception __ex) { Note.Swallow(__ex); } 
                proc = null;
                ExitCode = -1;
                return false;
            }

            Pid = proc.Id;
            StartedUtc = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 立刻关掉 stdin：GUI 没有任何交互输入通道，引擎若要选轨/确认会直接读到 EOF 报错退出，
            // 而不是无限期等待（D3）
            try { proc.StandardInput.Close(); } catch (Exception __ex) { Note.Swallow(__ex); }

            job = Native.CreateKillOnCloseJob();
            if (job != IntPtr.Zero)
            {
                bool ok = Native.AssignProcessToJobObject(job, proc.Handle);
                if (!ok && log != null) log.Diag("AssignProcessToJobObject 失败", "JobRunner");
            }

            // 先取局部 reader 再由闭包捕获：否则 Pump 线程被调度时才解引用 proc 字段，
            // 与 Dispose 的 proc=null 竞态会抛到后台线程，未处理异常直接崩掉整个进程（D4）
            Stream stdOut = proc.StandardOutput.BaseStream;
            Stream stdErr = proc.StandardError.BaseStream;

            thOut = new Thread(delegate () { Pump(stdOut, false); });
            thOut.IsBackground = true;
            thOut.Name = "re-stdout";
            thOut.Start();

            thErr = new Thread(delegate () { Pump(stdErr, true); });
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

        //  已废弃：原先在这里按 OEM 代码页给管道指定编码（固定一种编码必然在另一种输出下乱码）。
        //  现在改由 Enc.DecodeLine 按原始字节逐行判定，保留此注释说明历史成因，避免有人"补回来"。

        private void Pump(Stream stream, bool isErr)
        {
            try
            {
                PumpCore(stream, isErr);
            }
            catch (Exception __ex)
            {
                // 泵线程的任何未处理异常都会终止整个进程（net48 默认策略），必须兜底留痕（D4）
                Note.Swallow(__ex);
            }
        }

        /// <summary>
        /// 按字节读取引擎输出并切行：先断言「这行字节是不是合法 UTF-8」，合法按 UTF-8 解，
        /// 否则按系统 ANSI(OEM) 解，最后统一过 Enc.Repair 兜底（见 Core\Enc.cs）。
        /// 逐行而不是逐块判定，才能避免整段输出被一个坏字节带偏；
        /// 行边界只认 0x0A / 0x0D —— UTF-8 与 GBK 的多字节尾字节都不会落到这两个值。
        /// </summary>
        private void PumpCore(Stream stream, bool isErr)
        {
            byte[] buf = new byte[4096];
            byte[] line = new byte[8192];
            int len = 0;
            while (true)
            {
                int n;
                try { n = stream.Read(buf, 0, buf.Length); }
                catch (Exception __ex) { Note.Swallow(__ex); break; }
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b == 0x0A || b == 0x0D)
                    {
                        if (len > 0)
                        {
                            Push(Enc.DecodeLine(line, 0, len), isErr);
                            len = 0;
                        }
                        continue;
                    }
                    if (len >= line.Length)
                    {
                        byte[] bigger = new byte[line.Length * 2];
                        Buffer.BlockCopy(line, 0, bigger, 0, line.Length);
                        line = bigger;
                    }
                    line[len++] = b;
                }
                // 引擎偶发吐超长单行（无换行的整段 JSON / 大段 m3u8）：到点就先吐出去，
                // 不能让它把缓冲区涨到爆内存
                if (len > 65536)
                {
                    Push(Enc.DecodeLine(line, 0, len), isErr);
                    len = 0;
                }
            }
            if (len > 0) Push(Enc.DecodeLine(line, 0, len), isErr);
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
            catch (Exception __ex) { Note.Swallow(__ex); }
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
            catch (Exception __ex) { Note.Swallow(__ex); }
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

        /// <summary>
        /// 暂停/停止：必须让「引擎 + 它的 ffmpeg 等子进程」整棵树退出。
        /// 早前只 proc.Kill() 杀引擎本体——混流阶段的 ffmpeg 子进程会被留下，
        /// 继续占着成品文件与 CPU，且下一次同目录任务会因文件被占用而失败（P1-2）。
        /// 顺序：Job 对象整组终止 → 快照枚举后代自底向上强杀（Job 未生效时的兜底）→ 杀根进程 → 等回收。
        /// </summary>
        public void Stop()
        {
            killed = true;
            int pid = -1;
            try
            {
                if (proc != null)
                {
                    try { pid = proc.Id; } catch (Exception __ex) { Note.Swallow(__ex); pid = -1; }
                }
            }
            catch (Exception __ex) { Note.Swallow(__ex); }

            try
            {
                if (job != IntPtr.Zero)
                {
                    Native.TerminateJobObject(job, 1);
                }
            }
            catch (Exception __ex) { Note.Swallow(__ex); }

            // 子进程先死：父进程先被杀时，尚未退出的子进程会被过继给 explorer 继续跑
            int childKilled = 0;
            if (pid > 0)
            {
                try { childKilled = Native.KillProcessTree(pid); }
                catch (Exception __ex) { Note.Swallow(__ex); }
            }
            if (childKilled > 0 && log != null) log.Diag("暂停：已连带终止 " + childKilled + " 个子进程", "Stop");

            try
            {
                if (proc != null && !proc.HasExited) proc.Kill();
                else if (pid > 0) Native.TerminateByPid(pid);
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
            try
            {
                // 强杀后给一个短暂的回收窗口，避免后续 HasExited 仍然为假导致收尾逻辑误判
                if (proc != null && !proc.HasExited) proc.WaitForExit(5000);
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
            // 等两个读取线程收尾，确认 ffmpeg/引擎的输出管道都已关闭（不再有残留输出线程挂在旧进程上）
            try
            {
                if (thOut != null) thOut.Join(1500);
                if (thErr != null) thErr.Join(1500);
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
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
                        try { proc.WaitForExit(5000); } catch (Exception __ex) { Note.Swallow(__ex); }
                    }
                    // 超时后仍未退出就如实给 -1，不拿“猜的退出码”冒充结果
                    ExitCode = proc.HasExited ? proc.ExitCode : -1;
                }
            }
            catch (Exception __ex) { Note.Swallow(__ex); ExitCode = -1; }
            return ExitCode;
        }

        /// <summary>
        /// 非阻塞收尾：进程已退出则取退出码并返回 true；仍在跑返回 false，绝不阻塞调用线程。
        /// UI 线程每拍调一次，替掉原来 Finish() 里最长 5 秒的 WaitForExit（3.5）。
        /// </summary>
        public bool TryFinish(out int code)
        {
            code = ExitCode;
            try
            {
                if (proc == null) return true;
                if (!proc.HasExited) return false;
                ExitCode = proc.ExitCode;
                code = ExitCode;
                return true;
            }
            catch (Exception __ex)
            {
                // 进程对象已回收等异常：按“可收尾”处理，退出码沿用上一次取到的值
                Note.Swallow(__ex);
                code = ExitCode;
                return true;
            }
        }

        /// <summary>两个读取线程是否都已读完并退出（收尾阶段非阻塞判定用，替代 Join(500)*2）。</summary>
        public bool OutputDrained
        {
            get
            {
                try
                {
                    bool a = thOut == null || !thOut.IsAlive;
                    bool b = thErr == null || !thErr.IsAlive;
                    return a && b;
                }
                catch (Exception __ex) { Note.Swallow(__ex); return true; }
            }
        }

        public bool WasKilled { get { return killed; } }

        public void Dispose()
        {
            try
            {
                if (thOut != null) thOut.Join(500);
                if (thErr != null) thErr.Join(500);
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
            try
            {
                if (job != IntPtr.Zero)
                {
                    Native.CloseHandle(job);
                    job = IntPtr.Zero;
                }
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
            try
            {
                if (proc != null)
                {
                    proc.Dispose();
                    proc = null;
                }
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
        }
    }
}
