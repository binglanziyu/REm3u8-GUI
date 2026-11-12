// =====================================================================
//  Services\Config.cs — 配置读写（Header / 代理用 DPAPI 加密落盘）
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Rem.Core;

namespace Rem.Services
{
    public sealed class AppConfig
    {
        public string SaveDir = "";
        public int Threads = 16;
        public int Retries = 3;
        public int Timeout = 15;

        // 限速：固定单位 M/s，最多 3 位小数（0.001M ≈ 1KB/s），整数最多 3 位；0 表示不限速
        public double SpeedMB = 0;

        public string Proxy = "";
        public string Headers = "";
        public string ExtraArgs = "";

        // ---- 手动解密（解密方式固定 AES-128 / KEY / IV）----
        // KEY 与 IV 和代理、Header 同等敏感：落盘一律 DPAPI 加密，界面默认打码只读
        // 解密方式只保留 AES-128：HlsMethod 恒为 "AES_128"，不再提供其它取值
        public string HlsMethod = HlsMethodAes128;
        public string HlsKey = "";             // HEX 或 Base64（引擎的 --custom-hls-key 原生接受这两种）
        public string HlsIv = "";              // HEX 或 Base64，一般留空

        /// <summary>
        /// 唯一的解密方式。解密引擎（--decryption-engine）、解密程序路径（--decryption-binary-path）、
        /// 实时解密（--mp4-real-time-decryption）、--key / --key-text-file 等能力已整体移除，
        /// 填了 KEY/IV 需要解密时，--custom-hls-method 取该常量（留空则不下发解密参数）。
        /// </summary>
        public const string HlsMethodAes128 = "AES_128";

        // 默认勾选：预检 / 混流为 MP4 / 自动最高画质 / 完成后删分片 / 校验分片
        public bool Smart = true;
        public bool Mux = true;
        public bool Concurrent = false;
        public bool Binary = false;
        public bool SkipMerge = false;
        public bool SkipDownload = false;
        public bool AppendUrlParams = false;
        public bool DelAfterDone = true;
        public bool CheckSegments = true;
        public bool AutoSelect = true;

        /// <summary>
        /// 坏片自动补片（默认开）：引擎因 403/404 分片失败（含 exit=0 但分片数量校验不通过）后，
        /// 先重拉清单只补缺失分片再跑一次；仍不齐则用前后相邻分片顶替并重新合并，尽量让任务跑完。
        /// 关掉即恢复“失败就停、保留续传文件等人工处理”的旧行为。
        /// </summary>
        public bool AutoPatch = true;

        // 系统代理开关（D8）：false = 不使用系统代理（默认值，界面默认不勾），
        // 此时 ArgBuilder 显式下发 --use-system-proxy false；true = 使用系统代理，不下发该参数。
        // 注意：引擎自带默认是 True，所以 false 必须显式写出来才对得上界面。
        public bool UseSystemProxy = false;

        public string EngineSel = "";
        public string EngineExt = "";
        public int LogLimit = 200000;

        /// <summary>配置结构版本：用来把老配置一次性迁到新默认值，之后用户手动改动不再被覆盖。</summary>
        public int CfgRev = CfgRevCurrent;

        /// <summary>3 = 二进制合并默认关闭（2 = 混流为 MP4 / 自动最高画质默认开，并清掉旧版残留的"跳过合并/仅解析"）。</summary>
        /// <summary>4 = 保存目录默认值改为软件所在目录（旧默认「我的视频\REM-GUI」一并迁走）。</summary>
        public const int CfgRevCurrent = 4;

        /// <summary>限速上限：M/s 整数最多 3 位。</summary>
        public const double SpeedMaxMB = 999.999;

        /// <summary>日志上限的合法区间（字符数），主界面与高级设置共用同一组边界。</summary>
        public const int LogLimitMin = 20000;
        public const int LogLimitMax = 2000000;

        /// <summary>把 M/s 数值编译成 N_m3u8DL-RE 认识的形式（8M / 0.5M）；不限速返回空串。</summary>
        public string SpeedArg()
        {
            if (SpeedMB <= 0) return "";
            double v = Math.Round(SpeedMB, 3, MidpointRounding.AwayFromZero);
            return v.ToString("0.###", CultureInfo.InvariantCulture) + "M";
        }

        /// <summary>旧版「数值 + 单位」迁移成 M/s（KB 折算小数，GB 折算千倍）。</summary>
        public static double ToMB(int value, string unit)
        {
            string u = (unit == null) ? "" : unit.Trim().ToUpperInvariant();
            if (u.Length > 0 && u[0] == 'G') return value * 1000.0;
            if (u.Length > 0 && u[0] == 'K') return value / 1000.0;
            return value;
        }

