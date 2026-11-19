// =====================================================================
//  UI\MainForm.Job.Resume.cs — MainForm 的续传发现与载入分区（partial 拆分）
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
            catch (Exception __ex) { Note.Swallow(__ex); }
        }

        private void LoadResumeFile(string path)
        {
            try
            {
                // 上一次拖入的 resume 还在探活：直接忽略，避免两个探测各自 BeginRun
                if (resumeProbing)
                {
                    SetStatus("正在校验续传链接，请稍候", Theme.Orange);
                    return;
                }
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
                    SetStatus("请先暂停当前任务", Theme.Orange);
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
                // 不再直接 BeginRun：resume 里存的是上次抓到的地址，token 可能早已过期，
                // 闷头续传要白跑 10 次重试（实测 15 秒）才报错。先 HEAD 探一次再决定
                ProbeResumeUrlThenRun(url, dir, exe, ff, tmp);
            }
            catch (Exception ex)
            {
                log.Err("读取续传文件失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 续传前的链接探活：HEAD 一次原地址，403/401 就地中止续传。
        /// 必须放在后台线程——网络请求在 UI 线程会卡住窗口（最长 8 秒）。
        /// </summary>
        private void ProbeResumeUrlThenRun(string url, string dir, string exe, string ff, string tmp)
        {
            resumeProbing = true;
            SetStatus("正在校验续传链接…", Theme.Dim);
            log.Dim(">> 续传前先校验链接是否仍然有效（HEAD 一次，不下载内容）");
            AppConfig cfgSnap = cfg;
            string headers = cfgSnap == null ? "" : cfgSnap.Headers;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int st = 0;
                try { st = Http.Head(url, cfgSnap, headers, 8000); }
                catch (Exception __ex) { Note.Swallow(__ex); st = 0; }
                try
                {
                    BeginInvoke(new Action(delegate { OnResumeProbeDone(url, dir, exe, ff, tmp, st); }));
                }
                catch (Exception __ex2) { Note.Swallow(__ex2); }
            });
        }

        /// <summary>探活结果回到 UI 线程再落地：只有这里会真正发起续传或中止。</summary>
        private void OnResumeProbeDone(string url, string dir, string exe, string ff, string tmp, int status)
        {
            resumeProbing = false;
            if (IsDisposed) return;
            if (status == 401 || status == 403)
            {
                // 与预检同一口径：链接需要鉴权或已失效。签名过期的地址重试多少次都是 403，
                // 所以既不进补片、也不重试，直接把结论给用户
                log.Err(">> 续传中止：续传链接已过期（HTTP " + status + "），请重新获取链接");
                log.Dim(">> 与预检口径一致：链接需要鉴权或已失效，请刷新 Cookie/Header 后重试；旧链接签名过期后无法靠重试恢复");
                SetStatus("续传链接已过期，请重新获取链接", Theme.Red);
                return;
            }
            if (state != RunState.Idle)
            {
                SetStatus("请先暂停当前任务", Theme.Orange);
                return;
            }
            if (status > 0) log.Dim(">> 续传链接可访问（HTTP " + status + "），继续续传");
            else log.Dim(">> 续传链接未能探活（网络异常或无响应），仍按原链接尝试续传");
            try
            {
                BeginRun(null, url, dir, exe, ff, tmp);
            }
            catch (Exception ex)
            {
                log.Err("续传启动失败：" + ex.Message);
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
            catch (Exception __ex)
            {
                Note.Swallow(__ex);
                return false;
            }
        }
    }
}
