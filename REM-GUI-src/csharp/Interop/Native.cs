// =====================================================================
//  Interop\Native.cs — 系统 API：暗色标题栏、暗色滚动条、Job 对象、重绘控制
// =====================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Rem.Core;
namespace Rem.Interop
{
    internal static class Native
    {
        // ---------- 窗口外观 ----------
        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string subIdList);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public const int WM_SETREDRAW = 0x000B;

        /// <summary>Win10 1903+ 起 DWMWA_USE_IMMERSIVE_DARK_MODE = 20，旧版为 19。</summary>
        public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            int v = dark ? 1 : 0;
            try
            {
                int hr = DwmSetWindowAttribute(hwnd, 20, ref v, Marshal.SizeOf(typeof(int)));
                if (hr != 0) DwmSetWindowAttribute(hwnd, 19, ref v, Marshal.SizeOf(typeof(int)));
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
        }

        public static void ApplyDarkScrollBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try { SetWindowTheme(hwnd, "DarkMode_CFD", null); }
            catch (Exception __ex) { Note.Swallow(__ex); }
        }

        // ---------- 原生滚动条的隐藏与滚动控制（自绘细滚动条要用） ----------
        public const int SB_VERT = 1;
        public const int EM_GETLINECOUNT = 0x00BA;
        public const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        public const int EM_LINESCROLL = 0x00B6;
        public const int EM_SETRECT = 0x00B3;
        public const int WM_MOUSEWHEEL = 0x020A;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        public static extern IntPtr SendMessageRect(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

        /// <summary>
        /// 给 RichEdit 设格式化矩形，右侧留出 inset 像素空白。
        /// 日志控件不再申请原生竖条后，文本会一直铺到控件右缘，必须让出这一点宽度给自绘 ThinScroll，
        /// 否则每行行尾会被压在细条底下。RichEdit 在尺寸变化时会按同一 inset 自动重算，这里做兜底重设。
        /// </summary>
        public static void SetTextRightInset(IntPtr hwnd, int clientWidth, int clientHeight, int inset)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                if (inset < 0) { inset = 0; }
                int right = clientWidth - inset;
                if (right < 16) { right = 16; }
                if (clientHeight < 1) { clientHeight = 1; }
                RECT rc = new RECT();
                rc.Left = 0;
                rc.Top = 0;
                rc.Right = right;
                rc.Bottom = clientHeight;
                SendMessageRect(hwnd, EM_SETRECT, IntPtr.Zero, ref rc);
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
        }

        [DllImport("user32.dll")]
        public static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        public const int GWL_STYLE = -16;
        public const int WS_VSCROLL = 0x00200000;

        /// <summary>控件当前是否还带着系统垂直滚动条样式位。</summary>
        public static bool HasVertScrollStyle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return (GetWindowLong(hwnd, GWL_STYLE) & WS_VSCROLL) != 0; }
            catch (Exception __ex) { Note.Swallow(__ex); return false; }
        }

        /// <summary>藏掉控件自带的系统滚动条（右键菜单/滚轮/键盘滚动都不受影响）。</summary>
        public static void HideScrollBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try { ShowScrollBar(hwnd, SB_VERT, false); }
            catch (Exception __ex) { Note.Swallow(__ex); }
        }

        public static int LineCount(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            try { return (int)SendMessage(hwnd, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception __ex) { Note.Swallow(__ex); return 0; }
        }

        public static int FirstVisibleLine(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            try { return (int)SendMessage(hwnd, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception __ex) { Note.Swallow(__ex); return 0; }
        }

        /// <summary>滚到指定显示行：先大负数回顶，再下移，避开相对滚动误差累积。</summary>
        public static void ScrollToLine(IntPtr hwnd, int line)
        {
            if (hwnd == IntPtr.Zero) return;
            if (line < 0) line = 0;
            try
            {
                SendMessage(hwnd, EM_LINESCROLL, IntPtr.Zero, new IntPtr(-1000000));
                if (line > 0) SendMessage(hwnd, EM_LINESCROLL, IntPtr.Zero, new IntPtr(line));
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
        }

        public static void ScrollBy(IntPtr hwnd, int lines)
        {
            if (hwnd == IntPtr.Zero || lines == 0) return;
            try { SendMessage(hwnd, EM_LINESCROLL, IntPtr.Zero, new IntPtr(lines)); }
            catch (Exception __ex) { Note.Swallow(__ex); }
        }

        public static void SetRedraw(IntPtr hwnd, bool enable)
        {
            if (hwnd == IntPtr.Zero) return;
            try { SendMessage(hwnd, WM_SETREDRAW, enable ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero); }
            catch (Exception __ex) { Note.Swallow(__ex); }
        }

        // ---------- Job 对象：父进程退出时子进程一起结束 ----------
        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, int cbInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(IntPtr hJob, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        /// <summary>创建并配置“关闭即杀”的 Job 对象，失败返回 IntPtr.Zero。</summary>
        public static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = IntPtr.Zero;
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr p = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(info, p, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, p, len))
                    {
                        CloseHandle(job);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(p);
                }
            }
            catch (Exception)
            {
                if (job != IntPtr.Zero) { try { CloseHandle(job); } catch (Exception __ex) { Note.Swallow(__ex); } }
                return IntPtr.Zero;
            }
            return job;
        }

        // ---------- 进程树强杀（P1-2：暂停要连 ffmpeg 子进程一起收掉） ----------
        // Job 对象在正常路径下已经能连带子进程，但 AssignProcessToJobObject 存在失败场景
        // （引擎已被别的 Job 占用、或作业对象创建失败），此时过去只 Kill 了引擎本体，
        // 混流阶段的 ffmpeg 会残留下来继续占着成品文件与 CPU。这里补一条按 Toolhelp32
        // 枚举父子关系、自底向上强杀整棵树的兜底路径。

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        public const uint TH32CS_SNAPPROCESS = 0x00000002;
        public const uint PROCESS_TERMINATE = 0x0001;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        private const int INVALID_HANDLE_VALUE_I = -1;

        /// <summary>
        /// 取全系统父子关系快照：pid → parentPid。失败返回空表（调用方据此退化为只杀根进程）。
        /// </summary>
        private static Dictionary<int, int> SnapshotParents()
        {
            Dictionary<int, int> map = new Dictionary<int, int>();
            IntPtr snap = IntPtr.Zero;
            try
            {
                snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snap == IntPtr.Zero || snap.ToInt64() == INVALID_HANDLE_VALUE_I) return map;
                PROCESSENTRY32W pe = new PROCESSENTRY32W();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                if (!Process32FirstW(snap, ref pe)) return map;
                do
                {
                    map[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID;
                    pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                }
                while (Process32NextW(snap, ref pe));
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
            finally
            {
                if (snap != IntPtr.Zero && snap.ToInt64() != INVALID_HANDLE_VALUE_I)
                {
                    try { CloseHandle(snap); } catch (Exception __ex) { Note.Swallow(__ex); }
                }
            }
            return map;
        }

        /// <summary>把某进程的所有后代按「由深到浅」排列（先杀叶子，避免父进程先死导致子进程被过继）。</summary>
        public static List<int> DescendantsDeepestFirst(int rootPid)
        {
            List<int> order = new List<int>();
            try
            {
                Dictionary<int, int> parent = SnapshotParents();
                if (parent.Count == 0) return order;
                // 逐层展开：每一层收集后立即记录，层序即深度序
                List<int> level = new List<int>();
                level.Add(rootPid);
                HashSet<int> seen = new HashSet<int>();
                seen.Add(rootPid);
                for (int depth = 0; depth < 32 && level.Count > 0; depth++)
                {
                    List<int> next = new List<int>();
                    for (int i = 0; i < level.Count; i++)
                    {
                        int pid = level[i];
                        foreach (KeyValuePair<int, int> kv in parent)
                        {
                            if (kv.Value != pid) continue;
                            if (kv.Key <= 4) continue;                 // 跳过 System/Idle 等保护进程
                            if (!seen.Add(kv.Key)) continue;
                            next.Add(kv.Key);
                        }
                    }
                    for (int i = 0; i < next.Count; i++) order.Add(next[i]);
                    level = next;
                }
                order.Reverse();                                        // 深层在前
            }
            catch (Exception __ex) { Note.Swallow(__ex); }
            return order;
        }

        /// <summary>强杀一棵进程树（先子后父）。返回成功终止的进程数；根进程不在其中。</summary>
        public static int KillProcessTree(int rootPid)
        {
            int killed = 0;
            if (rootPid <= 4) return killed;
            List<int> kids = DescendantsDeepestFirst(rootPid);
            for (int i = 0; i < kids.Count; i++)
            {
                if (TerminateByPid(kids[i])) killed++;
            }
            return killed;
        }

        /// <summary>按 pid 强杀单个进程（不抛异常，失败返回 false）。</summary>
        public static bool TerminateByPid(int pid)
        {
            if (pid <= 4) return false;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_TERMINATE, false, pid);
                if (h == IntPtr.Zero) return false;
                return TerminateProcess(h, 1);
            }
            catch (Exception __ex) { Note.Swallow(__ex); return false; }
            finally
            {
                if (h != IntPtr.Zero)
                {
                    try { CloseHandle(h); } catch (Exception __ex) { Note.Swallow(__ex); }
                }
            }
        }

        // ---------- 完成提醒 ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        /// <summary>
        /// 闪烁任务栏按钮（FLASHW_ALL | FLASHW_TIMERNOFG）：窗口切到后台/最小化时，
        /// 用户也能看到"跑完了"。窗口一旦被激活，闪烁由系统自动停止。
        /// </summary>
        internal static void FlashTaskbar(IntPtr hwnd, uint count)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                FLASHWINFO fi = new FLASHWINFO();
                fi.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
                fi.hwnd = hwnd;
                // FLASHW_TRAY(0x2) 只闪任务栏按钮、且只闪本程序那一格；FLASHW_TIMERNOFG(0xC) 表示
                // 一直闪到窗口被切到前台为止。注意 0x2|0xC 里没有 FLASHW_CAPTION，标题栏不会一起闪（别写成 FLASHW_ALL=0x3）
                fi.dwFlags = 0x00000002u | 0x0000000Cu;
                fi.uCount = count;   // TIMERNOFG 下倒计时由“是否前台”决定，count 仅作次数上界
                fi.dwTimeout = 0;
                FlashWindowEx(ref fi);
            }
            catch (Exception ex)
            {
                Note.Swallow(ex);
            }
        }
    }
}
