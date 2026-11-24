# DCCW++ 开发路线图

更新于 2026-11-24。当前基线为 0.2 SDR 开发版本。阶段按依赖关系排序，版本号是目标，实机结果和商店认证决定实际发布进度。

## 产品目标与首发范围

DCCW++ 是面向 Windows 11 的独立 WinUI 3 应用。首发目标是用户能用 DCCW++ 完成原版 DCCW 的 SDR 视觉校准流程，并在 Windows 中保留、查看和恢复结果。安装入口计划是 Microsoft Store，代码采用 GPL-3.0-only。应用保留自己的 Windows 入口和商店标识，通过公开 API 工作。

首发包含 Gamma、显示器亮度和对比度引导、灰阶 RGB 平衡、前后比较、ICC 保存与恢复、ClearType 入口，以及多显示器、预设交换、登录恢复、三语言和 Agent CLI。HDR 和 Advanced Color 提供模式识别及相应的 Windows 校准入口，作为首发范围约定。独立 MHC2 后端另行验证。比较效果以视觉验收为主，面板 Gamma、白点、色域等定量结果只有在仪器测量后才记录。

## 当前基线

| 能力 | 0.2 状态 | 仍缺的证据或实现 |
| --- | --- | --- |
| WinUI 3 向导 | 已接入选屏、Gamma 参考图、亮度、对比度、灰阶、限时比较、保存和 ClearType，可跳过硬件调整。初始窗口和选屏移动按目标显示器工作区定位 | 原版对照、实际点击、多屏定位、缩放及无障碍验收 |
| SDR 写入和 ICC | 已有曲线读回、`vcgt` 生成、默认关联提交和限时撤回预览。关联前后检查安装文件的摘要，回滚检查日志中的摘要 | 标准用户系统加载、撤回关联、退出和重连恢复的实机验收 |
| 多屏和重连 | 已探测活动拓扑和彩色模式，分开当前查询 ID 和持久身份 | 同型号/无序列号屏、扩展和复制切换、换端口的实机验证 |
| HDR 与 Advanced Color 入口 | 区分 SDR、WCG 和 HDR，查询对应的默认 ICC，向导已接入 Windows 设置和 HDR 校准入口 | 核对实际 HDR、WCG 和 MHC2 的显示提示与跳转 |
| 预设与 CLI | 已有草稿参数、绑定保护、重复导入、机器可读的命令发现；应用预设、撤回校准和试用 ICC 都支持预演和请求 ID | 已验证 x64 开发包别名；仍需实机显示事务及装包 GUI 与 CLI 互通验收 |
| 登录恢复 | StartupTask 与逐屏恢复开关已接通。x64 沙盒普通用户可通过装包 CLI 查询初始关闭状态 | 需验证装包后的后台启动、恢复、关闭和兼容性 |
| 语言和照片 | en-US、zh-Hans、zh-Hant，可打开本机照片 | 三语言 UI 验收，内置照片依赖项目所有者供片及许可 |
| 商店包 | 双架构开发 MSIX、对应源码与依赖许可已验证。x64 沙盒已通过普通用户安装、CLI 执行、原地升级保留数据及卸载 | GUI 冷启动、原生 ARM64 安装与运行、Store 签名包升级、DCCW++ 专属商店身份及认证 |

## 开发顺序与交付物

| 阶段 | 交付物 | 进入下一阶段的条件 |
| --- | --- | --- |
| M1 · SDR 实机闭环 | SDR 实机记录，含曲线、ICC、作用域和取消后状态 | 标准用户保存与恢复重复通过，异常退出和多屏状态可解释、可恢复 |
| M2 · 原版流程对齐 | 同机与原版 DCCW 的逐步对照，三语言及 DPI 验收记录 | 校准步骤、可跳过控制、前后比较和 ClearType 均通过验收 |
| M3 · 自动恢复 | 登录、唤醒、拓扑变更，以及逐屏关闭的验收记录 | 实机确认恢复只作用于匹配的 SDR 屏，遇到不匹配时可诊断 |
| M4 · Agent 和预设 | CLI v1 契约、旧数据和装包 GUI/CLI 互通记录 | 可重试、可预演，导入和升级保留兼容性 |
| M5 · 商店发布 | 双架构候选包、相同版本源码、商店材料及测试记录 | DCCW++ 专属身份、Store 签名安装和 Partner Center 认证通过 |

开发包和商店材料可与实机验证并行。先用单台 SDR 显示器建立最小可重复闭环，再扩展到多屏、模式切换及 ARM64。首个公开版本的范围以这些测试实际覆盖的能力为准。

## M1 · 0.3 · SDR 实机闭环

以 0.2 为基线，先验证 Windows 颜色状态能完整恢复。对固定的 Windows build、GPU 驱动和 SDR 显示器组合，记录预览前默认 profile、作用域和逻辑曲线。分别验证取消、超时、确认、异常退出和重新登录后的状态。保存样例应在 Windows 颜色配置页可见。确认前后的 ICC、曲线、读回和视觉观察各自留证。

