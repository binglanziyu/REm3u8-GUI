# 历史快照存档（legacy）

本目录存放 REm3u8-GUI 在 3.0.1 阶段、尚未纳入 Git 的过渡态源码与交付副本。这些内容在仓库历史上从未被跟踪，为避免丢失，统一归档到 `legacy` 分支，**不进入 `main`**。

- 分支：`legacy`（从 `main` 的 `42f7be9` 分出，仅追加归档提交，不改写任何已有历史）
- 归档时间：2026-11-19
- 上游来源目录：`F:\1gui\REM-GUI_archive_20261118`（本地备份目录）

## 目录结构

| 路径 | 说明 |
|:--|:--|
| `snapshots/20261118_182852/` | 备份目录 `REM-GUI-src.bak.20261118_182852` 中，与 Git 3.0.1 末次提交 `d7f7863` 内容不同的 15 个文件 |
| `snapshots/20261118_pre_refactor/` | 备份目录 `REM-GUI-src.bak.20261118_211630_pre_refactor` 中，与 `d7f7863` 内容不同的 17 个文件（重构前最后一版，比上一份多 `csharp/Engine/ArgBuilder.cs` 与 `csharp/UI/ThinScroll.cs`） |
| `REm3u8-GUI-fixed/` | 3.0.1 修复副本全量（43 个文件，含 `fixes-batch1.patch`、`修复说明.md`、`CHANGELOG.md` 及源码树） |
| `REm3u8-GUI-3.0.1-fixed-20261117.zip` | 同上的打包形态（`Downloads\REm3u8-GUI-3.0.1-fixed-20261117.zip` 原件） |
| `readme-drafts/README.new.md` | 3.0.0 时期 README 改写草稿（版本徽章为 3.0.0） |
| `readme-drafts/README.v3.0.0.old.md` | 被上一份草稿替代的原始 3.0.0 README |

## 归档范围判定

- 只收录**与已入库内容存在实质差异**的文件，逐文件以 SHA-256 比对确认；与 `d7f7863` 完全一致的快照（`REM-GUI-src.bak.20261118_172928`）不再重复归档。
- `REM-GUI-src.bak.20261118_184647_pre7` 与 `_182852` 逐文件哈希完全一致，属同一份内容，仅归档一份。
- **未收录**各快照下的 `temp/` 目录：其内容为探针测试残留（`.ts`/`.m3u8`/日志/`verify_dl.exe` 等，约 23 MB），非源码，无回溯价值。
- **未收录** `REM-GUI.exe.bak.20261118_190926`（25 MB 二进制）：`main` 的 `.gitignore` 一贯不跟踪 `dist/*.exe`，二进制不进仓库。

## 使用方式

需要回溯某个过渡态时，直接在该分支下读取对应目录即可，例如：

```
git show legacy:legacy/snapshots/20261118_pre_refactor/csharp/UI/MainForm.cs
```

或在本地检出：

```
git worktree add ../REM-GUI_legacy legacy
```

`main` 分支保持为 3.1.1 的干净发布状态，与这些历史快照互不影响。
