// =====================================================================
//  tests\CoreTests.cs — 零依赖断言测试（不引入 xUnit/Pester，保持单 exe 工程）
//  覆盖：Core 纯函数（ArgQ / Mask / Ver / Fmt / Safe / Fs / Json）
//        Services 纯逻辑（AppConfig.Clamp / SpeedArg / ToMB）
//        Engine 纯计算（ArgBuilder.HeaderLines / GuessNameFromUrl / UniqueName）
//  用法：powershell -File src\build_csharp.ps1 -Test
// =====================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Rem.Core;
using Rem.Engine;
using Rem.Services;

namespace Rem.Tests
{
    public static class Entry
    {
        private static int pass;
        private static int fail;
        private static readonly List<string> failures = new List<string>();

        private static void Ok(bool cond, string name)
        {
            if (cond) { pass++; return; }
            fail++;
            failures.Add(name);
            Console.WriteLine("[FAIL] " + name);
        }

        private static void Eq(string expect, string actual, string name)
        {
            bool same = string.Equals(expect, actual, StringComparison.Ordinal);
            Ok(same, name + "  (expect=[" + expect + "] actual=[" + actual + "])");
        }

        private static void Eq(double expect, double actual, string name)
        {
            Ok(Math.Abs(expect - actual) < 0.0001, name + "  (expect=" + expect + " actual=" + actual + ")");
        }

        private static void Eq(int expect, int actual, string name)
        {
            Ok(expect == actual, name + "  (expect=" + expect + " actual=" + actual + ")");
        }

        /// <summary>取 "--name value" 形式参数的值，缺失时返回 "&lt;none&gt;"，便于一条 Eq 断言"有没有"和"值对不对"。</summary>
        private static string ArgVal(List<string> args, string name)
        {
            int i = args.IndexOf(name);
            if (i < 0 || i + 1 >= args.Count) return "<none>";
            return args[i + 1];
        }

        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            TestArgQ();
            TestMask();
            TestVer();
            TestFmt();
            TestSafe();
            TestFs();
            TestJson();
            TestConfig();
            TestUpdateManifest();
            TestArgBuilder();
            TestProgress();
            TestSegmentPatcher();

            Console.WriteLine();
            Console.WriteLine("pass=" + pass + "  fail=" + fail);
            if (fail > 0)
            {
                Console.WriteLine("---- 失败项 ----");
                for (int i = 0; i < failures.Count; i++) Console.WriteLine("  " + failures[i]);
                return 1;
            }
            Console.WriteLine("ALL PASS");
            return 0;
        }

