// =====================================================================
//  Core\Note.cs — 静默兜底记录的统一出口（消灭空 catch）
//  设计要点：
//   1. Core 层不允许引用 Rem.Services，故用 Note.Sink 交由上层注入落盘实现；
//   2. 全静态、无状态、无锁竞争；ThreadStatic 重入保护防止“记日志本身抛异常”递归；
//   3. 即使 Sink 未注入也至少写入 Debug 通道，排障时有据可查。
// =====================================================================
using System;
using System.Diagnostics;

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
    }
}
