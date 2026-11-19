// =====================================================================
//  Services\FailSignals.cs — 引擎失败信号的文本判定（纯函数，UI 与断言测试共用）
//  为什么放在 Services 而不是留在 UI 里：这些判定错一次的代价很高——
//  把 token 过期的 403 当成「源站分片残缺」，会白跑一轮自动补片（实测 15 秒）
//  并给用户一个完全错误的结论。放到 Services 才能被 tests\CoreTests.cs 直接断言。
//  依赖方向不变：UI → Services（本文件只用 BCL，不反向依赖 UI）。
// =====================================================================
using System;

namespace Rem.Services
{
    public static class FailSignals
    {
        /// <summary>
        /// 鉴权/网络类失败：链接的 token 过期、Cookie/Header 缺失时，引擎会重试 10 次
        /// 后把 HttpRequestException(401/403) 包成 Unhandled exception 抛出来。
        /// 这类失败和分片本身无关，补片、换相邻分片都救不了，必须单独识别。
        /// </summary>
        public static bool LooksLikeAuthFailure(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            if (t.IndexOf("401 (Unauthorized)", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("403 (Forbidden)", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("HttpRequestException", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("RetryUtil", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// 分片数量校验失败 / 合并混流失败（原有三条，口径不动）。
        /// </summary>
        public static bool LooksLikePartFailure(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            if (t.IndexOf("分片数量校验不通过", StringComparison.Ordinal) >= 0) return true;
            if (t.IndexOf("Mux failed", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("ERROR: Failed", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// 解密失败特征：源站把分片尾部写截断后，密文长度不再是 16 的整数倍，
        /// AES-128-CBC 解密必然抛 CryptographicException（not a complete block / Padding is invalid）。
        /// 只认这几条专属词：引擎的鉴权失败、重试耗尽同样会顶上 "Unhandled exception"，
        /// 把它当特征词等于把所有失败都算成残片（3.1.1 的误报就是这么来的）。
        /// </summary>
        public static bool LooksLikeDecryptFailure(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            if (t.IndexOf("CryptographicException", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("not a complete block", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("Padding is invalid", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// 该行是否算「引擎失败信号」（退出码在这些场景全是 0，只能靠文本）。
        /// 鉴权/网络失效一律排除：它走自己的提示与中止分支，不进补片状态机。
        /// </summary>
        public static bool LooksLikeEngineFailure(string t)
        {
            if (LooksLikeAuthFailure(t)) return false;
            return LooksLikePartFailure(t) || LooksLikeDecryptFailure(t);
        }
    }
}
