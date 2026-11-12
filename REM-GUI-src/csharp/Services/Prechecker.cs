// =====================================================================
//  Services\Prechecker.cs — 下载前预检：主清单解析 / 分片可用性探测
//  目的：在把任务丢给引擎之前识别 401/403/失效链接，省掉一次完整等待
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Rem.Services
{
    public sealed class VariantInfo
    {
        public long Bandwidth = 0;
        public string Resolution = "";
        public string Codecs = "";
        public string Fps = "";
        public string Uri = "";
        public string ResolvedUri = "";

        public string Label
        {
            get
            {
                string bw = Bandwidth > 0 ? (Bandwidth / 1000).ToString(CultureInfo.InvariantCulture) + " kbps" : "";
                string codec = Codecs;
                if (codec.Length > 28) codec = codec.Substring(0, 28);
                string s = (Resolution.Length > 0 ? Resolution : "未知清晰度");
                if (bw.Length > 0) s += "   " + bw;
                if (Fps.Length > 0) s += "   " + Fps + "fps";
                if (codec.Length > 0) s += "   " + codec;
                return s;
            }
        }
    }

    public sealed class PrecheckResult
    {
        public string Kind = "ok";     // ok / master / abort
        public string Message = "";
        public List<VariantInfo> Variants = new List<VariantInfo>();
        public string ResolvedUrl = "";
        public int Segments = 0;
    }

    public static class Prechecker
    {
        /// <summary>链接合法性判定：必须是能解析的绝对 http/https 地址（D6）。</summary>
        public static bool IsHttpUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            try
            {
                Uri u;
                if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
                return u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly Regex RxAttr = new Regex("([A-Z0-9\\-]+)=(\"[^\"]*\"|[^,]*)", RegexOptions.Compiled);

        public static PrecheckResult Run(string url, AppConfig cfg, Logger log)
        {
            PrecheckResult r = new PrecheckResult();
            if (string.IsNullOrEmpty(url))
            {
                r.Kind = "abort";
                r.Message = "链接为空";
                return r;
            }
            // 非法 URL 不再“跳过预检直接交给引擎”：引擎实跑时只会给 exit=1 和
            // 藏在 stdout 前段的“加载URL失败”，界面根本看不出原因（D6）
            if (!IsHttpUrl(url))
            {
                r.Kind = "abort";
                r.Message = "链接格式不正确：需要以 http:// 或 https:// 开头的完整地址";
                return r;
            }

            int status;
            long ms;
            string text = Http.GetText(url, cfg, 15000, out status, out ms);

            if (text == null)
            {
                if (status == 401 || status == 403)
                {
                    r.Kind = "abort";
                    r.Message = "预检中止：链接需要鉴权或已失效（HTTP " + status + "），请刷新 Cookie/Header 后重试";
                }
                else if (status == 404)
                {
                    r.Kind = "abort";
                    r.Message = "预检中止：清单不存在（HTTP 404）";
                }
                else
                {
                    // 探测不到就交给引擎自己报错，不阻断用户
                    r.Kind = "ok";
                    r.Message = "预检跳过：清单无法读取（状态 " + status + "），交由引擎直接尝试";
                }
                return r;
            }

            string body = text.TrimStart('\uFEFF', '\r', '\n', ' ');
            if (body.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) == false)
            {
                r.Kind = "ok";
                r.Message = "预检通过：非 HLS 清单，按直链处理";
                return r;
            }

            if (body.IndexOf("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                r.Variants = ParseMaster(body, url);
                if (r.Variants.Count > 0)
                {
                    r.Kind = "master";
                    r.Message = "发现 " + r.Variants.Count + " 条清晰度可选";
                    return r;
                }
            }

            // 媒体清单：数分片 + 探首个分片
            string[] lines = body.Replace("\r\n", "\n").Split('\n');
            int count = 0;
            string firstSeg = "";
            string lastSeg = "";
            bool encrypted = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i].Trim();
                if (ln.Length == 0) continue;
                if (ln.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) count++;
                else if (ln.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase))
                {
                    // METHOD=NONE 是明文，其余（AES-128 / SAMPLE-AES）都要密钥才能取
                    if (ln.IndexOf("METHOD=NONE", StringComparison.OrdinalIgnoreCase) < 0) encrypted = true;
                }
                else if (ln[0] != '#')
                {
                    if (firstSeg.Length == 0) firstSeg = ln;
                    lastSeg = ln;
                }
            }
            r.Segments = count;

            // 首片和尾片都探一次：失效链接常见于尾部（签名过期后正好断在最后几个分片）
            string badSeg = "";
            int badStatus = 0;
            ProbeSegment(url, firstSeg, cfg, ref badSeg, ref badStatus);
            if (badSeg.Length == 0 && lastSeg.Length > 0 && !string.Equals(lastSeg, firstSeg, StringComparison.Ordinal))
            {
                ProbeSegment(url, lastSeg, cfg, ref badSeg, ref badStatus);
            }
            if (badSeg.Length > 0)
            {
                // 单个分片 401/403/404 不再中止任务：签名过期、个别分片被 CDN 剔除都属常态。
                // 交给引擎重试；真下不来时由 GUI 的自动补片用前后相邻分片补齐，比直接不跑更有价值
                r.Kind = "ok";
                r.Message = "预检提示：有分片不可用（HTTP " + badStatus + "），仍会尝试下载，失败时自动用相邻分片补齐";
                return r;
            }

            r.Kind = "ok";
            string msg = "预检通过：" + (count > 0 ? count + " 个分片" : "分片数未知");
            if (encrypted) msg += "（加密流，需 KEY 才能解密）";
            r.Message = msg;
            return r;
        }

        /// <summary>探测单个分片；不可用时把地址与状态码带出，供上层给出明确中止原因。</summary>
        private static void ProbeSegment(string baseUrl, string seg, AppConfig cfg, ref string badSeg, ref int badStatus)
        {
            if (string.IsNullOrEmpty(seg)) return;
            string abs;
            try { abs = new Uri(new Uri(baseUrl), seg).ToString(); }
            catch (Exception) { abs = seg; }
            int st = Http.Head(abs, cfg, cfg == null ? "" : cfg.Headers, 10000);
            if (st == 401 || st == 403 || st == 404)
            {
                badSeg = abs;
                badStatus = st;
            }
        }

        private static List<VariantInfo> ParseMaster(string body, string baseUrl)
        {
            List<VariantInfo> list = new List<VariantInfo>();
            string[] lines = body.Replace("\r\n", "\n").Split('\n');
            string pending = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i].Trim();
                if (ln.Length == 0) continue;
                if (ln.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                {
                    pending = ln;
                    continue;
                }
                if (ln[0] == '#') continue;
                if (pending != null)
                {
                    VariantInfo v = ParseAttrs(pending);
                    v.Uri = ln;
                    try { v.ResolvedUri = new Uri(new Uri(baseUrl), ln).ToString(); }
                    catch (Exception) { v.ResolvedUri = ln; }
                    list.Add(v);
                    pending = null;
                }
            }
            list.Sort(delegate (VariantInfo a, VariantInfo b) { return b.Bandwidth.CompareTo(a.Bandwidth); });
            return list;
        }

        private static VariantInfo ParseAttrs(string line)
        {
            VariantInfo v = new VariantInfo();
            string raw = line;
            int colon = raw.IndexOf(':');
            if (colon >= 0) raw = raw.Substring(colon + 1);
            foreach (Match m in RxAttr.Matches(raw))
            {
                string key = m.Groups[1].Value.ToUpperInvariant();
                string val = m.Groups[2].Value.Trim().Trim('"');
                if (key == "BANDWIDTH") { long bw; if (long.TryParse(val, out bw)) v.Bandwidth = bw; }
                else if (key == "RESOLUTION") v.Resolution = val;
                else if (key == "CODECS") v.Codecs = val;
                else if (key == "FRAME-RATE") v.Fps = val;
            }
            return v;
        }
    }
}
