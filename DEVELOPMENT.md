# DesktopLayoutGuardian 开发说明

本文档保存项目的开发上下文、关键约束和发布流程。添加新功能前，应先阅读本文档和 `CHANGELOG.md`，避免破坏已经过实机验证的行为。

## 1. 产品定位与当前边界

DesktopLayoutGuardian 是 Windows 11 单显示器桌面图标布局管理器。目标场景是在以下环境之间切换时，自动恢复各自的图标坐标：

- 笔记本内置屏幕；
- 外接实体显示器；
- UU 远程超级屏等间接或虚拟显示器。

v1.0.0 只支持“恰好一个活动显示器”。当前不应为了多屏兼容而改变已经稳定的单屏匹配与恢复流程；多屏支持需要单独设计数据模型和完整测试。

已经实机验证的设备包括天马笔记本内屏、ROG XG27AQDMES 和 UU 超级屏。UU 超级屏在 Windows 中可表现为名为 `S2719DGF` 的虚拟显示器，重复连接时当前识别信息稳定。

## 2. 不可破坏的安全约束

1. 未保存过的显示环境绝不能自动移动桌面图标。
2. 恢复时只移动方案中仍然存在的图标；后来新增的图标保持当前位置。
3. 桌面开启“自动排列图标”时拒绝恢复，并提示用户关闭。
4. 每次自动或手动恢复前必须保存磁盘级恢复快照。
5. 恢复、保存、撤销和方案修改使用同一桌面操作互斥锁，不能并发执行。
6. 显示环境变化必须经过防抖与二次稳定性采集，避免 UU 超级屏初始化期间误恢复。
7. 方案、设置和恢复快照继续使用原子写入；删除方案必须移入 `deleted`，不能直接永久删除。
8. 关闭主窗口只隐藏到托盘；托盘“退出”才真正结束后台保护。
9. 刷新率不参与布局匹配。显示器身份、分辨率、缩放和方向参与匹配。
10. 旧版本方案与设置需要保持向后兼容；数据结构升级时增加 `SchemaVersion` 并提供容错默认值。

## 3. 显示环境识别规则

`DisplayConfigurationService` 使用 Windows `QueryDisplayConfig`、`DisplayConfigGetDeviceInfo`、显示器 DPI 与监视器信息采集活动路径。

每块显示器生成两种身份：

- 精确身份：设备路径、适配器路径与目标 ID 的哈希；
- 兼容身份：实体/虚拟类型、友好名称、输出技术和 EDID 信息的哈希。

配置标识包含精确身份、分辨率、缩放、方向和主显示器状态。刷新率只用于诊断展示，不参与配置标识。虚拟显示器识别会参考间接显示输出技术和设备路径中的 virtual、indirect、IDD、remote、UU 等特征。

方案匹配优先精确身份，必要时使用兼容身份；如果存在多个兼容候选，应稳定选择最近更新的方案。

## 4. 桌面图标读写

`DesktopIconLayoutService` 通过 Windows Shell 的 `IFolderView`、`IShellBrowser`、`IShellItem` 等 COM 接口工作，不通过修改注册表模拟图标位置。

图标身份优先使用桌面绝对解析名称，显示名称只作为回退。恢复通过 `SelectAndPositionItems` 一次性定位已匹配图标。所有相关调用必须位于 STA 线程，并正确释放 PIDL、IUnknown 指针和 COM 对象。

桌面操作可能在 Explorer 重启或显示切换期间短暂失败。调用层保留有限次数重试，但不能无限循环或在 UI 线程长时间阻塞。

## 5. 主要代码职责

- `MainWindow.xaml/.cs`：界面、显示变化消息、防抖、稳定性确认和操作编排。
- `App.xaml/.cs`：单实例、系统托盘、后台启动与窗口唤醒。
- `DisplayConfigurationService`：活动显示路径、DPI、身份与配置标识。
- `DesktopIconLayoutService`：桌面图标身份、坐标采集与恢复。
- `DesktopLayoutProfileStore`：当前方案、历史、重命名、安全删除和清理。
- `DesktopLayoutRecoveryStore`：最近一次恢复前的可撤销快照。
- `DesktopLayoutPreviewService`：由壁纸、图标与坐标合成隐私友好的桌面预览。
- `ApplicationSettingsStore`：设置读取、迁移与原子保存。
- `DiagnosticLogService`：诊断报告、JSONL 日志与轮换。
- `StartupRegistrationService`：当前用户的 Windows 登录启动项。

