// =====================================================================
//  Core\Text.cs — 参数转义、掩码、版本比较、格式化（零依赖纯函数）
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Rem.Core;
namespace Rem.Core
{
    /// <summary>按 Windows 命令行解析规则转义单个参数。</summary>
    public static class ArgQ
    {
        public static string Quote(string value)
        {
            string v = value == null ? "" : value;
            if (v.Length == 0) return "\"\"";
            // 额外参数里用户自己带的引号（如 --header "Cookie: a=b"）经 Split 会保留在 token 内，
            // 再走常规转义就变成 \"Cookie:...\"，引擎拿到的是含字面引号的 header 名，
            // 且界面展示的命令行与实际下发的 argv 不再一一对应（D9）。
            // 这里识别“整段已由一对引号包住”的 token，原样下发，不再二次转义。
            if (IsPreQuoted(v)) return v;
            bool need = false;
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i] == ' ' || v[i] == '\t' || v[i] == '"') { need = true; break; }
            }
            if (!need) return v;

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int bs = 0;
            for (int i = 0; i < v.Length; i++)
            {
                char ch = v[i];
                if (ch == '\\') { bs++; continue; }
                if (ch == '"')
                {
                    if (bs > 0) sb.Append('\\', bs * 2 + 1); else sb.Append('\\');
                    sb.Append('"');
                    bs = 0;
                }
                else
                {
                    if (bs > 0) { sb.Append('\\', bs); bs = 0; }
                    sb.Append(ch);
                }
            }
            if (bs > 0) sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>token 是否已被一对引号整体包住（内部无裸引号），此类 token 不再二次转义。</summary>
        private static bool IsPreQuoted(string v)
        {
            if (v.Length < 2) return false;
            if (v[0] != '"' || v[v.Length - 1] != '"') return false;
            for (int i = 1; i < v.Length - 1; i++)
            {
                if (v[i] == '"') return false;
            }
            return true;
        }

        private static readonly Regex RxToken = new Regex("(?:\"[^\"]*\"|[^\\s])+", RegexOptions.Compiled);

        public static List<string> Split(string raw)
        {
            List<string> list = new List<string>();
            if (raw == null || raw.Trim().Length == 0) return list;
            foreach (Match m in RxToken.Matches(raw))
            {
                if (m.Value.Length > 0) list.Add(m.Value);
            }
            return list;
        }
    }

    /// <summary>凭证掩码：命令预览、Header 展示一律不出现明文。</summary>
    public static class Mask
    {
        private static readonly Regex RxHead = new Regex(
            "(?i)(Cookie|Authorization|Proxy-Authorization|X-[A-Za-z0-9\\-_]*(?:Token|Key|Auth|Sign)[A-Za-z0-9\\-_]*)\\s*:\\s*[^\"\\r\\n]*",
            RegexOptions.Compiled);
        private static readonly Regex RxUserInfo = new Regex("(?i)([a-z0-9+]+://)[^/\\s@\"]+@", RegexOptions.Compiled);
        private static readonly Regex RxQuery = new Regex(
            "(?i)([?&](?:token|sign|signature|auth|auth_key|key|wsSecret|wsTime|expires)=)[^&\\s\"]+", RegexOptions.Compiled);
        // 命令行里的解密密钥：--custom-hls-key / --custom-hls-iv 后面那一段一律打码，
        // 否则命令预览和日志里会把用户手填的 KEY 明文带出去。
        // 解密方式固定 AES_128、--key（KID:KEY 多组）能力已整体移除，规则里不再有 --key 分支
        private static readonly Regex RxArgKey = new Regex(
            "(?i)(--(?:custom-hls-key|custom-hls-iv)\\s+)(\"[^\"]*\"|[^\\s\"]+)", RegexOptions.Compiled);

        public static string Secret(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string t = text;
            try
            {
                t = RxHead.Replace(t, "$1: ****");
                t = RxUserInfo.Replace(t, "$1****@");
                t = RxQuery.Replace(t, "$1****");
                t = RxArgKey.Replace(t, "$1****");
            }
            catch (Exception)
            {
                return text;
            }
            return t;
        }

        /// <summary>逐行打码：已知敏感头用 Secret，其余把冒号后的值换成星号。</summary>
        public static string HeaderBlock(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder();
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            bool first = true;
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i].Trim();
                if (ln.Length == 0) continue;
                string m = Secret(ln);
                if (m == ln)
                {
                    int k = ln.IndexOf(':');
                    m = k > 0 ? ln.Substring(0, k + 1) + " ******" : "******";
                }
                if (!first) sb.Append("\r\n");
                first = false;
                sb.Append(m);
            }
            return sb.ToString();
        }
    }

    /// <summary>版本号归一化 / 显示 / 比较。</summary>
    public static class Ver
    {
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string x = s.Trim();
            if (x.Length > 0 && (x[0] == 'v' || x[0] == 'V')) x = x.Substring(1);
            int cut = x.IndexOfAny(new char[] { '+', '-' });
            if (cut >= 0) x = x.Substring(0, cut);
            return x.Trim();
        }

        public static string Display(string s)
        {
            string x = Normalize(s);
            if (x.Length == 0) return "v?";
            bool numeric = true;
            for (int i = 0; i < x.Length; i++)
            {
                char c = x[i];
                if (!((c >= '0' && c <= '9') || c == '.')) { numeric = false; break; }
            }
            if (numeric) return "v" + x;
            if (x.Length > 12) return "v" + x.Substring(0, 12) + "…";
            return "v" + x;
        }

        public static int Compare(string a, string b)
        {
            int[] pa = Parts(a);
            int[] pb = Parts(b);
            int len = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                int x = i < pa.Length ? pa[i] : 0;
                int y = i < pb.Length ? pb[i] : 0;
                if (x > y) return 1;
                if (x < y) return -1;
            }
            return 0;
        }

        private static int[] Parts(string s)
        {
            string x = Normalize(s);
            if (x.Length == 0) return new int[0];
            string[] raw = x.Split('.');
            List<int> outp = new List<int>();
            for (int i = 0; i < raw.Length; i++)
            {
                int n;
                if (!int.TryParse(raw[i], out n)) n = 0;
                outp.Add(n);
            }
            return outp.ToArray();
        }
    }

    public static class Fmt
    {
        public static string Bytes(long bytes)
        {
            if (bytes < 0) return "未知";
            if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= 1048576L) return (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024L) return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }
    }

    public static class Hash
    {
        /// <summary>整文件 SHA256（小写十六进制）；失败返回空串。</summary>
        public static string Sha256File(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder(h.Length * 2);
                    for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                    return sb.ToString();
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        public static string Short(string text)
        {
            try
            {
                using (MD5 md5 = MD5.Create())
                {
                    byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(text == null ? "" : text));
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < 4; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                    return sb.ToString();
                }
            }
            catch (Exception)
            {
                return "00000000";
            }
        }
    }

    public static class Safe
    {
        private static readonly char[] Bad = new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

        /// <summary>文件名合法化：非法字符换成下划线，折叠 ".." 防穿越，超长截断。</summary>
        public static string FileName(string s)
        {
            if (s == null) return "";
            string v = s;
            for (int i = 0; i < Bad.Length; i++) v = v.Replace(Bad[i], '_');
            // 分隔符已全部换成下划线，此处再把残留的 ".." 折叠，杜绝 ".." 被拼进路径
            while (v.IndexOf("..", StringComparison.Ordinal) >= 0) v = v.Replace("..", "_.");
            v = v.Trim().TrimEnd('.');
            if (v.Length > 120) v = v.Substring(0, 120);
            return v;
        }

        /// <summary>URL 里取第一条非注释行（用户常整块粘贴多行/带说明）。</summary>
        public static string UrlFirst(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string[] lines = raw.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string v = lines[i].Trim();
                if (v.Length == 0) continue;
                if (v[0] == '#') continue;
                int sp = v.IndexOfAny(new char[] { ' ', '\t' });
                if (sp > 0) v = v.Substring(0, sp);
                return v.Trim();
            }
            return "";
        }

        public static string FirstLine(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string[] lines = raw.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string v = lines[i].Trim();
                if (v.Length > 0) return v;
            }
            return "";
        }
    }

    /// <summary>文件系统小工具：头部字节探测、原子替换、安全删目录。</summary>
    public static class Fs
    {
        public const byte MZ0 = 0x4D;
        public const byte MZ1 = 0x5A;
        public const byte PK0 = 0x50;
        public const byte PK1 = 0x4B;

        /// <summary>读文件头若干字节；失败返回空数组。</summary>
        public static byte[] Head(string path, int n)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] buf = new byte[n];
                    int r = fs.Read(buf, 0, n);
                    if (r == n) return buf;
                    byte[] part = new byte[r];
                    Array.Copy(buf, part, r);
                    return part;
                }
            }
            catch (Exception)
            {
                return new byte[0];
            }
        }

        /// <summary>文件头是否为指定两字节魔数。</summary>
        public static bool StartsWith(string path, byte b0, byte b1)
        {
            byte[] h = Head(path, 2);
            return h.Length >= 2 && h[0] == b0 && h[1] == b1;
        }

        /// <summary>是否为 Windows PE（exe/dll）。</summary>
        public static bool IsPE(string path)
        {
            return StartsWith(path, MZ0, MZ1);
        }

        /// <summary>是否为 zip 容器。</summary>
        public static bool IsZip(string path)
        {
            return StartsWith(path, PK0, PK1);
        }

        /// <summary>移动并覆盖目标：同卷用 Replace 保证原子性，跨卷退回 Move。</summary>
        public static bool MoveAtomic(string src, string dst)
        {
            try
            {
                if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) return true;
                if (File.Exists(dst))
                {
                    try
                    {
                        File.Replace(src, dst, null, true);
                        return true;
                    }
                    catch (Exception)
                    {
                        File.Delete(dst);
                    }
                }
                File.Move(src, dst);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>尽力删除目录，任何异常都不抛。</summary>
        public static void TryDeleteDir(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception __ex) { Note.Swallow("Text.cs:357", __ex); }
        }

        public static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception __ex) { Note.Swallow("Text.cs:366", __ex); }
        }
    }
}
