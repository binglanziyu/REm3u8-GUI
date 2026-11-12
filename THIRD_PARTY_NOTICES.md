# 第三方组件声明

`REm3u8 可视化界面`（程序名 REM-GUI）在运行时使用以下第三方可执行文件。这些文件以内嵌资源形式存放于 `REM-GUI.exe`，首次启动时释放到程序目录。各组件版权归原作者所有，本文件仅作声明，未对原程序做任何修改。

---

## 1. N_m3u8DL-RE

- 版本：`0.6.0+df70f0b3da0c630bd413bf617e758051f6b64757`
- 项目地址：https://github.com/nilaoda/N_m3u8DL-RE
- 许可证：MIT License
- 使用方式：内嵌于 `REM-GUI.exe`，缺失时释放为 `N_m3u8DL-RE.exe`；同目录存在外部文件时优先使用外部文件

MIT 许可证要求保留原始版权声明与许可全文。原项目免责声明要点：本软件基于 MIT License 开源，按「原样」提供，不附带任何明示或暗示的保证；仅用于学习和技术研究目的，使用者应遵守所在国家或地区的法律法规，仅下载和获取拥有合法权限的流媒体内容。

---

## 2. FFmpeg

- 版本：`N-94813-g85386c36e3-ffmpeg-for-N_m3u8DL-CLI`（构建于 2019 年，gcc 8.2.0，x86 / win32）
- 项目地址：https://ffmpeg.org/
- 许可证：**GNU Lesser General Public License v3（LGPL v3）**
- 使用方式：内嵌于 `REM-GUI.exe`，缺失时释放为 `ffmpeg.exe`；同目录存在外部文件时优先使用外部文件

### 许可证判定依据

对该二进制执行 `ffmpeg -buildconf` 得到的真实配置行包含 `--enable-version3`，且**不含** `--enable-gpl`、`--enable-nonfree`。

依据 FFmpeg 官方许可说明：默认许可为 LGPL v2.1+；`--enable-gpl` 会把整体许可升级为 GPL v2+；在未启用 GPL 组件的前提下，`--enable-version3` 将 (L)GPL 升级为第 3 版。因此本二进制适用 **LGPL v3**，**不适用 GPL**。

### 合规措施

1. **未修改原程序**：分发的是原样二进制，未打补丁、未裁剪
2. **允许替换**：程序优先使用同目录下的 `ffmpeg.exe`，用户可替换为任意兼容版本；内嵌副本仅在文件缺失时才释放
3. **源码获取**：FFmpeg 源码见 https://ffmpeg.org/download.html#get-sources，对应版本标签 `N-94813-g85386c36e3`；编译参数可通过 `ffmpeg -buildconf` 原样复现
4. **许可全文**：LGPL v3 见 https://www.gnu.org/licenses/lgpl-3.0.txt 及 FFmpeg 源码中的 `COPYING.LGPLv3`

### 本项目的许可状态

本项目通过**独立进程调用**方式使用 `ffmpeg.exe`（`Process.Start`），不链接 FFmpeg 任何库、不静态包含其代码，属于聚合分发而非衍生作品，因此本项目自身代码不受 LGPL 传染，采用 MIT 许可。

### 本构建启用的外部库

| 库 | 许可 |
|---|---|
| bzip2 | BSD 类 |
| iconv | LGPL |
| libass | ISC |
| FreeType | FTL 或 GPLv2（双许可，此处按 FTL） |
| libilbc | BSD-3-Clause |
| LAME (libmp3lame) | LGPL |
| OpenJPEG | BSD-2-Clause |
| VisualOn AMR-WB (libvo-amrwbenc) | Apache-2.0 |
| zlib | zlib License |

以上均为宽松许可或 LGPL，不含 GPL 组件，故整体适用 LGPL v3。

---

## 3. Microsoft .NET Framework 4.8

运行时由 Windows 系统提供，本项目不分发任何 .NET 运行时文件，适用 Microsoft 软件许可条款。

---

## 4. 本项目自身

- 许可证：MIT License，见仓库根目录 `LICENSE`
- 版权：Copyright (c) 2026 binglanziyu
- 本项目 MIT 许可仅覆盖本项目源码，不改变上述第三方组件的许可条款

---

如认为本项目侵犯了你的权益，请通过 Issue 联系，将及时处理。
*（内容由AI生成，仅供参考）*
