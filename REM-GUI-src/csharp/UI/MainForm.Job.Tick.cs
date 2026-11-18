// =====================================================================
//  UI\MainForm.Job.Tick.cs — MainForm 的定时轮询分区（partial 拆分）
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
    }
}
