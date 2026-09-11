# 译制录音棚工作台（DubStudio）

跨平台桌面应用：C# / .NET 8 / .NET MAUI（Windows、macOS）。工程数据存于 SQLite，
媒体解码/探测全部通过**独立的 ffmpeg / ffprobe 子进程**完成，应用本身不内置任何原生解码器。

## 解决方案结构

```
DubStudio.sln
├── src/DubStudio.Core        # 核心库（net8.0，无 UI 依赖，可单测）
│   ├── Models/               # SQLite 实体 + 稳定编号规则 StableIds
│   ├── Data/                 # StudioDatabase（一工程一 .dsproj 数据库）
│   ├── Import/               # 剧本解析（txt/json）与导入模型
│   ├── Services/             # 导入、译文版本、录音条次、导演选用、扫描、归档
│   ├── Services/Revision*    # 混录后修订：影响分析、拆分合并、撤回、修订包交付、交叉测试
│   ├── Media/                # FFmpeg 独立进程、VFR 检测、文件摘要
│   └── Timecode/             # 帧率与 SMPTE 时间码（含 29.97/59.94 丢帧）
├── src/DubStudio.App         # MAUI 桌面端（WinUI / Mac Catalyst）
│   ├── Pages + ViewModels    # 工程、剧本与口型、录音条次、导演选用、待复核、扫描归档
│   ├── Services/             # 工程会话、桌面文件对话框
│   └── Platforms/            # Windows / MacCatalyst 入口与原生对话框
└── tests/DubStudio.Core.Tests# xUnit 全量业务测试
    └── samples/              # 两种格式的示例剧本（仓库 /samples）
```

## 构建与运行

前置：.NET 8 SDK、MAUI 工作负载（`dotnet workload install maui`）、系统 PATH 中的 ffmpeg 与 ffprobe。

```bash
dotnet build DubStudio.sln
dotnet test  DubStudio.sln

# Windows 桌面
dotnet build src/DubStudio.App/DubStudio.App.csproj -f net8.0-windows10.0.19041.0
# macOS（Mac Catalyst）
dotnet build src/DubStudio.App/DubStudio.App.csproj -f net8.0-maccatalyst
```

## 业务规则与需求对应

| 需求 | 实现 |
| --- | --- |
| 角色台词 / 口型关键点 / 录音条次 / 导演选用互相关联 | `ScriptLine`、`Take(LineId, TextVersionId)`、`DirectorPick(LineId, TakeId)` 外键式关联 |
| SQLite 存项目 | `StudioDatabase`，一个工程 = 一个 `.dsproj` SQLite 文件；唯一索引保证编号稳定 |
| 独立 FFmpeg 进程解码 | `IProcessRunner` + `SystemProcessRunner`，`FfmpegMediaService` 仅以进程调用 ffmpeg/ffprobe |
| 按集/场/角色/原语句稳定编号 | `StableIds`：`E01-S012-C_田中-03`；行键含集、场、角色码、原文哈希与同句出现序 |
| 译配人员记录入点/闭口点/重音/长度限制 | `ScriptLine.InPointMs/MouthCloseMs/AccentMs/MaxDurationMs`，闭口点不得早于入点 |
| 文字每次改动形成版本 | `LineTextVersion` 不可变，相同文本不产生新版本 |
| 录音师保存演员/麦克风/时间段/文件摘要/现场备注 | `RecordingService.AddTakeAsync`，保存时计算 SHA-256 与大小 |
| 导演只能从实际条次中选择 | `DirectorService.PickTake` 校验条次存在、归属正确、未被否决、音频文件存在；每句至多一条 |
| 移位/换句后旧录音待复核，不自动适用 | `ReviewWorkflow.InvalidateLine`：条次→NeedsReview、选用→PendingReview、入复核队列 |
| 交付音频缺失扫描 | `AudioScanService`：缺失/大小变化/SHA-256 变化/旧版本/已否决/待选用 |
| 工程归档 | `ArchiveService`：zip（project.dsproj + manifest.json + audio/），归档后往返校验 |
| 可变帧率 | ffprobe 包 PTS 间隔变异系数检测 VFR；时间统一以毫秒为权威，帧号按标称帧率换算 |
| 同角多句 | `CharacterSlot` 与同(场,角色,原文)`Occurrence` 区分重复台词 |
| 重名角色 | 同名无限定视为同一人；`角色@限定` 生成不同稳定码，导入时给出重名警告 |
| 外部文件改动测试 | 录音存 SHA-256/大小，扫描发现外部覆写即入复核（见测试） |

## 混录后剧本修订（修订包）

已进入混录后，译配编辑通过 `RevisionDraft`（JSON/文本，见 `samples/episode01.rev.json`）提交新稿。
`RevisionPackageService.Analyze` 按**稳定语句身份**（集|场|角色|原文哈希|出现序；原文改写时用
字符二元组相似度兜底识别“文字微调”而非误判删/增）把每条差异圈为：

