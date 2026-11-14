// =====================================================================
//  UI\MainForm.Job.cs — MainForm 的任务生命周期与进度轮询分区（partial 拆分）
//  纯代码搬移，逻辑与行为与原 MainForm.cs 完全一致
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Rem.Core;
using Rem.Engine;
using Rem.Interop;
using Rem.Services;

namespace Rem.UI
{
    public sealed partial class MainForm : Form
    {
        // ================= 坏片自动补片（auto-patch）状态机 =================
        // 0 = 未介入；1 = 已重拉清单续跑一次；2 = 已用相邻分片补好并再次续跑；>=2 不再接续
        // 每次用户新开任务在 OnStart 里归零；引擎失败后只在这里单调推进，杜绝续跑死循环
        private int patchStage;
        // 引擎的失败信号：坏片「分片数量校验不通过」、合并/混流失败「Mux failed / ERROR: Failed」，
        // 这几种情况的退出码全是 0，只认退出码会把失败当成完成并清掉续传与分片
        private bool sawJobFailed;
        private string lastUrl = "";
        private string lastDir = "";
        private string lastExe = "";
        private string lastFF = "";
        private string lastName = "";
        private string lastTmp = "";
        private string lastReLog = "";

        private void OnStart()
        {
            if (state != RunState.Idle)
            {
                SetStatus("任务进行中，请先停止", Theme.Orange);
                return;
            }

            // 用户新开任务：清空上一轮自动补片状态，避免接续标记串到新任务
            patchStage = 0;
            sawJobFailed = false;
            lastTmp = "";

            string url = Safe.UrlFirst(txtUrl.Text);
            if (url.Length == 0)
            {
                SetStatus("请先粘贴下载链接", Theme.Orange);
                txtUrl.Focus();
                return;
            }
            // 先判格式：非法链接下发给引擎只会换来 exit=1 + 一句藏在 stdout 里的“加载URL失败”（D6）
            if (!Prechecker.IsHttpUrl(url))
            {
                log.Err(">> 链接格式不正确（需要 http:// 或 https:// 开头的完整地址）：" + Mask.Secret(url));
                SetStatus("链接格式不正确", Theme.Red);
                txtUrl.Focus();
                return;
            }

            string exe = ResolveEngineExe();
            if (exe.Length == 0 || !File.Exists(exe))
            {
                log.Err("未找到 N_m3u8DL-RE.exe，请放到程序目录或选择外部引擎");
                SetStatus("引擎缺失", Theme.Red);
                return;
            }

            string dir = txtDir.Text.Trim();
            if (dir.Length == 0)
            {
                dir = DefaultSaveDir();
                txtDir.Text = dir;
            }
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // 只判“能建目录”不够：目录已存在但只读 / 介质满时会被放行，引擎要先把整条 HTTP 重试探完
                // 才在写盘阶段失败，用户只看到“退出码 1”。这里做一次真实写入探针（写后立即删）。（D5）
                string probe = Path.Combine(dir, ".rem_write_test_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                log.Err("保存目录不可写：" + dir + " —— " + ex.Message);
                SetStatus("保存目录不可写", Theme.Red);
                return;
            }

            long free = AppPaths.FreeBytes(dir);
            if (free >= 0 && free < 200L * 1024 * 1024)
            {
                log.Warn(">> 目标磁盘剩余 " + Fmt.Bytes(free) + "，空间可能不足");
            }

            CollectConfig();
            SaveConfig();

            string ff = AppPaths.FindTool(AppPaths.FFmpegName);
            if (ff.Length == 0) log.Warn(">> 未找到 ffmpeg.exe，混流可能失败");

            log.OpenFile(Path.Combine(AppPaths.LogDir, "gui_" + DateTime.Now.ToString("yyyyMMdd") + ".log"));

            if (cfg.Smart && !cfg.SkipDownload && !IsSocksProxy(cfg.Proxy))
            {
                StartPrecheck(url, dir, exe, ff);
            }
            else
            {
                BeginRun(null, url, dir, exe, ff, "");
            }
        }

        private static bool IsSocksProxy(string proxy)
        {
            return !string.IsNullOrEmpty(proxy) &&
                   proxy.Trim().StartsWith("socks", StringComparison.OrdinalIgnoreCase);
        }

