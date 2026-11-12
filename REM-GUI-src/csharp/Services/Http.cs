// =====================================================================
//  Services\Http.cs — 统一的 HTTP 取数（关键修复：Accept 必须用属性赋值）
//  .NET Framework 的 HttpWebRequest 上 Accept / User-Agent 是受限头，
//  走 Headers.Add() 会抛 ArgumentException，历史上正是“镜像不可达”的根因。
// =====================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using Rem.Core;

namespace Rem.Services
{
    public static class Http
    {
        private const string UA = "REM-GUI/3.0 (+https://github.com/nilaoda/N_m3u8DL-RE)";

        /// <summary>最近一次失败的原始原因（按线程隔离，避免多个后台任务互相覆写）。</summary>
        [ThreadStatic]
        private static string lastError;

        public static string LastError
        {
            get { return lastError == null ? "" : lastError; }
        }

        public static IWebProxy MakeProxy(string proxy)
        {
            if (string.IsNullOrEmpty(proxy)) return null;
            string p = proxy.Trim();
            if (p.StartsWith("socks", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                if (p.IndexOf("://") < 0) p = "http://" + p;
                Uri u = new Uri(p);
                WebProxy wp = new WebProxy(u);
                string userInfo = u.UserInfo;
                if (!string.IsNullOrEmpty(userInfo))
                {
                    string[] kv = userInfo.Split(':');
                    string user = Uri.UnescapeDataString(kv[0]);
                    string pass = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
                    wp.Credentials = new NetworkCredential(user, pass);
                }
                return wp;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static HttpWebRequest Create(string url, AppConfig cfg, int timeoutMs, string extraAccept)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = UA;                                  // 受限头 → 属性赋值
            bool githubApi = url.IndexOf("api.github.com", StringComparison.OrdinalIgnoreCase) >= 0;
            req.Accept = githubApi ? "application/vnd.github+json"   // ← 修复点：属性赋值
                                   : (string.IsNullOrEmpty(extraAccept) ? "*/*" : extraAccept);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 6;
            req.KeepAlive = false;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            IWebProxy proxy = MakeProxy(cfg == null ? "" : cfg.Proxy);
            if (proxy != null) req.Proxy = proxy;
            return req;
        }

        public static string GetText(string url, AppConfig cfg, int timeoutMs, out int status, out long elapsedMs)
        {
            status = 0;
            Stopwatch sw = Stopwatch.StartNew();
            elapsedMs = 0;
            try
            {
                HttpWebRequest req = Create(url, cfg, timeoutMs, null);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    status = (int)resp.StatusCode;
                    using (Stream s = resp.GetResponseStream())
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8, true))
                    {
                        string text = sr.ReadToEnd();
                        sw.Stop();
                        elapsedMs = sw.ElapsedMilliseconds;
                        return text;
                    }
                }
            }
            catch (WebException wex)
            {
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                HttpWebResponse r = wex.Response as HttpWebResponse;
                if (r != null) { status = (int)r.StatusCode; try { r.Close(); } catch (Exception __ex) { Note.Swallow("Http.cs:99", __ex); } }
                else status = -1;
                lastError = "web: " + wex.Status + " / " + wex.Message;
                return null;
            }
            catch (Exception ex)
            {
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                status = -1;
                lastError = "exc: " + ex.Message;
                return null;
            }
        }