        public void Clamp()
        {
            if (SpeedMB < 0) SpeedMB = 0;
            if (SpeedMB > SpeedMaxMB) SpeedMB = SpeedMaxMB;
            SpeedMB = Math.Round(SpeedMB, 3, MidpointRounding.AwayFromZero);
            if (Threads < 1) Threads = 1;
            if (Threads > 128) Threads = 128;
            if (Retries < 0) Retries = 0;
            if (Retries > 100) Retries = 100;
            if (Timeout < 1) Timeout = 1;
            if (Timeout > 600) Timeout = 600;
            if (LogLimit < LogLimitMin) LogLimit = LogLimitMin;
            if (LogLimit > LogLimitMax) LogLimit = LogLimitMax;

            // 解密相关：空串/空引用一律归一，避免 UI 里 NullReference、命令行里出现空参数
            // 解密方式只剩 AES-128：不论旧配置里存了什么（自动识别/ SAMPLE_AES / CENC …），一律拉回 AES_128
            HlsMethod = HlsMethodAes128;
            if (HlsKey == null) HlsKey = "";
            else HlsKey = HlsKey.Trim();
            if (HlsIv == null) HlsIv = "";
            else HlsIv = HlsIv.Trim();

            // 系统代理是布尔开关，没有区间可夹紧；这里只做闭环占位说明：
            // 该字段不参与 DPAPI 加密（不含敏感信息），读取缺失时保留默认 false
            // （默认不使用系统代理，与界面默认不勾一致）
        }
    }

    public static class ConfigStore
    {
        // 最近一次 Load 中 DPAPI 解密失败、被清空的字段中文名（D15）。UI 据此在状态栏提示，
        // 不让"配置还在"的假象留着；每次 Load 开头清空，只反映本次结果。
        public static readonly List<string> DecryptFailedFields = new List<string>();

        public static AppConfig Load(string path, Logger log)
        {
            AppConfig c = new AppConfig();
            DecryptFailedFields.Clear();
            try
            {
                if (!File.Exists(path)) return c;
                string text = File.ReadAllText(path, Encoding.UTF8);
                JVal j = JVal.Parse(text);
                if (j == null || j.Kind != JKind.Object)
                {
                    // 结构不对就别当成“空配置”直接覆盖掉，先留一份现场
                    BackupBroken(path, log);
                    return c;
                }

                c.SaveDir = j.GetStr("saveDir", c.SaveDir);
                c.Threads = j.GetInt("threads", c.Threads);
                c.Retries = j.GetInt("retries", c.Retries);
                c.Timeout = j.GetInt("timeout", c.Timeout);
                c.SpeedMB = j.GetDouble("speedMB", c.SpeedMB);
                // 兼容旧版配置：先试「数值 + 单位」，再试更早的单一字符串限速（"5M" / "800K"）
                if (c.SpeedMB <= 0)
                {
                    int oldValue = j.GetInt("speedValue", 0);
                    if (oldValue > 0) c.SpeedMB = AppConfig.ToMB(oldValue, j.GetStr("speedUnit", "MB"));
                    else
                    {
                        string legacySpeed = j.GetStr("speedLimit", "");
                        if (legacySpeed.Length > 0) ParseLegacySpeed(c, legacySpeed);
                    }
                }

                string proxyEnc = j.GetStr("proxyEnc", "");
                string hdrEnc = j.GetStr("headerEnc", "");
                if (proxyEnc.Length > 0) c.Proxy = Unprotect(proxyEnc, "proxyEnc", log);
                else c.Proxy = j.GetStr("proxy", "");
                if (hdrEnc.Length > 0) c.Headers = Unprotect(hdrEnc, "headerEnc", log);
                else c.Headers = j.GetStr("headers", "");

                c.ExtraArgs = j.GetStr("extraArgs", c.ExtraArgs);
                // 解密方式固定 AES-128：配置文件里的 hlsMethod 旧值（含空 = 自动识别）一律不采信
                c.HlsMethod = AppConfig.HlsMethodAes128;
                // KEY / IV 只认加密字段：老配置或加密失败时一律留空，绝不回落到明文读取
                string keyEnc = j.GetStr("hlsKeyEnc", "");
                string ivEnc = j.GetStr("hlsIvEnc", "");
                if (keyEnc.Length > 0) c.HlsKey = Unprotect(keyEnc, "hlsKeyEnc", log);
                if (ivEnc.Length > 0) c.HlsIv = Unprotect(ivEnc, "hlsIvEnc", log);
                // decEngine / keyTextFile 已随解密引擎与密钥文件能力移除，旧配置里的残留键直接忽略
                c.Smart = j.GetBool("smart", c.Smart);
                c.Mux = j.GetBool("mux", c.Mux);
                c.Concurrent = j.GetBool("concurrent", c.Concurrent);
                c.Binary = j.GetBool("binary", c.Binary);
                c.SkipMerge = j.GetBool("skipMerge", c.SkipMerge);
                c.SkipDownload = j.GetBool("skipDownload", c.SkipDownload);
                c.AppendUrlParams = j.GetBool("appendUrlParams", c.AppendUrlParams);
                c.DelAfterDone = j.GetBool("delAfterDone", c.DelAfterDone);
                c.CheckSegments = j.GetBool("checkSegments", c.CheckSegments);
                c.AutoSelect = j.GetBool("autoSelect", c.AutoSelect);
                c.AutoPatch = j.GetBool("autoPatch", c.AutoPatch);
                c.UseSystemProxy = j.GetBool("useSystemProxy", c.UseSystemProxy);
                c.EngineSel = j.GetStr("engineSel", c.EngineSel);
                c.EngineExt = j.GetStr("engineExt", c.EngineExt);
                c.LogLimit = j.GetInt("logLimit", c.LogLimit);

                // 老配置（没有 cfgRev）里 mux/autoSelect 是旧默认 false，还可能残留 e2e 测试留下的
                // "跳过合并/仅解析"；一次性迁到新默认，之后完全尊重用户自己的勾选。
                // 逐级迁移：每级只改自己那一项，避免新版把用户后来手动调过的设置又覆盖一遍。
                c.CfgRev = j.GetInt("cfgRev", 0);
                if (c.CfgRev < 2)
                {
                    c.Mux = true;
                    c.AutoSelect = true;
                    c.SkipMerge = false;
                    c.SkipDownload = false;
                    c.CfgRev = 2;
                }
                if (c.CfgRev < 3)
                {
                    // 二进制合并只适合分片本身已是完整 ts/m4s 的源，继续默认开启容易合出音画不同步
                    c.Binary = false;
                    c.CfgRev = 3;
                }
                if (c.CfgRev < 4)
                {
                    // 保存目录默认值改为「软件所在目录」。只有值仍等于旧默认（我的视频\REM-GUI，
                    // 说明用户从没手动改过）才清空交给 UI 重算，用户自己指定的路径一律原样保留。
                    if (IsLegacyDefaultSaveDir(c.SaveDir)) c.SaveDir = "";
                    c.CfgRev = 4;
                }
                c.Clamp();

                // 发生过版本迁移就立刻回盘：否则磁盘上一直是旧 cfgRev + 旧默认值，
                // 每次启动都得重算一遍，用户看配置文件也和实际生效的设置对不上
                if (c.CfgRev > j.GetInt("cfgRev", 0))
                {
                    Save(path, c, log);
                }
            }
            catch (Exception ex)
            {
                if (log != null) log.Diag("读取配置失败：" + ex.Message, "Config.Load");
                BackupBroken(path, log);
            }
            return c;
        }

