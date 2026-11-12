<div align="center">

# REm3u8 可视化界面

**把 N_m3u8DL-RE 的参数表，变成一张能看懂的表单。**

[![Version](https://img.shields.io/badge/version-3.0.0-2ea043?style=flat-square)](../../releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?style=flat-square)](#环境要求)
[![Runtime](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4?style=flat-square)](#环境要求)
[![License](https://img.shields.io/badge/license-MIT-3DA639?style=flat-square)](LICENSE)

[下载](#下载) · [特性](#特性) · [构建](#从源码构建) · [许可](#依赖与许可)

</div>

---

可视化图形界面，简单好用~

## 下载

前往 [Releases](../../releases) 获取 **`REM-GUI.exe`**，双击即用。

一个文件，就是全部。下载引擎与混流工具已内嵌在程序内，首次启动自动释放；不需要安装包、不需要运行时、不需要再下载任何附件。

## 特性

| | |
|---|---|
| **单文件交付** | 内嵌 N_m3u8DL-RE 与 FFmpeg，文件被误删会自动重新释放 |
| **链接即下** | 粘贴、拖入、多链接排队；重复启动会把新链接转交给已开窗口 |
| **开跑前预检** | 先校验链接格式、保存目录是否真的可写、内容类型，不把错误丢给引擎去报退出码 |
| **坏片自动补片** | 引擎报分片校验不通过时两段自救：先重拉清单续跑补齐，仍缺则用前后相邻分片顶替让它继续合并；缺口超过 20 片或 30% 会放弃补片并如实报错 |
| **断点续传** | 中断后拖回原文件即续传；分片已被清理时明说将从头下载，而不是静默重下 |
| **手动 AES-128** | 填 KEY 与 IV 即可（HEX / Base64 / 文件），DPAPI 加密落盘，界面与日志一律打码 |
| **过程可视** | 实时进度、速度、剩余时间；日志可滚动，写盘前自动抹掉 Cookie、Token 与密钥 |
| **参数齐全** | 容器格式、合并方式、轨道选择、限速、代理、请求头、超时重试、下载后删分片 |
| **高 DPI** | 100% / 125% / 150% 三档下布局不重叠 |
| **版本检查** | 版本清单内嵌在程序里，无需任何外部文件 |

## 快速开始

```text
1.  双击 REM-GUI.exe
2.  粘贴视频页面链接，或拖 m3u8 / mpd 文件进窗口
3.  选保存目录，点「开始下载」
```

需要解密的资源，打开「高级设置」→ 解密区，填 KEY 与 IV 即可。加密方式固定为 AES_128，不需要选择引擎，也不需要额外程序。KEY 与 IV 留空时不下发任何解密参数，明文流按清单原样处理（早期版本恒发 AES_128 会让明文流被强行解密而直接失败）。

## 环境要求

| 项 | 要求 |
| :-- | :-- |
| 系统 | Windows 10 / 11（x64） |
| 运行时 | .NET Framework 4.8（系统自带） |
| 其他 | 无 |

## 从源码构建

只用 Windows 自带的 csc，不需要 .NET SDK，也不需要 Visual Studio。

**构建前置**：仓库不收录二进制。先把两个工具放进 `dist\` 顶层（脚本只查顶层，不递归；缺文件直接报错退出）：

| 文件 | 版本 | 来源 |
| :-- | :-- | :-- |
| `N_m3u8DL-RE.exe` | 0.6.0 | [N_m3u8DL-RE Releases](https://github.com/nilaoda/N_m3u8DL-RE/releases) |
| `ffmpeg.exe` | N-94813-g85386c36e3 | 需与 Release 内嵌版本一致，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) |

```powershell
# 仅语法语义检查，不产出 exe
powershell -ExecutionPolicy Bypass -File .\REM-GUI-src\build_csharp.ps1 -CheckOnly

# 单元测试（148 条纯逻辑断言）
powershell -ExecutionPolicy Bypass -File .\REM-GUI-src\build_csharp.ps1 -Test

# 正式构建，产物为 dist\REM-GUI.exe
powershell -ExecutionPolicy Bypass -File .\REM-GUI-src\build_csharp.ps1
```

## 目录结构

```text
REm3u8可视化界面/
├─ dist/                        # 构建输出（不入库）
├─ REM-GUI-src/
│  ├─ csharp/
│  │  ├─ Core/                  # JSON、文本、提示条
│  │  ├─ Engine/                # 参数拼装、进程管理
│  │  ├─ Services/              # 配置、路径、HTTP、预检、更新
│  │  ├─ UI/                    # 窗体与自绘控件
│  │  └─ Interop/               # Win32 调用
│  ├─ tests/CoreTests.cs        # 单元测试
│  ├─ tools/                    # 冒烟与端到端脚本
│  ├─ build_csharp.ps1          # 构建脚本
│  ├─ update.json               # 版本清单模板（构建时内嵌）
│  └─ 使用说明.txt
├─ THIRD_PARTY_NOTICES.md
├─ LICENSE
└─ README.md
```

## 实现约束

工程刻意保持小而清晰，以下约束在构建与评审中强制执行：

- **单向依赖** `UI → Services → Engine → Core`，Engine 层不引用任何 UI 类型
- **不引入 C# 6+ 语法**：不使用 `?.`、`$""`、`nameof`、`out var`，以适配系统自带的 in-box 编译
- **失败必须可见**：工具缺失、目录不可写、参数非法一律前置报错，禁止静默降级
- **密钥不落明文**：配置经 DPAPI 加密，日志与界面统一掩码

## 依赖与许可

| 组件 | 版本 | 许可 | 角色 |
| :-- | :-- | :-- | :-- |
| [N_m3u8DL-RE](https://github.com/nilaoda/N_m3u8DL-RE) | 0.6.0 | MIT | 下载引擎 |
| [FFmpeg](https://ffmpeg.org/) | N-94813 | LGPL v3 | 合并与混流（构建未启用 GPL 组件） |
| .NET Framework | 4.8 | Microsoft | 运行时，随系统提供 |

本项目通过独立进程调用使用上述工具，不链接其库，自身代码采用 MIT 许可。各组件的许可全文与合规说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 使用边界

> **本项目不提供任何 DRM 绕过能力。**
> 仅支持 HLS AES-128 手动密钥，不含 CENC / Widevine / PlayReady 等受保护方案。
> 请仅下载你拥有合法权限的内容，并遵守所在地区法律法规与网站服务条款。

## 免责声明

本软件按「原样」提供，不附带任何明示或暗示的保证。作者不对因使用本软件产生的任何直接或间接损失承担责任。任何非法或侵权使用行为均与作者无关，使用者需自行承担全部法律责任。

## 许可证

[MIT License](LICENSE) © 2026 binglanziyu

<div align="center">

如果这个工具替你省下了翻文档的时间，点个 Star 就够了。
