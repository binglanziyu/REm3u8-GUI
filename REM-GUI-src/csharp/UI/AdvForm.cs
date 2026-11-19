// =====================================================================
//  UI\AdvForm.cs — 高级设置：全部下载选项 + 代理 / Header / 附加参数
//  线程、重试、超时、限速已上提到主界面，此处不再重复
//  代理与 Header 默认打码且只读，必须勾选"显示明文"才能编辑
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Rem.Core;
using Rem.Engine;
using Rem.Services;

namespace Rem.UI
{
    public sealed partial class AdvForm : Form
    {
        // ---- 下载选项（原主界面选项卡片迁移至此） ----
        public bool Smart;
        public bool Mux;
        public bool Concurrent;
        public bool Binary;
        public bool SkipMerge;
        public bool SkipDownload;
        public bool AppendUrlParams;
        public bool DelAfterDone;
        public bool CheckSegments;
        public bool AutoSelect;

        // ---- 网络与扩展 ----
        public string Proxy;
        public string Headers;
        public string ExtraArgs;

        // 系统代理开关（D8）：true = 使用系统代理（勾选，不下发参数，沿用引擎默认 True）；
        // false = 不勾「使用系统代理」（默认）→ 下发 --use-system-proxy false
        public bool UseSystemProxy;

        // ---- 解密（解密方式固定 AES-128 + 手动 KEY / IV）----
        public string HlsMethod;
        public string HlsKey;
        public string HlsIv;

        // ---- 界面 ----
        public int LogLimit;

        // ---- 赞助提醒频率 ----
        // 二选一：Sponsor.FreqWeek（每周一次，默认）/ Sponsor.FreqDay（每日一次）
        public string SponsorFreq;

        // ---- 自动补片 ----
        // 补片上限（缺失率 %，1–70，默认 50）：超过该比例才整组放弃补片
        public int PatchPercent;

        private CheckBox chkSmart;
        private CheckBox chkMux;
        private CheckBox chkConcurrent;
        private CheckBox chkBinary;
        private CheckBox chkSkipMerge;
        private CheckBox chkSkipDownload;
        private CheckBox chkAppend;
        private CheckBox chkDel;
        private CheckBox chkCheck;
        private CheckBox chkAuto;

        private TextBox txtProxy;
        private TextBox txtHeaders;
        private TextBox txtExtra;
        private ThinScroll scHeaders;
        private ThinScroll scExtra;
        private CheckBox chkPlain;
        private CheckBox chkSysProxy;
        private TextBox numLogLimit;
        private TextBox numPatch;
        // 赞助提醒频率二选一（勾一个自动放开另一个，不允许两个都关）
        private CheckBox chkFreqWeek;
        private CheckBox chkFreqDay;
        private string plainProxy;
        private string plainHeaders;

        private TextBox txtKey;
        private TextBox txtIv;
        private Label lbKeyShape;
        private string plainKey;
        private string plainIv;

        private ToolTip tips;
        private readonly Dictionary<CheckBox, string> tipText = new Dictionary<CheckBox, string>();
        // 被互斥锁住的开关 → 拦住它的那一项名字（用于提示"先取消谁"）
        private readonly Dictionary<CheckBox, string> lockReason = new Dictionary<CheckBox, string>();
        // 被引擎强制开启、点也点不掉的开关 → 强制来源（勾了混流，RE 自己就会开二进制合并）
        private readonly Dictionary<CheckBox, string> forceReason = new Dictionary<CheckBox, string>();

        /// <summary>
        /// 关闭前把本窗体所有开关从 Theme 的静态集合摘掉：MainForm.OnAdv 用 using 每次新建一个
        /// AdvForm，不摘的话每开一次高级设置就多留一批已释放控件的强引用，只增不减（3.4）。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseToggleTree(this);
            base.Dispose(disposing);
        }

        private static void ReleaseToggleTree(Control root)
        {
            if (root == null) return;
            for (int i = 0; i < root.Controls.Count; i++)
            {
                Control c = root.Controls[i];
                CheckBox cb = c as CheckBox;
                if (cb != null) Theme.ReleaseToggle(cb);
                if (c.Controls.Count > 0) ReleaseToggleTree(c);
            }
        }

        public AdvForm(AppConfig cfg)
        {
            Smart = cfg.Smart;
            Mux = cfg.Mux;
            Concurrent = cfg.Concurrent;
            Binary = cfg.Binary;
            SkipMerge = cfg.SkipMerge;
            SkipDownload = cfg.SkipDownload;
            AppendUrlParams = cfg.AppendUrlParams;
            DelAfterDone = cfg.DelAfterDone;
            CheckSegments = cfg.CheckSegments;
            AutoSelect = cfg.AutoSelect;

            Proxy = cfg.Proxy;
            Headers = cfg.Headers;
            ExtraArgs = cfg.ExtraArgs;
            UseSystemProxy = cfg.UseSystemProxy;
            LogLimit = cfg.LogLimit;
            // 频率非法值一律收敛成默认（每周一次），避免配置被手改成怪值后二选一都是空
            SponsorFreq = Sponsor.NormalizeFreq(cfg.SponsorFreq);
            PatchPercent = cfg.PatchPercent;
            plainProxy = cfg.Proxy;
            plainHeaders = cfg.Headers;

            HlsMethod = cfg.HlsMethod;
            HlsKey = cfg.HlsKey;
            HlsIv = cfg.HlsIv;
            plainKey = cfg.HlsKey;
            plainIv = cfg.HlsIv;

            BuildUi();
        }


