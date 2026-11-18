// =====================================================================
//  Core\Note.cs — 静默兜底记录的统一出口（消灭空 catch）
//  设计要点：
//   1. Core 层不允许引用 Rem.Services，故用 Note.Sink 交由上层注入落盘实现；
//   2. 全静态、无状态、无锁竞争；ThreadStatic 重入保护防止“记日志本身抛异常”递归；
//   3. 即使 Sink 未注入也至少写入 Debug 通道，排障时有据可查。
// =====================================================================
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Rem.Core
{
    public static class Note
    {
        /// <summary>上层（Program）注入的落盘实现，通常指向 Logger.Diag。</summary>
        public static Action<string, Exception> Sink;

        [ThreadStatic]
        private static bool busy;

        /// <summary>吞掉异常但留下痕迹。tag 建议写成 “文件:行/语义”。</summary>
        public static void Swallow(string tag, Exception ex)
        {
            if (busy) return;
            busy = true;
            try
            {
                string line = "[swallow] " + (tag == null ? "" : tag);
                if (ex != null) line = line + " :: " + ex.GetType().Name + ": " + ex.Message;
                Debug.WriteLine(line);

                Action<string, Exception> sink = Sink;
                if (sink != null && ex != null) sink(tag, ex);
            }
            catch (Exception ex2)
            {
                Debug.WriteLine("[swallow][sink-failed] " + ex2.Message);
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>不需要异常对象时的轻量痕迹（用于“预期内失败但值得知道”的分支）。</summary>
        public static void Swallow(string tag)
        {
            Swallow(tag, null);
        }

        /// <summary>
        /// 吞掉异常但留下痕迹，定位标签由编译器在调用点自动填充成「成员名(行号)」。
        /// 手写标签（如 "Config.cs:153"）会随文件增删行持续漂移，实测最大偏差近 1100 行，
        /// 故调用点一律写成 Note.Swallow(ex) 由编译器填充（重构方案第 4 章 E1）。
        /// 显式传 tag 时仍以调用方给出的 tag 为准。
        /// </summary>
        public static void Swallow(Exception ex,
                                   string tag = null,
                                   [CallerMemberName] string member = null,
                                   [CallerLineNumber] int line = 0)
        {
            Swallow(tag == null ? BuildTag(member, line) : tag, ex);
        }

        /// <summary>把编译器填充的成员名与行号拼成定位标签；成员名取不到时退化为行号。</summary>
        private static string BuildTag(string member, int line)
        {
            if (member == null || member.Length == 0) return "?(" + line.ToString() + ")";
            return member + "(" + line.ToString() + ")";
        }
    }
}
