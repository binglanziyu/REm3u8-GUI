// =====================================================================
//  Services\Sponsor.cs — 赞助提醒的频率与到期判定（纯函数，可单测）
//  配置项：SponsorFreq（"week" 每周一次 / "day" 每日一次，默认 weekly）、
//          SponsorLastUtc（上次自动弹出的 Unix 秒时间戳）。
//  判定只看「上次弹出时间 + 周期」，同一周期内不重复弹；从未弹过（0）视为到期。
// =====================================================================
using System;

namespace Rem.Services
{
    public static class Sponsor
    {
        public const string FreqDay = "day";
        public const string FreqWeek = "week";
        public const string FreqDefault = FreqWeek;

        /// <summary>把配置里读到的任意字符串收敛成合法值：只认 "day"，其余一律 "week"。</summary>
        public static string NormalizeFreq(string v)
        {
            if (string.Equals(v, FreqDay, StringComparison.OrdinalIgnoreCase)) return FreqDay;
            return FreqWeek;
        }

        /// <summary>周期小时数：每日 24、每周 168。</summary>
        public static int PeriodHours(string freq)
        {
            return NormalizeFreq(freq) == FreqDay ? 24 : 168;
        }

        /// <summary>展示用文案。</summary>
        public static string FreqLabel(string freq)
        {
            return NormalizeFreq(freq) == FreqDay ? "每日一次" : "每周一次";
        }

        /// <summary>
        /// 是否到期该弹：lastUtc 为 0（从未弹过）直接到期；否则按周期比较，
        /// 时钟回拨（now 早于 last）也算未到期，避免反复弹。
        /// </summary>
        public static bool Due(string freq, long lastUtc, DateTime nowUtc)
        {
            if (lastUtc <= 0) return true;
            long now = ToUnix(nowUtc);
            if (now <= lastUtc) return false;
            return (now - lastUtc) >= (long)PeriodHours(freq) * 3600L;
        }

        /// <summary>DateTime(UTC) → Unix 秒；本地时间也按 UTC 换算，避免时区差一小时就提前弹。</summary>
        public static long ToUnix(DateTime t)
        {
            DateTime u = t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
            return (long)(u - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>按配置判定此刻是否到期该弹一次（配置为空时一律不弹）。</summary>
        public static bool DueNow(AppConfig cfg)
        {
            if (cfg == null) return false;
            return Due(cfg.SponsorFreq, cfg.SponsorLastUtc, DateTime.UtcNow);
        }

        /// <summary>记录本次弹出/查看时间（UTC 秒），同一周期内不再重复弹。</summary>
        public static void Touch(AppConfig cfg, DateTime nowUtc)
        {
            if (cfg == null) return;
            cfg.SponsorLastUtc = ToUnix(nowUtc);
        }

        /// <summary>记录「现在」为已提醒时间。</summary>
        public static void Touch(AppConfig cfg)
        {
            Touch(cfg, DateTime.UtcNow);
        }
    }
}
