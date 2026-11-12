// =====================================================================
//  Core\Json.cs — 极简 JSON 读写（零外部依赖）
//  仅覆盖配置、update.json、GitHub Releases API 三类结构所需能力
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rem.Core
{
    public enum JKind { Null, Bool, Number, String, Array, Object }

    public sealed class JVal
    {
        public JKind Kind = JKind.Null;
        public bool B;
        public double N;
        public string S = "";
        public List<JVal> Arr;
        public Dictionary<string, JVal> Obj;

        public bool IsNull { get { return Kind == JKind.Null; } }

        public int Count
        {
            get
            {
                if (Arr != null) return Arr.Count;
                if (Obj != null) return Obj.Count;
                return 0;
            }
        }

        public static JVal Null() { return new JVal(); }

        public static JVal Of(string v)
        {
            JVal j = new JVal();
            j.Kind = JKind.String;
            j.S = v == null ? "" : v;
            return j;
        }

        public static JVal Of(int v)
        {
            JVal j = new JVal();
            j.Kind = JKind.Number;
            j.N = v;
            return j;
        }

        public static JVal Of(double v)
        {
            JVal j = new JVal();
            j.Kind = JKind.Number;
            j.N = v;
            return j;
        }

        public static JVal Of(bool v)
        {
            JVal j = new JVal();
            j.Kind = JKind.Bool;
            j.B = v;
            return j;
        }

        public static JVal NewObj()
        {
            JVal j = new JVal();
            j.Kind = JKind.Object;
            j.Obj = new Dictionary<string, JVal>(StringComparer.OrdinalIgnoreCase);
            return j;
        }

        public static JVal NewArr()
        {
            JVal j = new JVal();
            j.Kind = JKind.Array;
            j.Arr = new List<JVal>();
            return j;
        }

        public JVal Get(string key)
        {
            if (Kind != JKind.Object || Obj == null || key == null) return null;
            JVal v;
            if (Obj.TryGetValue(key, out v)) return v;
            return null;
        }

        public string GetStr(string key, string def)
        {
            JVal v = Get(key);
            if (v == null) return def;
            if (v.Kind == JKind.String) return v.S;
            if (v.Kind == JKind.Number) return v.N.ToString(CultureInfo.InvariantCulture);
            if (v.Kind == JKind.Bool) return v.B ? "true" : "false";
            return def;
        }

        public int GetInt(string key, int def)
        {
            JVal v = Get(key);
            if (v == null) return def;
            if (v.Kind == JKind.Number) return (int)Math.Round(v.N);
            int n;
            if (v.Kind == JKind.String && int.TryParse(v.S, out n)) return n;
            return def;
        }

        public double GetDouble(string key, double def)
        {
            JVal v = Get(key);
            if (v == null) return def;
            if (v.Kind == JKind.Number) return v.N;
            double d;
            if (v.Kind == JKind.String && double.TryParse(v.S, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return def;
        }

        public bool GetBool(string key, bool def)
        {
            JVal v = Get(key);
            if (v == null) return def;
            if (v.Kind == JKind.Bool) return v.B;
            if (v.Kind == JKind.Number) return Math.Abs(v.N) > 0.0001;
            if (v.Kind == JKind.String) return string.Equals(v.S, "true", StringComparison.OrdinalIgnoreCase);
            return def;
        }

        public List<JVal> GetArr(string key)
        {
            JVal v = Get(key);
            if (v == null || v.Arr == null) return new List<JVal>();
            return v.Arr;
        }

        public JVal Set(string key, JVal v)
        {
            if (Kind != JKind.Object || Obj == null)
            {
                Kind = JKind.Object;
                Obj = new Dictionary<string, JVal>(StringComparer.OrdinalIgnoreCase);
            }
            Obj[key] = v == null ? Null() : v;
            return this;
        }

        public JVal Set(string key, string v) { return Set(key, Of(v)); }
        public JVal Set(string key, int v) { return Set(key, Of(v)); }
        public JVal Set(string key, double v) { return Set(key, Of(v)); }
        public JVal Set(string key, bool v) { return Set(key, Of(v)); }

        public void Add(JVal v)
        {
            if (Arr == null) { Kind = JKind.Array; Arr = new List<JVal>(); }
            Arr.Add(v == null ? Null() : v);
        }

        public string ToJson()
        {
            StringBuilder sb = new StringBuilder();
            Write(sb);
            return sb.ToString();
        }

        private void Write(StringBuilder sb)
        {
            switch (Kind)
            {
                case JKind.Null:
                    sb.Append("null");
                    break;
                case JKind.Bool:
                    sb.Append(B ? "true" : "false");
                    break;
                case JKind.Number:
                    if (Math.Abs(N - Math.Round(N)) < 0.0000001 && Math.Abs(N) < 1e15)
                    {
                        sb.Append(((long)Math.Round(N)).ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(N.ToString("R", CultureInfo.InvariantCulture));
                    }
                    break;
                case JKind.String:
                    WriteStr(sb, S);
                    break;
                case JKind.Array:
                    sb.Append('[');
                    if (Arr != null)
                    {
                        for (int i = 0; i < Arr.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            if (Arr[i] == null) sb.Append("null"); else Arr[i].Write(sb);
                        }
                    }
                    sb.Append(']');
                    break;
                case JKind.Object:
                    sb.Append('{');
                    if (Obj != null)
                    {
                        bool first = true;
                        foreach (KeyValuePair<string, JVal> kv in Obj)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            WriteStr(sb, kv.Key);
                            sb.Append(':');
                            if (kv.Value == null) sb.Append("null"); else kv.Value.Write(sb);
                        }
                    }
                    sb.Append('}');
                    break;
            }
        }

        private static void WriteStr(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ')
                            {
                                sb.Append("\\u");
                                sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        public static JVal Parse(string text)
        {
            if (text == null) return Null();
            int i = 0;
            JVal v = ParseValue(text, ref i);
            return v == null ? Null() : v;
        }

        private static void SkipWs(string t, ref int i)
        {
            while (i < t.Length)
            {
                char c = t[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\uFEFF') { i++; continue; }
                break;
            }
        }

        private static JVal ParseValue(string t, ref int i)
        {
            SkipWs(t, ref i);
            if (i >= t.Length) return Null();
            char c = t[i];
            if (c == '{') return ParseObject(t, ref i);
            if (c == '[') return ParseArray(t, ref i);
            if (c == '"') return Of(ParseString(t, ref i));
            if (c == 't' && i + 3 < t.Length && t.Substring(i, 4) == "true") { i += 4; return Of(true); }
            if (c == 'f' && i + 4 < t.Length && t.Substring(i, 5) == "false") { i += 5; return Of(false); }
            if (c == 'n' && i + 3 < t.Length && t.Substring(i, 4) == "null") { i += 4; return Null(); }
            int before = i;
            JVal num = ParseNumber(t, ref i);
            // 兜底推进：非法 token 走 ParseNumber 时不消耗任何字符，必须至少吃掉一个字符，
            // 否则外层 while (i < t.Length) 会原地空转 → 死循环、界面冻结（D2）
            if (i <= before) i = before + 1;
            return num;
        }

        private static JVal ParseObject(string t, ref int i)
        {
            JVal o = NewObj();
            i++; // {
            while (i < t.Length)
            {
                int loopStart = i;
                SkipWs(t, ref i);
                if (i >= t.Length) break;
                if (t[i] == '}') { i++; break; }
                if (t[i] == ',') { i++; continue; }
                string key = ParseString(t, ref i);
                SkipWs(t, ref i);
                if (i < t.Length && t[i] == ':') i++;
                JVal v = ParseValue(t, ref i);
                o.Set(key, v);
                // 游标停滞保护：一整轮没有前进说明遇上了解析不了的畸形片段，
                // 直接结束并返回已解析部分，绝不让主线程卡在这（D2）
                if (i <= loopStart) { i = t.Length; break; }
            }
            return o;
        }

        private static JVal ParseArray(string t, ref int i)
        {
            JVal a = NewArr();
            i++; // [
            while (i < t.Length)
            {
                int loopStart = i;
                SkipWs(t, ref i);
                if (i >= t.Length) break;
                if (t[i] == ']') { i++; break; }
                if (t[i] == ',') { i++; continue; }
                a.Add(ParseValue(t, ref i));
                if (i <= loopStart) { i = t.Length; break; }
            }
            return a;
        }

        private static string ParseString(string t, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            SkipWs(t, ref i);
            if (i >= t.Length) return "";
            if (t[i] != '"')
            {
                // 非法键/裸 token：吃掉一个字符再返回空串，保证调用方循环一定有推进（D2）
                i++;
                return "";
            }
            i++;
            while (i < t.Length)
            {
                char c = t[i];
                if (c == '"') { i++; break; }
                if (c == '\\')
                {
                    i++;
                    if (i >= t.Length) break;
                    char e = t[i];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < t.Length)
                            {
                                int code;
                                if (int.TryParse(t.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static JVal ParseNumber(string t, ref int i)
        {
            int start = i;
            while (i < t.Length)
            {
                char c = t[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') { i++; continue; }
                break;
            }
            string s = t.Substring(start, i - start);
            double n;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return Of(n);
            return Null();
        }
    }
}
