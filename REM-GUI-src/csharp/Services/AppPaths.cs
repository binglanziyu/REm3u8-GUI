// =====================================================================
//  Services\AppPaths.cs — 程序目录 / 数据目录 / 内嵌工具自愈
//  程序目录：REM-GUI.exe、N_m3u8DL-RE.exe、ffmpeg.exe、logs\
//  运行期数据（配置 / 引擎 / 工具）落在 %LOCALAPPDATA%\REM-GUI\
//  日志跟着程序走：程序目录不可写时才退回数据目录，避免日志静默丢失
// =====================================================================
using System;
using System.IO;
using System.Reflection;

using Rem.Core;
namespace Rem.Services
{
    public static class AppPaths
    {
        public static string ExePath = "";
        public static string AppDir = "";
        public static string DataDir = "";
        public static string LogDir = "";
        public static string ConfigPath = "";
        public static string EngineDir = "";
        public static string ToolsDir = "";
        public static string DropFile = "";
        public static string DiagFile = "";

        public const string ReName = "N_m3u8DL-RE.exe";
        public const string FFmpegName = "ffmpeg.exe";

        public static void Init()
        {
            ExePath = Assembly.GetExecutingAssembly().Location;
            AppDir = Path.GetDirectoryName(ExePath);
            if (string.IsNullOrEmpty(AppDir)) AppDir = Environment.CurrentDirectory;

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = Path.Combine(AppDir, "data");
            DataDir = Path.Combine(local, "REM-GUI");
            // 日志放程序目录下的 logs：程序搬到哪，日志就跟到哪，不让同一个程序的产物散在两处
            LogDir = Path.Combine(AppDir, "logs");
            EngineDir = Path.Combine(DataDir, "engine");
            ToolsDir = Path.Combine(DataDir, "tools");
            ConfigPath = Path.Combine(DataDir, "config.json");
            DropFile = Path.Combine(DataDir, "drop.txt");

            TryCreate(DataDir);
            // 建不出 logs 子目录（只读介质等极端情况）就直接把日志放程序目录本体，
            // 绝不再散落到 %LOCALAPPDATA%：用户要求同一程序的产物只出现在程序目录里
            if (!TryCreate(LogDir)) LogDir = AppDir;
            TryCreate(LogDir);
            DiagFile = Path.Combine(LogDir, "diag.log");
            TryCreate(EngineDir);
            TryCreate(ToolsDir);
        }

        /// <summary>确保目录存在，返回是否可用（创建失败返回 false）。</summary>
        private static bool TryCreate(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return true;
            }
            catch (Exception __ex)
            {
                Note.Swallow("AppPaths.cs:59", __ex);
                return false;
            }
        }

        public static string BuiltinRE { get { return Path.Combine(AppDir, ReName); } }
        public static string BuiltinFFmpeg { get { return Path.Combine(AppDir, FFmpegName); } }

        /// <summary>
        /// 内嵌工具自愈：程序目录缺哪个就从资源里解出哪个（只判存在，不比对内容）。
        /// 程序目录不可写时退到 %LOCALAPPDATA%\REM-GUI\tools。
        /// </summary>
        public static void EnsureTools(bool quiet, Logger log)
        {
            EnsureOne(ReName, quiet, log);
            EnsureOne(FFmpegName, quiet, log);
        }

        private static void EnsureOne(string name, bool quiet, Logger log)
        {
            string inApp = Path.Combine(AppDir, name);
            if (File.Exists(inApp)) return;
            string inTools = Path.Combine(ToolsDir, name);
            if (File.Exists(inTools)) return;

            string target = inApp;
            try
            {
                using (Stream src = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (src == null) return;
                    try
                    {
                        WriteStream(src, target);
                    }
                    catch (Exception)
                    {
                        target = inTools;
                        using (Stream src2 = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                        {
                            if (src2 == null) return;
                            WriteStream(src2, target);
                        }
                    }
                }
                if (!quiet && log != null) log.Add(">> 已释放内置 " + name + " → " + target, Colors.Green);
            }
            catch (Exception ex)
            {
                if (log != null) log.Diag("释放内置工具失败 " + name + "：" + ex.Message, "EnsureTools");
            }
        }

        private static void WriteStream(Stream src, string target)
        {
            string tmp = target + ".part";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                byte[] buf = new byte[81920];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
                fs.Flush(true);
            }
            // 原子替换：目标文件在最后一刻才被换掉，中途崩溃最多留下 .part / .bak
            if (File.Exists(target))
            {
                string bak = target + ".bak";
                try
                {
                    try { if (File.Exists(bak)) File.Delete(bak); } catch (Exception __ex) { Note.Swallow("AppPaths.cs:122", __ex); }
                    File.Replace(tmp, target, bak, true);
                    try { if (File.Exists(bak)) File.Delete(bak); } catch (Exception __ex) { Note.Swallow("AppPaths.cs:124", __ex); }
                }
                catch (Exception)
                {
                    // 跨卷或文件系统不支持 Replace 时退回 Move（先删再移，窗口最小）
                    try { File.Delete(target); } catch (Exception __ex) { Note.Swallow("AppPaths.cs:129", __ex); }
                    File.Move(tmp, target);
                }
            }
            else
            {
                File.Move(tmp, target);
            }
        }

        /// <summary>
        /// 定位工具：程序目录 → tools → engine\&lt;版本&gt;。
        /// 不再扫描 PATH —— 同名程序放在 PATH 目录里会被直接执行，构成劫持面。
        /// </summary>
        public static string FindTool(string fileName)
        {
            try
            {
                string p = Path.Combine(AppDir, fileName);
                if (File.Exists(p)) return p;
                p = Path.Combine(ToolsDir, fileName);
                if (File.Exists(p)) return p;
                if (Directory.Exists(EngineDir))
                {
                    string[] subs = Directory.GetDirectories(EngineDir);
                    Array.Sort(subs);
                    Array.Reverse(subs);
                    for (int i = 0; i < subs.Length; i++)
                    {
                        p = Path.Combine(subs[i], fileName);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch (Exception __ex) { Note.Swallow("AppPaths.cs:163", __ex); }
            return "";
        }

        public static long FreeBytes(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return -1;
                string full = Path.GetFullPath(dir);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return -1;
                DriveInfo di = new DriveInfo(root);
                if (!di.IsReady) return -1;
                return di.AvailableFreeSpace;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }

    /// <summary>日志配色（与 UI 主题同源，服务层只存 ARGB 值）。</summary>
    public static class Colors
    {
        public const int Default = 0;
        public static readonly int Red = System.Drawing.Color.FromArgb(224, 117, 110).ToArgb();
        public static readonly int Orange = System.Drawing.Color.FromArgb(217, 160, 91).ToArgb();
        public static readonly int Green = System.Drawing.Color.FromArgb(127, 208, 138).ToArgb();
        public static readonly int Blue = System.Drawing.Color.FromArgb(111, 182, 242).ToArgb();
        public static readonly int Dim = System.Drawing.Color.FromArgb(116, 129, 144).ToArgb();
        public static readonly int Gold = System.Drawing.Color.FromArgb(217, 191, 134).ToArgb();
    }
}
