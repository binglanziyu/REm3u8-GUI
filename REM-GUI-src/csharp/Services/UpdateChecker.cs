// =====================================================================
//  Services\UpdateChecker.cs — 引擎更新检查（镜像竞速 + 测速择优）
//                              + 主程序自身更新检查（GitHub Releases API）
//  清单来源：外部 update.json → 内嵌资源 update.json → DefaultJson 兜底
//  交付形态：dist 只有一个 REM-GUI.exe，内置清单保证单文件也能检查更新
// =====================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Rem.Core;

namespace Rem.Services
{
    public sealed class UpdateManifest
    {
        /// <summary>
        /// 本程序的发布版本：取自 update.json 的 version，缺文件时由 DefaultJson 兜底。
        /// 过去这里硬编码 "2.0.0" 且解析后全工程无人使用，是"字段存在却从不使用"的误导项；
        /// 现在它参与 GUI 自身版本比对（见 UpdateChecker.Check 的 appVer 参数），并由
        /// build_csharp.ps1 以 Program.Version 为真源自动回写，不再手写。
        /// </summary>
        public string AppVer = "";
        public string Notes = "";
        public string Repo = "nilaoda/N_m3u8DL-RE";
        public string AssetHint = "win-x64";
        public List<string> Mirrors = new List<string>();

        /// <summary>主程序自身的仓库（与 engine 并列的 self 段）。</summary>
        public string SelfRepo = DefaultSelfRepo;
        /// <summary>主程序 Release 资产名提示，用于告诉用户该下载哪个文件。</summary>
        public string SelfAssetHint = DefaultSelfAssetHint;

        /// <summary>内嵌清单的资源名（build_csharp.ps1 以 /resource:...,update.json 嵌入）。</summary>
        public const string EmbeddedResourceName = "update.json";
        /// <summary>self 段缺失时的默认仓库：仓库名含中文，拼 URL 前必须逐段编码。</summary>
        public const string DefaultSelfRepo = "binglanziyu/REm3u8-GUI";
        public const string DefaultSelfAssetHint = "REM-GUI.exe";

        /// <summary>内置默认清单：外部文件与内嵌资源都不可用时的兜底。</summary>
        public const string DefaultJson =
            "{" +
            "\"version\":\"3.0.0\"," +
            "\"notes\":\"C# WinForms 重建版\"," +
            "\"engine\":{\"repo\":\"nilaoda/N_m3u8DL-RE\",\"assetHint\":\"win-x64\"," +
            "\"mirrors\":[\"\",\"https://gh-proxy.com/\"]}," +
            "\"self\":{\"repo\":\"binglanziyu/REm3u8-GUI\",\"assetHint\":\"REM-GUI.exe\"}" +
            "}";

        /// <summary>
        /// 读取更新清单，三级回退：外部 update.json（便于手动覆盖调试）→
        /// 内嵌资源（单 exe 交付时的主力来源）→ DefaultJson（编译期兜底）。
        /// 任何一级出问题都不抛异常，保证主流程与引擎检查照常进行。
        /// </summary>
        public static UpdateManifest Load(string appDir, Logger log)
        {
            UpdateManifest m = new UpdateManifest();
            string text = null;
            string origin = "";

            try
            {
                string path = Path.Combine(appDir, "update.json");
                if (File.Exists(path))
                {
                    string raw = File.ReadAllText(path, Encoding.UTF8);
                    if (raw != null && raw.Trim().Length > 2) { text = raw; origin = "外部 update.json"; }
                }
            }
            catch (Exception ex)
            {
                if (log != null) log.Diag("读取外部 update.json 失败：" + ex.Message, "UpdateManifest");
            }

            if (text == null)
            {
                string raw = ReadEmbedded();
                if (raw != null && raw.Trim().Length > 2) { text = raw; origin = "内嵌资源 update.json"; }
            }

            if (text == null) { text = DefaultJson; origin = "DefaultJson 兜底"; }
            if (log != null) log.Diag("更新清单来源：" + origin, "UpdateManifest");

            JVal j = JVal.Parse(text);
            m.AppVer = j.GetStr("version", m.AppVer);
            m.Notes = j.GetStr("notes", m.Notes);
            JVal eng = j.Get("engine");
            if (eng != null)
            {
                m.Repo = eng.GetStr("repo", m.Repo);
                m.AssetHint = eng.GetStr("assetHint", m.AssetHint);
                List<JVal> arr = eng.GetArr("mirrors");
                m.Mirrors.Clear();
                for (int i = 0; i < arr.Count; i++)
                {
                    string s = arr[i].Kind == JKind.String ? arr[i].S : "";
                    if (s == null) s = "";
                    m.Mirrors.Add(s);
                }
            }
            if (m.Mirrors.Count == 0) m.Mirrors.Add("");

            // self 段：老版本清单里没有时用常量兜底，不至于让自更新检查整块失效
            JVal sf = j.Get("self");
            m.SelfRepo = sf != null ? sf.GetStr("repo", DefaultSelfRepo) : DefaultSelfRepo;
            m.SelfAssetHint = sf != null ? sf.GetStr("assetHint", DefaultSelfAssetHint) : DefaultSelfAssetHint;
            if (m.SelfRepo == null) m.SelfRepo = DefaultSelfRepo;
            if (m.SelfAssetHint == null) m.SelfAssetHint = DefaultSelfAssetHint;
            return m;
        }

