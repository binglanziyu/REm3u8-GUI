// =====================================================================
//  Program.cs — 进程入口：单实例、DPI 感知、路径初始化、异常兜底
//  启动序列严格按「<5ms 抢锁 → 只判存在 → 立刻显示窗体 → 后台补活」编排，
//  任何可能阻塞超过 10ms 的动作都不允许出现在 Application.Run 之前。
// =====================================================================
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Rem.Services;
using Rem.UI;

using Rem.Core;
public static class Program
{
    /// <summary>应用版本：配置文件、窗口标题、日志首行统一取这里。</summary>
    public const string Version = "3.0.1";

    /// <summary>单实例互斥体名（Local\ 作用域，多用户会话互不干扰）。</summary>
    private const string MutexName = "Local\\REM-GUI-Single-7F1C4A2E";

    /// <summary>进程入口时刻（毫秒），用于冷启动耗时自测。</summary>
    public static readonly int StartTick = Environment.TickCount;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [STAThread]
    private static void Main(string[] args)
    {
        // ---------- 1. 单实例：抢锁失败就把参数转交给已运行实例后退出 ----------
        Mutex mutex = null;
        bool isFirst = true;
        try
        {
            mutex = new Mutex(true, MutexName, out isFirst);
        }
        catch (Exception)
        {
            isFirst = true;
        }

        if (!isFirst)
        {
            Handoff(args);
            return;
        }

        // ---------- 2. 网络栈：显式开 TLS 1.2/1.3，默认 SystemDefault 在本机会协商失败 ----------
        EnableModernTls();

        // ---------- 3. DPI：PerMonitorV2 优先，失败退到系统 DPI 感知 ----------
        EnableDpiAwareness();

        // ---------- 3. 路径：只做字符串拼接与目录创建，不碰网络与磁盘扫描 ----------
        try
        {
            AppPaths.Init();
        }
        catch (Exception)
        {
            // 路径层失败也要让窗体起来，由界面提示
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ---------- 4. 异常兜底：一律落盘诊断日志，绝不弹系统崩溃框 ----------
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Core 层的静默兜底统一汇入诊断日志，避免“吞掉异常且无痕迹”
        Note.Sink = delegate (string tag, Exception ex)
        {
            Diag("[swallow] " + tag + " :: " + ex.GetType().Name + ": " + ex.Message);
        };

        // ---------- 5. 外部链接先写中转文件，窗体显示后由界面取走 ----------
        if (args != null && args.Length > 0)
        {
            WriteDropFile(args);
        }

        try
        {
            using (MainForm form = new MainForm())
            {
                Application.Run(form);
            }
        }
        catch (Exception ex)
        {
            Diag("主循环异常：" + ex);
        }
        finally
        {
            try
            {
                if (mutex != null)
                {
                    try { mutex.ReleaseMutex(); } catch (Exception __ex) { Note.Swallow("Program.cs:100", __ex); }
                    mutex.Dispose();
                }
            }
            catch (Exception __ex) { Note.Swallow("Program.cs:104", __ex); }
        }
    }

    // ========================= 单实例转交 =========================
    private static void Handoff(string[] args)
    {
        try
        {
            AppPaths.Init();
            if (args != null && args.Length > 0)
            {
                WriteDropFile(args);
                return;
            }
            // 无参数启动的第二个实例：仅提示，不抢焦点
            MessageBox.Show("REM-GUI 已经在运行。\r\n\r\n把链接或续传文件拖到已开窗口即可。",
                "REM-GUI", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Diag("转交失败：" + ex.Message);
        }
    }

    private static void WriteDropFile(string[] args)
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.IsNullOrEmpty(a)) continue;
                if (a.StartsWith("-", StringComparison.Ordinal)) continue;
                sb.AppendLine(a);
            }
            string text = sb.ToString().Trim();
            if (text.Length == 0) return;
            string dir = Path.GetDirectoryName(AppPaths.DropFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(AppPaths.DropFile, text, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Diag("写中转文件失败：" + ex.Message);
        }
    }

    // ========================= DPI =========================
    /// <summary>
    /// 显式启用 TLS 1.2 / 1.3。
    /// 不设时 .NET Framework 走 SystemDefault，本机实测对所有 HTTPS 报
    /// SecureChannelFailure（未能创建 SSL/TLS 安全通道），更新检查因此全部判为镜像不可达。
    /// </summary>
    private static void EnableModernTls()
    {
        try
        {
            const int Tls12 = 3072;
            const int Tls13 = 12288;
            int flags = Tls12;
            try { flags |= Tls13; }
            catch (Exception __ex) { Note.Swallow("Program.cs:167", __ex); }
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)flags;
        }
        catch (Exception)
        {
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; } catch (Exception __ex) { Note.Swallow("Program.cs:172", __ex); }
        }
        try
        {
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 16;
            ServicePointManager.CheckCertificateRevocationList = false;
        }
        catch (Exception __ex) { Note.Swallow("Program.cs:180", __ex); }
    }

    private static void EnableDpiAwareness()
    {
        try
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
            IntPtr r = SetThreadDpiAwarenessContext(new IntPtr(-4));
            if (r != IntPtr.Zero) return;
        }
        catch (Exception __ex) { Note.Swallow("Program.cs:191", __ex); }
        try
        {
            SetProcessDPIAware();
        }
        catch (Exception __ex) { Note.Swallow("Program.cs:196", __ex); }
    }

    // ========================= 异常 =========================
    private static readonly object promptGate = new object();
    private static DateTime lastPrompt = DateTime.MinValue;

    private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
    {
        string detail = (e == null || e.Exception == null) ? "" : e.Exception.ToString();
        Diag("UI 线程异常：" + detail);
        // 只写日志不提示等于用户侧完全无感知：这里给一次提示，并做 30 秒节流避免连环弹窗
        bool show = false;
        lock (promptGate)
        {
            if ((DateTime.Now - lastPrompt).TotalSeconds >= 30)
            {
                lastPrompt = DateTime.Now;
                show = true;
            }
        }
        if (!show) return;
        try
        {
            string firstLine = detail;
            int nl = firstLine.IndexOf('\n');
            if (nl > 0) firstLine = firstLine.Substring(0, nl);
            if (firstLine.Length > 300) firstLine = firstLine.Substring(0, 300) + " …";
            MessageBox.Show(
                "程序内部出现异常，已记录到诊断日志，可以继续使用。\r\n\r\n"
                + firstLine + "\r\n\r\n日志：" + AppPaths.DiagFile,
                "REM-GUI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception __ex) { Note.Swallow("Program.cs:229", __ex); }
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        string detail = (e == null ? "" : Convert.ToString(e.ExceptionObject));
        Diag("未捕获异常：" + detail);
        // 进程即将终止，必须给出可见提示，否则用户只看到窗口凭空消失
        try
        {
            MessageBox.Show(
                "REM-GUI 遇到无法恢复的错误，需要退出。\r\n\r\n详情已写入：" + AppPaths.DiagFile,
                "REM-GUI", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception __ex) { Note.Swallow("Program.cs:243", __ex); }
    }

    private static void Diag(string msg)
    {
        try
        {
            string path = AppPaths.DiagFile;
            if (string.IsNullOrEmpty(path)) return;
            // 崩溃详情里可能带完整 URL / 请求头 / 密钥参数，落盘前必须过掩码
            msg = Mask.Secret(msg);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [Program] " + msg + "\r\n",
                new UTF8Encoding(false));
        }
        catch (Exception __ex) { Note.Swallow("Program.cs:258", __ex); }
    }
}
