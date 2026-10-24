# 开发交接 — 2026-10-24

基础框架之外，已增加只读显示器探测。校准仍未应用到屏幕。

## 已交付

- DCCW++ 独立工程，C# / .NET 10 / WinUI 3，GPL-3.0-only。公开仓库为 [Water-Run/DCCWpp](https://github.com/Water-Run/DCCWpp)。
- Core、Windows、App、CLI 和测试项目。
- 校准草稿、预设、参考照片、设置四个界面区域。
- 只读显示器探测：QueryDisplayConfig 活动路径、SDR/WCG/HDR 颜色模式、当前默认 ICC 配置文件名。结果可保存为观察记录。没有写入显示配置的接口。
- 简中、繁中、英语，跟随系统及运行时文字更新机制。
- 预设保存、导入、导出、严格格式校验、重复字段拒绝和原子写入；公开 JSON schema。
- Agent CLI：JSON、退出码、隔离目录、预设操作、显示器与启动状态、应用预演。
- MSIX 基础：独立 GUI/CLI 入口、执行别名、默认关闭的 StartupTask、自包含运行时、原生图标资源。
- ImageGen 位图参考稿、独立绘制的可编辑 SVG、PNG/ICO 多尺度资源和再生成脚本。
- 开发计划、商店发布说明、CLI 文档、摄影选片需求、CI 工作流。

## 实际验证结果

- Debug 未打包 WinUI 和 CLI 构建成功。
- Release MSIX 构建成功；产物为 `artifacts/packages/DccwPlus.App_0.1.0.0_x64_Test/DccwPlus.App_0.1.0.0_x64.msix`，约 127.2 MiB。
- 23 项核心测试通过。
- 20 项 CLI 进程检查通过，使用临时目录，未改变显示器设置。
- 本机只读探测返回三台活动显示器，拓扑 `extend`，颜色模式均为 `sdr`，`hdrActive` 为 false。其中一台返回了当前 ICC 文件名，另外两台没有默认配置文件名。这不是测量，也没有写入显示设置。
- 包内 GUI/CLI、运行时、启动声明、console 执行别名、图标、PRI、GPL 文件验证通过。
- WinUI 进程启动成功，创建 `DCCW++` 窗口并保持响应。
- 已修复未打包启动时误用 UWP 语言接口的问题，改为 `Microsoft.Windows.Globalization.ApplicationLanguages`。

## 未验证或未完成

- 界面自动化工具错误地将 DCCW++ 窗口归属到 OneDrive，重新绑定仍返回 `window id ... no longer belongs to ... OneDrive.App.exe`，因此截图、点击操作、语言切换和预设界面的实际交互验收未完成。
- 未安装 MSIX，尚未验证安装包中的真实 CLI 别名、自启动、升级和卸载；未运行 WACK、未做 ARM64 真机测试。
- 清单使用开发身份，包未签名；未关联或发布到 Microsoft Store。
- 校准引擎、ICC 写入、效果预览、超时/崩溃恢复、持久应用尚未实现。HDR 只被读取，不会因此改走另一条校准路径。启动任务只记录状态；CLI apply 返回不可用，预演和观察保存都不会改变屏幕。
- 参考摄影素材尚待提供。

## 恢复工作入口

M1 的只读部分已经落地：活动路径、颜色模式和当前 ICC 文件名可以查询并保存。下一步是可验证的恢复；测试不能改本机显示设置。恢复确认前，不把草稿编辑器升级为真正应用按钮。

本地开发：`./scripts/Build.ps1 -Test`。

包构建：`./scripts/Build.ps1 -Configuration Release -Package -Test`。

CLI 检查：`python scripts/smoke_cli.py artifacts/cli/win-x64/dccwpp.exe`。

包检查：`python scripts/verify_package.py artifacts/packages/DccwPlus.App_0.1.0.0_x64_Test/DccwPlus.App_0.1.0.0_x64.msix`。