        /// <summary>读内嵌 update.json；资源缺失或读取异常一律返回 null，由调用方继续回退。</summary>
        private static string ReadEmbedded()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream(EmbeddedResourceName))
                {
                    if (s == null) return null;
                    using (StreamReader rd = new StreamReader(s, Encoding.UTF8, true))
                    {
                        return rd.ReadToEnd();
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public sealed class UpdateResult
    {
        public bool Ok = false;
        public string Message = "";
        /// <summary>REM-GUI 自身版本检查：清单里声明的发布版本。</summary>
        public string AppLatest = "";
        /// <summary>清单版本高于当前程序版本。</summary>
        public bool AppNewer = false;
        /// <summary>GUI 自身版本的展示文案（与引擎版本文案分开，界面单独展示）。</summary>
        public string AppMessage = "";
        /// <summary>在线查到的自身最新版本号（tag_name / name 里解析出的那一个）。</summary>
        public string AppTag = "";
        /// <summary>自身发布页地址：优先 Release 的 html_url，网络不通时回落 releases 列表页。</summary>
        public string AppPageUrl = "";
        /// <summary>自身 Release 里命中的资产名，用于提示用户下载哪个文件。</summary>
        public string AppAssetName = "";
        /// <summary>清单里声明的自身资产名提示（REM-GUI.exe）。</summary>
        public string AppAssetHint = "";
        /// <summary>主程序自身更新检查耗时（毫秒），仅用于诊断日志。</summary>
        public long SelfApiMs = 0;
        public string LatestTag = "";
        public bool Newer = false;
        public string AssetUrl = "";
        public string AssetName = "";
        public long AssetSize = 0;
        public string AssetDigest = "";   // GitHub asset digest，形如 sha256:xxxx，用于下载后完整性校验
        public string BestMirror = "";
        public int ApiMs = -1;
        public int Kbps = -1;
    }

    public static class UpdateChecker
    {
        public static UpdateResult Check(UpdateManifest m, string installedVer, string appVer, AppConfig cfg, Logger log)
        {
            UpdateResult r = new UpdateResult();

            // GUI 自身版本：先拿清单里的 version 与运行中程序版本做纯本地比对，
            // 不依赖网络；镜像全挂、API 403 时也照样能提示"程序有新版本"。
            string appLatest = m == null ? "" : Ver.Normalize(m.AppVer);
            string appCur = Ver.Normalize(appVer);
            if (appLatest.Length > 0 && appCur.Length > 0)
            {
                r.AppLatest = appLatest;
                r.AppNewer = Ver.Compare(appLatest, appCur) > 0;
                if (r.AppNewer)
                {
                    r.AppMessage = "REM-GUI 有新版本 " + Ver.Display(appLatest) + "（当前 " + Ver.Display(appCur) + "）";
                    // 无论后面镜像通不通，这条都要留痕：它决定状态栏是否常驻升级提示
                    if (log != null) log.Warn(">> " + r.AppMessage);
                }
            }

            // 主程序自身更新：联网查 GitHub Releases，拿到版本号与发布页。
            // 内部逐层 try/catch，网络失败 / 403 限流 / JSON 缺字段都不抛、不阻塞引擎检查。
            CheckSelf(m, appCur, cfg, log, r);

            if (m == null || string.IsNullOrEmpty(m.Repo))
            {
                r.Message = "检查失败：未配置引擎仓库";
                return r;
            }

            string apiPath = "https://api.github.com/repos/" + m.Repo + "/releases/latest";
            JVal best = null;
            int bestMs = int.MaxValue;
            string bestMirror = "";
            int okCount = 0;

            for (int i = 0; i < m.Mirrors.Count; i++)
            {
                string mirror = m.Mirrors[i] == null ? "" : m.Mirrors[i].Trim();
                string url = mirror.Length == 0 ? apiPath : Combine(mirror, apiPath);
                int status;
                long ms;
                string body = Http.GetText(url, cfg, 6000, out status, out ms);
                if (body == null || body.Length < 10)
                {
                    if (log != null) log.Diag("镜像请求失败 " + (mirror.Length == 0 ? "直连" : mirror) + " status=" + status + " ms=" + ms + " (" + Http.LastError + ")", "Update");
                    continue;
                }
                JVal j = JVal.Parse(body);
                string tag = j.GetStr("tag_name", "");
                if (tag.Length == 0) continue;
                okCount++;
                if (ms < bestMs)
                {
                    bestMs = (int)ms;
                    best = j;
                    bestMirror = mirror;
                }
            }

            if (best == null)
            {
                r.Message = "检查失败：镜像不可达";
                // 引擎查不到不代表程序没新版：本地比对的结论照样带给界面
                if (r.AppNewer) r.Message += "；" + r.AppMessage;
                if (log != null) log.Warn(">> 检查更新失败：" + r.Message);
                return r;
            }

            r.Ok = true;
            r.ApiMs = bestMs;
            r.BestMirror = bestMirror;
            r.LatestTag = best.GetStr("tag_name", "");

            // 选资产：优先 assetHint，其次含 win 的 zip
            string assetUrl = "";
            string assetName = "";
            long assetSize = 0;
            string assetDigest = "";
            List<JVal> assets = best.GetArr("assets");
            JVal pick = null;
            for (int pass = 0; pass < 3 && pick == null; pass++)
            {
                for (int i = 0; i < assets.Count; i++)
                {
                    string name = assets[i].GetStr("name", "");
                    string lower = name.ToLowerInvariant();
                    bool hit;
                    if (pass == 0) hit = m.AssetHint.Length > 0 && lower.IndexOf(m.AssetHint.ToLowerInvariant()) >= 0;
                    else if (pass == 1) hit = lower.IndexOf("win") >= 0 && lower.EndsWith(".zip");
                    else hit = lower.EndsWith(".zip");
                    if (hit) { pick = assets[i]; break; }
                }
            }
            if (pick != null)
            {
                assetUrl = pick.GetStr("browser_download_url", "");
                assetName = pick.GetStr("name", "");
                assetSize = pick.GetInt("size", 0);
                assetDigest = NormalizeDigest(pick.GetStr("digest", ""));
            }
            r.AssetUrl = assetUrl;
            r.AssetName = assetName;
            r.AssetSize = assetSize;
            r.AssetDigest = assetDigest;
            r.Newer = Ver.Compare(r.LatestTag, installedVer) > 0;

            // 用 64KB 探测下载速率，挑最快的镜像前缀
            if (assetUrl.Length > 0)
            {
                int bestKbps = -1;
                string speedMirror = bestMirror;
                for (int i = 0; i < m.Mirrors.Count; i++)
                {
                    string mirror = m.Mirrors[i] == null ? "" : m.Mirrors[i].Trim();
                    string url = mirror.Length == 0 ? assetUrl : Combine(mirror, assetUrl);
                    int st;
                    long ms;
                    byte[] data = Http.GetBytes(url, cfg, 9000, 65536, out st, out ms);
                    if (data == null || data.Length < 4096 || ms <= 0) continue;
                    int kbps = (int)(data.Length / 1024.0 * 1000.0 / ms);
                    if (kbps > bestKbps)
                    {
                        bestKbps = kbps;
                        speedMirror = mirror;
                    }
                }
                r.Kbps = bestKbps;
                r.BestMirror = speedMirror;
                if (speedMirror.Length > 0 && assetUrl.Length > 0)
                {
                    r.AssetUrl = Combine(speedMirror, assetUrl);
                }
            }

            if (log != null)
            {
                log.Info(">> 更新检查完成：最新 " + Ver.Display(r.LatestTag)
                         + "，用时 " + r.ApiMs + " ms"
                         + (r.Kbps > 0 ? "，带宽约 " + r.Kbps + " KB/s" : "")
                         + (r.BestMirror.Length > 0 ? "，镜像 " + r.BestMirror : "，直连"));
            }

            if (r.Newer) r.Message = "发现新版本 " + Ver.Display(r.LatestTag);
            else r.Message = "已是最新 " + Ver.Display(r.LatestTag);
            // 程序自身版本的结论单独给出，不混进"引擎已是最新"的文案里误导用户
            return r;
        }

        // ------------------------------------------------------------------
        //  主程序自身更新（GitHub Releases API）
        //  只做「提示 + 可打开/复制发布页」，不下载、不替换运行中的 exe：
        //  Windows 上覆盖正在运行的 exe 必然失败，自动替换属于另一次性的安装器工程。
        // ------------------------------------------------------------------

        /// <summary>
        /// 检查 REM-GUI 自身是否有新版本。任何异常（网络不通、403 限流、JSON 缺字段）都在内部吃掉，
        /// 只写诊断日志，绝不向外抛，也不影响引擎检查与主流程。
        /// </summary>
        public static void CheckSelf(UpdateManifest m, string appCur, AppConfig cfg, Logger log, UpdateResult r)
        {
            if (m == null || r == null) return;

            string repo = m.SelfRepo == null ? "" : m.SelfRepo.Trim();
            r.AppAssetHint = m.SelfAssetHint == null ? "" : m.SelfAssetHint;
            if (repo.Length == 0) return;

            // 先给出仓库发布页：即使这次联网失败，界面也还有可点的地址
            r.AppPageUrl = RepoPageUrl(repo);

            try
            {
                int status;
                long ms;
                string body = Http.GetText(RepoApiUrl(repo), cfg, 6000, out status, out ms);
                r.SelfApiMs = ms;
                if (body == null || body.Length < 10)
                {
                    if (log != null)
                    {
                        log.Diag("主程序更新检查未拿到有效响应 status=" + status
                                 + " " + Http.LastError, "UpdateSelf");
                    }
                    return;
                }

                JVal j = JVal.Parse(body);
                string url = j.GetStr("html_url", "");
                if (url.Length > 0) r.AppPageUrl = url;

                string asset = PickAssetName(j, m.SelfAssetHint);
                if (asset.Length > 0) r.AppAssetName = asset;

                string ver = PickVersion(j.GetStr("tag_name", ""), j.GetStr("name", ""));
                if (ver.Length == 0)
                {
                    if (log != null) log.Diag("主程序更新：Release 里没有可识别的版本号", "UpdateSelf");
                    return;
                }
                r.AppTag = ver;

                if (appCur.Length == 0 || Ver.Compare(ver, appCur) <= 0)
                {
                    // 已是最新：不清空清单侧可能已经给出的"有新版本"结论，只补一条日志
                    if (log != null) log.Diag("主程序更新：线上最新 " + ver + "，当前 " + appCur, "UpdateSelf");
                    return;
                }

                r.AppLatest = ver;
                r.AppNewer = true;
                r.AppMessage = "REM-GUI 有新版本 " + Ver.Display(ver) + "（当前 " + Ver.Display(appCur) + "）";
                if (log != null)
                {
                    log.Warn(">> " + r.AppMessage
                             + (r.AppAssetName.Length > 0 ? "，下载 " + r.AppAssetName : "")
                             + "，发布页 " + r.AppPageUrl);
                }
            }
            catch (Exception ex)
            {
                if (log != null) log.Diag("主程序更新检查异常：" + ex.Message, "UpdateSelf");
            }
        }

        /// <summary>
        /// 仓库名逐段百分号编码。仓库名含中文时若直接拼进 URL，
        /// api.github.com 会返回 404，因此按 "/" 拆段、每段单独 Uri.EscapeDataString 后再拼回。
        /// </summary>
        public static string EncodeRepo(string repo)
        {
            if (string.IsNullOrEmpty(repo)) return "";
            string[] seg = repo.Trim().Split('/');
            List<string> outp = new List<string>();
            for (int i = 0; i < seg.Length; i++)
            {
                string s = seg[i].Trim();
                if (s.Length == 0) continue;
                outp.Add(Uri.EscapeDataString(s));
            }
            return string.Join("/", outp.ToArray());
        }

        /// <summary>最新 Release 的 API 地址（仓库段已编码）。</summary>
        public static string RepoApiUrl(string repo)
        {
            return "https://api.github.com/repos/" + EncodeRepo(repo) + "/releases/latest";
        }

        /// <summary>仓库的 Releases 页面地址（仓库段已编码），网络不通时作为可点的兜底地址。</summary>
        public static string RepoPageUrl(string repo)
        {
            return "https://github.com/" + EncodeRepo(repo) + "/releases";
        }

        private static readonly Regex VerRx = new Regex(@"\d+(?:\.\d+){1,3}", RegexOptions.Compiled);

        /// <summary>
        /// 从 tag_name / name 里取版本号：tag 本身是纯版本号（如 v3.1.0）就用它，
        /// 否则到 Release 名称里找形如 3.1.0 的数字串。都找不到返回空串。
        /// </summary>
        public static string PickVersion(string tag, string name)
        {
            string v = Ver.Normalize(tag);
            if (IsNumericVer(v)) return v;
            Match m1 = VerRx.Match(name == null ? "" : name);
            if (m1.Success) return m1.Value;
            Match m2 = VerRx.Match(tag == null ? "" : tag);
            return m2.Success ? m2.Value : "";
        }

        /// <summary>是否形如 3 / 3.1 / 3.1.2（只含数字与点，且至少一个点或纯数字）。</summary>
        public static bool IsNumericVer(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            int dots = 0;
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '.') { dots++; continue; }
                if (c < '0' || c > '9') return false;
            }
            return v.Length > 0 && v[v.Length - 1] != '.';
        }

        /// <summary>
        /// 在 Release 的 assets 里挑出该提示用户下载的文件：先找名字里含 assetHint 的，
        /// 再退化为任意 .exe；都没有就回落到清单声明的 hint。
        /// </summary>
        private static string PickAssetName(JVal release, string hint)
        {
            if (release == null) return hint == null ? "" : hint;
            List<JVal> assets = release.GetArr("assets");
            string want = hint == null ? "" : hint.Trim().ToLowerInvariant();
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < assets.Count; i++)
                {
                    string name = assets[i].GetStr("name", "");
                    if (name.Length == 0) continue;
                    string lower = name.ToLowerInvariant();
                    bool hit = pass == 0 ? (want.Length > 0 && lower.IndexOf(want) >= 0)
                                         : lower.EndsWith(".exe");
                    if (hit) return name;
                }
            }
            return hint == null ? "" : hint;
        }

        /// <summary>把 API 返回的 "sha256:xxxx" 归一成纯十六进制；无法识别返回空串。</summary>
        private static string NormalizeDigest(string digest)
        {
            if (string.IsNullOrEmpty(digest)) return "";
            string s = digest.Trim();
            int colon = s.IndexOf(':');
            if (colon > 0)
            {
                string algo = s.Substring(0, colon).Trim();
                if (!string.Equals(algo, "sha256", StringComparison.OrdinalIgnoreCase)) return "";
                s = s.Substring(colon + 1).Trim();
            }
            return s.Length == 64 ? s : "";
        }

        private static string Combine(string prefix, string url)
        {
            if (string.IsNullOrEmpty(prefix)) return url;
            string p = prefix.Trim();
            if (!p.EndsWith("/")) p += "/";
            return p + url.TrimStart('/');
        }
    }
}