        private void StartPrecheck(string url, string dir, string exe, string ff)
        {
            state = RunState.Precheck;
            preUrl = url;
            preDir = dir;
            preExe = exe;
            preFF = ff;
            preResult = null;
            preDone = false;
            preBusy = true;
            preCanceled = false;
            int seq = ++preSeq;
            // 预检本身可能耗时十几秒（清单 15s 超时 + 首尾分片各 10s 探测），
            // 必须让“停止”从预检一开始就可用，否则用户只能干等（D4）
            btnStart.Enabled = false;
            btnStop.Enabled = true;
            SetStatus("预检中…", Theme.Blue);
            log.Dim(">> 预检：" + Mask.Secret(url));

            AppConfig snapshot = cfg;
            ThreadPool.QueueUserWorkItem(delegate (object st)
            {
                PrecheckResult r;
                try
                {
                    r = Prechecker.Run(url, snapshot, log);
                }
                catch (Exception ex)
                {
                    r = new PrecheckResult();
                    r.Kind = "ok";
                    r.Message = "预检异常，直接开始：" + ex.Message;
                }
                // 已被取消或被新一次预检取代：结果直接丢弃，不能回填给 UI（D4）
                if (seq != preSeq || preCanceled) return;
                preResult = r;
                preDone = true;
                preBusy = false;
            });
        }

        private void ConsumePrecheck()
        {
            // 用户已在预检期间点了停止：忽略结果并回到空闲，不再启动下载（D4）
            if (preCanceled)
            {
                preCanceled = false;
                preResult = null;
                preDone = false;
                preBusy = false;
                if (state != RunState.Idle) state = RunState.Idle;
                btnStart.Enabled = true;
                btnStop.Enabled = false;
                return;
            }

            PrecheckResult r = preResult;
            preResult = null;
            preDone = false;
            if (r == null)
            {
                BeginRun(null, preUrl, preDir, preExe, preFF, "");
                return;
            }

            if (r.Kind == "abort")
            {
                log.Err(">> " + r.Message);
                SetStatus("预检未通过", Theme.Red);
                state = RunState.Idle;
                btnStart.Enabled = true;
                btnStop.Enabled = false;
                return;
            }

            if (r.Kind == "master" && r.Variants.Count > 1)
            {
                log.Info(">> " + r.Message);
                using (VariantForm f = new VariantForm(r.Variants, "选择清晰度"))
                {
                    if (f.ShowDialog(this) != DialogResult.OK)
                    {
                        SetStatus("已取消");
                        state = RunState.Idle;
                        btnStart.Enabled = true;
                        btnStop.Enabled = false;
                        return;
                    }
                    if (f.AutoBest)
                    {
                        bool old = cfg.AutoSelect;
                        cfg.AutoSelect = true;
                        BeginRun(null, preUrl, preDir, preExe, preFF, "");
                        cfg.AutoSelect = old;
                        return;
                    }
                    VariantInfo v = r.Variants[f.SelectedIndex];
                    log.Info(">> 已选：" + v.Label);
                    bool oldAuto = cfg.AutoSelect;
                    cfg.AutoSelect = false;
                    BeginRun(null, v.ResolvedUri, preDir, preExe, preFF, "");
                    cfg.AutoSelect = oldAuto;
                    return;
                }
            }

            log.Info(">> " + r.Message);
            BeginRun(null, preUrl, preDir, preExe, preFF, "");
        }

        // 解密方式固定 AES-128：只剩「填了 KEY / IV」这一种情况需要提示，
        // 解密引擎、解密程序路径与密钥文件已移除，不再检测 mp4decrypt / shaka-packager 是否存在。
        private void CheckDecryptReady()
        {
            string key = cfg.HlsKey == null ? "" : cfg.HlsKey.Trim();
            string iv = cfg.HlsIv == null ? "" : cfg.HlsIv.Trim();
            if (key.Length > 0 && iv.Length == 0)
                log.Warn(">> 已填 KEY 但没填 IV：源播放列表若未声明 IV，解出来的数据会是坏的");
        }

