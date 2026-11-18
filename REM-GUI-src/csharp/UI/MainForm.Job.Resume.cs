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
            catch (Exception __ex)
            {
                Note.Swallow(__ex);
                return false;
            }
        }
    }
}