- 核对系统是否加载 `vcgt`、何时会重载，以及设备上校准加载器的状态。记录彩色管理应用与普通 SDR 应用的差异，验证标准用户可用的 API 及安装路径。发现标准用户权限阻断时，先解决发布后端路径，再启用对应的保存入口。
- 审查安装、关联、提交记录、预设及绑定持久化之间的失败窗口。验证进程在每一步中断后的恢复，以及用户或其他程序在预览时切换 profile 或作用域的处理。记录系统选用的作用域、实际 ICC 关联来源和查询状态。读取过期日志时以原物理目标、当前模式和状态所有权重新校验。
- 拔出显示器时记录待恢复状态，目标重新出现且身份匹配后再恢复。克隆、远程会话、HDR/Advanced Color、目标不明确或驱动拒绝时，返回可操作的提示和稳定原因码。
- 确认 `SetDeviceGammaRamp` 仅在受控 SDR 兼容路径使用。确定回读容差、驱动拒写后的处理，以及休眠、分辨率变化和外部工具覆盖后的预期行为。使用 ICC 和 Windows 关联作为持久化路线。微软记录了 Gamma Ramp 静默拒写、系统覆盖和 HDR 行为未定义等限制。[Gamma Ramp 文档](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp) · [默认 ICC 关联](https://learn.microsoft.com/en-us/windows/win32/api/icm/nf-icm-colorprofilesetdisplaydefaultassociation)

验收以独立的 SDR 显示器完成整轮校准、退出后恢复、确认后重新加载和恢复原关联为准。额外验证撤回已确认校准时，取消和超时可回到撤回前状态，确认可还原前一个 ICC、作用域和曲线。扩展桌面至少验证两台屏幕，确认另一屏的 ICC 和曲线保持原状。模拟测试覆盖失败分支，真实写入仅在人工实机验收中进行。

每个步骤前后都可用 `dccwpp calibration capture --json` 保存全屏幕的只读报告。用 `dccwpp calibration compare <before.json> <after.json> --json` 核对拓扑、ICC 摘要、作用域及逻辑曲线，同时检查未选屏。记录不可读或不支持的目标、会话原因码、Windows 设置所示配置及视觉观察。[采样与比对命令](CLI.md)

## M2 · 0.4 · DCCW 行为对齐与 WinUI 体验

在同一台测试机和同一目标屏对照 Windows 设置中的 `Calibrate display` 流程，记录 Windows build、入口、默认值、跳过行为和保存结果。Windows 设置会针对运行校准工具所在的显示器创建配置文件，多屏流程需明确选屏和移动画布。[Windows 显示颜色设置](https://support.microsoft.com/en-us/windows/hardware/display-graphics/change-display-brightness-and-color-in-windows)

| 原版任务 | DCCW++ 验收 |
| --- | --- |
| 准备与选屏 | 标明目标、位置和色彩模式。把校准画布放在所选屏上。跨屏或拓扑变化时重新验证目标。 |
| Gamma | 可辨识过低、合适、过高的视觉参考。图案在 100%、150%、200% 缩放和全屏按物理像素呈现。支持数值微调、重置和返回。 |
| 亮度与对比度 | 使用独立暗部/高光阶梯，指导用户调显示器控制。支持跳过，提醒用户恢复手动调整过的 OSD 设置。 |
| 灰阶色彩平衡 | RGB 单独调整，保留灰阶层次。键盘可微调和恢复默认。切换步骤保留当前草稿。 |
| 前后对比 | 比较同一目标屏的原始状态和候选状态。确认全屏图案随步骤更新、切到参考照片页后按现有期限恢复，以及从其他页面取消和显示恢复反馈。 |
| 完成、取消与 ClearType | 确认后可在 Windows 中识别新 ICC，取消恢复原状态。完成后可按用户选择打开 ClearType。 |

依照 WinUI Gallery 的控件和交互习惯完善布局。实测浅色、深色、高对比度、窄窗口、键盘导航、Narrator、焦点和三种语言。新文案同步维护三个 catalog，CLI 的字段和原因码保持语言无关。[WinUI Gallery](https://github.com/microsoft/WinUI-Gallery)

数学图案优先用于校准。用户提供的照片作为视觉复核的补充，首批覆盖中性灰、肤色、暗部、高光、饱和色与渐变。每张图片记录色彩配置、作者、许可及署名。自带照片功能可独立验收。[选片要求](PHOTOGRAPHY.md)

## M3 · 0.5 · 登录、唤醒与重连

- 从 MSIX StartupTask 接入共享的绑定/恢复用例。将“Windows 允许登录启动”和“此屏启用预设恢复”作为两个独立状态。默认关闭，遵循 Windows 的用户和组织策略状态。
- 绑定目标使用可靠的持久身份并校验已确认 ICC 的名称和摘要。恢复已确认的绝对曲线，避免重复叠加相对参数。设备路径是较弱的回退身份，换端口或同型号匹配不明确时需要重新确认。
- 处理登录、续睡、屏幕重连、扩展/复制切换和驱动重置。先对账未完成预览，再检查已确认绑定。合并重复事件，限制重试，避免与 GUI、CLI 的活动预览争用。
- 恢复前重新核对 SDR、用户选用的配置、曲线及目标。用户改默认配置或系统改变当前模式时暂停并记录原因。提供暂停、移除绑定和最近一次恢复结果。
- 验收覆盖首次启用、Windows 禁用后再次登录、重启、睡眠唤醒、拔插、换端口、两个同型号屏和升级。登录不会弹出向导，失败有可查询原因。[StartupTask 状态](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptaskstate)

## M4 · 0.6 · 预设与 Agent 接口稳定

- 补齐预设重命名、删除、重复导入的处理、绑定状态和本地数据迁移。共享 JSON 仅携带校准数据和描述，显示器身份与已安装 ICC 引用留在本机。说明他人的预设需要当前显示器重新预览。
- 固定 CLI v1 JSON 封装、退出码、状态和原因码。维护 `capabilities --json` 的命令效果、预演和请求 ID 支持字段。定义超时和相同请求的处理，验证绑定查询和恢复诊断。保留 0.2 命令兼容，变更时记录迁移方式。
- GUI、CLI 和后台恢复共用预设校验、会话所有权和恢复路径。验证 CLI 中断的曲线和 ICC 预览都可在向导中发现并尝试恢复。覆盖同一目标并发、不同显示器并发、会话重启及非交互环境。包内 GUI 和 CLI 使用同一数据存储。
- 验收覆盖旧版预设、无效/超大 JSON、陌生 schema 版本、导入后未应用、非 ASCII 和含空格的路径、别名禁用以及本地日志的可读性。[现有 CLI 契约](CLI.md)

## M5 · 1.0 RC → Microsoft Store

商店身份、素材和提交材料可与 M1–M4 并行准备。候选包以 M1–M4 的首发验收结果为准。

1. 参考 RunOnce 已上架的单项目 MSIX、清单资源和 Store 关联流程，为 DCCW++ 在 Partner Center 预留名称并关联独立产品身份。开发身份与商店身份分别构建。关联文件和签名材料留在本地。[RunOnce](https://github.com/Water-Run/RunOnce)
2. 从候选版本源码构建 x64 和 ARM64，验证对应 GUI/CLI、运行时、图标、启动任务、别名和语言资源均随包发布。核对版本递增、架构选择和 `runFullTrust` 说明。提交优先使用生成并验证过的上传包。[上传 MSIX](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages)
3. 在干净的 Windows 11 x64 和原生 ARM64 设备上验证普通用户安装、冷启动、命令行、登录启动、升级保留数据和卸载。验证已保存 ICC 的移除和恢复指引。可用 WACK 辅助本地检查，正式认证以 Partner Center 为准。[MSIX 打包与验收](https://learn.microsoft.com/en-us/windows/msix/package/packaging-uwp-apps)
4. 准备英文、简中和繁中商店说明、实拍应用截图、Win11 图标、支持入口、隐私说明及认证测试步骤。根据实际验收填写 SDR 范围和已知限制。归档相同版本完整对应源码、构建方式、依赖许可和照片许可。
5. 首次发布可用 Private audience 做 Store 签名包的安装验证。已有正式版本后，评估使用 package flights 验证更新。跟踪 Partner Center 认证和 Store 签名安装结果，公开发布后再增加 README 的正式商店入口。[Private audience](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/visibility-options) · [Package flights](https://learn.microsoft.com/en-us/windows/apps/publish/package-flights)

## 发布后的 HDR 与 Advanced Color

首发对 HDR、Advanced Color 和包含 MHC2 的配置显示明确的状态及可用的 Windows 校准入口。检测到 MHC2 的 SDR 曲线预览会在写入前结束。独立实现先验证能力探测、HDR 图案呈现、MHC2 配置和关联恢复，再决定支持组合。用仪器或可复核元数据核对亮度、色域与白点。分别测试 SDR 预设和 HDR 配置在模式切换后的归属、应用及恢复。[Windows MHC2 校准管线](https://learn.microsoft.com/en-us/windows/win32/wcs/display-calibration-mhc)

## 验收记录

每个实机案例记录构建版本、Windows build、架构、GPU 和驱动、显示器类型、HDR/Advanced Color 状态、连接方式和拓扑。保存步骤、预期、实际、恢复结果与证据。基础矩阵包含笔记本内屏、直连外屏、扩展双屏、同型号双屏、扩展坞/热插拔、复制、HDR 切换和远程会话。覆盖 Windows 11 最低声明版本的运行能力和当前发布版本。发布前检查每项失败是否已修复，或已通过能力检测明确限制在支持范围外。

构建、核心测试、CLI 只读/预演检查和 MSIX 静态检查用于回归验证。端到端实机报告单独记录视觉结果、逻辑曲线读回、ICC 关联和重启后加载结果。当前交接见 [STATUS](STATUS.md)，打包步骤见 [STORE](STORE.md)。
