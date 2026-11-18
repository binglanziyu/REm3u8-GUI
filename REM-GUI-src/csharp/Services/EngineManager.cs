// =====================================================================
//  Services\EngineManager.cs — 引擎版本枚举、版本探测、更新安装
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Rem.Core;

namespace Rem.Services
{
    public sealed class EngineEntry
    {
        public string Label = "";
        public string ExePath = "";
        public string Version = "";
        public bool IsBuiltin = false;
        public string Dir = "";

        public override string ToString()
        {
            if (string.IsNullOrEmpty(Version)) return Label;
            return Label + "  " + Version;
        }
    }

    public static class EngineManager
    {
        private static readonly Regex RxVer = new Regex("v?([0-9]+(?:\\.[0-9]+){1,3}(?:-[A-Za-z0-9.]+)?)", RegexOptions.Compiled);
        private static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Cached(string exePath)
        {
            lock (cache)
            {
                string v;
                if (cache.TryGetValue(exePath, out v)) return v;
            }
            return "";
        }

        public static void Remember(string exePath, string version)
        {
            lock (cache) { cache[exePath] = version; }
        }

        /// <summary>枚举可用引擎：内置版在最上，其余按目录名倒序（新版本优先）。</summary>
        public static List<EngineEntry> List()
        {
            List<EngineEntry> list = new List<EngineEntry>();
            string builtin = AppPaths.FindTool(AppPaths.ReName);
            string builtinInApp = AppPaths.BuiltinRE;

            EngineEntry b = new EngineEntry();
            b.Label = "内置";
            b.IsBuiltin = true;
            b.ExePath = File.Exists(builtinInApp) ? builtinInApp : builtin;
            b.Version = Cached(b.ExePath);
            list.Add(b);

            try
            {
                if (Directory.Exists(AppPaths.EngineDir))
                {
                    string[] dirs = Directory.GetDirectories(AppPaths.EngineDir);
                    Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                    Array.Reverse(dirs);
                    for (int i = 0; i < dirs.Length; i++)
                    {
                        string exe = Path.Combine(dirs[i], AppPaths.ReName);
                        if (!File.Exists(exe)) continue;
                        EngineEntry e = new EngineEntry();
                        e.Dir = dirs[i];
                        string name = Path.GetFileName(dirs[i]);
                        e.Label = "已下载 " + Ver.Display(name);
                        e.ExePath = exe;
                        e.Version = Cached(exe);
                        if (string.IsNullOrEmpty(e.Version)) e.Version = Ver.Display(name);
                        list.Add(e);
                    }
                }
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
            return list;
        }

        /// <summary>同步探测版本（只用于后台线程，最多阻塞 5 秒）。</summary>
        public static string Probe(string exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return "";
            string cached = Cached(exePath);
            if (!string.IsNullOrEmpty(cached)) return cached;
            string found = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.Arguments = "--version";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    StringBuilder sb = new StringBuilder();
                    try
                    {
                        string line = p.StandardOutput.ReadLine();
                        if (line != null) sb.Append(line);
                        string errLine = p.StandardError.ReadLine();
                        if (errLine != null) sb.Append(' ').Append(errLine);
                    }
                    catch (Exception __ex) { Note.Swallow(__ex); }
                    try { p.WaitForExit(5000); } catch (Exception __ex) { Note.Swallow(__ex); }
                    try { if (!p.HasExited) p.Kill(); } catch (Exception __ex) { Note.Swallow(__ex); }
                    Match m = RxVer.Match(sb.ToString());
                    if (m.Success) found = "v" + m.Groups[1].Value;
                }
            }
            catch (Exception __ex)
            {
                Note.Swallow(__ex);
                found = "";
            }
            if (found.Length > 0) Remember(exePath, found);
            return found;
        }

