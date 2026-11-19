// =====================================================================
//  UI\MainForm.Job.Run.cs — MainForm 的任务启动 / 预检分区（partial 拆分）
//  纯代码搬移，逻辑与行为与拆分前完全一致（重构方案第 2 章）
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
        private void OnStart()
        {
            if (state != RunState.Idle)
            {
                SetStatus("任务进行中，请先暂停", Theme.Orange);
                return;
            }

            // 用户新开任务：清空上一轮自动补片状态，避免接续标记串到新任务
            patchStage = 0;
            sawJobFailed = false;
            decryptHintShown = false;
            sawAuthFailed = false;
            authHintShown = false;
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
            btnStop.Text = "暂停";
            btnStop.ForeColor = Theme.Red;
            resumeReady = false;
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
                    Note.Swallow(ex);
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
                txtCmd.Text = MaskPreview(args.CommandLine);

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
                btnStop.Text = "暂停";
                btnStop.ForeColor = Theme.Red;
                resumeReady = false;
                SetStatus("下载中 " + args.SaveName, Theme.Accent);
                log.Info(">> 开始任务 #" + jobIndex + "：" + args.SaveName);
                log.Dim(">> " + MaskPreview(args.CommandLine));

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
    }
}
