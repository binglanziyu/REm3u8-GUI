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
        // 解密失败提示只打一次：引擎会对每个残片抛一条 CryptographicException，逐条提示会刷屏
        private bool decryptHintShown;
        // 鉴权/网络失效（token 过期、401/403）：与残片分开记，补片阶段据此直接跳过，
        // 否则会拿一条早已过期的链接再跑一轮重试，白等十几秒还报同样的错
        private bool sawAuthFailed;
        // 鉴权失效提示也只打一次：重试耗尽的 10 行日志全是同一句 403
        private bool authHintShown;
        // 续传前的链接探活正在进行：防止用户连拖两个 resume 文件撞出两次探测
        private bool resumeProbing;
        private string lastUrl = "";
        private string lastDir = "";
        private string lastExe = "";
        private string lastFF = "";
        private string lastName = "";
        private string lastTmp = "";
        private string lastReLog = "";


        // 解密方式固定 AES-128：只剩「填了 KEY / IV」这一种情况需要提示，
        // 解密引擎、解密程序路径与密钥文件已移除，不再检测 mp4decrypt / shaka-packager 是否存在。


        private void OnStop()
        {
            // 空闲且有续传：这里当「继续」用，直接续跑上次任务，不靠拖 resume 文件
            if (state == RunState.Idle && resumeReady)
            {
                ResumeJob();
                return;
            }
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
            log.Warn(">> 正在暂停…");
            SetStatus("暂停中…", Theme.Orange);
            runner.Stop();
        }

        /// <summary>「继续」：用保留的续传文件接着下；文件不在了就按上次参数重新起任务。</summary>
        private void ResumeJob()
        {
            resumeReady = false;
            if (!string.IsNullOrEmpty(currentResumeFile) && File.Exists(currentResumeFile))
            {
                LoadResumeFile(currentResumeFile);
                return;
            }
            if (lastUrl.Length > 0 && lastExe.Length > 0 && File.Exists(lastExe))
            {
                BeginRun(null, lastUrl, lastDir, lastExe, lastFF, lastTmp);
                return;
            }
            SetStatus("没有可继续的任务", Theme.Orange);
        }


        // 收尾阶段机的拍数上限（OnTick 一拍 100ms）：等进程退出最多 3 秒、等读取线程最多 1 秒。
        // 到点就如实往下走并提示，绝不在 UI 线程上 wait/join 到底（3.5）
        private const int DoneExitWaitTicks = 30;
        private const int DoneDrainWaitTicks = 10;
        private int doneExitTicks = 0;
        private int doneDrainTicks = 0;


        // =================================================================
        //  自动补片：把「错误片段按前后相关片段补充，让任务继续」
        //  两段式自救，越往前走越“凑合”，第二次仍失败就老实报错：
        //    ① 重拉清单再续跑 —— 引擎按临时目录续传，只补下缺失分片（内容是真的）
        //    ② 相邻分片顶替 + 再续跑合并 —— 片段真取不到时用邻居填坑，画面该处会重复
        //  两段都走普通续跑，不加 --skip-download：本机引擎（Beta 20260628）实测在该参数下
        //  exit=0 却零产物，会被误判成“完成”进而清掉续传与分片，比不补还糟
        // =================================================================


    }
}
