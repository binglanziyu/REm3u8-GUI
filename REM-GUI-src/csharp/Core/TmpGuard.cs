// =====================================================================
//  Core\TmpGuard.cs — 续传临时目录的归属校验（P1-1）
//  背景：*.resume.json 里记录的 tmp 是纯文本，任何能改文件的人都能把它写成
//  "D:\" 或 "C:\Users"，而续传成功后 PruneParts() 会对它做 Directory.Delete(x, true)。
//  这里给出唯一判定口径：只有落在「本项目为该任务预分配的 .parts\<名字>」之下的路径
//  才允许作为 --tmp-dir 使用、才允许被递归清理，其余一律忽略并重建。
// =====================================================================
using System;
using System.IO;

namespace Rem.Core
{
    public static class TmpGuard
    {
        /// <summary>
        /// 归一化路径用于比较：绝对化 + 去掉尾部目录分隔符 + 统一分隔符。
        /// 不解析符号链接（Windows 上不做 reparse point 展开），只做字面归一。
        /// </summary>
        public static string Norm(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p = path.Trim();
            if (p.Length == 0) return "";
            try { p = Path.GetFullPath(p); }
            catch (Exception __ex) { Note.Swallow(__ex); return ""; }
            p = p.TrimEnd('\\', '/');
            // 根目录（"D:" / "D:\"）TrimEnd 后只剩盘符，补回分隔符以免误判为普通目录
            if (p.Length == 2 && p[1] == ':') p += "\\";
            return p;
        }

        /// <summary>本项目为某任务预分配的临时分片目录：&lt;saveDir&gt;\.parts\&lt;name&gt;。</summary>
        public static string Expected(string saveDir, string name)
        {
            if (string.IsNullOrEmpty(saveDir) || string.IsNullOrEmpty(name)) return "";
            string root = saveDir.Trim();
            if (root.Length == 0) return "";
            string p;
            try { p = Path.Combine(root, ".parts", name); }
            catch (Exception __ex) { Note.Swallow(__ex); return ""; }
            return Norm(p);
        }

        /// <summary>
        /// tmp 是否归属于当前任务：必须与预期路径完全一致，或位于其下（子目录）。
        /// 返回 false 时调用方必须忽略该 tmp（当作新任务重新生成），且绝不可对其做递归删除。
        /// </summary>
        public static bool Owned(string saveDir, string name, string tmp)
        {
            string want = Expected(saveDir, name);
            string got = Norm(tmp);
            if (want.Length == 0 || got.Length == 0) return false;
            if (string.Equals(want, got, StringComparison.OrdinalIgnoreCase)) return true;
            // 必须是 want + 分隔符 的前缀，避免 "name2" 命中 "name" 这类同前缀误判
            return got.StartsWith(want + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }
}
