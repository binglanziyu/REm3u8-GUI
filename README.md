<div align="center">

# REm3u8-GUI

**把 N_m3u8DL-RE 的参数表，变成一张能看懂的表单。**

![Version](https://img.shields.io/badge/version-3.0.0-2ea043?style=flat-square)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?style=flat-square)
![Runtime](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4?style=flat-square)
![License](https://img.shields.io/badge/license-MIT-3DA639?style=flat-square)

[特性](#特性) · [快速开始](#快速开始) · [从源码构建](#从源码构建) · [数据与日志](#数据与日志) · [许可](#依赖与许可)

</div>

REm3u8-GUI 是 [N_m3u8DL-RE](https://github.com/nilaoda/N_m3u8DL-RE) 的 Windows 图形前端。产物是一个 `REM-GUI.exe`：下载引擎与 FFmpeg 已编进程序内部，首次启动自动释放到程序目录，不需要安装包、不需要 .NET SDK、不需要另外下载任何附件。

它的重点不是「多一个界面」，而是把命令行里最容易踩的三件事接过来：**开跑前预检、失败后自救、出错时说实话**。

## 特性

### 交付与运行

| 能力 | 说明 |
|:--|:--|
| **单文件交付** | `REM-GUI.exe` 内嵌 N_m3u8DL-RE 与 FFmpeg，程序目录里被误删哪个就重新释放哪个 |
| **零运行时依赖** | 只吃系统自带的 .NET Framework 4.8，Windows 10 / 11 开箱可跑 |
| **单实例接管** | 重复双击不会开出两个窗口，新链接直接转交给已运行的实例排队 |
| **高 DPI 适配** | PerMonitorV2 感知，界面按实际 DPI 等比缩放，跨显示器拖动不糊不错位 |
| **崩溃留证据** | 未捕获异常写进 `logs\diag.log` 并弹窗告知路径，不是无声消失 |

### 下载链路

| 能力 | 说明 |
|:--|:--|
| **开跑前预检** | 先看链接格式是否合法、清单能不能读到、是否是多码率清单、分片有多少、首尾分片是否可达、保存目录是否真的能写；能提前判死的绝不丢给引擎去报一个退出码 |
| **坏片自动补片** | 引擎报分片校验不通过或合并失败时两段自救：先重拉清单续跑补齐真分片，仍缺则用前后相邻分片顶替让它能合并；缺口占比超过 30% 才放弃补片并如实报错，比例以内一律补，不设绝对片数上限 |
| **失败判定不只看退出码** | 引擎在某些情况下退出码为 0 但日志已报错，这类任务会被识别为失败，不会当成完成去清掉续传文件与分片 |
| **断点续传** | 中断后把 `.resume.json` 拖回窗口即可续传；分片目录已被清空时明确告知将从头下载，而不是在空目录里假装续传出 0 个分片 |
| **多码率选择** | 预检发现多清晰度清单时给出可选列表，选定后再进入下载 |

### 参数与安全

| 能力 | 说明 |
|:--|:--|
| **手动 AES-128** | 填 KEY 与 IV 即可（HEX / Base64 / 文件三种写法）；都留空时不下发任何解密参数，明文流按清单原样处理 |
| **常用参数全覆盖** | 容器格式、合并方式、轨道选择、线程数、限速、代理、请求头、超时与重试、下载后是否删分片、是否校验分片数 |
| **凭证不落明文** | 配置经 DPAPI 加密落盘；命令预览、界面输入框、日志文件统一打码 Cookie / Authorization / `?token=` / HLS 密钥 |
| **引擎可管理** | 内置引擎之外还能列出目录中的其他版本；下载安装带 SHA256 校验、解压防目录穿越、旧版本自动回收并只保留一个历史版本 |
| **更新清单内嵌** | 版本清单编进程序（`update.json`），无需外部文件；查询走 GitHub API 并附镜像加速。引擎支持一键下载安装（SHA256 校验 + 解压防目录穿越）；程序自身的更新只做提示与跳转发布页——运行中的 exe 无法自我替换 |

## 一次下载发生了什么

```text
粘贴链接 / 拖入 m3u8·mpd 文件 / 拖入 .resume.json
        │
        ├─ 预检 ── 格式·清单·分片·目录可写性 ──► 致命问题在此中止并说明原因
        │
        ├─ 参数拼装 ── 明文流不下发解密参数，凭证只以掩码形式出现在预览里
        │
        ├─ 起独立进程 ── 实时解析进度/速度/剩余时间，日志落盘前统一打码
        │
        ├─ 结束判定 ── 退出码 + 日志失败信号双条件
        │      ├─ 失败 ──► ① 重拉清单续跑补齐 → ② 相邻分片顶替 → 仍不行则保留续传并报错
        │      └─ 成功 ──► 清续传文件、清分片目录，标记完成
        │
        └─ 产物 ── 保存目录下的成品文件
```

## 快速开始

```text
1. 双击 REM-GUI.exe
2. 粘贴视频页面链接，或把 m3u8 / mpd 文件拖进窗口
3. 选保存目录，点「开始下载」
```

需要解密的资源，打开「高级设置」→ 解密区填 KEY 与 IV。加密方式固定为 AES-128，不用选引擎、不用额外程序。

首次启动时程序会把内嵌的 N_m3u8DL-RE 与 FFmpeg 释放到 `REM-GUI.exe` 所在目录（该目录不可写时退到 `%LOCALAPPDATA%\REM-GUI\tools`），所以首次启动会比之后慢一点。

## 环境要求

| 项 | 要求 |
|:--|:--|
| 系统 | Windows 10 / 11（x64） |
| 运行时 | .NET Framework 4.8（系统自带） |
| 其他 | 无 |

## 从源码构建

只用 Windows 自带的 csc，不需要 .NET SDK，也不需要 Visual Studio。仓库不收录这两个二进制，构建前先把它们放进 `dist\` 顶层（脚本只看顶层，不递归，缺文件直接报错退出）：

| 文件 | 版本 | 来源 |
|:--|:--|:--|
| `N_m3u8DL-RE.exe` | 0.6.0 | [N_m3u8DL-RE Releases](https://github.com/nilaoda/N_m3u8DL-RE/releases) |
| `ffmpeg.exe` | N-94813-g85386c36e3 | 需与发布版本内嵌的一致，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) |

```powershell
# 仅语法语义检查，不产出 exe
powershell -ExecutionPolicy Bypass -File .\REM-GUI-src\build_csharp.ps1 -CheckOnly

# 单元测试（198 条纯逻辑断言）
powershell -ExecutionPolicy Bypass -File .\REM-GUI-src\build_csharp.ps1 -Test

# 正式构建，产物为 dist\REM-GUI.exe
powershell -ExecutionPolicy Bypass -File .\REM-GUI-src\build_csharp.ps1
```

构建脚本会做三件事：检查版本号在 `AssemblyInfo` / `app.manifest` / `update.json` / 内置默认清单四处是否一致；把引擎、FFmpeg、`update.json`、图标作为资源编进 exe；产物自检（内嵌资源是否齐全、清单是否含 `engine` 与 `self` 段）。

补充验证脚本（需先完成正式构建）：

```powershell
# 冒烟：启动、窗口可见耗时、内存与空闲 CPU、截图、退出后目录是否残留
powershell -ExecutionPolicy Bypass -File .\REM-GUI-src\tools\smoke_test.ps1

# 端到端：真实拉一个测试流，校验进度读数与产物
powershell -ExecutionPolicy Bypass -File .\REM-GUI-src\tools\e2e_test.ps1
```

> 当前仓库尚未发布 Release，没有可直接下载的成品 exe，请按上面的步骤自行构建。

## 目录结构

```text
REm3u8-GUI/
├─ dist/                        # 构建输出与二进制放置处（exe 不入库）
├─ REM-GUI-src/
│  ├─ csharp/
│  │  ├─ Core/                  # JSON、文本、掩码、提示条（零依赖纯函数）
│  │  ├─ Engine/                # 参数拼装、进程与输出管道
│  │  ├─ Services/              # 配置、路径、HTTP、预检、补片、引擎与更新
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

## 数据与日志

| 位置 | 内容 |
|:--|:--|
| `REM-GUI.exe` 所在目录 | `logs\re_*.log`（任务日志）、`logs\diag.log`（诊断日志）、释放出的引擎与 FFmpeg |
| `%LOCALAPPDATA%\REM-GUI\` | `config.json`（DPAPI 加密的配置，首次改动设置时生成）、`engine\`（下载的引擎版本）、`tools\`（程序目录不可写时的兜底位置）、`drop.txt`（单实例转交的链接） |
| 保存目录 | 成品文件、任务进行中的 `.resume.json`、以及下载期间的 `.parts\` 分片目录 |

日志与配置不散落在系统其他位置：程序搬到哪，日志就跟到哪。

## 实现约束

工程刻意保持小而清晰，以下约束在构建与评审中强制执行：

- **单向依赖** `UI → Services → Engine → Core`，Engine 层不引用任何 UI 类型
- **不引入 C# 6+ 语法**：不使用 `?.`、`$""`、`nameof`、`out var`，以适配系统自带的 in-box 编译器
- **失败必须可见**：工具缺失、目录不可写、参数非法一律前置报错，禁止静默降级
- **密钥不落明文**：配置经 DPAPI 加密，命令预览、界面与日志统一掩码
- **不静默吞异常**：确需忽略的异常统一走 `Note.Swallow(位置, ex)` 记录出处，而不是空 `catch`

## 依赖与许可

| 组件 | 版本 | 许可 | 角色 |
|:--|:--|:--|:--|
| [N_m3u8DL-RE](https://github.com/nilaoda/N_m3u8DL-RE) | 0.6.0 | MIT | 下载引擎 |
| [FFmpeg](https://ffmpeg.org/) | N-94813 | LGPL v3 | 合并与混流（构建未启用 GPL 组件） |
| .NET Framework | 4.8 | Microsoft | 运行时，随系统提供 |

本项目通过独立进程调用上述工具，不链接其库，自身代码采用 MIT 许可。各组件的许可全文与合规说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

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

</div>