        private void BeginRun(PrecheckResult pre, string url, string dir, string exe, string ff, string resumeTmp)
        {
            try
            {
                CheckDecryptReady();
                string reLog = Path.Combine(AppPaths.LogDir,
                    "re_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + (jobIndex + 1) + ".log");

                BuiltArgs args = ArgBuilder.Build(cfg, url, dir, txtName.Text, exe, ff, reLog, resumeTmp, "");
                currentArgs = args;
                txtCmd.Text = args.CommandLine;

                // 记下本次任务的原始参数：自动补片续跑要用同一份 url / 目录 / 引擎 / 临时目录
                lastUrl = url;
                lastDir = dir;
                lastExe = exe;
                lastFF = ff;
                lastName = args.SaveName;
                lastTmp = args.TmpDir;
                lastReLog = reLog;

                runner = new JobRunner();
                if (!runner.Start(exe, args, dir, log))
                {
                    runner.Dispose();
                    runner = null;
                    state = RunState.Idle;
                    btnStart.Enabled = true;
                    btnStop.Enabled = false;
                    SetStatus("引擎启动失败", Theme.Red);
                    return;
                }

                jobIndex++;
                jobStart = DateTime.Now;
                prog.Reset();
                barDone = false;
                lblStatus.Font = Theme.Ui(9F);   // 上一次成功的加粗收回去，状态栏长度不跳
                SetTitleState(null);            // 标题也从"✔ 下载完成"复位回正常
                HideBanner();
                RefreshProgressUi();
                state = RunState.Running;
                btnStart.Enabled = false;
                btnStop.Enabled = true;
                SetStatus("下载中 " + args.SaveName, Theme.Accent);
                log.Info(">> 开始任务 #" + jobIndex + "：" + args.SaveName);
                log.Dim(">> " + args.CommandLine);

                currentResumeFile = WriteResume(args, resumeTmp);
            }
            catch (Exception ex)
            {
                log.Err("启动失败：" + ex.Message);
                state = RunState.Idle;
                btnStart.Enabled = true;
                btnStop.Enabled = false;
                SetStatus("启动失败", Theme.Red);
            }
        }

        private void OnStop()
        {
            // 预检阶段还没有 runner：置取消标志并立刻回到空闲，异步线程的结果由标志 + 世代号丢弃（D4）
            if (state == RunState.Precheck)
            {
                preCanceled = true;
                preSeq++;
                preBusy = false;
                preDone = false;
                preResult = null;
                state = RunState.Idle;
                btnStart.Enabled = true;
                btnStop.Enabled = false;
                log.Warn(">> 已取消预检");
                SetStatus("已取消预检", Theme.Orange);
                return;
            }
            if (runner == null) return;
            log.Warn(">> 正在停止…");
            SetStatus("停止中…", Theme.Orange);
            runner.Stop();
        }

        private void OnTick(object sender, EventArgs e)
        {
            ticks++;

            // 每个子步骤独立兜底（P17）：单点异常不再连带跳过本 tick 的其余逻辑，
            // 也不会让一次 UI 更新失误演进成“进度卡死但无日志”。
            Step("drain", delegate () { FlushLog(); });
            Step("banner", delegate ()
            {
                // 横幅到点自动收，别一直压着日志；收起后日志原样露出来
                if (banner != null && banner.Visible && bannerUntil != DateTime.MinValue
                    && DateTime.Now >= bannerUntil) HideBanner();
            });
            Step("job", delegate ()
            {
                if (runner != null)
                {
                    List<LineItem> lines = runner.Drain();
                    if (lines != null) HandleLines(lines);
                    if (runner.HasExited) CompleteJob();
                }
            });
            Step("precheck", delegate ()
            {
                if (state == RunState.Precheck && !preBusy && preDone) ConsumePrecheck();
            });
            Step("update", delegate ()
            {
                if (!updBusy && updDone)
                {
                    updDone = false;
                    ConsumeUpdate();
                }
            });
            Step("engine", delegate ()
            {
                if (engineProbeDone)
                {
                    engineProbeDone = false;
                    ApplyEngineVersions();
                }
            });
            Step("tools", delegate ()
            {
                if (toolsReady)
                {
                    toolsReady = false;
                    RefreshEngineList();
                    StartEngineProbe();
                }
            });
            Step("installPct", delegate ()
            {
                if (installBusy && installPct >= 0)
                {
                    lblUpd.Text = "下载中 " + installPct + "%";
                    lblUpd.ForeColor = Theme.Blue;
                }
            });
            Step("install", delegate ()
            {
                if (installDone)
                {
                    installDone = false;
                    ConsumeInstall();
                }
            });
            Step("deferred", delegate ()
            {
                if (deferredUpdate && ticks >= 10)
                {
                    deferredUpdate = false;
                    StartUpdateCheck(true);
                }
            });
            // 第二个实例或系统拖放传来的链接：每 1 秒探一次中转文件
            Step("drop", delegate ()
            {
                if (ticks % 10 == 0) DrainStartupDrop();
            });
            // 会话日志每秒落盘一次，避免异常退出丢最后几行
            Step("flush", delegate () { if (ticks % 10 == 0) log.FlushFile(); });
            Step("status", delegate ()
            {
                // 落盘整体失效时常驻提示，别让用户以为日志文件里还能查到本次会话（D18）
                if (log.DiskBroken)
                {
                    SetStatus("日志未落盘：写文件失败，界面日志仍可用", Theme.Orange);
                    return;
                }
                if (state == RunState.Running && ticks % 5 == 0)
                {
                    TimeSpan el = DateTime.Now - jobStart;
                    if (el.TotalSeconds >= 1) SetStatus("下载中 " + currentName() + "   " + FormatDuration(el), Theme.Accent);
                }
            });
        }

        /// <summary>执行一个 tick 子步骤：异常就地兜底 + 落诊断，不牵连其他步骤。</summary>
        private void Step(string tag, Action run)
        {
            try
            {
                run();
            }
            catch (Exception ex)
            {
                Note.Swallow("Tick:" + tag, ex);
                log.Diag("Tick 步骤失败 " + tag + "：" + ex.Message + "\r\n" + ex.StackTrace, "Tick");
            }
        }

        private string currentName()
        {
            return currentArgs == null ? "" : currentArgs.SaveName;
        }

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalHours >= 1) return ((int)t.TotalHours) + ":" + t.ToString("mm\\:ss");
            return t.ToString("mm\\:ss");
        }

