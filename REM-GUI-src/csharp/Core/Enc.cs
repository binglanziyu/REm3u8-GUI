// =====================================================================
//  Core\Enc.cs — 文本编码修复（乱码 mojibake 还原）唯一入口
//  引擎、子进程、外部文本的编码并不统一：同一个程序在不同机器/不同版本上
//  可能按 UTF-8 也可能按系统 ANSI(OEM 936) 输出；一旦用错代码页解码，
//  中文就会变成「株 ÷ çŽ°æ‹‰æŽ¥çš„æ²¹」这类乱码（UTF-8 字节被按单字节代码页解出）。
//
//  本类提供两道闸：
//    ① DecodeLine —— 拿到【原始字节】时用：先严格试 UTF-8，失败退系统 ANSI，
//                    不解码成字符串就无从判断，所以必须在这一层按字节判断。
//    ② Repair     —— 拿到【已经是字符串】的文本时用（用户输入、续传文件、
//                    日志写入、状态栏…）：试探性还原 mojibake。
//
//  两者都只在「看起来像乱码」时才动手转换，正常中文原样返回；
//  Repair 幂等，可安全地在多处重复调用（重复调用不会二次破坏正常文本）。
// =====================================================================
using System;
using System.Text;

namespace Rem.Core
{
    public static class Enc
    {
        private static readonly object gate = new object();
        private static Encoding utf8Strict;
        private static Encoding oem;
        private static Encoding w1252;
        private static Encoding latin1;
        private static bool oemBroken;

        /// <summary>严格 UTF-8：非法字节序列直接抛异常，用来判定「这段字节到底是不是 UTF-8」。</summary>
        public static Encoding Utf8Strict
        {
            get
            {
                lock (gate)
                {
                    if (utf8Strict == null) utf8Strict = new UTF8Encoding(false, true);
                    return utf8Strict;
                }
            }
        }

        /// <summary>
        /// 系统 ANSI/OEM 代码页（简中 = 936）。与子进程重定向管道的实际输出编码一致：
        /// 控制台程序被重定向后按 OEM 代码页写字节，而不是 UTF-8。
        /// </summary>
        public static Encoding Oem
        {
            get
            {
                lock (gate)
                {
                    if (oem == null && !oemBroken)
                    {
                        int cp = 0;
                        try { cp = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage; }
                        catch (Exception) { cp = 0; }
                        if (cp <= 0) cp = 936;
                        try { oem = Encoding.GetEncoding(cp); }
                        catch (Exception)
                        {
                            // 代码页不可用（精简系统 / .NET Core 无 936 提供程序）：退回默认编码，
                            // 并记住失败，避免每行日志都白抛一次异常
                            oemBroken = true;
                            try { oem = Encoding.Default; } catch (Exception) { oem = new UTF8Encoding(false, false); }
                        }
                    }
                    if (oem == null) { try { oem = Encoding.Default; } catch (Exception) { oem = new UTF8Encoding(false, false); } }
                    return oem;
                }
            }
        }

        /// <summary>Windows-1252：UTF-8 字节被按「西欧」代码页解码时的元凶（çŽ° 就出自这里）。</summary>
        public static Encoding W1252
        {
            get
            {
                lock (gate)
                {
                    if (w1252 == null) w1252 = SafeGet(1252, Latin1);
                    return w1252;
                }
            }
        }

        /// <summary>ISO-8859-1：与 1252 近似，覆盖 0x80-0x9F 被当控制符的情况。</summary>
        public static Encoding Latin1
        {
            get
            {
                lock (gate)
                {
                    if (latin1 == null) latin1 = SafeGet(28591, new UTF8Encoding(false, false));
                    return latin1;
                }
            }
        }

        private static Encoding SafeGet(int cp, Encoding fallback)
        {
            try { return Encoding.GetEncoding(cp); }
            catch (Exception) { return fallback; }
        }