| 差异类型 | 默认圈定处置 | 说明 |
| --- | --- | --- |
| 文字微调 `TextTweak` | 需补录 | 译文/原文变了，旧录音念的是旧词 |
| 时长变化 `DurationChanged` | 仅需重剪 | 入点/闭口点/长度限制变，文字未变，录音可沿用 |
| 文字+时长 `TextAndDuration` | 需补录 | 两者同时变化 |
| 删句 `Deleted` | 退役删句 | 稳定身份在新稿消失，录音保留但不再交付 |
| 新增句 `Added` | 等待补录 | 无历史录音的新身份 |
| 拆分 `Split` / 合并 `Merged` | 源句补录/新片段补录/合并句重剪 | 显式操作，写入 `LineGenealogy` 血缘 |

- **声音导演逐条确认处置**：`SetDisposition`；与系统建议不同（含“可以保留”）必须填写**保存依据**。
- **批量判断也要展开保存依据**：`ApplySuggestedDispositions(pkg, 依据)` 把未决定项按建议圈定，
  每条落 `DecisionBasis = 批量：<依据>（系统建议…）`，已逐条决定的不被覆盖。
- **分析阶段不动在制数据**；`ConfirmPackage` 要求全部条目已决定，才把译文版本/时序/退役/平移落库，
  旧条次按处置转“需补录（待复核）”，重剪条次保留录音并入复核，保留项关闭影响。
- **时间轴整体平移**：`RevisionDraft.TimelineShiftMs` 平移关键点与条次时间段，圈定为“仅需重剪”。
- **修订撤回** `WithdrawPackage`：草案撤回仅清分析；已确认/已交付撤回按逐条快照恢复译文版本、
  时序、句状态、条次/选用状态并回收本包复核项；已生成的交付目录**保留**作追溯，只能用新修订包重新交付。
- **已交混音不原位替换**：`RevisionDeliveryService` 每次交付写独立目录 `REV-n_vN/`，
  复制当前有效音频并附 `changelist.json` / `changelist.txt`（逐条差异、处置、依据、新旧文、被挡条次）。
- **角色换演员** `CastService.SetActor`：历史录音全部保留；旧演员条次入复核且
  `IsCurrentActor=false`，交付修订包时计入 `CastBlocked` 绝不混入新批次；以旧演员名义新录也自动入复核。
- **两个修订包交叉测试** `RevisionCrossCheckService.CrossCheck`：按身份找出退役/保留硬冲突、
  拆分合并结构冲突与“一个补录一个重剪”等软警告。

数据模型新增 `RevisionPackage / RevisionChange / LineGenealogy / CharacterCast / MixDelivery`，
`ReviewItem.PackageId` 归属修订包；老数据库打开时由 `CreateTable` 自动补齐新列。

## 时间码与 VFR

- 内部全部用毫秒存储关键点和条次时间段，避免 VFR 下帧号漂移。
- `FrameRate` 支持 23.976/24/25/29.97/30000/1001/59.94；29.97、59.94 按 SMPTE 丢帧换算。
- VFR 判定：优先比较逐包 PTS 间隔的变异系数（阈值默认 0.05），并以 `r_frame_rate` 与
  `avg_frame_rate` 差异做粗检。

## 剧本格式

见 `samples/episode01.txt` 与 `samples/episode01.json`。文本格式：

```
FPS 29.97          # 可选
VFR                # 可选，标记可变帧率
EPISODE E01 标题
SCENE S001 场景标记
角色名@限定: 台词   # @限定 用于重名角色
（无冒号开头的行并入上一句）
# 注释
```

重新导入是幂等的：编号不变；新句新增，消失的句退役，复现的句恢复并触发复核，
移位/换句一律让旧录音进入“待复核”而不是静默沿用。

## 测试覆盖（DubStudio.Core.Tests）

- 稳定编号、同角多句、重名角色、重复台词出现序
- 重复导入幂等、换句/移位/恢复 → 旧条次与选用待复核
- 译文不可变版本、口型约束（闭口≥入点）、长度超限
- 导演只能选真实存在、归属正确、未否决且有文件的条次；每句一条选用
- VFR：CFR/VFR 的 PTS 变异系数、帧率粗检、ffprobe JSON 解析
- FFmpeg：用假进程执行器验证独立进程参数、成功哈希输出与失败报错
- 交付音频缺失、外部覆写（SHA-256 变化）入复核
- 归档内容、往返重开、缺/改文件计数、仅数据库归档
- 混录后修订：稳定身份四分类（微调/时长/删/增）、模糊改写识别、导演逐条与批量保存依据、
  确认生效、拆分/合并血缘、时间轴整体平移、草案/已确认/已交付撤回快照回退、
  修订包非破坏式交付（独立 REV-n_vN 目录 + 变更清单）、换演员历史录音隔离、两个修订包交叉测试