## 6. 本地数据布局

数据根目录为 `%LOCALAPPDATA%\DesktopLayoutGuardian`：

```text
profiles/                 当前显示方案
history/                  覆盖保存前的历史版本
previews/                 纯桌面布局预览
deleted/                  安全删除的方案
recovery/                 最近一次恢复前快照
diagnostics/              显示事件日志
settings.json             应用设置
```

预览引用可能来自当前方案、历史或安全删除记录。清理无引用预览时必须同时检查这些来源。

## 7. UI 与资源约定

- WPF 目标框架：`net10.0-windows`。
- 中文字体优先使用 `Microsoft YaHei UI`，启用 ClearType、像素对齐和 Display 文本格式。
- 主色为 `#4F73FF`，整体采用轻量 Fluent 风格。
- 应用图标位于 `src/DesktopLayoutGuardian/Assets/AppIcon.png` 和 `AppIcon.ico`。
- 正式版本号同时存在于项目文件和 `MainWindow.xaml` 的侧栏、关于区域；发布前必须一起更新。
- 方案预览不能直接截取屏幕，以免把其他窗口或隐私内容写入图片。

## 8. 构建与自动测试

需要 .NET 10 SDK：

```powershell
dotnet build .\src\DesktopLayoutGuardian\DesktopLayoutGuardian.csproj -c Release
dotnet run --project .\src\DesktopLayoutGuardian.CoreTests\DesktopLayoutGuardian.CoreTests.csproj -c Release
```

核心测试不依赖第三方测试框架，覆盖设置原子写入、方案兼容与历史、预览隐私、安全删除、清理、恢复快照和日志。

发布小型、依赖 .NET Desktop Runtime 的单文件版本：

```powershell
dotnet publish .\src\DesktopLayoutGuardian\DesktopLayoutGuardian.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\artifacts\DesktopLayoutGuardian-v1.0.0-win-x64
```

## 9. 实机回归清单

涉及显示识别、恢复逻辑或 Shell 接口的改动，至少验证：

1. 现有方案能够继续读取和恢复。
2. 保存、手动恢复和撤销在当前显示环境正常工作。
3. 未保存的新分辨率不会移动图标。
4. ROG 外接屏与 UU 超级屏来回切换后分别恢复正确方案。
5. UU 超级屏重复连接后仍匹配同一身份。
6. 新增图标不会被旧方案覆盖位置。
7. 关闭窗口后只剩一个托盘实例；重复启动会唤醒原窗口。
8. 退出、通知、开机启动和三档检测策略行为正常。

旧版的详细人工清单仍保存在 `docs/testing-v0.3.0.md`，后续可按版本新增测试记录。

## 10. 发布流程

1. 更新 `DesktopLayoutGuardian.csproj`、界面版本文字和 `CHANGELOG.md`。
2. 确认工作区只包含本次变更，执行 Release 构建和核心测试。
3. 生成 win-x64 单文件，并确认 EXE 中包含正确应用图标。
4. 计算 SHA-256，随 Release Notes 发布。
5. 提交代码并推送 `main`。
6. 创建带注释标签 `vX.Y.Z`，发布对应 GitHub Release 和 EXE 附件。
7. Release 发布后再次确认下载链接、文件名、大小与校验值。

## 11. 后续方向

优先保持单屏版本稳定，再考虑：

- 多显示器组合方案与每屏坐标系；
- 更完善的异常恢复和诊断导出；
- 安装包与自动更新；
- 更多 Windows 11 设备与远程显示协议兼容性。

方案导入导出暂不列入规划：换电脑或重装系统后桌面图标身份通常已经变化，重新保存方案更可靠。