        /// <summary>
        /// 把一行【原始字节】解码成文本：去 BOM → 严格 UTF-8 → 系统 ANSI(936)，最后统一过 Repair。
        /// 行边界（0x0A / 0x0D）由调用方切分，GBK/UTF-8 的多字节尾字节都不会是这两个值。
        /// </summary>
        public static string DecodeLine(byte[] buf, int off, int len)
        {
            if (buf == null) return "";
            if (off < 0) off = 0;
            if (off > buf.Length) return "";
            if (len < 0 || off + len > buf.Length) len = buf.Length - off;
            if (len <= 0) return "";
            // UTF-8 BOM：有就直接按 UTF-8 走，并跳过头三字节
            if (len >= 3 && off + 2 < buf.Length && buf[off] == 0xEF && buf[off + 1] == 0xBB && buf[off + 2] == 0xBF)
            {
                off += 3;
                len -= 3;
                if (len <= 0) return "";
                try { return Repair(Esc(Utf8Strict, buf, off, len)); }
                catch (Exception) { /* BOM 后仍非法：落到下面的自动判定 */ }
            }

            string s = null;
            try { s = Esc(Utf8Strict, buf, off, len); }
            catch (Exception) { s = null; }
            if (s == null)
            {
                // 不是合法 UTF-8 → 按系统 ANSI 解；解出来若仍是 mojibake，Repair 会再兜一次
                try { s = Esc(Oem, buf, off, len); }
                catch (Exception) { s = Esc(Latin1, buf, off, len); }
            }
            return Repair(s);
        }

        private static string Esc(Encoding e, byte[] buf, int off, int len)
        {
            return e.GetString(buf, off, len);
        }

        /// <summary>
        /// 试探性还原 mojibake：只在文本「看起来像乱码」时尝试，且只接受评分更高的结果。
        /// 幂等 —— 对正常中文、对已修复的文本再调一次都原样返回。
        /// </summary>
        public static string Repair(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (!Suspect(s)) return s;   // 正常中文/纯 ASCII 直接返回，不做任何编码转换

            string whole = RepairWhole(s);
            // 整串能修就整串修；修不了（例如一行里混了「已经解错的汉字前缀 + 乱码尾段」，
            // 汉字在 1252 下无字节可反推）就退一步按片段抢救，至少把可读部分还原出来
            if (!string.Equals(whole, s, StringComparison.Ordinal)) return whole;
            return SalvageLatin(s);
        }

        /// <summary>整串按候选编码假设还原（见 Repair 注释）。</summary>
        private static string RepairWhole(string s)
        {
            string best = s;
            int bestScore = Score(s);
            string cur = s;
            // 最多两层：允许「UTF-8 被 GBK 解 → 又被 1252 解」这类双重错误
            for (int round = 0; round < 2; round++)
            {
                if (!Suspect(cur)) break;
                string pick = null;
                int pickScore = bestScore;
                string[] cand = new string[5];
                try
                {
                    // 按「反推字节再用正确编码解」的假设逐个试：西欧代码页解出的乱码、
                    // GBK 解出的乱码（鐜板湪 一族）都在其中
                    cand[0] = TryUtf8(W1252, cur);
                    cand[1] = TryUtf8(Latin1, cur);
                    cand[2] = TryUtf8(Oem, cur);
                    cand[3] = TryAnsi(W1252, cur);
                    cand[4] = TryAnsi(Latin1, cur);
                }
                catch (Exception) { break; }
                for (int i = 0; i < cand.Length; i++)
                {
                    string c = cand[i];
                    if (c == null || c.Length == 0 || string.Equals(c, cur, StringComparison.Ordinal)) continue;
                    // 有损候选一律否决：转换过程中冒出 '?'/'\uFFFD' 说明「这个编码假设」不成立，
                    // 结果是「以乱码换乱码」，比保持原样更糟（真机上曾把正常文字二次毁掉）
                    if (Lossy(cur, c)) continue;
                    int sc = Score(c);
                    if (sc > pickScore)
                    {
                        pickScore = sc;
                        pick = c;
                    }
                }
                if (pick == null) break;    // 没有更优解 → 保持原样，绝不为了「转换」而转换
                best = pick;
                bestScore = pickScore;
                cur = pick;
            }
            return best;
        }

        /// <summary>
        /// 片段级抢救：把连续的「西欧/拉丁扩展/通用标点」字符当成一整段乱码，
        /// 反推字节后按 UTF-8 解；每段独立判定，解不出来就原样保留。
        /// 用于整串修复失败但局部仍是典型 mojibake 的行（截图里那种「株 ÷ çŽ°...」混合行）。
        /// </summary>
        private static string SalvageLatin(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (!IsLatinSuspect(s[i]))
                {
                    sb.Append(s[i]);
                    i++;
                    continue;
                }
                int j = i;
                while (j < s.Length && IsLatinSuspect(s[j])) j++;
                string run = s.Substring(i, j - i);
                string fixedRun = null;
                // 单字符不足以构成 UTF-8 多字节序列，动了只会引入新的错误
                if (j - i >= 2)
                {
                    string c = TryUtf8(W1252, run);
                    if (c != null && !Lossy(run, c) && Score(c) > Score(run)) fixedRun = c;
                }
                sb.Append(fixedRun != null ? fixedRun : run);
                i = j;
            }
            return sb.ToString();
        }