        // ==== 布局基准与行距常量（S3 拆分：原 BuildUi 内局部常量上提，取值与拆分前逐字一致）====
            const int x = 14;
            const int labelW = 76;
            // 行距常量（D7 压缩行距）：150% 缩放下本窗高度必须 ≤ 1032px（本机工作区高），
            // 折到设计值只有 688 左右，因此行高与行间隙统一收敛：
            //   rowH  单行输入框/下拉的高度（保持 26：内部 TextBox 固定 22px + 上下留白，压小会切字）
            //   step  单行控件占用的纵向步长 = rowH + 2px 行间隙
            //   note  控件下方 8pt 只读说明文字的占位（说明行紧贴控件下沿，不额外留空）
            const int rowH = 26;
            const int step = 28;
            const int note = 44;    // 控件(26) + 说明文字(18) 的整行占位，说明行底边正好压在下一行顶边

        /// <summary>窗体骨架与装配顺序：自身属性 → 五个卡片分区 → 由内容反推窗体尺寸。各分区按纵向游标 y 依次排布，返回值是下一段的起始 y。</summary>
        private void BuildUi()
        {
            Text = "高级设置";
            Icon = Theme.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9F);
            // 关闭 WinForms 自带的字体缩放：与主窗体一致改为手工缩放，
            // 否则 AutoScaleMode.Font 的整体缩放会和 Theme.S 的逐点缩放叠加，高 DPI 下控件互相挤压（D7）
            AutoScaleMode = AutoScaleMode.None;

            // 布局基准与行距常量已上提为类常量（S3 拆分，取值与拆分前逐字一致）
            int y = 10;

            y = BuildOptionCard(y);
            y = BuildPatchCard(y);
            y = BuildCryptoCard(y);
            y = BuildNetworkCard(y);
            y = BuildFooterCard(y);

            // 窗体尺寸由内容反推，不再手写固定值：
            // 宽度 = 左 14 + 内容 442 + 右 14；高度 = 底部按钮下沿（y + 8 + 26）+ 10 留白。
            // 全部经 S() 缩放，保证高 DPI 下内容不会被 ClientSize 裁掉（D7）
            // 行距已按 150% 缩放下 ≤ 1032px 收敛（1.0 / 1.25 / 1.5 三档均不超出工作区高）
            ClientSize = new Size(S(x + 442 + 14), S(y + 8 + 26 + 10));
        }



        private void Accept()
        {
            Smart = chkSmart.Checked;
            Mux = chkMux.Checked;
            Concurrent = chkConcurrent.Checked;
            // 强制开启不算用户的选择：混流在场时引擎本来就会合并，把这个状态写进配置的话，
            // 用户下次取消混流会发现「二进制合并」莫名还是勾着的（D13）
            Binary = chkBinary.Checked && !forceReason.ContainsKey(chkBinary);
            SkipMerge = chkSkipMerge.Checked;
            SkipDownload = chkSkipDownload.Checked;
            AppendUrlParams = chkAppend.Checked;
            DelAfterDone = chkDel.Checked;
            CheckSegments = chkCheck.Checked;
            AutoSelect = chkAuto.Checked;
            UseSystemProxy = chkSysProxy.Checked;

            if (chkPlain.Checked)
            {
                Proxy = txtProxy.Text.Trim();
                Headers = txtHeaders.Text;
            }
            else
            {
                Proxy = plainProxy;
                Headers = plainHeaders;
            }
            ExtraArgs = txtExtra.Text.Trim();

            // 解密：方式固定 AES-128（不再是下拉项，直接取常量）；KEY/IV 只在勾了"显示明文"时才认输入框内容
            // （未勾选时输入框里是打码串，读出来会把星号当成密钥写进配置）
            HlsMethod = AppConfig.HlsMethodAes128;
            HlsKey = (chkPlain.Checked ? txtKey.Text : plainKey).Trim();
            HlsIv = (chkPlain.Checked ? txtIv.Text : plainIv).Trim();
            // 空/非法回落默认值，越界按统一边界夹紧，避免手滑把上限设成个位数后看不到日志
            int logLimit;
            if (!int.TryParse(numLogLimit.Text.Trim(), out logLimit)) logLimit = 200000;
            if (logLimit < AppConfig.LogLimitMin) logLimit = AppConfig.LogLimitMin;
            if (logLimit > AppConfig.LogLimitMax) logLimit = AppConfig.LogLimitMax;
            LogLimit = logLimit;
            // 补片上限同样空/非法回落默认，越界按统一边界夹紧
            int patchPct;
            if (!int.TryParse(numPatch.Text.Trim(), out patchPct)) patchPct = 50;
            if (patchPct < AppConfig.PatchPercentMin) patchPct = AppConfig.PatchPercentMin;
            if (patchPct > AppConfig.PatchPercentMax) patchPct = AppConfig.PatchPercentMax;
            PatchPercent = patchPct;
            // 二选一：以「每日一次」是否勾选为准，勾了就 day，否则 week（两个都没勾按默认 week）
            SponsorFreq = (chkFreqDay != null && chkFreqDay.Checked) ? Sponsor.FreqDay : Sponsor.FreqWeek;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