        private void CompleteJob()
        {
            JobRunner r = runner;
            // 顺序很关键：Finish 内部 WaitForExit(5000)，必须先等进程真正退出、管道读完，
            // 再取队列。反过来的话等待期间管道里剩下的最后几行（往往就是真实错误原因）永远取不到（D11）
            int code = r.Finish();
            bool killed = r.WasKilled;
            List<LineItem> rest = r.Drain();
            if (rest != null) HandleLines(rest);
            int dropped = r.DroppedLines;
            r.Dispose();
            // 读取线程 Join 之后仍可能有残余行落到队列里，再补取一次
            List<LineItem> tail = r.Drain();
            if (tail != null) HandleLines(tail);
            runner = null;

            state = RunState.Idle;
            btnStart.Enabled = true;
            btnStop.Enabled = false;
            btnStop.ForeColor = Theme.Dim;

            string name = currentName();
            TimeSpan el = DateTime.Now - jobStart;

            // 失败判定 = 退出码非 0，或退出码 0 但日志里已出现引擎的失败信号
            // （坏片是「分片数量校验不通过」，合并/混流失败是「Mux failed / ERROR: Failed」，
            //   不拦的话会被当成“完成”并删掉续传文件与分片）
            // 先落一个局部判定再交给自动补片：TryAutoPatch 内部会把 sawJobFailed 清掉（补片是"重来一次"），
            // 清掉之后若补片自己也没救回来，这里就再也看不出这轮本该判失败，于是 code==0 被误判成功（D14）
            bool jobFailed = !killed && (code != 0 || sawJobFailed);
            if (jobFailed)
            {
                if (TryAutoPatch(code))
                {
                    log.FlushFile();
                    return;
                }
            }

            if (killed)
            {
                log.Warn(">> 已停止：" + name);
                SetStatus("已停止", Theme.Orange);
                SetTitleState("■ 已停止");
                ShowBanner("■  已停止", name + "   ·   已下 " + prog.MainText + "   ·   点此收起", Theme.Orange, 6);
                Native.FlashTaskbar(Handle, 3);
            }
            else if (!jobFailed)
            {
                jobOk++;
                log.Ok(">> 完成：" + name + "  耗时 " + FormatDuration(el));
                SetStatus("完成：" + name, Theme.Green);
                RemoveResume();
                PruneParts();
                prog.Pct = 100;
                if (prog.Total > 0) prog.Done = prog.Total;   // 完成即 100%：分片计数同步拉齐，口径一致
                prog.Speed = "";                              // 完成后不该再挂着最后一帧的瞬时速率
                barDone = true;
                RefreshProgressUi();
                // 完成必须有存在感：进度条满格转绿 + 日志顶部整宽绿色横幅 + 任务栏按钮闪烁。
                // 横幅常驻（seconds=0）：切到别的窗口再回来，跑完这件事还在，直到点它或开始下一次任务
                lblStatus.Font = Theme.UiBold(9.5F);
                SetTitleState("✔ 下载完成");
                ShowBanner("✔  下载完成",
                    name + "   ·   " + FormatDuration(el) + "   ·   " + prog.MainText + "   ·   点此收起",
                    Theme.Green, 0);
                Native.FlashTaskbar(Handle, 4);
                log.Diag("进度收口 " + prog.Text, "Progress");
            }
            else
            {
                jobFail++;
                string why = code != 0 ? "退出码 " + code : "引擎报错（见日志）";
                log.Err(">> 失败：" + name + "  " + why + "  耗时 " + FormatDuration(el));
                log.Dim(">> 续传文件已保留，把它拖进窗口即可继续");
                SetStatus("失败：" + why, Theme.Red);
                SetTitleState("✕ 下载失败");
                prog.Speed = "";
                RefreshProgressUi();
                ShowBanner("✕  下载失败（" + why + "）",
                    name + "   ·   续传文件已保留，拖进窗口即可继续   ·   点此收起", Theme.Red, 0);
                Native.FlashTaskbar(Handle, 5);
            }

            if (dropped > 0)
                log.Warn(">> 本次共丢弃 " + dropped + " 行日志（输出过快，界面队列上限 20000 行），完整内容见日志文件");

            log.Dim(">> 累计 成功 " + jobOk + " / 失败 " + jobFail);
            log.FlushFile();
        }

