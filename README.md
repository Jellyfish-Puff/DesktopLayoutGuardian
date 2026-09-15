# 桌面布局（开发中）

这是一个面向 Windows 11 单显示器切换场景的桌面图标布局管理器。目前处于第二阶段：布局保存与恢复测试。

## 当前版本能做什么

- 读取 Windows 当前活动显示路径；
- 显示显示器名称、分辨率、缩放、刷新率、方向和输出类型；
- 区分常见实体显示器与间接/虚拟显示器；
- 同时生成精确身份和兼容身份，用于分析 UU 超级屏设备标识是否稳定；
- 监听分辨率、显示设备和系统设置变化，等待 2.5 秒稳定后重新采集；
- 将每次采集追加到 `%LOCALAPPDATA%\DesktopLayoutGuardian\diagnostics\display-events.jsonl`；
- 常驻系统托盘。
- 通过 Windows 官方 `IFolderView` Shell 接口读取桌面图标身份与坐标；
- 为当前显示环境手动保存一份图标布局；
- 再次保存同一方案前自动备份旧版本；
- 手动恢复已保存布局；
- 切换到保存过的显示环境后自动恢复；
- 未保存过的显示环境不会移动图标；
- 新增图标保持当前位置，不会覆盖进旧方案，直到用户再次保存。

当前版本只支持恰好一个活动显示器。刷新率不参与布局匹配；显示器身份、分辨率、缩放和方向参与匹配。

## 数据位置

- 显示诊断日志：`%LOCALAPPDATA%\DesktopLayoutGuardian\diagnostics\display-events.jsonl`
- 当前布局方案：`%LOCALAPPDATA%\DesktopLayoutGuardian\profiles`
- 布局历史版本：`%LOCALAPPDATA%\DesktopLayoutGuardian\history`

## 开发构建

需要 .NET 10 SDK：

```powershell
dotnet build .\src\DesktopLayoutGuardian\DesktopLayoutGuardian.csproj
```

## 布局测试步骤

1. 在一个显示环境中整理好桌面图标。
2. 启动程序并点击“保存当前布局”。
3. 可以先轻微移动一个不重要的快捷方式，再点击“恢复已保存布局”验证。
4. 分别在笔记本内屏、ROG 外接屏和 UU 超级屏下保存各自布局。
5. 保持程序在托盘运行并切换屏幕，等待约 5 秒观察自动恢复。

自动排列图标必须关闭；将图标与网格对齐可以保持开启。