        /// <summary>是否属于「正常中文里不会出现的西欧/标点字符」——即乱码的嫌疑字符。</summary>
        private static bool IsLatinSuspect(char c)
        {
            if (c >= 0x80 && c <= 0x2FF) return true;
            if (c >= 0x2000 && c <= 0x214F) return true;
            return false;
        }

        /// <summary>把字符串按 src 拆成字节，再按 UTF-8 严格解码；解不出来返回 null（该假设不成立）。</summary>
        private static string TryUtf8(Encoding src, string s)
        {
            byte[] b;
            try { b = src.GetBytes(s); }
            catch (Exception) { return null; }
            if (b == null || b.Length == 0) return null;
            try { return Utf8Strict.GetString(b); }
            catch (Exception) { return null; }
        }

        /// <summary>把字符串按 src 拆成字节，再按系统 ANSI 解码（覆盖「GBK 字节被按西欧代码页解」）。</summary>
        private static string TryAnsi(Encoding src, string s)
        {
            byte[] b;
            try { b = src.GetBytes(s); }
            catch (Exception) { return null; }
            if (b == null || b.Length == 0) return null;
            try { return Oem.GetString(b); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 候选是否发生了编码替换（U+FFFD 或 '?'）：原文里没有就说明这个假设把字节丢了，必须否决。
        /// </summary>
        private static bool Lossy(string src, string dst)
        {
            bool srcBad = HasLossChar(src);
            return !srcBad && HasLossChar(dst);
        }

        private static bool HasLossChar(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '?' || c == 0xFFFD) return true;
            }
            return false;
        }

        /// <summary>
        /// 是否值得尝试还原：含「西欧重音字母 / 拉丁扩展 / 通用标点 / 替换符」这类
        /// 正常中文里根本不会出现的字符，才可能是 mojibake。
        /// </summary>
        private static bool Suspect(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x80) continue;
                if (c >= 0x4E00 && c <= 0x9FFF) continue;   // 常用汉字
                if (c >= 0x3400 && c <= 0x4DBF) continue;   // 扩展 A
                if (c >= 0xF900 && c <= 0xFAFF) continue;   // 兼容汉字
                if (c >= 0x3000 && c <= 0x303F) continue;   // 中文标点
                if (c >= 0xFF00 && c <= 0xFF65) continue;   // 全角字符
                if (c >= 0x2E80 && c <= 0x2FFF) continue;   // 康熙部首等
                return true;
            }
            return false;
        }

        /// <summary>
        /// 给一段文本打分：正规中文得正分，mojibake 的典型字符（é ç Ž ° ‹ æ ‰ ‰ € 等）扣分。
        /// 只用于「两个候选谁更像人话」的比较，不追求绝对准确。
        /// </summary>
        private static int Score(string s)
        {
            if (string.IsNullOrEmpty(s)) return int.MinValue / 2;
            int score = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x80)
                {
                    if (c < 0x20) score -= 6;    // 控制符
                    else if (c == '?') score -= 8;   // 编码替换留下的问号
                    else score += 1;
                }
                else if (c >= 0x4E00 && c <= 0x9FFF) score += 2;   // 汉字
                else if (c >= 0x3400 && c <= 0x4DBF) score += 2;
                else if (c >= 0xF900 && c <= 0xFAFF) score += 2;
                else if (c >= 0x3000 && c <= 0x303F) score += 2;   // 。、「」等
                else if (c >= 0xFF00 && c <= 0xFF65) score += 2;   // 全角
                else if (c == 0xFFFD) score -= 8;                  // 替换符：解码失败的铁证
                else if (c <= 0x00FF) score -= 4;                  // é ç Ž ° ÷ 这一族
                else if (c <= 0x02FF) score -= 4;                  // 拉丁扩展
                else if (c >= 0x2000 && c <= 0x206F) score -= 4;   // 通用标点（‰ ‹ ‚ “ ”）
                else if (c >= 0x20A0 && c <= 0x20CF) score -= 4;   // 货币符 €
                else if (c >= 0x2100 && c <= 0x214F) score -= 4;   // 字母式符号 ™
                else score += 1;
            }
            return score;
        }
    }
}