        // =================================================================
        //  自动补片：把「错误片段按前后相关片段补充，让任务继续」
        //  两段式自救，越往前走越“凑合”，第二次仍失败就老实报错：
        //    ① 重拉清单再续跑 —— 引擎按临时目录续传，只补下缺失分片（内容是真的）
        //    ② 相邻分片顶替 + 再续跑合并 —— 片段真取不到时用邻居填坑，画面该处会重复
        //  两段都走普通续跑，不加 --skip-download：本机引擎（Beta 20260628）实测在该参数下
        //  exit=0 却零产物，会被误判成“完成”进而清掉续传与分片，比不补还糟
        // =================================================================
        private bool TryAutoPatch(int code)
        {
            if (cfg == null || !cfg.AutoPatch) return false;
            if (patchStage >= 2) return false;
            if (string.IsNullOrEmpty(lastTmp) || !Directory.Exists(lastTmp))
            {
                log.Warn(">> 自动补片：本次没有可用的分片目录，跳过");
                return false;
            }

            string reason = sawJobFailed ? "引擎报错（坏片或合并失败）" : "退出码 " + code;

            if (patchStage == 0)
            {
                patchStage = 1;
                sawJobFailed = false;
                log.Warn(">> 自动补片 1/2：" + reason + "，重新拉取清单，保留已下载分片只补缺的部分");
                return RestartRun();
            }

            patchStage = 2;
            sawJobFailed = false;
            log.Warn(">> 自动补片 2/2：重新下载后仍有缺失，改用前后相邻分片顶替");

            PatchReport pr;
            try
            {
                pr = SegmentPatcher.Patch(lastTmp, true, log);
            }
            catch (Exception ex)
            {
                log.Err(">> 补片异常：" + ex.Message);
                return false;
            }
            if (pr == null || pr.Patched == 0)
            {
                log.Warn(">> 补片结果：" + (pr == null ? "无" : pr.Summary) + "，无法继续");
                return false;
            }

            log.Ok(">> 补片结果：" + pr.Summary + "，分片已补齐，继续合并");
            return RestartRun();
        }

        /// <summary>
        /// 用同一份参数续跑：引擎按临时目录续传，已存在的分片直接跳过、只补缺的，
        /// 齐了就进入合并。刻意不加 --skip-download（原因见 TryAutoPatch 上方注释）。
        /// </summary>
        private bool RestartRun()
        {
            try
            {
                // 名字框为空时补回上次的实际文件名：否则续跑会算出一个新名字，另存成另一个文件
                if (lastName.Length > 0 && txtName.Text.Trim().Length == 0) txtName.Text = lastName;
                BeginRun(null, lastUrl, lastDir, lastExe, lastFF, lastTmp);
                return runner != null;
            }
            catch (Exception ex)
            {
                log.Err(">> 自动补片启动失败：" + ex.Message);
                return false;
            }
        }

