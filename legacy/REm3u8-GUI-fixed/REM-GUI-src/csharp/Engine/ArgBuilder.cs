// =====================================================================
//  Engine\ArgBuilder.cs — 把界面状态编译成 N_m3u8DL-RE 命令行
//  纯计算，不碰 UI、不碰进程；仅对磁盘做“同名文件是否存在”的只读判断
// =====================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Rem.Core;
using Rem.Services;

namespace Rem.Engine
{
    public sealed class BuiltArgs
    {
        public List<string> Args = new List<string>();
        public string Url = "";
        public string SaveDir = "";
        public string SaveName = "";
        public string TmpDir = "";
        public string LogFile = "";
        public string CommandLine = "";
    }

    public static class ArgBuilder
    {
        private static readonly string[] MediaExt = new string[]
        {
            ".mp4", ".mkv", ".ts", ".flv", ".m4a", ".mp3", ".aac", ".m4v",
            ".mov", ".webm", ".mts", ".m2ts", ".srt", ".ass", ".sup", ".vtt"
        };

        public static BuiltArgs Build(AppConfig cfg, string url, string saveDir, string rawName,
                                      string engineExe, string ffmpegExe, string logFile,
                                      string resumeTmp, string selectVideo)
        {
            BuiltArgs b = new BuiltArgs();
            b.Url = url;
            b.SaveDir = saveDir;
            b.LogFile = logFile;

            bool resume = !string.IsNullOrEmpty(resumeTmp);

            string name = Safe.FileName(rawName);
            if (name.Length == 0) name = Safe.FileName(GuessNameFromUrl(url));
            if (name.Length == 0) name = "download_" + DateTime.Now.ToString("MMdd_HHmmss");
            if (!resume) name = UniqueName(saveDir, name);
            b.SaveName = name;

            // 续传时 resumeTmp 已经是「...\.parts\<名字>」的最终值（续传文件里写的就是它），
            // 这里再拼一次文件名会变成「...\.parts\名\名」，引擎永远命中不了原分片，
            // 表现为「续传静默退化成全量重下 + .parts 下每次再叠一层同名目录」（D1）
            b.TmpDir = resume ? resumeTmp : Path.Combine(saveDir, ".parts", name);

            List<string> a = b.Args;
            a.Add(url);
            a.Add("--save-dir"); a.Add(saveDir);
            a.Add("--save-name"); a.Add(name);
            a.Add("--tmp-dir"); a.Add(b.TmpDir);

            a.Add("--thread-count"); a.Add(cfg.Threads.ToString());
            a.Add("--download-retry-count"); a.Add(cfg.Retries.ToString());
            a.Add("--http-request-timeout"); a.Add(cfg.Timeout.ToString());

            if (!string.IsNullOrEmpty(ffmpegExe))
            {
                a.Add("--ffmpeg-binary-path"); a.Add(ffmpegExe);
            }
            if (cfg.Mux && !cfg.SkipMerge)
            {
                a.Add("-M"); a.Add("format=mp4");
            }
            if (cfg.Concurrent && !cfg.SkipDownload) a.Add("-mt");
            // 混流一旦生效，引擎会自己打开二进制合并（日志：「你已开启下载完成后混流，自动开启二进制合并」）。
            // 这里显式下发，让参数预览和真实行为一致：预览里看得到，用户才不会以为没合并。
            if (cfg.Binary || (cfg.Mux && !cfg.SkipMerge && !cfg.SkipDownload)) a.Add("--binary-merge");
            if (cfg.SkipMerge) a.Add("--skip-merge");
            if (cfg.SkipDownload) a.Add("--skip-download");
            if (cfg.AppendUrlParams) a.Add("--append-url-params");
            if (!cfg.DelAfterDone) { a.Add("--del-after-done"); a.Add("false"); }
            if (!cfg.CheckSegments) { a.Add("--check-segments-count"); a.Add("false"); }

            if (!string.IsNullOrEmpty(selectVideo))
            {
                a.Add("-sv"); a.Add(selectVideo);
            }
            else if (cfg.AutoSelect)
            {
                // "自动最高画质"名副其实：显式要求视频/音频各取最佳（-sv best / -sa best，见引擎 morehelp），
                // + --auto-select 自动确认选轨，避免无输入通道时挂住
                a.Add("--auto-select");
                a.Add("-sv"); a.Add("best");
                a.Add("-sa"); a.Add("best");
            }
            else
            {
                // 用户关掉“自动选择”时也必须给兜底轨选择：GUI 没有交互式选轨界面，
                // 引擎一旦进入选轨提问就会一直等 stdin（stdin 已重定向且启动后立即关闭）→ 任务挂住（D3）
                a.Add("-sv"); a.Add("best");
                a.Add("-sa"); a.Add("best");
            }

            // ---- 解密：AES-128 + 手动 KEY / IV（只在用户真填了 KEY/IV 时才下发）----
            // 已彻底移除的能力：--decryption-engine、--decryption-binary-path、--mp4-real-time-decryption、
            // --key（KID:KEY 多组）、--key-text-file —— 这些参数一律不再出现在命令行里。
            // 必须条件下发：恒发 --custom-hls-method AES_128 会让引擎对明文流也强行 AES 解密，
            // 直接崩在 Crypto.AESUtil.AES128Decrypt，实测 exit=1、一个分片都落不了盘。
            string hk = NormKey(cfg.HlsKey);
            string hiv = NormKey(cfg.HlsIv);
            if (hk.Length > 0 || hiv.Length > 0)
            {
                a.Add("--custom-hls-method"); a.Add(AppConfig.HlsMethodAes128);
            }
            if (hk.Length > 0) { a.Add("--custom-hls-key"); a.Add(hk); }
            if (hiv.Length > 0) { a.Add("--custom-hls-iv"); a.Add(hiv); }

            List<string> hdrs = HeaderLines(cfg.Headers);
            for (int i = 0; i < hdrs.Count; i++)
            {
                a.Add("-H"); a.Add(hdrs[i]);
            }

            if (!string.IsNullOrEmpty(cfg.Proxy))
            {
                a.Add("--custom-proxy"); a.Add(cfg.Proxy.Trim());
            }

            // 系统代理开关（D8）：默认 false = 不用系统代理，必须显式下发 --use-system-proxy false
            // （引擎自带默认是 True，不下发就等于走系统代理，所以默认必须写出来）；
            // 勾选「使用系统代理」= true → 不下发该参数，沿用引擎默认 True。
            // 界面命令行预览与实际下发同源（都取 BuiltArgs.CommandLine）
            if (!cfg.UseSystemProxy)
            {
                a.Add("--use-system-proxy"); a.Add("false");
            }
            string speed = cfg.SpeedArg();
            if (speed.Length > 0)
            {
                a.Add("--max-speed"); a.Add(speed);
            }
            if (!string.IsNullOrEmpty(logFile))
            {
                a.Add("--log-file-path"); a.Add(logFile);
            }

            List<string> extra = ArgQ.Split(cfg.ExtraArgs);
            for (int i = 0; i < extra.Count; i++) a.Add(extra[i]);

            StringBuilder sb = new StringBuilder();
            sb.Append(ArgQ.Quote(engineExe));
            for (int i = 0; i < a.Count; i++)
            {
                sb.Append(' ');
                sb.Append(ArgQ.Quote(a[i]));
            }
            b.CommandLine = Mask.Secret(sb.ToString());
            return b;
        }

