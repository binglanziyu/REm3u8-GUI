// =====================================================================
//  Services\SegmentPatcher.cs — 坏片自动补片：用前后相邻分片顶替缺失分片
//  背景：引擎遇到 403/404 分片，重试耗尽后直接报「分片数量校验不通过 … Failed」，
//        整条任务报废。这里在引擎退出后按清单预期数量核查已下载分片，
//        缺失项用相邻分片（优先前一片，其次后一片）复制顶替，
//        让分片序列恢复连续，再由调用方用同一套参数续跑一次：引擎发现分片已存在会直接跳过
//        下载、只补缺的，齐了就进入合并。刻意不用 --skip-download —— 本机引擎（Beta 20260628）
//        实测在该参数下 exit=0 却零产物，会被当成“完成”进而清掉续传与分片。
//  铁律：只读引擎目录结构 + 复制文件；绝不删除、绝不覆盖已存在的分片。
//  分层：Services 层，仅依赖 System.*，不引用 UI / Engine 控件类型。
// =====================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Rem.Services
{
    /// <summary>单个缺失分片的补片记录。</summary>
    public sealed class PatchItem
    {
        public string Dir = "";
        public int Index = -1;
        public string SourceName = "";
        public string TargetName = "";
        public bool Done = false;
        public string Reason = "";
    }

    /// <summary>一次补片的汇总结果。</summary>
    public sealed class PatchReport
    {
        public int Total = 0;       // 预期分片总数（各流累加）
        public int Present = 0;     // 本来就在的
        public int Missing = 0;     // 缺失的
        public int Patched = 0;     // 已用相邻分片补上的
        public int Failed = 0;      // 缺了但找不到可用相邻分片
        public int Skipped = 0;     // 缺口过大、按策略放弃补的
        public List<PatchItem> Items = new List<PatchItem>();
        public string Summary = "";

        public bool Changed { get { return Patched > 0; } }
    }

    public static class SegmentPatcher
    {
        // 引擎实测目录结构：<tmpDir>\<name>\0____\<index>.<ext>
        //   0____ 的后缀是 4 个下划线（流序号 + 分隔符），不同版本可能换成别的数字
        private const string StreamSuffix = "____";

        // 补片上限只看「缺失率」：缺片占比不超过该比例就一律补，不设绝对片数硬上限
        // （1000 片允许补到 300 片）。整段缺失靠邻片顶替会把成片补成同一片反复重复，
        // 内容严重失真，比老实报错更坑，所以超过这个比例才整组放弃。
        public const int MaxPatchPercent = 30;

        /// <summary>
        /// 扫描 tmpDir 下已下载的分片，把缺失索引用相邻分片复制顶替。
        /// </summary>
        /// <param name="tmpDir">引擎的 --tmp-dir 值（形如 &lt;保存目录&gt;\.parts\&lt;名字&gt;）</param>
        /// <param name="apply">false 时只统计不改盘（供测试与预演使用）</param>
        /// <param name="log">可空</param>
        public static PatchReport Patch(string tmpDir, bool apply, Logger log)
        {
            PatchReport report = new PatchReport();
            if (string.IsNullOrEmpty(tmpDir) || !Directory.Exists(tmpDir))
            {
                report.Summary = "分片目录不存在：" + (tmpDir == null ? "" : tmpDir);
                return report;
            }

            string partsRoot = FindPartsRoot(tmpDir);
            if (partsRoot.Length == 0)
            {
                report.Summary = "未找到分片目录";
                return report;
            }

            int expected = ExpectedCount(partsRoot);
            List<string> dirs = FindStreamDirs(partsRoot);
            if (dirs.Count == 0)
            {
                report.Summary = "未发现流分片目录（可能分片已被清理）";
                return report;
            }

            for (int i = 0; i < dirs.Count; i++)
            {
                PatchOneDir(dirs[i], expected, apply, report, log);
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("分片 ").Append(report.Total).Append(" 个，缺失 ").Append(report.Missing);
            sb.Append("，已补 ").Append(report.Patched);
            if (report.Failed > 0) sb.Append("，无法补 ").Append(report.Failed);
            if (report.Skipped > 0) sb.Append("，缺口过大放弃 ").Append(report.Skipped);
            report.Summary = sb.ToString();
            return report;
        }

        /// <summary>定位「分片根目录」：即包含 &lt;流&gt;____ 子目录的那一层（实测是 &lt;tmp&gt;\&lt;name&gt;）。</summary>
        public static string FindPartsRoot(string tmpDir)
        {
            if (HasStreamDir(tmpDir)) return tmpDir;
            string[] subs;
            try { subs = Directory.GetDirectories(tmpDir); }
            catch (Exception) { return ""; }
            for (int i = 0; i < subs.Length; i++)
            {
                if (HasStreamDir(subs[i])) return subs[i];
            }
            // 兜底：没有 ____ 结构时假定分片直接落在 tmpDir 下，交由后续扫描判断
            return tmpDir;
        }

        private static bool HasStreamDir(string dir)
        {
            try
            {
                string[] subs = Directory.GetDirectories(dir);
                for (int i = 0; i < subs.Length; i++)
                {
                    if (subs[i].EndsWith(StreamSuffix, StringComparison.Ordinal)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>列出真正的分片目录（含索引命名文件的目录）。</summary>
        public static List<string> FindStreamDirs(string partsRoot)
        {
            List<string> list = new List<string>();
            try
            {
                string[] subs = Directory.GetDirectories(partsRoot);
                for (int i = 0; i < subs.Length; i++)
                {
                    if (subs[i].EndsWith(StreamSuffix, StringComparison.Ordinal)) list.Add(subs[i]);
                }
                if (list.Count == 0)
                {
                    // 再下一层：兼容「<tmp>\<name>\<更高层级>\0____」这类嵌套
                    for (int i = 0; i < subs.Length; i++)
                    {
                        string[] grand;
                        try { grand = Directory.GetDirectories(subs[i]); }
                        catch (Exception) { continue; }
                        for (int k = 0; k < grand.Length; k++)
                        {
                            if (grand[k].EndsWith(StreamSuffix, StringComparison.Ordinal)) list.Add(grand[k]);
                        }
                    }
                }
                if (list.Count == 0 && HasIndexedFiles(partsRoot)) list.Add(partsRoot);
            }
            catch (Exception) { }
            return list;
        }

        private static bool HasIndexedFiles(string dir)
        {
            try
            {
                string[] files = Directory.GetFiles(dir);
                for (int i = 0; i < files.Length; i++)
                {
                    int idx;
                    string ext;
                    if (ParseIndex(Path.GetFileName(files[i]), out idx, out ext)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>从引擎写出的 m3u8 里数出预期分片数（取各清单最大值）；读不到返回 0。</summary>
        public static int ExpectedCount(string partsRoot)
        {
            int best = 0;
            try
            {
                string[] m3u8 = Directory.GetFiles(partsRoot, "*.m3u8", SearchOption.AllDirectories);
                for (int i = 0; i < m3u8.Length; i++)
                {
                    string body;
                    try { body = File.ReadAllText(m3u8[i], Encoding.UTF8); }
                    catch (Exception) { continue; }
                    int n = 0;
                    string[] lines = body.Replace("\r\n", "\n").Split('\n');
                    for (int k = 0; k < lines.Length; k++)
                    {
                        if (lines[k].TrimStart().StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) n++;
                    }
                    if (n > best) best = n;
                }
            }
            catch (Exception) { }
            return best;
        }

        /// <summary>文件名 → 分片索引 + 扩展名；不是「纯数字.扩展名」就返回 false。</summary>
        public static bool ParseIndex(string fileName, out int index, out string ext)
        {
            index = -1;
            ext = "";
            if (string.IsNullOrEmpty(fileName)) return false;
            int dot = fileName.LastIndexOf('.');
            if (dot <= 0 || dot >= fileName.Length - 1) return false;
            string num = fileName.Substring(0, dot);
            ext = fileName.Substring(dot);          // 带点，如 ".ts"
            for (int i = 0; i < num.Length; i++)
            {
                if (num[i] < '0' || num[i] > '9') return false;
            }
            int v;
            if (!int.TryParse(num, out v)) return false;
            if (v < 0) return false;
            index = v;
            return true;
        }

        private static void PatchOneDir(string dir, int expected, bool apply, PatchReport report, Logger log)
        {
            Dictionary<string, SortedDictionary<int, string>> byExt =
                new Dictionary<string, SortedDictionary<int, string>>();
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception) { return; }

            for (int i = 0; i < files.Length; i++)
            {
                int idx;
                string ext;
                if (!ParseIndex(Path.GetFileName(files[i]), out idx, out ext)) continue;
                if (!byExt.ContainsKey(ext)) byExt[ext] = new SortedDictionary<int, string>();
                byExt[ext][idx] = files[i];
            }

            int extCount = byExt.Count;
            foreach (KeyValuePair<string, SortedDictionary<int, string>> kv in byExt)
            {
                SortedDictionary<int, string> map = kv.Value;
                string ext = kv.Key;
                int maxIdx = -1;
                int width = 1;
                foreach (int k in map.Keys)
                {
                    if (k > maxIdx) maxIdx = k;
                    int w = k.ToString().Length;
                    if (w > width) width = w;
                }

                int total = maxIdx + 1;
                // 清单数的是「整条流」的分片数。同一目录里同时有视频与音频分片时两者数量本来就不同
                // （音频常更少），套用清单数会凭空造出一长串假片，所以只在单一扩展名时才补尾部缺口
                if (extCount == 1 && expected > total) total = expected;

                List<int> missing = new List<int>();
                for (int i = 0; i < total; i++)
                {
                    report.Total++;
                    if (map.ContainsKey(i)) { report.Present++; continue; }
                    missing.Add(i);
                }
                if (missing.Count == 0) continue;
                report.Missing += missing.Count;

                // 缺口占比过大才整组放弃：整段缺失靠邻片顶替会把成片补成“同一片无限重复”，得不偿失
                if (missing.Count * 100 > total * MaxPatchPercent)
                {
                    report.Skipped += missing.Count;
                    if (log != null)
                    {
                        log.Warn(">> 补片：" + Path.GetFileName(dir) + " 的 " + ext + " 缺 " + missing.Count
                                 + "/" + total + "，超过自动补片上限（缺失率 "
                                 + MaxPatchPercent + "%），放弃补片");
                    }
                    continue;
                }

                for (int m = 0; m < missing.Count; m++)
                {
                    int i = missing[m];
                    PatchItem item = new PatchItem();
                    item.Dir = dir;
                    item.Index = i;
                    item.TargetName = Pad(i, width) + ext;

                    int srcIdx = map.ContainsKey(i - 1) ? i - 1 : (map.ContainsKey(i + 1) ? i + 1 : -1);
                    if (srcIdx < 0)
                    {
                        item.Reason = "前后都没有可用分片";
                        report.Failed++;
                        report.Items.Add(item);
                        if (log != null) log.Warn(">> 补片：缺第 " + i + " 片，前后均无可用分片，跳过");
                        continue;
                    }

                    item.SourceName = Path.GetFileName(map[srcIdx]);
                    string srcPath = map[srcIdx];
                    string dstPath = Path.Combine(dir, item.TargetName);

                    if (!apply)
                    {
                        item.Reason = "预演（未落盘）";
                        report.Items.Add(item);
                        continue;
                    }

                    try
                    {
                        File.Copy(srcPath, dstPath, false);
                        item.Done = true;
                        report.Patched++;
                        map[i] = dstPath;
                        report.Items.Add(item);
                        if (log != null)
                        {
                            log.Warn(">> 补片：" + item.TargetName + " ← " + item.SourceName
                                     + "（第 " + i + " 片不可用，用相邻分片顶替，画面该处会重复）");
                        }
                    }
                    catch (Exception ex)
                    {
                        item.Reason = "复制失败：" + ex.Message;
                        report.Failed++;
                        report.Items.Add(item);
                        if (log != null) log.Err(">> 补片失败 " + item.TargetName + "：" + ex.Message);
                    }
                }
            }
        }

        /// <summary>按原目录的命名宽度补零（引擎实际用 "0.ts" 这种无填充命名，宽度 1 时原样输出）。</summary>
        private static string Pad(int index, int width)
        {
            string s = index.ToString();
            while (s.Length < width) s = "0" + s;
            return s;
        }
    }
}