        // ---------------- SegmentPatcher：坏片自动补片 ----------------
        private static void TestSegmentPatcher()
        {
            int idx;
            string ext;
            Ok(SegmentPatcher.ParseIndex("12.ts", out idx, out ext) && idx == 12 && ext == ".ts", "补片索引 12.ts → 12 /.ts");
            Ok(SegmentPatcher.ParseIndex("007.ts", out idx, out ext) && idx == 7, "补片索引 007.ts → 7");
            Ok(!SegmentPatcher.ParseIndex("raw.m3u8", out idx, out ext), "补片索引 raw.m3u8 不算分片");
            Ok(!SegmentPatcher.ParseIndex("1.m4s.bak", out idx, out ext), "补片索引 1.m4s.bak 不算分片");

            string root = Path.Combine(Path.GetTempPath(), "rem_patch_" + Guid.NewGuid().ToString("N"));
            try
            {
                // 复刻引擎目录：<tmp>\<name>\0____\<n>.ts + 同层 raw.m3u8（清单 4 片）
                string nameDir = Path.Combine(root, "demo");
                string stream = Path.Combine(nameDir, "0____");
                Directory.CreateDirectory(stream);
                File.WriteAllText(Path.Combine(nameDir, "raw.m3u8"),
                    "#EXTM3U" + "\n" + "#EXTINF:4," + "\n" + "a.ts" + "\n" +
                    "#EXTINF:4," + "\n" + "b.ts" + "\n" +
                    "#EXTINF:4," + "\n" + "c.ts" + "\n" +
                    "#EXTINF:4," + "\n" + "d.ts" + "\n" + "#EXT-X-ENDLIST" + "\n");
                Eq(4, SegmentPatcher.ExpectedCount(nameDir), "补片: 从 raw.m3u8 数出 4 片");
                for (int i = 0; i < 4; i++)
                {
                    if (i == 1) continue;   // 第 1 片制造 404 缺失
                    File.WriteAllText(Path.Combine(stream, i + ".ts"), "SEG" + i);
                }

                PatchReport dry = SegmentPatcher.Patch(root, false, null);
                Eq(4, dry.Total, "补片预演: 预期 4 片");
                Eq(1, dry.Missing, "补片预演: 缺 1 片");
                Eq(0, dry.Patched, "补片预演: 不落盘");
                Ok(!File.Exists(Path.Combine(stream, "1.ts")), "补片预演: 未创建任何文件");

                PatchReport real = SegmentPatcher.Patch(root, true, null);
                Eq(1, real.Patched, "补片执行: 补上 1 片");
                Ok(File.Exists(Path.Combine(stream, "1.ts")), "补片执行: 1.ts 已生成");
                Eq("SEG0", File.ReadAllText(Path.Combine(stream, "1.ts")), "补片执行: 用前一片顶替且内容一致");
                Eq(4, Directory.GetFiles(stream, "*.ts").Length, "补片执行: 只新增不删除，共 4 片");

                // 缺口过大（4/9 > 30%）时放弃，避免把成片补成同一片无限重复
                string bigDir = Path.Combine(root, "big");
                string bigStream = Path.Combine(bigDir, "0____");
                Directory.CreateDirectory(bigStream);
                for (int i = 0; i < 10; i++)
                {
                    if (i % 2 == 0) File.WriteAllText(Path.Combine(bigStream, i + ".ts"), "B" + i);
                }
                PatchReport big = SegmentPatcher.Patch(bigDir, true, null);
                Eq(0, big.Patched, "补片上限: 缺口过半不补");
                Eq(4, big.Skipped, "补片上限: 放弃 4 片");

                // 只看缺失率，不再有绝对片数上限：100 片缺 30 片（正好 30%）也要全部补
                string rateDir = Path.Combine(root, "rate");
                string rateStream = Path.Combine(rateDir, "0____");
                Directory.CreateDirectory(rateStream);
                StringBuilder m3u = new StringBuilder("#EXTM3U" + "\n");
                for (int i = 0; i < 100; i++)
                {
                    m3u.Append("#EXTINF:4,").Append("\n").Append(i).Append(".ts\n");
                }
                File.WriteAllText(Path.Combine(rateDir, "raw.m3u8"), m3u.ToString());
                Eq(100, SegmentPatcher.ExpectedCount(rateDir), "补片比例: 清单 100 片");
                for (int i = 0; i < 70; i++) File.WriteAllText(Path.Combine(rateStream, i + ".ts"), "R" + i);

                PatchReport rate = SegmentPatcher.Patch(rateDir, true, null);
                Eq(30, rate.Missing, "补片比例: 缺 30 片");
                Eq(30, rate.Patched, "补片比例: 缺 30% 也全补，无 20 片硬上限");
                Eq(0, rate.Skipped, "补片比例: 未触发放弃");
                Eq(100, Directory.GetFiles(rateStream, "*.ts").Length, "补片比例: 已补齐到 100 片");

                // 越过 30% 一线即放弃：100 片缺 31 片
                string overDir = Path.Combine(root, "over");
                string overStream = Path.Combine(overDir, "0____");
                Directory.CreateDirectory(overStream);
                File.WriteAllText(Path.Combine(overDir, "raw.m3u8"), m3u.ToString());
                for (int i = 0; i < 69; i++) File.WriteAllText(Path.Combine(overStream, i + ".ts"), "O" + i);

                PatchReport over = SegmentPatcher.Patch(overDir, true, null);
                Eq(0, over.Patched, "补片比例: 缺 31% 不补");
                Eq(31, over.Skipped, "补片比例: 放弃 31 片");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception) { }
            }
        }

        // ---------------- ArgQ ----------------
        private static void TestArgQ()
        {
            Eq("abc", ArgQ.Quote("abc"), "Quote 无需引号时不加引号");
            Eq("\"a b\"", ArgQ.Quote("a b"), "Quote 含空格加引号");
            Eq("\"a\\\"b\"", ArgQ.Quote("a\"b"), "Quote 内部双引号转义");
            Eq("\"\"", ArgQ.Quote(""), "Quote 空串加引号（命令行传空参数更安全）");
            Eq("\"\"", ArgQ.Quote(null), "Quote null 归一为空串再加引号");

            List<string> p = ArgQ.Split("--thread-count 16 -M format=mp4");
            Eq(4, p.Count, "Split 基本分词");
            Eq("--thread-count", p[0], "Split 首项");
            Eq("format=mp4", p[3], "Split 末项");

            List<string> q = ArgQ.Split("--save-dir \"D:\\a b\\c\"  -H \"X: 1\"");
            Eq(4, q.Count, "Split 引号内含空格不拆碎");
            Eq("\"D:\\a b\\c\"", q[1], "Split 保留引号（预览按原样还原命令行）");

            Eq(0, ArgQ.Split("   ").Count, "Split 空白串返回空列表");
            Eq(0, ArgQ.Split(null).Count, "Split null 返回空列表");

            // ---- D9 回归：token 自带引号时不得二次转义（否则引擎收到含字面引号的 header 名）----
            Eq("\"Cookie: a=b\"", ArgQ.Quote("\"Cookie: a=b\""), "Quote 预引号 token 原样保留");
            List<string> r = ArgQ.Split("--header \"Cookie: a=b\" --add-header \"Referer: https://x.com/a b\"");
            Eq(4, r.Count, "Split 预引号额外参数仍是 4 个 token");
            Eq("\"Cookie: a=b\"", ArgQ.Quote(r[1]), "Quote 不再给预引号 token 套第二层引号");
            // 展示命令行回拆后每个 token 必须与原 token 完全一致（一一对应）
            StringBuilder rebuilt = new StringBuilder();
            for (int i = 0; i < r.Count; i++)
            {
                if (i > 0) rebuilt.Append(' ');
                rebuilt.Append(ArgQ.Quote(r[i]));
            }
            List<string> again = ArgQ.Split(rebuilt.ToString());
            Eq(r.Count, again.Count, "Quote→Split 往返 token 数一致");
            bool same = true;
            for (int i = 0; i < r.Count && i < again.Count; i++)
            {
                if (r[i] != again[i]) same = false;
            }
            Ok(same, "Quote→Split 往返 token 内容一一对应");
        }

        // ---------------- Mask ----------------
        private static void TestMask()
        {
            string s1 = Mask.Secret("--custom-proxy http://user:pass@127.0.0.1:8888");
            Ok(s1.IndexOf("pass", StringComparison.Ordinal) < 0, "Secret 遮盖代理口令");
            string s2 = Mask.Secret("-H \"Cookie: SESSDATA=abc123\"");
            Ok(s2.IndexOf("abc123", StringComparison.Ordinal) < 0, "Secret 遮盖 Cookie");
            Eq("plain", Mask.Secret("plain"), "Secret 无敏感项原样返回");
        }

        // ---------------- Ver ----------------
        private static void TestVer()
        {
            Eq("1.2.3", Ver.Normalize("v1.2.3"), "Normalize 去 v 前缀");
            Eq("1.2.3", Ver.Normalize("V1.2.3"), "Normalize 去大写 V 前缀");
            Eq("1.2.3", Ver.Normalize(" 1.2.3 "), "Normalize 去空白");
            Ok(Ver.Compare("1.2.10", "1.2.9") > 0, "Compare 数字段按数值比较");
            Ok(Ver.Compare("1.2.3", "1.2.3") == 0, "Compare 相等");
            Ok(Ver.Compare("1.0.0", "2.0.0") < 0, "Compare 小版本在前");
            Ok(Ver.Compare("0.6.0+df70f0b", "0.6.0") >= 0, "Compare 容忍构建号后缀");
        }

        // ---------------- Fmt ----------------
        private static void TestFmt()
        {
            Ok(Fmt.Bytes(0).Length > 0, "Bytes 0 有输出");
            Ok(Fmt.Bytes(1048576).IndexOf("MB", StringComparison.OrdinalIgnoreCase) >= 0, "Bytes 1 MiB 用 MB 单位");
        }

        // ---------------- Safe ----------------
        private static void TestSafe()
        {
            Eq("abc", Safe.FileName("abc"), "FileName 普通名");
            Ok(Safe.FileName("a/b\\c:d*e?f\"g<h>i|j").IndexOfAny(new char[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) < 0,
                "FileName 清除非法字符");
            Ok(Safe.FileName("../x").IndexOf("..", StringComparison.Ordinal) < 0, "FileName 拦截路径穿越");
            Eq("https://a.com/b.m3u8", Safe.UrlFirst("https://a.com/b.m3u8 忽略我"), "UrlFirst 取首个 URL");
            Eq("first", Safe.FirstLine("first\r\nsecond"), "FirstLine 取首行");
        }

        // ---------------- Fs ----------------
        private static void TestFs()
        {
            string dir = Path.Combine(Path.GetTempPath(), "rem_gui_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string pe = Path.Combine(dir, "a.exe");
                File.WriteAllBytes(pe, new byte[] { 0x4D, 0x5A, 0x00, 0x01 });
                Ok(Fs.IsPE(pe), "IsPE 识别 MZ 头");
                Ok(!Fs.IsPE(Path.Combine(dir, "nope.exe")), "IsPE 不存在文件返回 false");

                string zip = Path.Combine(dir, "a.zip");
                File.WriteAllBytes(zip, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
                Ok(Fs.IsZip(zip), "IsZip 识别 PK 头");

                string txt = Path.Combine(dir, "a.txt");
                File.WriteAllText(txt, "x");
                Ok(!Fs.IsPE(txt) && !Fs.IsZip(txt), "普通文本既非 PE 也非 ZIP");

                string mv = Path.Combine(dir, "moved.txt");
                Ok(Fs.MoveAtomic(txt, mv), "MoveAtomic 返回 true");
                Ok(File.Exists(mv) && !File.Exists(txt), "MoveAtomic 完成搬运");

                Ok(Fs.Head(zip, 2).Length == 2, "Head 读取指定字节数");
            }
            finally
            {
                Fs.TryDeleteDir(dir);
            }
        }

        // ---------------- Json ----------------
        private static void TestJson()
        {
            JVal v = JVal.Parse("{\"a\":1,\"b\":\"x\",\"c\":true,\"d\":[1,2],\"e\":{\"f\":null}}");
            Ok(v != null && v.Kind == JKind.Object, "Parse 对象");
            Eq("x", v.GetStr("b", ""), "GetStr");
            Eq(1, v.GetInt("a", -1), "GetInt");
            Ok(v.GetBool("c", false), "GetBool");
            Eq(2, v.GetArr("d").Count, "GetArr 长度");
            Ok(v.Get("e").Get("f").IsNull, "嵌套 null");
            Eq("fallback", v.GetStr("zz", "fallback"), "缺键回落默认值");

            Ok(SafeParse("not json") == null || SafeParse("not json").IsNull || SafeParse("not json").Kind == JKind.Number,
                "Parse 非法输入不抛异常");
            Ok(SafeParse("") == null || SafeParse("").IsNull, "Parse 空串得到空值");
            Ok(SafeParse("[1,") == null || SafeParse("[1,").Kind != JKind.Null, "Parse 截断数组不抛异常");
            Ok(JVal.Parse(null) == null || JVal.Parse(null).IsNull, "Parse null 安全");

            // D2 回归：畸形 token 不得让解析器原地空转（修复前这三条会死循环，测试永不返回）
            Ok(JVal.Parse("{x:1}") != null, "畸形对象 {x:1} 返回而不死循环");
            Ok(JVal.Parse("{\"a\":na}") != null, "畸形值 {\"a\":na} 返回而不死循环");
            JVal badArr = JVal.Parse("{\"a\":[1,x]}");
            Ok(badArr != null && badArr.GetArr("a").Count >= 1, "畸形数组 {\"a\":[1,x]} 返回已解析部分");

            JVal o = JVal.NewObj();
            o.Set("k", "值\"含引号");
            o.Set("n", 3);
            o.Set("b", false);
            JVal arr = JVal.NewArr();
            arr.Add(JVal.Of(1));
            o.Set("arr", arr);
            string text = o.ToJson();
            JVal back = JVal.Parse(text);
            Ok(back != null, "ToJson→Parse 往返可解析");
            if (back != null)
            {
                Eq("值\"含引号", back.GetStr("k", ""), "往返保留引号转义");
                Eq(3, back.GetInt("n", 0), "往返保留整数");
                Eq(1, back.GetArr("arr").Count, "往返保留数组");
            }
        }

        // ---------------- 进度解析（OnTick 的核心算法，纯函数可测） ----------------
        private static void TestProgress()
        {
            Progress p = new Progress();
            string spd;

            Ok(!p.Feed("普通日志一行", out spd), "Feed 非进度行返回 false");
            Ok(!p.Feed("", out spd), "Feed 空串返回 false");
            Ok(!p.Feed(null, out spd), "Feed null 返回 false");

            Ok(p.Feed("[下载] 12/51", out spd), "Feed 识别分片计数帧");
            Eq(12, p.Done, "Feed Done");
            Eq(51, p.Total, "Feed Total");
            Eq(23, p.PercentInt, "Feed 百分比向下取整");

            // 计数与百分比同帧时以计数为准，避免 Done<Total 却显示 100%
            p.Reset();
            p.Feed("50/100 100.0%", out spd);
            Eq(50, p.Done, "同帧以计数为准 Done");
            Eq(50, p.PercentInt, "同帧忽略虚高百分比");

            // 纯百分比帧（合并阶段只刷 %）：满 100 时把计数拉齐
            p.Reset();
            p.Feed("0/40", out spd);
            p.Feed("100.0%", out spd);
            Eq(40, p.Done, "纯百分比满 100 时计数拉齐");

            // 速度解析
            p.Reset();
            p.Feed("8/10 1.5MB/s", out spd);
            Ok(p.Speed.IndexOf("MB/s", StringComparison.Ordinal) >= 0, "Feed 解析速度单位");
            Eq("1.5 MB/s", p.Speed, "Feed 速度文本归一");

            // 越界保护：Done > Total 时截断
            p.Reset();
            p.Feed("60/50", out spd);
            Eq(50, p.Done, "Done 超 Total 时截断");

            // 文本与重置
            p.Reset();
            Eq("0/?   --%", p.Text, "Text 初始占位");
            p.Feed("5/10", out spd);
            Ok(p.PercentInt == 50, "PercentInt 50");
            p.Reset();
            Eq(-1.0, p.Pct, "Reset 回到未开始");
        }

        private static JVal SafeParse(string t)
        {
            try { return JVal.Parse(t); }
            catch (Exception) { return null; }
        }

        // ---------------- AppConfig ----------------
        private static void TestConfig()
        {
            AppConfig c = new AppConfig();
            Ok(c.Threads > 0 && c.Retries >= 0 && c.Timeout > 0, "默认线程/重试/超时有值");
            Ok(c.SpeedArg().Length == 0, "默认不限速");

            c.SpeedMB = 5;
            Eq("5M", c.SpeedArg(), "SpeedArg 整数 M");
            c.SpeedMB = 0.5;
            Eq("0.5M", c.SpeedArg(), "SpeedArg 小数 M（500KB/s）");
            c.SpeedMB = 0.001;
            Eq("0.001M", c.SpeedArg(), "SpeedArg 最小粒度 1KB/s");
            c.SpeedMB = 12.3456;
            Eq("12.346M", c.SpeedArg(), "SpeedArg 四舍五入到 3 位");
            c.SpeedMB = 0;
            Eq("", c.SpeedArg(), "SpeedArg 0 表示不限");

            c.SpeedMB = -3;
            c.Clamp();
            Eq(0.0, c.SpeedMB, "Clamp 限速负值归零");
            c.SpeedMB = 5000;
            c.Clamp();
            Eq(999.999, c.SpeedMB, "Clamp 限速上限 999.999");

            Eq(0.8, AppConfig.ToMB(800, "KB"), "旧配置 KB 折算成 M");
            Eq(3000.0, AppConfig.ToMB(3, "GB"), "旧配置 GB 折算成 M");

            c.Threads = 9999; c.Retries = -5; c.Timeout = 0; c.LogLimit = 1;
            c.Clamp();
            Ok(c.Threads <= 256, "Clamp 线程上限");
            Ok(c.Retries >= 0, "Clamp 重试非负");
            Ok(c.Timeout >= 1, "Clamp 超时下限");
            Ok(c.LogLimit >= 20000, "Clamp 日志上限下限");

            // ---- 解密字段持久化：解密方式固定 AES_128；KEY/IV 只落 DPAPI 加密字段，磁盘上不能见明文 ----
            AppConfig c2 = new AppConfig();
            c2.HlsMethod = "SAMPLE_AES";           // 旧配置 / 手工塞进来的其它取值
            c2.HlsKey = "00112233445566778899aabbccddeeff";
            c2.HlsIv = "ABCDEF00112233445566778899AABBCC";
            c2.Clamp();

            AppConfig fresh = new AppConfig();
            Eq("AES_128", fresh.HlsMethod, "默认解密方式 AES_128（唯一取值）");
            Eq("AES_128", c2.HlsMethod, "Clamp 把任意旧加密方式拉回 AES_128");

            c2.HlsKey = null; c2.HlsMethod = null;
            c2.Clamp();
            Eq("", c2.HlsKey, "Clamp 把 null KEY 归一为空串");
            Eq("AES_128", c2.HlsMethod, "Clamp 把 null 加密方式也拉回 AES_128");

            c2.HlsKey = "00112233445566778899aabbccddeeff";   // 恢复，供下面的落盘往返用
            string cfgPath = Path.Combine(Path.GetTempPath(), "rem_cfg_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ConfigStore.Save(cfgPath, c2);
                string raw = File.ReadAllText(cfgPath);
                Ok(raw.IndexOf("00112233445566778899aabbccddeeff") < 0, "配置文件不落明文 KEY");
                Ok(raw.IndexOf("hlsKeyEnc") >= 0, "配置文件写 hlsKeyEnc 加密字段");
                Ok(raw.IndexOf("decEngine") < 0, "配置文件不再写 decEngine（解密引擎已移除）");
                Ok(raw.IndexOf("keyTextFile") < 0, "配置文件不再写 keyTextFile（密钥文件已移除）");

                AppConfig c3 = ConfigStore.Load(cfgPath, null);
                Eq(c2.HlsKey, c3.HlsKey, "KEY 存盘往返还原");
                Eq(c2.HlsIv, c3.HlsIv, "IV 存盘往返还原");
                Eq("AES_128", c3.HlsMethod, "解密方式往返恒为 AES_128");

                // 旧版本配置文件：hlsMethod=SAMPLE_AES + decEngine + keyTextFile 残留键，读取时一律不采信
                File.WriteAllText(cfgPath,
                    "{\"hlsMethod\":\"SAMPLE_AES\",\"decEngine\":\"FFMPEG\",\"keyTextFile\":\"D:\\\\k\\\\k.txt\"}",
                    new UTF8Encoding(false));
                AppConfig oldCfg = ConfigStore.Load(cfgPath, null);
                Eq("AES_128", oldCfg.HlsMethod, "旧配置 hlsMethod=SAMPLE_AES 读取后仍为 AES_128");
                BuiltArgs bo = ArgBuilder.Build(oldCfg, "https://a.com/v.m3u8", "C:\\tmp", "v",
                                               "eng.exe", "", "", null, null);
                Eq("<none>", ArgVal(bo.Args, "--decryption-engine"), "旧配置 decEngine 残留也不下发 --decryption-engine");
                Eq("<none>", ArgVal(bo.Args, "--key-text-file"), "旧配置 keyTextFile 残留也不下发 --key-text-file");
                Ok(!c3.UseSystemProxy, "系统代理默认不使用（默认值 false）");
                Ok(c3.UseSystemProxy == c2.UseSystemProxy, "系统代理开关存盘往返一致");
            }
            finally
            {
                Fs.TryDeleteFile(cfgPath);
            }

            // ---- D15 回归：DPAPI 解密失败必须留下字段名，不能静默清空 ----
            string badPath = Path.Combine(Path.GetTempPath(), "rem_cfg_bad_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                // proxyEnc 是非法 Base64 → Unprotect 必失败，代理字段被清空
                File.WriteAllText(badPath, "{\"saveDir\":\"C:\\\\dl\",\"proxyEnc\":\"这不是base64\"}", new UTF8Encoding(false));
                ConfigStore.DecryptFailedFields.Clear();
                AppConfig bad = ConfigStore.Load(badPath, null);
                Eq("", bad.Proxy, "解密失败时代理走空值");
                Ok(ConfigStore.DecryptFailedFields.Count == 1, "解密失败记录字段数=1");
                Eq("代理", ConfigStore.DecryptFailedFields[0], "解密失败记录字段名=代理");

                // 正常配置不得误报
                AppConfig good = new AppConfig();
                good.Proxy = "http://u:p@127.0.0.1:8888";
                good.Headers = "Cookie: a=1";
                ConfigStore.Save(badPath, good);
                ConfigStore.DecryptFailedFields.Clear();
                AppConfig back2 = ConfigStore.Load(badPath, null);
                Eq("http://u:p@127.0.0.1:8888", back2.Proxy, "正常配置代理往返还原");
                Eq(0, ConfigStore.DecryptFailedFields.Count, "正常配置不产生解密失败记录");
            }
            finally
            {
                Fs.TryDeleteFile(badPath);
            }
        }

        // ---------------- UpdateManifest / 自更新 URL ----------------
        private static void TestUpdateManifest()
        {
            string emptyDir = Path.Combine(Path.GetTempPath(), "rem_um_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(emptyDir);
            try
            {
                // ① 内嵌资源缺失 + 外部文件缺失 → DefaultJson 兜底：不抛异常、清单不空
                UpdateManifest d = UpdateManifest.Load(emptyDir, null);
                Eq(Program.Version, d.AppVer, "清单兜底：外部与内嵌都缺失时用 DefaultJson 版本");
                Eq("nilaoda/N_m3u8DL-RE", d.Repo, "清单兜底：引擎 repo 不回归");
                Eq("binglanziyu/REm3u8可视化界面", d.SelfRepo, "清单兜底：DefaultJson 带 self.repo");
                Eq("REM-GUI.exe", d.SelfAssetHint, "清单兜底：DefaultJson 带 self.assetHint");
                Ok(d.Mirrors.Count > 0, "清单兜底：镜像列表不空");

                // ② 外部文件存在 → 优先于内嵌/兜底（便于不改 exe 就覆盖清单调试）
                string ext = Path.Combine(emptyDir, "update.json");
                File.WriteAllText(ext,
                    "{\"version\":\"9.9.9\",\"engine\":{\"repo\":\"a/b\",\"assetHint\":\"x64\",\"mirrors\":[\"\"]}," +
                    "\"self\":{\"repo\":\"u/v\",\"assetHint\":\"X.exe\"}}",
                    new UTF8Encoding(false));
                UpdateManifest o = UpdateManifest.Load(emptyDir, null);
                Eq("9.9.9", o.AppVer, "清单读取：外部 update.json 优先");
                Eq("u/v", o.SelfRepo, "清单读取：外部文件里的 self.repo 生效");
                Eq("X.exe", o.SelfAssetHint, "清单读取：外部文件里的 self.assetHint 生效");

                // ③ 外部文件是空的/坏内容 → 不崩，回落下一级
                File.WriteAllText(ext, "   ", new UTF8Encoding(false));
                UpdateManifest z = UpdateManifest.Load(emptyDir, null);
                Eq(Program.Version, z.AppVer, "清单读取：外部文件为空时回落兜底");
                Eq("binglanziyu/REm3u8可视化界面", z.SelfRepo, "清单读取：回落兜底后 self.repo 仍在");

                // ④ 老清单没有 self 段 → 用常量兜底，自更新检查不至于整块失效
                File.WriteAllText(ext, "{\"version\":\"1.0.0\",\"engine\":{\"repo\":\"a/b\",\"mirrors\":[\"\"]}}",
                    new UTF8Encoding(false));
                UpdateManifest old = UpdateManifest.Load(emptyDir, null);
                Eq("1.0.0", old.AppVer, "清单读取：老清单版本正常");
                Eq("binglanziyu/REm3u8可视化界面", old.SelfRepo, "清单读取：缺 self 段时用默认仓库兜底");
                Eq("REM-GUI.exe", old.SelfAssetHint, "清单读取：缺 self 段时用默认资产名兜底");

                // ⑤ 中文仓库名逐段编码：不编码 api.github.com 会 404
                Eq("binglanziyu/REm3u8%E5%8F%AF%E8%A7%86%E5%8C%96%E7%95%8C%E9%9D%A2",
                   UpdateChecker.EncodeRepo("binglanziyu/REm3u8可视化界面"), "EncodeRepo 中文段百分号编码");
                Eq("https://api.github.com/repos/binglanziyu/REm3u8%E5%8F%AF%E8%A7%86%E5%8C%96%E7%95%8C%E9%9D%A2/releases/latest",
                   UpdateChecker.RepoApiUrl("binglanziyu/REm3u8可视化界面"), "自更新 API URL 中文段已编码");
                Eq("https://github.com/binglanziyu/REm3u8%E5%8F%AF%E8%A7%86%E5%8C%96%E7%95%8C%E9%9D%A2/releases",
                   UpdateChecker.RepoPageUrl("binglanziyu/REm3u8可视化界面"), "发布页 URL 中文段已编码");
                Eq("nilaoda/N_m3u8DL-RE", UpdateChecker.EncodeRepo(" nilaoda/N_m3u8DL-RE "), "EncodeRepo ASCII 仓库名原样（去空白）");
                Ok(UpdateChecker.RepoApiUrl("binglanziyu/REm3u8可视化界面").IndexOf("可视化") < 0,
                   "自更新 URL 里不得出现裸中文");
                Eq("", UpdateChecker.EncodeRepo(""), "EncodeRepo 空串返回空");

                // ⑥ tag_name / name 里取版本号
                Eq("3.1.0", UpdateChecker.PickVersion("v3.1.0", ""), "PickVersion 取 tag_name");
                Eq("3.2.1", UpdateChecker.PickVersion("", "REM-GUI v3.2.1 发布"), "PickVersion tag 缺失时取名称");
                Eq("4.0.0", UpdateChecker.PickVersion("latest", "REM-GUI 4.0.0"), "PickVersion tag 非版本号时取名称");
                Eq("", UpdateChecker.PickVersion("nightly", "无数字"), "PickVersion 无版本号返回空");
                Ok(UpdateChecker.IsNumericVer("3.1.0"), "IsNumericVer 认识 3.1.0");
                Ok(!UpdateChecker.IsNumericVer("v3.1.0"), "IsNumericVer 不吃 v 前缀");

                // ⑦ 自更新检查的异常兜底：没配 self.repo 时不联网、不误报
                UpdateManifest nz = new UpdateManifest();
                nz.SelfRepo = "";
                UpdateResult rz = new UpdateResult();
                UpdateChecker.CheckSelf(nz, Program.Version, null, null, rz);
                Eq("", rz.AppPageUrl, "自更新：未配置 self.repo 不给地址");
                Ok(!rz.AppNewer, "自更新：未配置 self.repo 不误报新版本");

                // ⑧ 清单为 null 时引擎检查也不崩（早退，不影响主流程）
                UpdateResult rn = UpdateChecker.Check(null, "0.6.0", Program.Version, null, null);
                Ok(rn.Message.IndexOf("引擎仓库") >= 0, "Check(null) 安全早退");
            }
            finally
            {
                try { Directory.Delete(emptyDir, true); } catch (Exception) { }
            }
        }

        // ---------------- ArgBuilder ----------------
        private static void TestArgBuilder()
        {
            List<string> h = ArgBuilder.HeaderLines("Cookie: a=1\r\nUser-Agent: X\r\n坏行没有冒号\r\n:空键");
            Eq(2, h.Count, "HeaderLines 只保留合法行");
            Eq("Cookie: a=1", h[0], "HeaderLines 原样归一");

            Eq("abc", ArgBuilder.GuessNameFromUrl("https://a.com/p/abc.m3u8?t=1"), "GuessNameFromUrl 常规");
            Eq("p", ArgBuilder.GuessNameFromUrl("https://a.com/p/index.m3u8"), "GuessNameFromUrl index 取父目录");
            Eq("", ArgBuilder.GuessNameFromUrl(""), "GuessNameFromUrl 空串");

            Eq("x", ArgBuilder.UniqueName("", "x"), "UniqueName 目录为空时不改名");

            // ---- 解密：未填任何 KEY 时不能凭空拼出密钥参数；解密方式恒为 AES_128 ----
            AppConfig d0 = new AppConfig();
            d0.Clamp();
            BuiltArgs b0 = ArgBuilder.Build(d0, "https://a.com/v.m3u8", "C:\\tmp", "v",
                                            "eng.exe", "", "", null, null);
            Eq("<none>", ArgVal(b0.Args, "--custom-hls-key"), "解密：KEY 为空不传 --custom-hls-key");
            Eq("<none>", ArgVal(b0.Args, "--custom-hls-iv"), "解密：IV 为空不传 --custom-hls-iv");
            Eq("<none>", ArgVal(b0.Args, "--custom-hls-method"), "解密：未填 KEY/IV 时不发 --custom-hls-method（明文流会被强行解密而崩溃）");

            // ---- 已移除的解密能力：五个参数在任何配置下都不得出现 ----
            Eq("<none>", ArgVal(b0.Args, "--decryption-engine"), "已删：不下发 --decryption-engine");
            Eq("<none>", ArgVal(b0.Args, "--decryption-binary-path"), "已删：不下发 --decryption-binary-path");
            Eq("<none>", ArgVal(b0.Args, "--mp4-real-time-decryption"), "已删：不下发 --mp4-real-time-decryption");
            Eq("<none>", ArgVal(b0.Args, "--key"), "已删：不下发 --key");
            Eq("<none>", ArgVal(b0.Args, "--key-text-file"), "已删：不下发 --key-text-file");
            Ok(b0.CommandLine.IndexOf("--decryption-engine") < 0, "预览命令行同样无 --decryption-engine");
            Ok(b0.CommandLine.IndexOf("--key-text-file") < 0, "预览命令行同样无 --key-text-file");
            Ok(b0.CommandLine.IndexOf("--mp4-real-time-decryption") < 0, "预览命令行同样无 --mp4-real-time-decryption");

            // ---- 解密：填了就按引擎参数原样拼，KEY 去空白、IV 原样 ----
            AppConfig d1 = new AppConfig();
            d1.HlsMethod = "aes-128";              // 旧写法：大小写/连字符各异的加密方式
            d1.HlsKey = " 00112233445566778899aabbccddeeff ";
            d1.HlsIv = "00000000000000000000000000000000";
            d1.Clamp();
            BuiltArgs b1 = ArgBuilder.Build(d1, "https://a.com/v.m3u8", "C:\\tmp", "v",
                                            "eng.exe", "", "", null, null);
            Eq("AES_128", ArgVal(b1.Args, "--custom-hls-method"), "解密：填了 KEY/IV 才下发，取值恒为 AES_128");
            Eq("00112233445566778899aabbccddeeff", ArgVal(b1.Args, "--custom-hls-key"), "解密：KEY 去空白后原样下发");
            Eq("00000000000000000000000000000000", ArgVal(b1.Args, "--custom-hls-iv"), "解密：IV 原样下发");

            // ---- 旧配置残留：HlsMethod 塞别的值也不会凭空下发解密参数 ----
            AppConfig d2 = new AppConfig();
            d2.HlsMethod = "CENC";
            d2.Clamp();
            BuiltArgs b2 = ArgBuilder.Build(d2, "https://a.com/v.m3u8", "C:\\tmp", "v",
                                            "eng.exe", "", "", null, null);
            Eq("<none>", ArgVal(b2.Args, "--custom-hls-method"), "解密：无 KEY/IV 时 CENC 等旧值也不会下发任何解密方式");
            Eq("<none>", ArgVal(b2.Args, "--decryption-engine"), "解密：旧值不会带出 --decryption-engine");
            Ok(Mask.Secret("--custom-hls-key ABCDEF0123 --proxy-a FFMPEG").IndexOf("FFMPEG") > 0,
               "掩码只打 KEY/IV 的值，不误伤后续其它参数值");
            Ok(Mask.Secret("--custom-hls-key ABCDEF0123").IndexOf("ABCDEF0123") < 0, "KEY 值仍被打码");
            Ok(Mask.Secret("--custom-hls-iv 0000111122223333").IndexOf("0000111122223333") < 0, "IV 值仍被打码");

            // ---- 归一函数已删除：解密方式不再有"自动识别/其它取值"的归一入口，只剩一个常量 ----
            Eq("AES_128", AppConfig.HlsMethodAes128, "唯一解密方式常量为 AES_128");

            // ---- D8 系统代理开关（语义已反转）：默认不勾 = 显式下发 false；勾选 = 不下发 ----
            AppConfig p0 = new AppConfig();
            p0.Clamp();
            Ok(!p0.UseSystemProxy, "系统代理默认不使用（默认 false）");
            BuiltArgs bp0 = ArgBuilder.Build(p0, "https://a.com/v.m3u8", "C:\\tmp", "v",
                                             "eng.exe", "", "", null, null);
            Eq("false", ArgVal(bp0.Args, "--use-system-proxy"), "默认不勾选 → 显式下发 --use-system-proxy false");
            Ok(bp0.Args.IndexOf("--use-system-proxy") + 1 == bp0.Args.IndexOf("false"),
               "参数名与取值紧邻成对（预览与实际下发一致）");

            AppConfig p1 = new AppConfig();
            p1.UseSystemProxy = true;
            p1.Clamp();
            BuiltArgs bp1 = ArgBuilder.Build(p1, "https://a.com/v.m3u8", "C:\\tmp", "v",
                                             "eng.exe", "", "", null, null);
            Eq("<none>", ArgVal(bp1.Args, "--use-system-proxy"), "勾选使用系统代理 → 不下发该参数（沿用引擎默认 True）");

            // 存盘往返：反转后的 true/false 都要原样还原
            string pxPath = Path.Combine(Path.GetTempPath(), "rem_px_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ConfigStore.Save(pxPath, p1);
                Ok(ConfigStore.Load(pxPath, null).UseSystemProxy, "系统代理 true 存盘往返还原");
                ConfigStore.Save(pxPath, p0);
                Ok(!ConfigStore.Load(pxPath, null).UseSystemProxy, "系统代理 false 存盘往返还原");
            }
            finally
            {
                Fs.TryDeleteFile(pxPath);
            }

            // ---- KEY 形态识别：只用于界面提示，识别不了不拦 ----
            Eq("HEX", ArgBuilder.KeyShape("00112233445566778899aabbccddeeff"), "KeyShape 32 位 HEX");
            Eq("HEX", ArgBuilder.KeyShape("0011223344556677"), "KeyShape 16 位 HEX（8 字节 IV）");
            Eq("BASE64", ArgBuilder.KeyShape("MDEyMzQ1Njc4OWFiY2RlZg=="), "KeyShape Base64");
            Eq("", ArgBuilder.KeyShape("zzz"), "KeyShape 既非 HEX 也非 Base64 返回空");
            Eq("", ArgBuilder.KeyShape(""), "KeyShape 空串返回空");
        }
    }
}