        private string WriteResume(BuiltArgs args, string resumeTmp)
        {
            try
            {
                JVal j = JVal.NewObj();
                j.Set("app", "REM-GUI");
                j.Set("url", args.Url);
                j.Set("saveDir", args.SaveDir);
                j.Set("name", args.SaveName);
                j.Set("tmp", args.TmpDir);
                j.Set("created", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                string path = Path.Combine(args.SaveDir, args.SaveName + ".resume.json");
                File.WriteAllText(path, j.ToJson(), new UTF8Encoding(false));
                return path;
            }
            catch (Exception)
            {
                return "";
            }
        }

        private void RemoveResume()
        {
            try
            {
                if (!string.IsNullOrEmpty(currentResumeFile) && File.Exists(currentResumeFile))
                {
                    File.Delete(currentResumeFile);
                }
            }
            catch (Exception __ex) { Note.Swallow("MainForm.cs:1355", __ex); }
            currentResumeFile = "";
        }

        private void PruneParts()
        {
            try
            {
                if (currentArgs == null) return;
                string parts = Path.Combine(currentArgs.SaveDir, ".parts");
                if (!Directory.Exists(parts)) return;
                // 递归清空：原先非递归 Directory.Delete 遇到残留子目录会抛异常并被空 catch 吞掉
                string nameDir = currentArgs.TmpDir;
                if (!string.IsNullOrEmpty(nameDir) && Directory.Exists(nameDir))
                {
                    string[] inner = Directory.GetFileSystemEntries(nameDir);
                    if (inner.Length == 0) Fs.TryDeleteDir(nameDir);
                }
                string[] sub = Directory.GetDirectories(parts);
                string[] files = Directory.GetFiles(parts);
                if (sub.Length == 0 && files.Length == 0) Fs.TryDeleteDir(parts);
            }
            catch (Exception ex)
            {
                log.Diag("清理分片目录失败：" + ex.Message, "PruneParts");
            }
        }

        private void ReportResumeTasks()
        {
            try
            {
                string dir = cfg.SaveDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                string[] files = Directory.GetFiles(dir, "*.resume.json");
                if (files.Length == 0) return;
                log.Warn(">> 保存目录里有 " + files.Length + " 个未完成任务，拖进窗口即可续传");
                for (int i = 0; i < files.Length && i < 5; i++)
                {
                    log.Dim("   " + Path.GetFileName(files[i]));
                }
            }
            catch (Exception __ex) { Note.Swallow("MainForm.cs:1397", __ex); }
        }

        private void LoadResumeFile(string path)
        {
            try
            {
                JVal j = JVal.Parse(File.ReadAllText(path, Encoding.UTF8));
                string url = j.GetStr("url", "");
                string dir = j.GetStr("saveDir", "");
                string name = j.GetStr("name", "");
                string tmp = j.GetStr("tmp", "");
                if (url.Length == 0)
                {
                    SetStatus("续传文件无效", Theme.Red);
                    return;
                }
                if (state != RunState.Idle)
                {
                    SetStatus("请先停止当前任务", Theme.Orange);
                    return;
                }
                txtUrl.Text = url;
                if (dir.Length > 0) txtDir.Text = dir;
                if (name.Length > 0) txtName.Text = name;

                CollectConfig();
                string exe = ResolveEngineExe();
                if (exe.Length == 0 || !File.Exists(exe))
                {
                    log.Err("未找到引擎，无法续传");
                    return;
                }
                // 分片目录不存在或已被清空时不能假装还能续传：明确告知从头下载，
                // 并把 tmp 置空交给 ArgBuilder 重新生成（否则引擎会在空目录里“续传”出 0 分片）
                if (tmp.Length > 0 && !PartsAlive(tmp))
                {
                    log.Warn(">> 分片已丢失，将从头下载：" + tmp);
                    SetStatus("分片已丢失，将从头下载", Theme.Orange);
                    tmp = "";
                }
                if (tmp.Length == 0)
                {
                    log.Info(">> 续传：" + name + "  （无可用分片，按新任务下载）");
                }
                else
                {
                    log.Info(">> 续传：" + name + "  临时目录 " + tmp);
                    log.Dim(">> 续传将沿用原临时目录，文件名保持不变");
                }
                string ff = AppPaths.FindTool(AppPaths.FFmpegName);
                Directory.CreateDirectory(txtDir.Text.Trim());
                BeginRun(null, url, dir, exe, ff, tmp);
            }
            catch (Exception ex)
            {
                log.Err("读取续传文件失败：" + ex.Message);
            }
        }

        /// <summary>续传分片目录是否真的还有内容：不存在或空目录都当作「分片已丢失」。</summary>
        private static bool PartsAlive(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                return Directory.GetFileSystemEntries(dir).Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
