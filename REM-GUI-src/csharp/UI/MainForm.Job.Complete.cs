// =====================================================================
//  UI\MainForm.Job.Complete.cs — MainForm 的任务收尾 / 自动补片 / 续传落盘分区（partial 拆分）
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
        /// <summary>
        /// 收尾。原来 Finish() 里的 WaitForExit(5000) 与 Dispose() 里的 Join(500)*2 全在 UI 线程
        /// 顺序执行，最坏冻结 6 秒以上；现改为每拍只推进一步、每步都不阻塞的阶段机（3.5）。
        /// </summary>
        private void CompleteJob()
        {
            JobRunner r = runner;
            if (r == null) return;

            // 阶段一 · 等进程真正退出：仍在跑就下一拍再来，不占着 UI 线程干等
            int code;
            if (!r.TryFinish(out code))
            {
                doneExitTicks++;
                if (doneExitTicks == DoneExitWaitTicks) SetStatus("引擎仍在退出，请稍候", Theme.Orange);
                return;
            }
            doneExitTicks = 0;

            // 顺序很关键：必须先等进程真正退出、管道读完，再取队列。反过来的话等待期间
            // 管道里剩下的最后几行（往往就是真实错误原因）永远取不到（D11）
            bool killed = r.WasKilled;
            List<LineItem> rest = r.Drain();
            if (rest != null) HandleLines(rest);

            // 阶段二 · 读取线程收尾：改成看 IsAlive 轮询，超 1 秒就不再等
            if (!r.OutputDrained && doneDrainTicks < DoneDrainWaitTicks)
            {
                doneDrainTicks++;
                return;
            }
            doneDrainTicks = 0;
            int dropped = r.DroppedLines;
            r.Dispose();
            // 读取线程退出后仍可能有残余行落到队列里，再补取一次
            List<LineItem> tail = r.Drain();
            if (tail != null) HandleLines(tail);
            runner = null;

            state = RunState.Idle;
            btnStart.Enabled = true;

            string name = currentName();
            TimeSpan el = DateTime.Now - jobStart;

            // 失败判定 = 退出码非 0，或退出码 0 但日志里已出现引擎的失败信号
            // （坏片是「分片数量校验不通过」，合并/混流失败是「Mux failed / ERROR: Failed」，
            //   不拦的话会被当成“完成”并删掉续传文件与分片）
            // 先落一个局部判定再交给自动补片：TryAutoPatch 内部会把 sawJobFailed 清掉（补片是"重来一次"），
            // 清掉之后若补片自己也没救回来，这里就再也看不出这轮本该判失败，于是 code==0 被误判成功（D14）
            // 产物兜底校验：引擎换措辞后文本匹配会漏，“成功”却没生成任何产物的一律按失败处理
            // （仅解析不下载除外），防止误判成功后清掉续传与分片
            bool noProduct = !killed && !cfg.SkipDownload && !HasProduct(lastDir, name);
            bool jobFailed = !killed && (code != 0 || sawJobFailed || noProduct);
            if (jobFailed)
            {
                if (TryAutoPatch(code))
                {
                    log.FlushFile();
                    return;
                }
            }

            // 暂停/失败后续传可用：按钮翻成「继续」，点它直接接着下；成功分支随后收回「暂停」
            bool canResume = !string.IsNullOrEmpty(currentResumeFile) && File.Exists(currentResumeFile);
            btnStop.Text = canResume ? "继续" : "暂停";
            btnStop.Enabled = canResume;
            btnStop.ForeColor = canResume ? Theme.Green : Theme.Red;
            resumeReady = canResume;

            if (killed)
            {
                log.Warn(">> 已暂停：" + name + "   进度已保留，点「继续」接着下");
                SetStatus("已暂停", Theme.Orange);
                SetTitleState("■ 已暂停");
                ShowBanner("■  已暂停", name + "   ·   已下 " + prog.MainText + "   ·   点「继续」接着下", Theme.Orange, 6);
                Native.FlashTaskbar(Handle, 3);
            }
            else if (!jobFailed)
            {
                jobOk++;
                log.Ok(">> 完成：" + name + "  耗时 " + FormatDuration(el));
                SetStatus("完成：" + name, Theme.Green);
                RemoveResume();
                PruneParts();
                // 成功后没有可续传的东西：按钮收回「暂停」禁用态
                resumeReady = false;
                btnStop.Text = "暂停";
                btnStop.Enabled = false;
                btnStop.ForeColor = Theme.Red;
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
                string why = code != 0 ? "退出码 " + code : (noProduct ? "未生成产物文件" : "引擎报错（见日志）");
                log.Err(">> 失败：" + name + "  " + why + "  耗时 " + FormatDuration(el));
                log.Dim(">> 续传文件已保留，点「继续」或拖进窗口即可续传");
                SetStatus("失败：" + why, Theme.Red);
                SetTitleState("✕ 下载失败");
                prog.Speed = "";
                RefreshProgressUi();
                ShowBanner("✕  下载失败（" + why + "）",
                    name + "   ·   续传文件已保留，点「继续」接着下   ·   点此收起", Theme.Red, 0);
                Native.FlashTaskbar(Handle, 5);
            }

            if (dropped > 0)
                log.Warn(">> 本次共丢弃 " + dropped + " 行日志（输出过快，界面队列上限 20000 行），完整内容见日志文件");

            log.Dim(">> 累计 成功 " + jobOk + " / 失败 " + jobFail);
            log.FlushFile();
        }

        private bool TryAutoPatch(int code)
        {
            if (cfg == null || !cfg.AutoPatch) return false;
            if (patchStage >= 2) return false;
            if (string.IsNullOrEmpty(lastTmp) || !Directory.Exists(lastTmp))
            {
                log.Warn(">> 自动补片：本次没有可用的分片目录，跳过");
                return false;
            }

            string reason = sawJobFailed ? "引擎报错（坏片或合并失败）" : (code != 0 ? "退出码 " + code : "未生成产物文件");

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
                pr = SegmentPatcher.Patch(lastTmp, true, log, cfg.PatchPercent);
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
            catch (Exception __ex)
            {
                Note.Swallow(__ex);
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
            catch (Exception __ex) { Note.Swallow(__ex); }
            currentResumeFile = "";
        }

        /// <summary>
        /// 成功后只留成品：本次任务的临时分片树（含 raw.m3u8 等辅助文件）整树删除，
        /// 混流成功后残留的同名分离轨文件一并清掉；.parts 根目录只在空了时才动，不碰其它任务的分片。
        /// </summary>
        private void PruneParts()
        {
            try
            {
                if (currentArgs == null) return;
                string nameDir = currentArgs.TmpDir;
                if (!string.IsNullOrEmpty(nameDir) && Directory.Exists(nameDir))
                {
                    try { Directory.Delete(nameDir, true); }
                    catch (Exception ex) { log.Diag("清理分片目录失败：" + ex.Message, "PruneParts"); }
                }
                string parts = Path.Combine(currentArgs.SaveDir, ".parts");
                if (Directory.Exists(parts))
                {
                    string[] left = Directory.GetFileSystemEntries(parts);
                    if (left.Length == 0) Fs.TryDeleteDir(parts);
                }
                PruneTracks(currentArgs.SaveDir, currentArgs.SaveName);
            }
            catch (Exception ex)
            {
                log.Diag("清理分片目录失败：" + ex.Message, "PruneParts");
            }
        }

        /// <summary>混流成功（成品 mp4 在）后，把同名的分离轨中间文件连带删掉；
        /// 没有 mp4 就不动——此时轨文件本身就是成品。</summary>
        private void PruneTracks(string dir, string name)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;
                string mp4 = Path.Combine(dir, name + ".mp4");
                if (!File.Exists(mp4)) return;
                string[] exts = { ".m4s", ".m4a", ".ts", ".aac", ".mp3" };
                for (int i = 0; i < exts.Length; i++)
                {
                    string p = Path.Combine(dir, name + exts[i]);
                    try { if (File.Exists(p)) File.Delete(p); }
                    catch (Exception ex) { log.Diag("清理轨文件失败 " + exts[i] + "：" + ex.Message, "PruneParts"); }
                }
            }
            catch (Exception ex) { Note.Swallow(ex); }
        }

        /// <summary>产物兜底校验：保存目录里至少有一个与任务同名、非辅助扩展名、非空的文件。</summary>
        private static bool HasProduct(string dir, string name)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return true;   // 名字算不出时不拦，交给退出码判定
                if (!Directory.Exists(dir)) return false;
                string[] files = Directory.GetFiles(dir);
                for (int i = 0; i < files.Length; i++)
                {
                    if (!string.Equals(Path.GetFileNameWithoutExtension(files[i]), name, StringComparison.OrdinalIgnoreCase)) continue;
                    string ext = Path.GetExtension(files[i]).ToLowerInvariant();
                    if (ext == ".json" || ext == ".m3u8" || ext == ".mpd" || ext == ".log" || ext == ".txt" || ext == ".tmp") continue;
                    if (new FileInfo(files[i]).Length > 0) return true;
                }
                return false;
            }
            catch (Exception __ex) { Note.Swallow(__ex); return true; }   // 查不动就不拦，避免误伤
        }
    }
}
