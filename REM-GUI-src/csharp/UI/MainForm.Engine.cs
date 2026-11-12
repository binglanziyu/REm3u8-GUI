// =====================================================================
//  UI\MainForm.Engine.cs — MainForm 的引擎、更新与安装分区（partial 拆分）
//  纯代码搬移，逻辑与行为与原 MainForm.cs 完全一致
// =====================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private void RefreshEngineList()
        {
            engines = EngineManager.List();
            int sel = 0;
            for (int i = 0; i < engines.Count; i++)
            {
                if (i > 0 && !string.IsNullOrEmpty(cfg.EngineSel))
                {
                    string dir = engines[i].Dir;
                    if (!string.IsNullOrEmpty(dir) &&
                        string.Equals(Path.GetFileName(dir), cfg.EngineSel, StringComparison.OrdinalIgnoreCase)) sel = i;
                }
            }
            engineIndex = (sel >= 0 && sel < engines.Count) ? sel : 0;
            UpdateEngineButton();
        }

        /// <summary>引擎按钮文字 = 当前引擎名 + 版本；没有引擎时显示"未选择"。</summary>
        private void UpdateEngineButton()
        {
            if (btnEngine == null) return;
            EngineEntry en = CurrentEngine();
            if (en == null)
            {
                btnEngine.Text = "未选择  ▾";
                btnEngine.ForeColor = Theme.Orange;
                return;
            }
            string v = en.Version;
            btnEngine.Text = (string.IsNullOrEmpty(v) ? en.Label : en.Label + "  " + v) + "  ▾";
            btnEngine.ForeColor = Theme.Text;
        }

        /// <summary>切换引擎版本（下拉换成了按钮+菜单，逻辑等价于原 SelectedIndexChanged）。</summary>
        private void SelectEngine(int idx)
        {
            if (idx < 0 || idx >= engines.Count) idx = 0;
            engineIndex = idx;
            if (engines.Count > 0)
            {
                EngineEntry en = engines[idx];
                cfg.EngineSel = string.IsNullOrEmpty(en.Dir) ? "" : Path.GetFileName(en.Dir);
            }
            UpdateEngineButton();
            EngineEntry cur = CurrentEngine();
            if (cur != null && !string.IsNullOrEmpty(cur.Version)) lblUpd.Text = "引擎 " + cur.Version;
            CollectConfig();
        }

        /// <summary>右键菜单配色：跟随深绿主题，避免系统默认浅灰菜单跳色。</summary>
        private sealed class GreenMenuColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Theme.BtnHover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.BtnHover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.BtnHover; } }
            public override Color MenuItemBorder { get { return Theme.Border; } }
            public override Color MenuBorder { get { return Theme.BorderStrong; } }
            public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Border; } }
        }

        private void StartEngineProbe()
        {
            if (engineProbeBusy) return;
            engineProbeBusy = true;
            engineProbeDone = false;
            List<EngineEntry> snapshot = new List<EngineEntry>(engines);
            ThreadPool.QueueUserWorkItem(delegate (object st)
            {
                try
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        if (string.IsNullOrEmpty(snapshot[i].Version) || snapshot[i].Version.StartsWith("v", StringComparison.Ordinal) == false)
                        {
                            string v = EngineManager.Probe(snapshot[i].ExePath);
                            if (v.Length > 0) snapshot[i].Version = v;
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Diag("引擎版本探测失败：" + ex.Message, "Probe");
                }
                engineProbeDone = true;
                engineProbeBusy = false;
            });
        }

        private void ApplyEngineVersions()
        {
            int sel = engineIndex;
            if (sel < 0 || sel >= engines.Count) sel = 0;
            engineIndex = sel;
            UpdateEngineButton();

            if (engines.Count > 0)
            {
                string v = engines[sel].Version;
                if (!string.IsNullOrEmpty(v)) lblUpd.Text = "引擎 " + v;
            }
        }

        private EngineEntry CurrentEngine()
        {
            int i = engineIndex;
            if (i >= 0 && i < engines.Count) return engines[i];
            return null;
        }

        private string ResolveEngineExe()
        {
            if (!string.IsNullOrEmpty(cfg.EngineExt) && File.Exists(cfg.EngineExt)) return cfg.EngineExt;
            EngineEntry en = CurrentEngine();
            if (en != null && !string.IsNullOrEmpty(en.ExePath) && File.Exists(en.ExePath)) return en.ExePath;
            return AppPaths.FindTool(AppPaths.ReName);
        }

        private void ShowEngineMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.BackColor = Theme.Card;
            menu.ForeColor = Theme.Text;
            menu.ShowImageMargin = false;
            menu.RenderMode = ToolStripRenderMode.Professional;
            menu.Renderer = new ToolStripProfessionalRenderer(new GreenMenuColors());

            // 版本列表直接进菜单，替掉原来那个常驻下拉框
            for (int i = 0; i < engines.Count; i++)
            {
                ToolStripMenuItem ver = new ToolStripMenuItem(engines[i].ToString());
                ver.Tag = i;
                ver.Checked = (i == engineIndex);
                ver.Click += delegate (object s, EventArgs e)
                {
                    ToolStripMenuItem self = (ToolStripMenuItem)s;
                    SelectEngine((int)self.Tag);
                };
                menu.Items.Add(ver);
            }
            if (engines.Count > 0) menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem pick = new ToolStripMenuItem("选择外部引擎 exe…");
            pick.Click += delegate (object s, EventArgs e) { PickExternalEngine(); };
            menu.Items.Add(pick);

            ToolStripMenuItem open = new ToolStripMenuItem("打开引擎目录");
            open.Click += delegate (object s, EventArgs e)
            {
                try { using (Process p = Process.Start("explorer.exe", AppPaths.EngineDir)) { } }
                catch (Exception __ex) { Note.Swallow("MainForm.cs:664", __ex); }
            };
            menu.Items.Add(open);

            ToolStripMenuItem refresh = new ToolStripMenuItem("刷新列表");
            refresh.Click += delegate (object s, EventArgs e) { RefreshEngineList(); StartEngineProbe(); };
            menu.Items.Add(refresh);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem delver = new ToolStripMenuItem("删除当前版本…");
            delver.Click += delegate (object s, EventArgs e) { DeleteCurrentEngine(); };
            menu.Items.Add(delver);

            menu.Show(btnEngine, new Point(0, btnEngine.Height));
        }

        /// <summary>删除当前选中的引擎版本目录（旧版右键 $miDelVer 的回退项），仅限数据目录内。</summary>
        private void DeleteCurrentEngine()
        {
            EngineEntry en = CurrentEngine();
            if (en == null || string.IsNullOrEmpty(en.Dir))
            {
                SetStatus("当前是内置/外部引擎，无法删除", Theme.Orange);
                return;
            }
            if (engines.Count <= 1)
            {
                SetStatus("只剩这一个引擎版本，不能删除", Theme.Orange);
                return;
            }
            if (state != RunState.Idle)
            {
                SetStatus("请先停止当前任务", Theme.Orange);
                return;
            }
            DialogResult r = MessageBox.Show(this,
                "将删除引擎版本目录（含其中文件）：\r\n\r\n" + en.Dir + "\r\n\r\n删除后需要重新下载该版本才能使用。",
                "删除引擎版本", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;
            if (EngineManager.DeleteVersion(en.Dir))
            {
                log.Ok(">> 已删除引擎版本：" + en.ToString());
                SetStatus("已删除引擎版本", Theme.Green);
                RefreshEngineList();
                StartEngineProbe();
            }
            else
            {
                log.Err(">> 删除失败（目录被占用或不在数据目录内）：" + en.Dir);
                SetStatus("删除引擎版本失败", Theme.Red);
            }
        }

        private void PickExternalEngine()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择 N_m3u8DL-RE.exe";
                dlg.Filter = "N_m3u8DL-RE.exe|*.exe";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    cfg.EngineExt = dlg.FileName;
                    string v = EngineManager.Probe(dlg.FileName);
                    lblUpd.Text = "外部引擎 " + (v.Length > 0 ? v : "?");
                    UpdateEngineButton();
                    log.Info(">> 使用外部引擎：" + dlg.FileName);
                }
            }
        }

        private void OnUpdateLink()
        {
            if (installBusy)
            {
                SetStatus("正在下载引擎…", Theme.Blue);
                return;
            }
            if (pendingUpdate != null && pendingUpdate.Newer && pendingUpdate.AssetUrl.Length > 0)
            {
                StartInstall();
                return;
            }
            StartUpdateCheck(false);
        }

        private void StartUpdateCheck(bool quiet)
        {
            if (updBusy) return;
            updBusy = true;
            updDone = false;
            updResult = null;
            lblUpd.Text = "检查中…";
            lblUpd.ForeColor = Theme.Blue;
            if (!quiet) SetStatus("正在检查更新…", Theme.Blue);

            UpdateManifest m = manifest;
            AppConfig snapshot = cfg;
            string installed = InstalledEngineVersion();

            ThreadPool.QueueUserWorkItem(delegate (object st)
            {
                UpdateResult r;
                try
                {
                    r = UpdateChecker.Check(m, installed, Program.Version, snapshot, log);
                }
                catch (Exception ex)
                {
                    r = new UpdateResult();
                    r.Message = "检查失败：" + ex.Message;
                }
                updResult = r;
                updDone = true;
                updBusy = false;
            });
        }

        private string InstalledEngineVersion()
        {
            EngineEntry en = CurrentEngine();
            if (en != null && !string.IsNullOrEmpty(en.Version)) return en.Version;
            string exe = ResolveEngineExe();
            if (exe.Length == 0) return "0.0.0";
            string v = EngineManager.Probe(exe);
            return v.Length > 0 ? v : "0.0.0";
        }

        private void ConsumeUpdate()
        {
            UpdateResult r = updResult;
            updResult = null;
            if (r == null) return;

            if (!r.Ok)
            {
                lblUpd.Text = r.Message;
                lblUpd.ForeColor = Theme.Red;
                log.Warn(">> " + r.Message);
                // 引擎查不到不掩盖程序自身的新版提示
                ShowAppUpdate(r);
                return;
            }

            lblUpd.Text = r.Message + (r.ApiMs >= 0 ? "  (" + r.ApiMs + " ms)" : "");
            // 程序自身有新版：走状态栏常驻提示，避免把 lblUpd 挤爆（这里只放引擎信息）
            ShowAppUpdate(r);
            if (r.Newer)
            {
                lblUpd.ForeColor = Theme.AccentLight;
                lnkUpdate.Text = "安装新版本";
                pendingUpdate = r;
                log.Warn(">> 引擎有新版本：" + Ver.Display(r.LatestTag)
                         + (r.AssetName.Length > 0 ? "   " + r.AssetName : "")
                         + (r.AssetSize > 0 ? "  " + Fmt.Bytes(r.AssetSize) : ""));
                log.Dim(">> 点“安装新版本”自动下载到数据目录，不污染程序目录");
            }
            else
            {
                lblUpd.ForeColor = Theme.Green;
                pendingUpdate = null;
                lnkUpdate.Text = "检查更新";
            }
        }

        /// <summary>
        /// 把主程序自身的新版本结论反映到状态栏：有新版本时常驻提示 + 状态栏可点击打开发布页、
        /// 右键可复制地址；没有新版本时恢复普通状态栏，不留下失效的可点击区域。
        /// 这里只做提示与跳转，不下载、不替换正在运行的 exe。
        /// </summary>
        private void ShowAppUpdate(UpdateResult r)
        {
            bool has = r != null && r.AppNewer && r.AppPageUrl != null && r.AppPageUrl.Length > 0;
            appReleaseUrl = has ? r.AppPageUrl : "";
            appAssetName = r == null || r.AppAssetName == null ? "" : r.AppAssetName;
            lblStatus.Cursor = has ? Cursors.Hand : Cursors.Default;
            lblStatus.ContextMenuStrip = has ? appMenu : null;
            if (!has) return;

            string file = appAssetName.Length > 0 ? appAssetName
                         : (r.AppAssetHint == null ? "" : r.AppAssetHint);
            SetStatus(r.AppMessage + (file.Length > 0 ? "，下载 " + file : "") + "（点此打开发布页）",
                      Theme.AccentLight);
            if (appAssetName.Length > 0)
            {
                log.Dim(">> 主程序下载 " + appAssetName + "：" + r.AppPageUrl);
            }
            else
            {
                log.Dim(">> 主程序发布页：" + r.AppPageUrl);
            }
        }

        /// <summary>打开发布页；失败时退化为复制地址，保证用户总有办法拿到链接。</summary>
        private void OnOpenAppRelease()
        {
            string url = appReleaseUrl;
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                using (Process p = Process.Start(url)) { }
                SetStatus("已在浏览器打开发布页，请下载最新 REM-GUI.exe", Theme.AccentLight);
                log.Info(">> 已打开发布页：" + url);
            }
            catch (Exception ex)
            {
                log.Warn(">> 打开发布页失败：" + ex.Message + "，已改为复制地址");
                OnCopyAppRelease();
            }
        }

        /// <summary>复制发布页地址到剪贴板。</summary>
        private void OnCopyAppRelease()
        {
            string url = appReleaseUrl;
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                Clipboard.SetText(url);
                SetStatus("发布页地址已复制到剪贴板", Theme.AccentLight);
                log.Info(">> 已复制发布页地址：" + url);
            }
            catch (Exception ex)
            {
                log.Warn(">> 复制发布页地址失败：" + ex.Message + "，地址：" + url);
            }
        }

        private void StartInstall()
        {
            if (installBusy || pendingUpdate == null) return;
            installBusy = true;
            installDone = false;
            installOk = false;
            installErr = "";
            installPct = -1;
            installTag = pendingUpdate.LatestTag;
            string assetUrl = pendingUpdate.AssetUrl;
            string tag = pendingUpdate.LatestTag;
            string digest = pendingUpdate.AssetDigest;
            AppConfig snapshot = cfg;

            SetStatus("正在下载引擎…", Theme.Blue);
            lblUpd.Text = "下载中 0%";
            lblUpd.ForeColor = Theme.Blue;

            ThreadPool.QueueUserWorkItem(delegate (object st)
            {
                string exe;
                string err;
                bool ok = false;
                try
                {
                    ok = EngineManager.Install(assetUrl, tag, digest, snapshot, log,
                        delegate (int pct) { installPct = pct; },
                        out exe, out err);
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }
                installOk = ok;
                installErr = err;
                installDone = true;
                installBusy = false;
            });
        }

        private void ConsumeInstall()
        {
            if (installBusy) return;
            if (installPct >= 0 && installPct < 100)
            {
                installPct = -1;
            }
            if (installOk)
            {
                lblUpd.Text = "已安装 " + Ver.Display(installTag);
                lblUpd.ForeColor = Theme.Green;
                lnkUpdate.Text = "检查更新";
                pendingUpdate = null;
                SetStatus("引擎更新完成", Theme.Green);
                RefreshEngineList();
                StartEngineProbe();
            }
            else
            {
                lblUpd.Text = "安装失败";
                lblUpd.ForeColor = Theme.Red;
                SetStatus("引擎安装失败", Theme.Red);
                if (!string.IsNullOrEmpty(installErr)) log.Err(">> 安装失败：" + installErr);
            }
        }
    }
}