        public static byte[] GetBytes(string url, AppConfig cfg, int timeoutMs, int maxBytes, out int status, out long elapsedMs)
        {
            status = 0;
            Stopwatch sw = Stopwatch.StartNew();
            elapsedMs = 0;
            try
            {
                HttpWebRequest req = Create(url, cfg, timeoutMs, null);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    status = (int)resp.StatusCode;
                    using (Stream s = resp.GetResponseStream())
                    {
                        MemoryStream ms = new MemoryStream();
                        byte[] buf = new byte[16384];
                        int total = 0;
                        while (total < maxBytes)
                        {
                            int n = s.Read(buf, 0, Math.Min(buf.Length, maxBytes - total));
                            if (n <= 0) break;
                            ms.Write(buf, 0, n);
                            total += n;
                        }
                        sw.Stop();
                        elapsedMs = sw.ElapsedMilliseconds;
                        return ms.ToArray();
                    }
                }
            }
            catch (WebException wex)
            {
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                HttpWebResponse r = wex.Response as HttpWebResponse;
                if (r != null) { status = (int)r.StatusCode; try { r.Close(); } catch (Exception __ex) { Note.Swallow("Http.cs:148", __ex); } }
                else status = -1;
                lastError = "web: " + wex.Status + " / " + wex.Message;
                return null;
            }
            catch (Exception ex)
            {
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                status = -1;
                lastError = "exc: " + ex.Message;
                return null;
            }
        }

        /// <summary>探测分片可下载性：只取响应头即断开；5xx 视为可重试，重试一次。</summary>
        public static int Head(string url, AppConfig cfg, string headers, int timeoutMs)
        {
            int code = HeadOnce(url, cfg, headers, timeoutMs);
            if (code >= 500) code = HeadOnce(url, cfg, headers, timeoutMs);
            return code;
        }

        private static int HeadOnce(string url, AppConfig cfg, string headers, int timeoutMs)
        {
            try
            {
                // 统一走 Create()：代理、UA、超时、重定向策略只有一处定义，
                // 早前这里是复制的第二份实现，改一处忘另一处就会行为不一致。
                HttpWebRequest req = Create(url, cfg, timeoutMs, null);
                bool hasRange = false;
                System.Collections.Generic.List<string> hs = Rem.Engine.ArgBuilder.HeaderLines(headers);
                for (int i = 0; i < hs.Count; i++)
                {
                    int k = hs[i].IndexOf(':');
                    if (k <= 0) continue;
                    string key = hs[i].Substring(0, k).Trim();
                    string val = hs[i].Substring(k + 1).Trim();
                    if (string.Equals(key, "Range", StringComparison.OrdinalIgnoreCase)) hasRange = true;
                    try
                    {
                        if (string.Equals(key, "Referer", StringComparison.OrdinalIgnoreCase)) req.Referer = val;
                        else if (string.Equals(key, "User-Agent", StringComparison.OrdinalIgnoreCase)) req.UserAgent = val;
                        else if (string.Equals(key, "Accept", StringComparison.OrdinalIgnoreCase)) req.Accept = val;
                        else if (string.Equals(key, "Cookie", StringComparison.OrdinalIgnoreCase)) req.Headers[HttpRequestHeader.Cookie] = val;
                        else req.Headers[key] = val;
                    }
                    catch (Exception __ex) { Note.Swallow("Http.cs:195", __ex); }
                }
                // 只想要响应头：带 Range 的请求不回整包，避免预检把一整个分片拉下来
                if (!hasRange)
                {
                    try { req.AddRange(0, 0); } catch (Exception __ex) { Note.Swallow("Http.cs:200", __ex); }
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)resp.StatusCode;
                    try { resp.Close(); } catch (Exception __ex) { Note.Swallow("Http.cs:206", __ex); }
                    // 206 是对分段请求的正常回应，归一成 200 便于上层统一判断
                    return code == 206 ? 200 : code;
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse r = wex.Response as HttpWebResponse;
                if (r != null) { int code = (int)r.StatusCode; try { r.Close(); } catch (Exception __ex) { Note.Swallow("Http.cs:214", __ex); } return code; }
                return -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>下载到文件，带进度回调（0-100，可能为 -1 表示未知）。先写 .part，完整校验后再原子换名。</summary>
        public static bool DownloadToFile(string url, AppConfig cfg, int timeoutMs, string target,
                                          Action<int> onProgress, out string error)
        {
            error = "";
            string part = target + ".part";
            try
            {
                int t = timeoutMs > 0 ? timeoutMs : 30000;
                HttpWebRequest req = Create(url, cfg, t, null);
                // 整体超时只管“连上并拿到响应头”，读取阶段按连接超时的 4 倍放行，避免大包被腰斩
                req.Timeout = t;
                req.ReadWriteTimeout = Math.Max(30000, t * 4);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                {
                    long len = resp.ContentLength;
                    byte[] buf = new byte[81920];
                    long total = 0;
                    int lastPct = -1;
                    using (FileStream fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                    {
                        while (true)
                        {
                            int n = s.Read(buf, 0, buf.Length);
                            if (n <= 0) break;
                            fs.Write(buf, 0, n);
                            total += n;
                            int pct = len > 0 ? (int)(total * 100 / len) : -1;
                            if (onProgress != null && pct != lastPct) { lastPct = pct; onProgress(pct); }
                        }
                        fs.Flush(true);
                    }
                    // 长度对不上 = 传输被截断，宁可失败也不留半个包
                    if (len > 0 && total != len)
                    {
                        error = "下载不完整：" + total + "/" + len + " 字节";
                        Fs.TryDeleteFile(part);
                        return false;
                    }
                }
                if (!Fs.MoveAtomic(part, target))
                {
                    error = "写入目标文件失败：" + target;
                    Fs.TryDeleteFile(part);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Fs.TryDeleteFile(part);
                return false;
            }
        }
    }
}