        /// <summary>旧版默认保存目录：&lt;我的视频&gt;\REM-GUI。用于判断用户是否从未手动改过保存目录。</summary>
        private static bool IsLegacyDefaultSaveDir(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return false;
                string v = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (string.IsNullOrEmpty(v)) return false;
                string legacy = Path.Combine(v, "REM-GUI");
                return string.Equals(
                    dir.Trim().TrimEnd('\\', '/'),
                    legacy.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>把损坏的配置另存为 .bad，避免下一次保存把用户设置直接抹掉。</summary>
        private static void BackupBroken(string path, Logger log)
        {
            try
            {
                if (!File.Exists(path)) return;
                string bak = path + ".bad";
                Fs.TryDeleteFile(bak);
                File.Copy(path, bak, true);
                if (log != null) log.Warn(">> 配置文件无法解析，已备份为 " + Path.GetFileName(bak));
            }
            catch (Exception __ex) { Note.Swallow("Config.cs:153", __ex); }
        }

        public static void Save(string path, AppConfig c)
        {
            Save(path, c, null);
        }

        /// <summary>原子保存：先写 .tmp 再整体替换，写到一半断电也不会留下半截配置。</summary>
        public static bool Save(string path, AppConfig c, Logger log)
        {
            string tmp = path + ".tmp";
            try
            {
                c.Clamp();
                JVal j = JVal.NewObj();
                j.Set("app", "REM-GUI");
                j.Set("ver", Program.Version);
                j.Set("saveDir", c.SaveDir);
                j.Set("threads", c.Threads);
                j.Set("retries", c.Retries);
                j.Set("timeout", c.Timeout);
                j.Set("speedMB", c.SpeedMB);

                string pe = Protect(c.Proxy);
                string he = Protect(c.Headers);
                if (pe.Length > 0) j.Set("proxyEnc", pe);
                if (he.Length > 0) j.Set("headerEnc", he);
                // 加密失败绝不回落明文：本次宁可少存一项凭据，也不能把密码写进明文配置
                if (c.Proxy != null && c.Proxy.Length > 0 && pe.Length == 0 && log != null)
                    log.Warn(">> 代理凭据加密失败，本次未写入配置（不会以明文落盘）");
                if (c.Headers != null && c.Headers.Length > 0 && he.Length == 0 && log != null)
                    log.Warn(">> 请求头加密失败，本次未写入配置（不会以明文落盘）");

                j.Set("extraArgs", c.ExtraArgs);
                j.Set("hlsMethod", AppConfig.HlsMethodAes128);
                string ke = Protect(c.HlsKey);
                string ie = Protect(c.HlsIv);
                if (ke.Length > 0) j.Set("hlsKeyEnc", ke);
                if (ie.Length > 0) j.Set("hlsIvEnc", ie);
                // 同代理/Header：加密失败绝不回落明文，只记一条警告
                if (c.HlsKey != null && c.HlsKey.Length > 0 && ke.Length == 0 && log != null)
                    log.Warn(">> HLS KEY 加密失败，本次未写入配置（不会以明文落盘）");
                if (c.HlsIv != null && c.HlsIv.Length > 0 && ie.Length == 0 && log != null)
                    log.Warn(">> HLS IV 加密失败，本次未写入配置（不会以明文落盘）");
                // decEngine / keyTextFile 两个键不再写入：Save 全量重建 JVal，旧配置里的残留键随本次保存自然消失
                j.Set("smart", c.Smart);
                j.Set("mux", c.Mux);
                j.Set("concurrent", c.Concurrent);
                j.Set("binary", c.Binary);
                j.Set("skipMerge", c.SkipMerge);
                j.Set("skipDownload", c.SkipDownload);
                j.Set("appendUrlParams", c.AppendUrlParams);
                j.Set("delAfterDone", c.DelAfterDone);
                j.Set("checkSegments", c.CheckSegments);
                j.Set("autoSelect", c.AutoSelect);
                j.Set("autoPatch", c.AutoPatch);
                j.Set("useSystemProxy", c.UseSystemProxy);
                j.Set("engineSel", c.EngineSel);
                j.Set("engineExt", c.EngineExt);
                j.Set("logLimit", c.LogLimit);
                j.Set("cfgRev", AppConfig.CfgRevCurrent);

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(tmp, j.ToJson(), new UTF8Encoding(false));
                if (!Fs.MoveAtomic(tmp, path))
                {
                    if (log != null) log.Diag("配置文件替换失败：" + path, "Config.Save");
                    Fs.TryDeleteFile(tmp);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Fs.TryDeleteFile(tmp);
                if (log != null) log.Diag("配置写入失败：" + ex.Message, "Config.Save");
                return false;
            }
        }

        /// <summary>解析旧版限速串（"5M"/"800K"/"1.5M"），统一折算成 M/s；解析失败保持不限速。</summary>
        private static void ParseLegacySpeed(AppConfig c, string raw)
        {
            try
            {
                string s = raw.Trim().ToUpperInvariant().Replace("/S", "").Replace("B", "");
                if (s.Length == 0) return;
                double scale = 1.0;
                char last = s[s.Length - 1];
                if (last == 'G') scale = 1000.0;
                else if (last == 'K') scale = 0.001;
                if (last == 'K' || last == 'M' || last == 'G') s = s.Substring(0, s.Length - 1);
                double v;
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v <= 0) return;
                c.SpeedMB = Math.Round(v * scale, 3, MidpointRounding.AwayFromZero);
            }
            catch (Exception __ex) { Note.Swallow("Config.cs:243", __ex); }
        }

        private static string Protect(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(text);
                byte[] enc = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string Unprotect(string b64, string tag, Logger log)
        {
            if (string.IsNullOrEmpty(b64)) return "";
            try
            {
                byte[] enc = Convert.FromBase64String(b64);
                byte[] raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch (Exception ex)
            {
                if (log != null) log.Diag("解密失败 " + tag + "：" + ex.Message, "Config.Unprotect");
                // 解密失败时该字段只能留空，但不能静默：记下被清空的字段名，由 UI 在状态栏提示（D15）
                string friendly = FriendlyField(tag);
                if (!DecryptFailedFields.Contains(friendly)) DecryptFailedFields.Add(friendly);
                return "";
            }
        }

        private static string FriendlyField(string tag)
        {
            if (tag == "proxyEnc") return "代理";
            if (tag == "headerEnc") return "请求头";
            if (tag == "hlsKeyEnc") return "解密 KEY";
            if (tag == "hlsIvEnc") return "解密 IV";
            return tag;
        }
    }
}
