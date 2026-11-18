// =====================================================================
//  UI\AdvForm.Skip.cs — 高级设置窗的选项互斥规则分区（partial 拆分）
//  纯代码搬移，逻辑与行为与拆分前完全一致（重构方案第 2 章）
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
        /// <summary>忽略合并时混流无意义；仅解析时混流与边下边转都不生效。</summary>
        private void ApplySkipRules()
        {
            bool skipMerge = chkSkipMerge.Checked;
            bool skipDl = chkSkipDownload.Checked;

            SetToggleLocked(chkMux, skipDl ? "仅解析不下载" : (skipMerge ? "跳过合并" : null));
            SetToggleLocked(chkConcurrent, skipDl ? "仅解析不下载" : null);

            if (lockReason.ContainsKey(chkMux)) chkMux.Checked = false;
            if (lockReason.ContainsKey(chkConcurrent)) chkConcurrent.Checked = false;

            // 一致性优先：只要混流真的生效，引擎就会自己打开二进制合并
            // （日志原文「你已开启下载完成后混流，自动开启二进制合并」）。
            // 界面必须如实渲染成"被强制开启"，不能停在没勾的样子：
            // 否则用户以为没合并、实际合并了，参数预览也和真实行为对不上。
            bool muxEffective = chkMux.Checked && !skipMerge && !skipDl;
            SetToggleForced(chkBinary, muxEffective ? "混流为 MP4" : null);
            if (forceReason.ContainsKey(chkBinary) && !chkBinary.Checked) chkBinary.Checked = true;
        }
    }
}
