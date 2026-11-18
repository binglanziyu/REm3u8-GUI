// =====================================================================
//  UI\MainForm.cs — 主窗体：布局、状态机、日志渲染、后台任务轮询
//  单定时器 100ms 驱动一切；后台线程只写标志与队列，UI 线程只读
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
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
        private enum RunState { Idle, Precheck, Running }

        // ---------- 服务 ----------
        private readonly Logger log = new Logger();
        private AppConfig cfg = new AppConfig();
        private UpdateManifest manifest;
        /// <summary>主程序自身的发布页地址（检查到新版本时才有值，空串表示无可点地址）。</summary>
        private string appReleaseUrl = "";
        /// <summary>主程序 Release 建议下载的文件名，用于状态栏/日志提示。</summary>
        private string appAssetName = "";
        /// <summary>状态栏右键菜单：打开/复制发布页。</summary>
        private ContextMenuStrip appMenu;

        // ---------- 控件 ----------
        private TextBox txtUrl;
        private TextBox txtName;
        private TextBox txtDir;
        private TextBox txtCmd;
        private Button btnEngine;
        private Button btnBrowse;
        private Button btnOpen;
        private Button btnAdv;
        private Button btnStart;
        private Button btnStop;
        private Button btnClear;
        private Label lblProg;
        private Label lblSpd;
        private Label lblStatus;
        private Label lblUpd;
        private LinkLabel lnkUpdate;
        private LinkLabel lnkClean;
        // 「诊断日志」入口（重构方案第 4 章 E3）：查看本次会话的内部异常记录
        private LinkLabel lnkDiag;
        private RichTextBox txtLog;
        private ThinScroll scLog;
        private ThinScroll scCmd;
        private Panel bar;
        // 进度条已成功收口：满格改绿，和"还没下完的绿"区分开
        private bool barDone;
        // 完成/失败横幅：盖在日志顶部的一条整宽色带，几秒后自动收，点击可立即收
        private Panel banner;
        private Label lbBannerTitle;
        private Label lbBannerDetail;
        private Color bannerTone = Theme.Green;
        private DateTime bannerUntil = DateTime.MinValue;
        private Panel cardOpt;
        private TextBox numThreads;
        private TextBox numRetries;
        private TextBox numTimeout;
        private TextBox txtSpeed;
        // 上一次合法的限速文本：输入非法（超位数/多个小数点）时原地回滚，不弹提示
        private string speedGood = "0";
        private System.Windows.Forms.Timer tick;

        // ---------- 运行期状态 ----------
        private int dpi = 96;
        private RunState state = RunState.Idle;
        private JobRunner runner;
        // 暂停/失败后有可用续传：底部按钮翻成「继续」，点它接着下（无续传时保持「暂停」禁用）
        private bool resumeReady = false;
        private readonly Progress prog = new Progress();
        private BuiltArgs currentArgs;
        private string currentResumeFile = "";
        private DateTime jobStart = DateTime.MinValue;
        private int ticks = 0;
        private int logLines = 0;
        private int jobIndex = 0;
        private int jobOk = 0;
        private int jobFail = 0;
        // 预检取消与世代号：取消后旧线程返回的结果必须被丢弃，否则会被当成新一次预检的结果消费（D4）
        private volatile bool preCanceled = false;
        private volatile int preSeq = 0;
        // 运行期收到外部链接时，记录已提示过的中转文件时间戳，避免重复刷状态（D12）
        private string dropNotifiedFor = "";
        private List<EngineEntry> engines = new List<EngineEntry>();
        // 当前引擎在 engines 里的下标（0 = 内置），替代原来的下拉框 SelectedIndex
        private int engineIndex = 0;

        // 后台任务标志
        private volatile bool preBusy = false;
        private volatile bool preDone = false;
        private PrecheckResult preResult = null;
        private string preUrl = "";
        private string preDir = "";
        private string preExe = "";
        private string preFF = "";

        private volatile bool updBusy = false;
        private volatile bool updDone = false;
        private UpdateResult updResult = null;
        private UpdateResult pendingUpdate = null;

        private volatile bool engineProbeBusy = false;
        private volatile bool engineProbeDone = false;
        private volatile bool toolsReady = false;
        private ToolTip tip;

        private volatile bool installBusy = false;
        private volatile bool installDone = false;
        private volatile int installPct = -1;
        private bool installOk = false;
        private string installErr = "";
        private string installTag = "";
        private bool deferredUpdate = false;

        public MainForm()
        {
            BuildUi();
        }

        // ========================= 工具方法 =========================
        /// <summary>
        /// 设计值 → 像素。唯一缩放入口是 Theme.S：主窗体是口径基准（PickDpi 把实测 dpi 写进
        /// Theme.Dpi），这里只做转发，杜绝「窗体一套 dpi、Theme 另一套」两套换算漂移（D7）。
        /// </summary>
        private int S(int v)
        {
            return Theme.S(v);
        }

        private void PickDpi()
        {
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    dpi = (int)Math.Round(g.DpiX);
                }
            }
            catch (Exception __ex) { Note.Swallow(__ex); dpi = 96; }
            if (dpi < 96) dpi = 96;
            Theme.Dpi = dpi;   // Theme 内部控件（圆角框 / 图标）按同一 DPI 缩放
        }

        // ========================= 布局 =========================
        /// <summary>窗体骨架与装配顺序：自身属性 → 四个分区 → 事件钩子。分区各自只往窗口上摆自己的控件，坐标都是绝对值，互不依赖。</summary>
        private void BuildUi()
        {
            SuspendLayout();
            PickDpi();

            Text = "REM-GUI " + Program.Version;
            Icon = Theme.AppIcon();
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9F);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            AllowDrop = true;
            ClientSize = new Size(S(504), S(732));

            BuildHeader();
            BuildEngineCard();
            BuildLogCard();
            BuildFooter();

            ResumeLayout(false);

            HookUnfocusClick(this);
            ApplyToolTips();

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 100;
            tick.Tick += OnTick;

            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            Shown += OnShown;
            FormClosing += OnFormClosing;
        }


        // ========================= 启动 =========================
        private void OnShown(object sender, EventArgs e)
        {
            try
            {
                Native.SetDarkTitleBar(Handle, true);
                if (scLog != null) { scLog.Attach(); }
                if (scCmd != null) { scCmd.Attach(); }
            }
            catch (Exception __ex) { Note.Swallow(__ex); }

            manifest = UpdateManifest.Load(AppPaths.AppDir, log);
            cfg = ConfigStore.Load(AppPaths.ConfigPath, log);
            ApplyConfig();

            log.OpenFile(Path.Combine(AppPaths.LogDir, "gui_" + DateTime.Now.ToString("yyyyMMdd") + ".log"));
            log.Info("REM-GUI " + Program.Version + "  (C# WinForms / .NET Framework 4.8)");
            log.Dim("程序目录 " + AppPaths.AppDir);
            log.Dim("数据目录 " + AppPaths.DataDir);

            RefreshEngineList();
            StartEngineProbe();
            ReportResumeTasks();
            DrainStartupDrop();

            txtUrl.Focus();
            tick.Start();
            deferredUpdate = true;
            SetStatus("就绪");

            // 解密失败的加密字段在 Load 时只能被清空，但不能静默（D15）：明确列出字段名并提示重填
            if (ConfigStore.DecryptFailedFields.Count > 0)
            {
                string fields = string.Join("、", ConfigStore.DecryptFailedFields.ToArray());
                log.Warn(">> 部分加密配置无法解密，已清空：" + fields);
                SetStatus("部分加密配置无法解密（" + fields + "），已清空，请重新填写", Theme.Orange);
            }

            // 冷启动耗时落盘（窗体可见时刻 - 进程入口时刻）
            try
            {
                log.Diag("冷启动 " + (Environment.TickCount - Program.StartTick) + " ms", "Startup");
            }
            catch (Exception __ex) { Note.Swallow(__ex); }

            // 工具自愈放后台：只有真缺失时才解压，绝不阻塞窗体显示
            ThreadPool.QueueUserWorkItem(delegate (object st)
            {
                try { AppPaths.EnsureTools(false, log); }
                catch (Exception ex) { log.Diag("工具自愈失败：" + ex.Message, "EnsureTools"); }
                // 自愈可能刚刚释放出引擎，通知 UI 线程重新枚举，避免停在“未找到引擎”
                toolsReady = true;
            });
        }

        private void ApplyConfig()
        {
            if (string.IsNullOrEmpty(cfg.SaveDir)) cfg.SaveDir = DefaultSaveDir();
            txtDir.Text = cfg.SaveDir;
            ApplyEngineParams();
        }

        /// <summary>把配置里的引擎参数灌进主界面控件（限速拆成数值 + 单位）。</summary>
        private void ApplyEngineParams()
        {
            numThreads.Text = cfg.Threads.ToString();
            numRetries.Text = cfg.Retries.ToString();
            numTimeout.Text = cfg.Timeout.ToString();
            speedGood = FormatSpeed(cfg.SpeedMB);
            txtSpeed.Text = speedGood;
        }


        private void CollectConfig()
        {
            cfg.SaveDir = txtDir.Text.Trim();
            cfg.Threads = NumOf(numThreads, 16);
            cfg.Retries = NumOf(numRetries, 3);
            cfg.Timeout = NumOf(numTimeout, 15);
            cfg.SpeedMB = ClampSpeed(ParseSpeed(txtSpeed.Text));
            if (engineIndex > 0 && engineIndex < engines.Count)
            {
                EngineEntry en = engines[engineIndex];
                cfg.EngineSel = string.IsNullOrEmpty(en.Dir) ? "" : Path.GetFileName(en.Dir);
            }
            else
            {
                cfg.EngineSel = "";
            }
            cfg.Clamp();
        }

        /// <summary>
        /// 默认保存目录 = 软件所在目录（REM-GUI.exe 同目录），下载产物跟着程序走，
        /// 不再默认落到「我的视频」或 %LOCALAPPDATA%。程序目录取不到时才逐级退回。
        /// </summary>
        private string DefaultSaveDir()
        {
            try
            {
                if (!string.IsNullOrEmpty(AppPaths.AppDir) && Directory.Exists(AppPaths.AppDir))
                    return AppPaths.AppDir;
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
            try
            {
                string v = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(v)) return AppPaths.DataDir;
                return v;
            }
            catch (Exception __ex)
            {
                Note.Swallow(__ex);
                return AppPaths.DataDir;
            }
        }

        // ========================= 引擎 =========================
        // ========================= 交互 =========================
        private void OnUrlKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                OnStart();
            }
        }

        // ========================= 参数框提交闭环 =========================


        private void BrowseDir()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择保存目录";
                dlg.SelectedPath = txtDir.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    txtDir.Text = dlg.SelectedPath;
                }
            }
        }

        private void OpenDir()
        {
            try
            {
                string dir = txtDir.Text.Trim();
                if (dir.Length == 0) dir = DefaultSaveDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // using 只为及时释放进程对象句柄：explorer 是独立进程，Dispose 不会影响它的运行
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start("explorer.exe", dir)) { }
            }
            catch (Exception ex)
            {
                log.Err("打开目录失败：" + ex.Message);
            }
        }

        private void OnAdv()
        {
            CollectConfig();
            using (AdvForm f = new AdvForm(cfg))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    cfg.Smart = f.Smart;
                    cfg.Mux = f.Mux;
                    cfg.Concurrent = f.Concurrent;
                    cfg.Binary = f.Binary;
                    cfg.SkipMerge = f.SkipMerge;
                    cfg.SkipDownload = f.SkipDownload;
                    cfg.AppendUrlParams = f.AppendUrlParams;
                    cfg.DelAfterDone = f.DelAfterDone;
                    cfg.CheckSegments = f.CheckSegments;
                    cfg.AutoSelect = f.AutoSelect;
                    cfg.UseSystemProxy = f.UseSystemProxy;
                    cfg.Proxy = f.Proxy;
                    cfg.Headers = f.Headers;
                    cfg.ExtraArgs = f.ExtraArgs;
                    // 解密方式固定 AES-128：只需回写 KEY / IV（AdvForm 里已把 HlsMethod 定格为常量）
                    cfg.HlsMethod = f.HlsMethod;
                    cfg.HlsKey = f.HlsKey;
                    cfg.HlsIv = f.HlsIv;
                    cfg.LogLimit = f.LogLimit;
                    cfg.PatchPercent = f.PatchPercent;
                    cfg.Clamp();
                    SaveConfig();
                    SetStatus("高级设置已保存");
                }
            }
        }

        // ========================= 开始 / 停止 =========================
        // ========================= 定时器 =========================
        // ========================= 续传 =========================

        // ========================= 拖放 =========================


        // ========================= 更新 =========================
        // ========================= 收尾 =========================
        private void SaveConfig()
        {
            try
            {
                // 传 log：DPAPI 加密失败、配置写盘失败都会被看见，不再静默丢配置
                if (!ConfigStore.Save(AppPaths.ConfigPath, cfg, log))
                {
                    log.Diag("配置未能原子写入：" + AppPaths.ConfigPath, "Config");
                }
            }
            catch (Exception ex)
            {
                log.Diag("保存配置异常：" + ex.Message, "Config");
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (runner != null)
            {
                DialogResult r = MessageBox.Show(this,
                    "任务仍在进行，关闭窗口会终止下载。\r\n\r\n确定关闭吗？",
                    "确认关闭", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
                try { runner.Stop(); } catch (Exception __ex) { Note.Swallow(__ex); }
            }
            else if (state == RunState.Precheck || installBusy)
            {
                // 预检 / 引擎安装同样是会丢结果的操作，关窗前也要问一声（下载中已在上面拦过）
                DialogResult r = MessageBox.Show(this,
                    "预检或引擎安装仍在进行，关闭窗口会中断当前操作。\r\n\r\n确定关闭吗？",
                    "确认关闭", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
            }
            try { tick.Stop(); } catch (Exception __ex) { Note.Swallow(__ex); }
            CollectConfig();
            SaveConfig();
            // 退出前把"本次会话吞了多少异常"留在会话日志里（重构方案第 4 章 E3）：
            // 只增一行记录，不弹窗、不改关闭流程的判定分支
            try
            {
                int diagN = Logger.DiagCount;
                if (diagN > 0) log.Warn(">> 本次会话记录了 " + diagN + " 条内部异常，详见：" + AppPaths.DiagFile);
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
            log.FlushFile();
            log.CloseFile();
        }
    }
}