        // 解密方式只剩 AES-128 一种：下拉选项常量与"加密方式/解密引擎"两个归一函数一并删除，
        // 只有填了 KEY/IV 时才下发 --custom-hls-method AES_128（值来自 AppConfig.HlsMethodAes128）

        /// <summary>KEY / IV 去空白与常见分隔符（冒号、空格）后的原样值，不做大小写改写。</summary>
        private static string NormKey(string raw)
        {
            if (raw == null) return "";
            return raw.Trim().Replace(" ", "").Replace("\t", "");
        }

        /// <summary>
        /// 判断 KEY/IV 的文本形态，仅用于界面即时提示（HEX / BASE64 / 无法识别）。
        /// 不做强制拦截：引擎对同一取值还有别的写法容忍度，拦死反而堵路。
        /// </summary>
        public static string KeyShape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Trim();
            if (s.Length == 0) return "";

            bool hex = true;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) { hex = false; break; }
            }
            // 16 字节 = 32 位 hex；也允许 8/24/32/48/64 位（IV 8 字节、多密钥拼接等）
            if (hex && (s.Length == 16 || s.Length == 32 || s.Length == 48 || s.Length == 64)) return "HEX";

            bool b64 = s.Length % 4 == 0;
            for (int i = 0; b64 && i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                          || c == '+' || c == '/' || c == '=';
                if (!ok) b64 = false;
            }
            if (b64) return "BASE64";

            return "";
        }

        public static List<string> HeaderLines(string headers)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(headers)) return list;
            string[] lines = headers.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i].Trim();
                if (ln.Length == 0) continue;
                int k = ln.IndexOf(':');
                if (k <= 0) continue;
                string key = ln.Substring(0, k).Trim();
                string val = ln.Substring(k + 1).Trim();
                if (key.Length == 0 || val.Length == 0) continue;
                list.Add(key + ": " + val);
            }
            return list;
        }

        /// <summary>同名去重：目标目录已存在同名媒体文件则追加 _2/_3…，规避 RE 的 .copy 链。</summary>
        public static string UniqueName(string dir, string name)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return name;
                if (!ExistsWithMediaExt(dir, name)) return name;
                for (int i = 2; i < 1000; i++)
                {
                    string cand = name + "_" + i.ToString();
                    if (!ExistsWithMediaExt(dir, cand)) return cand;
                }
            }
            catch (Exception __ex) { Note.Swallow("ArgBuilder.cs:154", __ex); }
            return name + "_" + DateTime.Now.ToString("HHmmss");
        }

        private static bool ExistsWithMediaExt(string dir, string name)
        {
            try
            {
                for (int i = 0; i < MediaExt.Length; i++)
                {
                    if (File.Exists(Path.Combine(dir, name + MediaExt[i]))) return true;
                }
            }
            catch (Exception __ex) { Note.Swallow("ArgBuilder.cs:167", __ex); }
            return false;
        }

        public static string GuessNameFromUrl(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return "";
                string u = url;
                int q = u.IndexOf('?');
                if (q >= 0) u = u.Substring(0, q);
                u = u.TrimEnd('/');
                int slash = u.LastIndexOf('/');
                string seg = slash >= 0 ? u.Substring(slash + 1) : u;
                int dot = seg.LastIndexOf('.');
                if (dot > 0) seg = seg.Substring(0, dot);
                if (seg.ToLowerInvariant() == "index" || seg.ToLowerInvariant() == "playlist" || seg.ToLowerInvariant() == "master")
                {
                    int prevSlash = slash > 0 ? u.LastIndexOf('/', slash - 1) : -1;
                    if (prevSlash >= 0 && prevSlash < slash)
                    {
                        string parent = u.Substring(prevSlash + 1, slash - prevSlash - 1);
                        if (parent.Length > 0) seg = parent;
                    }
                }
                return Safe.FileName(seg);
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