        /// <summary>
        /// 安装更新：下载 → SHA256 校验 → 安全解压（防 Zip Slip）→ 落到 engine\&lt;tag&gt;\
        /// 任何一步失败都返回 false 并清掉半成品，绝不把压缩包改名冒充 exe。
        /// </summary>
        public static bool Install(string assetUrl, string tag, string expectedSha256, AppConfig cfg, Logger log,
                                   Action<int> progress, out string installedExe, out string error)
        {
            installedExe = "";
            error = "";
            string tmpDir = Path.Combine(AppPaths.DataDir, "download");
            // 安装目标目录在 try 里才确定，但外层 catch 回滚半成品时要用到它，故先声明（3.7）
            string target = "";
            try
            {
                if (!Directory.Exists(tmpDir)) Directory.CreateDirectory(tmpDir);
                string tagName = Ver.Normalize(tag);
                // tag 来自远端清单/API，最终要拼进安装路径：先文件名合法化剔除分隔符与 ".."，
                // 再断言不含任何路径成分，不满足就回退时间戳，杜绝路径穿越（D-EM-1）
                tagName = Safe.FileName(tagName);
                if (tagName.Length == 0 || tagName == "." || tagName == ".." ||
                    tagName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    !string.Equals(Path.GetFileName(tagName), tagName, StringComparison.Ordinal))
                {
                    tagName = DateTime.Now.ToString("yyyyMMddHHmmss");
                }
                string zip = Path.Combine(tmpDir, "engine_" + tagName + ".zip");

                if (log != null) log.Info(">> 正在下载引擎 " + Ver.Display(tagName) + " …");
                string dlErr;
                if (!Http.DownloadToFile(assetUrl, cfg, 30000, zip, progress, out dlErr))
                {
                    error = dlErr;
                    return false;
                }

                // 完整性校验：GitHub release asset 自带 sha256 digest
                if (!string.IsNullOrEmpty(expectedSha256))
                {
                    string actual = Hash.Sha256File(zip);
                    if (actual.Length == 0 ||
                        !string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        error = "安装包校验失败（SHA256 不一致），已中止。期望 "
                                + Clip(expectedSha256) + "，实际 " + (actual.Length > 0 ? Clip(actual) : "无法读取");
                        Fs.TryDeleteFile(zip);
                        return false;
                    }
                    if (log != null) log.Ok(">> 安装包 SHA256 校验通过");
                }
                else if (log != null)
                {
                    log.Warn(">> 该发布未提供 SHA256 digest，本次跳过完整性校验");
                }

                target = Path.Combine(AppPaths.EngineDir, tagName);
                if (!Directory.Exists(target)) Directory.CreateDirectory(target);

                string exeInZip = "";
                // zip（PK）或 tar.gz（gzip 1f 8b）走解压；其余才按直链 exe 处理（D-EM-2）
                if (Fs.IsZip(zip) || Fs.StartsWith(zip, 0x1f, 0x8b))
                {
                    if (log != null) log.Info(">> 正在解压 …");
                    ArchiveExtract(zip, target, out exeInZip);
                    if (exeInZip.Length == 0 || !File.Exists(exeInZip))
                    {
                        error = "安装包解压后未找到 " + AppPaths.ReName + "（包可能已损坏或被打断）";
                        Fs.TryDeleteDir(target);
                        Fs.TryDeleteFile(zip);
                        return false;
                    }
                }
                else
                {
                    // 直链 exe：先验 PE 头再落盘
                    if (!Fs.IsPE(zip))
                    {
                        error = "下载内容既不是 zip 也不是可执行文件，已中止";
                        Fs.TryDeleteFile(zip);
                        return false;
                    }
                    exeInZip = Path.Combine(tmpDir, "engine_direct_" + tagName + ".exe");
                    Fs.TryDeleteFile(exeInZip);
                    File.Copy(zip, exeInZip, true);
                }

                string finalExe = Path.Combine(target, AppPaths.ReName);
                if (!string.Equals(exeInZip, finalExe, StringComparison.OrdinalIgnoreCase))
                {
                    if (!Fs.MoveAtomic(exeInZip, finalExe))
                    {
                        error = "引擎文件落盘失败：" + finalExe;
                        Fs.TryDeleteDir(target);
                        return false;
                    }
                }

                // 落盘后再验一次，杜绝“伪 exe”
                if (!Fs.IsPE(finalExe))
                {
                    error = "安装后的文件不是有效的 Windows 可执行文件";
                    Fs.TryDeleteDir(target);
                    return false;
                }

                Fs.TryDeleteFile(zip);

                installedExe = finalExe;
                Remember(finalExe, Ver.Display(tagName));
                if (log != null) log.Ok(">> 引擎已就绪：" + finalExe);
                CleanupOldVersions(target, log);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                // 解压配额越界等中途中止：半成品引擎目录必须清掉，否则界面上会留下
                // 一个“残缺但存在”的版本目录，下次启动还可能被当成可用引擎（3.7）
                try
                {
                    if (target.Length > 0 && Directory.Exists(target))
                    {
                        Fs.TryDeleteDir(target);
                        if (log != null) log.Diag("引擎安装失败，已回滚半成品目录 " + target + "：" + error, "Engine");
                    }
                }
                catch (Exception __ex) { Note.Swallow(__ex); }
                return false;
            }
        }

        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > 12 ? s.Substring(0, 12) + "…" : s;
        }

        /// <summary>回收引擎目录：当前版本之外最多再留一个历史版本。</summary>
        public static int CleanupOldVersions(string keepDir, Logger log)
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(AppPaths.EngineDir)) return 0;
                string keep = Path.GetFullPath(keepDir);
                string[] dirs = Directory.GetDirectories(AppPaths.EngineDir);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                Array.Reverse(dirs);   // 新 → 旧
                int kept = 0;
                for (int i = 0; i < dirs.Length; i++)
                {
                    string full = Path.GetFullPath(dirs[i]);
                    if (string.Equals(full, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    kept++;
                    if (kept > 1)
                    {
                        Fs.TryDeleteDir(dirs[i]);
                        if (!Directory.Exists(dirs[i])) n++;
                    }
                }
                if (n > 0 && log != null) log.Dim(">> 已回收 " + n + " 个旧引擎版本目录");
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
            return n;
        }

        /// <summary>删除指定引擎版本目录（仅限 engine 根目录之下）。</summary>
        public static bool DeleteVersion(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                string root = Path.GetFullPath(AppPaths.EngineDir);
                string full = Path.GetFullPath(dir);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
                Directory.Delete(full, true);
                return !Directory.Exists(full);
            }
            catch (Exception __ex)
            {
                Note.Swallow(__ex);
                return false;
            }
        }

        /// <summary>
        /// 解压总量越界：单列一个类型，避免被 ArchiveExtract 内部的条目级兜底 catch 吞掉，
        /// 从而把“超出配额”伪装成“包损坏 / 未找到引擎”（3.7）
        /// </summary>
        public sealed class ArchiveQuotaException : InvalidOperationException
        {
            public ArchiveQuotaException(string message) : base(message) { }
        }

        // 解压总量上限：正常发行包几十 MB（不足 100 MB），这里留约 20 倍余量；
        // 超过它只可能是异常包或 zip bomb，必须明确报错，不允许静默解一半（3.7）
        private const long ArchiveMaxBytes = 2L * 1024 * 1024 * 1024;

        /// <summary>安全解压：逐条断言落地路径不越出目标目录，越界条目直接丢弃。</summary>
        private static void ArchiveExtract(string zip, string target, out string exePath)
        {
            exePath = "";
            try
            {
                string root = Path.GetFullPath(target);
                if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    root += Path.DirectorySeparatorChar;

                using (ZipArchive ar = ZipFile.OpenRead(zip))
                {
                    long totalBytes = 0;
                    for (int i = 0; i < ar.Entries.Count; i++)
                    {
                        ZipArchiveEntry en = ar.Entries[i];
                        try
                        {
                            // 解压总量上限：防 zip bomb（声明几 KB、展开几十 GB 把磁盘写满）。
                            // 超限必须明确报错：原来在这里 break，等于“停在半包”再让上层报
                            // “未找到引擎”，把配额违规伪装成包损坏，用户与日志都看不出真相（3.7）
                            totalBytes += en.Length;
                            if (totalBytes > ArchiveMaxBytes)
                            {
                                throw new ArchiveQuotaException("引擎包解压总量超过上限 " + ArchiveMaxBytes
                                    + " 字节（已累计 " + totalBytes + " 字节），已中止解压");
                            }
                            string rel = en.FullName.Replace('/', Path.DirectorySeparatorChar);
                            if (rel.Length == 0) continue;
                            string dest = Path.GetFullPath(Path.Combine(root, rel));
                            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // Zip Slip 拦截

                            bool isDir = rel.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                                         en.Name.Length == 0;
                            if (isDir)
                            {
                                if (!Directory.Exists(dest)) Directory.CreateDirectory(dest);
                                continue;
                            }
                            string parent = Path.GetDirectoryName(dest);
                            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
                            Fs.TryDeleteFile(dest);
                            en.ExtractToFile(dest, true);
                        }
                        catch (ArchiveQuotaException) { throw; }   // 配额越界不是“坏条目”，必须穿透（3.7）
                        catch (Exception __ex)
                        {
                            // 单个坏条目跳过继续：若让异常把整个 zip 打回 tar 兜底，
                            // 等于把上面的 Zip Slip 校验绕过（D-EM-2）
                            Note.Swallow(__ex);
                        }
                    }
                }
                exePath = FindFirst(root, AppPaths.ReName, 0);
            }
            catch (ArchiveQuotaException) { throw; }   // 配额越界要一路传到 Install 的失败提示（3.7）
            catch (Exception __ex)
            {
                // zip 容器整体解析失败：是否走 tar 兜底由下面的魔数判断决定（D-EM-2）
                Note.Swallow(__ex);
            }

            if (exePath.Length == 0 && Fs.StartsWith(zip, 0x1f, 0x8b))
            {
                // 仅限真正的 tar.gz（gzip 魔数 1f 8b）才交给系统 tar。
                // Windows 自带 tar 能直接读 zip——对 zip 兜底等于把应用层 Zip Slip 校验整个绕过（D-EM-2）
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = "tar";
                    psi.Arguments = "-xf " + Rem.Core.ArgQ.Quote(zip) + " -C " + Rem.Core.ArgQ.Quote(target);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    using (Process p = Process.Start(psi)) { p.WaitForExit(60000); }
                    string found = FindFirst(target, AppPaths.ReName, 0);
                    if (found.Length > 0) exePath = found;
                }
                catch (Exception __ex) { Note.Swallow(__ex); }
            }
        }

        /// <summary>
        /// 在 dir 下按文件名查找可执行文件。
        /// depth 语义：<=0 取默认 3 层；否则按给定层数逐级下探（原实现完全忽略 depth，深目录会被全盘扫）。
        /// </summary>
        private static string FindFirst(string dir, string fileName, int depth)
        {
            int max = depth > 0 ? depth : 3;
            return FindAt(dir, fileName, max);
        }

        private static string FindAt(string dir, string fileName, int left)
        {
            if (left < 0) return "";
            try
            {
                string direct = Path.Combine(dir, fileName);
                if (File.Exists(direct)) return direct;
                string[] sub = Directory.GetDirectories(dir);
                for (int i = 0; i < sub.Length; i++)
                {
                    string found = FindAt(sub[i], fileName, left - 1);
                    if (found.Length > 0) return found;
                }
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
            return "";
        }
    }
}
