# DCCW++ 开发路线图

更新于 2026-11-25。当前基线为 0.2 SDR 开发版本。阶段按依赖关系排序，版本号是目标，实机结果和商店认证决定实际发布进度。

## 产品目标与首发范围

DCCW++ 是面向 Windows 11 的独立 WinUI 3 应用。首发目标是用户能用 DCCW++ 完成原版 DCCW 的 SDR 视觉校准流程，并在 Windows 中保留、查看和恢复结果。安装入口计划是 Microsoft Store，代码采用 GPL-3.0-only。应用保留自己的 Windows 入口和商店标识，通过公开 API 工作。

首发包含 Gamma、显示器亮度和对比度引导、灰阶 RGB 平衡、前后比较、ICC 保存与恢复、ClearType 入口，以及多显示器、预设交换、登录恢复、三语言和 Agent CLI。HDR 和 Advanced Color 提供模式识别及相应的 Windows 校准入口，作为首发范围约定。独立 MHC2 后端另行验证。比较效果以视觉验收为主，面板 Gamma、白点、色域等定量结果只有在仪器测量后才记录。

## 当前基线

| 能力 | 0.2 状态 | 仍缺的证据或实现 |
| --- | --- | --- |
| WinUI 3 向导 | 已接入选屏、Gamma 参考图、亮度、对比度、灰阶、限时比较、保存和 ClearType，可跳过硬件调整。x64 沙盒已在 200% 缩放验收宽窄布局、固定步骤栏和操作区、三语导航及全屏图案，并核对原版拒绝远程校准时的入口行为 | 本地会话下的原版逐页对照、实机多屏定位、其他 DPI 和无障碍验收 |
| SDR 写入和 ICC | 已有曲线读回、`vcgt` 生成、默认关联提交和限时撤回预览。关联前后检查安装文件的摘要，回滚检查日志中的摘要。x64 沙盒普通用户已通过 Windows API 验证虚拟显示器的 ICC 选择、替换和作用域回退。x64 本地控制台三条 SDR 显示路径的只读诊断和预演已通过，前后全桌面采样一致 | 本地实机验证曲线写入、系统加载、应用内确认与撤回、退出和重连恢复 |
| 多屏和重连 | 已探测活动拓扑和彩色模式，分开当前查询 ID 和持久身份 | 同型号/无序列号屏、扩展和复制切换、换端口的实机验证 |
| HDR 与 Advanced Color 入口 | 区分 SDR、WCG 和 HDR，查询对应的默认 ICC，向导已接入 Windows 设置和 HDR 校准入口 | 核对实际 HDR、WCG 和 MHC2 的显示提示与跳转 |
| 预设与 CLI | 已有草稿参数、绑定保护、重复导入和命令发现。直接预览、应用预设、撤回校准和试用 ICC 支持预演与请求 ID。Agent 可在限时预览中调整数值及切换原始与候选视图，也可查询或限时等待会话结果。x64 沙盒已验证 CLI 创建的预设在 GUI 可见，以及 GUI 导入、重复导入和显式副本的 CLI 查验 | 实机显示事务及跨版本兼容性 |
| 登录恢复 | StartupTask 与逐屏恢复开关已接通。x64 沙盒普通用户已验证实际重新登录触发无窗口后台，断开测试目标被跳过。用户禁用或设备策略禁用后，后台退出且下次登录不再触发。撤销测试策略后恢复原先的用户偏好 | 实机恢复匹配显示器、睡眠唤醒与热插拔 |
| 语言和照片 | en-US、zh-Hans、zh-Hant，可打开本机照片。x64 沙盒中可切换三语并保留向导当前步骤 | 各页面文案和参考照片的实机验收，内置照片依赖项目所有者供片及许可 |
| 商店包 | 双架构开发 MSIX、对应源码与依赖许可已验证。x64 已在沙盒验证普通用户安装、交互式 GUI 冷启动、CLI 执行、原地升级保留数据及卸载。GitHub 托管 x64 和 ARM64 分别验证开发包装卸、CLI 别名以及带目标和预设的原生 WinUI 冷启动 | Windows 11 x64 普通用户的装包 CLI 交接。ARM64 普通用户的交互安装、登录启动和升级。Store 签名包升级、DCCW++ 专属商店身份及认证 |

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
- 用同一条只读检查评估向导和 CLI 的新预览。验证无法解析的基准 ICC、缺失的设备标识及未恢复的事务能在写入曲线前报出原因。预演通过后仍需单独验收实际曲线写入和关联。
- 在显示操作锁已占用、Gamma 和 ICC 分别有待恢复日志的情况下，核对 Gamma 调整、ICC 切换和撤回的预演。记录原因码，并核对预演未占用会话 ID。
- 审查安装、关联、提交记录、预设及绑定持久化之间的失败窗口。验证进程在每一步中断后的恢复，以及用户或其他程序在预览时切换 profile 或作用域的处理。记录系统选用的作用域、实际 ICC 关联来源和查询状态。读取过期日志时以原物理目标、当前模式和状态所有权重新校验。
- 保存、撤回和登录恢复均需在确认前验证最终读回的两个作用域。分别保留同名 ICC 并切换每用户与系统作用域，覆盖回退到系统 ICC 及无默认 ICC 的情况，核对向导和 CLI 的确认结果。[Windows ICC 默认关联查询](https://learn.microsoft.com/windows/win32/api/icm/nf-icm-colorprofilegetdisplaydefault)
- 模拟缓慢的显示读取、日志写入和驱动调用前的校验跨过 Gamma 预览期限。核对开始、更新、进入比较和确认的结果。验证未获准的候选曲线不会在超时后写入，外部选用的 ICC 和外部写入的曲线继续保留。
- 在保存和撤回期间分别延迟 ICC 安装、作用域与关联写入、最终读回及确认日志发布。核对超时和窗口退出阻止后续写入。若已开始修改关联，核对之前的 ICC、作用域和曲线能否从日志恢复。
- 在确认日志写入临时文件期间改变 ICC 文件、作用域、活动路径或逻辑曲线。核对旧日志一直可读，失败后没有确认结果或待写绑定。撤回到系统作用域时，另行改变未生效的每用户默认 ICC 并核对确认结果。
- 使用非线性的起始曲线核对 ICC 切换及回退。分别在保存切换 ICC 后和回滚切回 ICC 时检查线性重置。覆盖外部曲线、拓扑变化和回滚写入中断。核对重试后原 ICC、作用域和逻辑曲线的读回结果。[Windows Gamma Ramp 限制](https://learn.microsoft.com/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp)
- 在回滚已切换 ICC、尚未读回曲线时中断工作进程。用持久日志重试，核对原拓扑可恢复非线性曲线，拓扑变化或外部曲线仍会保留。模拟日志写入失败，确认应记录恢复意图的 ICC 切换会等待。
- 在 ICC 回滚核对文件后或日志落盘时改变活动路径、有效关联、作用域或当前未选用的每用户默认配置。核对第一次关联写入被阻止，并检查日志记录的原因。
- 对已确认但本地记录仍待保存的事务，检查持久意图和当前 ICC，尝试补齐预设与绑定。验收应覆盖写入绑定后进程退出、用户随后删除记录、断开重连、较新的绑定以及撤回后的旧绑定清理。
- 确认保存或撤回后中断本地写入，重启或等 Windows 重新分配显示路径，再运行登录恢复或 `calibration recover`。核对匹配设备且 ICC 摘要一致时补齐记录，配置状态暂不可用时保留待处理状态，文件变化时保留用户当前设置。
- 拔出显示器时记录待恢复状态，目标重新出现且身份匹配后再恢复。克隆、远程会话、HDR/Advanced Color、目标不明确或驱动拒绝时，返回可操作的提示和稳定原因码。
- 在 Gamma 预览、ICC 预览和 ICC 保存或撤回中断后，重建活动显示路径并运行恢复。分别验证同一配置设备匹配、旧路径被另一屏复用、设备身份重复、原设备已变及恢复过程中路径再次改变。原 ICC、作用域和曲线按照读回结果分别验收。
- 两条独立 SDR 路径共用 Windows 配置设备时，核对 Gamma 和独立 ICC 关联的预演均拒绝该目标。分别在 ICC 安装后、独立 ICC 预览中及恢复过程中复查设备变为共用的情况，并核对两屏的作用域、ICC 与待恢复日志。核对缺少配置设备摘要的旧日志在原路径恢复前也会检查当前设备是否唯一。
- 预览期间在所选屏及另一屏分别只改变刷新率。核对 Gamma 和 ICC 预览均停止接受确认，并按原显示器身份和路径决定回滚或保留待恢复状态。用采样摘要核对刷新率变化已被记录。
- 模拟 ICC 预览的日志写入和 Windows 写入前的核对跨过期限，验证原 ICC 及外部选用的配置均保留。模拟写入或读回跨过期限，验证回滚和 CLI 的启动超时反馈。
- 在 ICC 预览就绪和确认的临时日志落盘时模拟超时、所有者退出、显示路径变化、外部选用其他配置及同名 ICC 文件替换。核对未发布无效的成功状态，日志可继续读取，并按配置归属回退或保留待恢复。
- 验证只留下 `prepared` 日志的 Gamma 和 ICC 预览可在断开或远程环境中结束。ICC 写入前的核对失败时，验证外部选用的候选配置继续保留。已记录 `applying` 的会话仍需核对显示状态。
- 模拟窗口进程分别在驱动或 ICC 写入前、写入过程中、以及 Gamma 确认日志落盘时退出。核对窗口拥有的预览终止、已开始的预览回滚，以及 CLI 发起的预览在原期限下继续由工作进程处理。
- 对独立的 ICC 关联预览，验证布局改变后可在同一源与目标路径恢复。覆盖同名 ICC 文件替换、不同屏或作用域、克隆和 HDR。当前或候选 ICC 包含 MHC2 时验证预演与实际预览都给出相同原因码。旧会话仍需验证回滚。没有持久身份的旧会话沿用原拓扑校验。验证确认读取跨过截止时间时回退，以及观察和命令能识别工作进程中断。
- 确认 `SetDeviceGammaRamp` 仅在受控 SDR 兼容路径使用。确定回读容差、驱动拒写后的处理，以及休眠、分辨率变化和外部工具覆盖后的预期行为。使用 ICC 和 Windows 关联作为持久化路线。微软记录了 Gamma Ramp 静默拒写、系统覆盖和 HDR 行为未定义等限制。[Gamma Ramp 文档](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp) · [默认 ICC 关联](https://learn.microsoft.com/en-us/windows/win32/api/icm/nf-icm-colorprofilesetdisplaydefaultassociation)

验收以独立的 SDR 显示器完成整轮校准、退出后恢复、确认后重新加载和恢复原关联为准。单独中断预览工作进程，确认向导保留恢复入口，CLI 返回 `recovery_required`，恢复后核对原曲线和 ICC。验证撤回已确认校准时，取消和超时可回到撤回前状态，确认可还原前一个 ICC、作用域和曲线。扩展桌面至少验证两台屏幕，确认另一屏的 ICC 和曲线保持原状。模拟测试覆盖失败分支，真实写入仅在人工实机验收中进行。

每个步骤前后都可用 `dccwpp calibration capture --json` 保存全桌面的只读报告。用 `dccwpp calibration compare <before.json> <after.json> --json` 核对拓扑、ICC 摘要、作用域及逻辑曲线。检查未选屏时要求 `fullDesktopCoverage: true`，并检查该屏的结果。记录不可读或不支持的目标、会话原因码、Windows 设置所示配置及视觉观察。[采样与比对命令](CLI.md)

在最后一次曲线读回时模拟另一屏断开或改变刷新率，以及所选屏切换 ICC 或配置设备。即使两次曲线读回相同，也应重新采样。用缺少配置设备证据的旧校准记录同时核对 `verify` 和向导，验证两者都只在原活动路径上比较。

开始实机验收前核对 `isRemoteSession: false`。远程采样可核对 Windows 读回的 ICC 关联和文件摘要。物理屏幕的曲线读回、显示效果和重新登录后加载使用本地交互会话验收。[Windows Gamma Ramp 读取条件](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getdevicegammaramp)

确认后及重新登录后可对同一会话运行 `dccwpp calibration verify <calibration-id> --json`。分别检查当前 ICC 选择、文件摘要和逻辑曲线，并记录 Windows 加载器设置。中断另一场可能已写入的预览，核对命令是否报告 `recovery_required`，再在回滚后重新检查同一会话。多屏验收继续用全桌面采样核对其他目标。[实时核对命令](CLI.md)

在向导第一步检查所选显示器的已保存校准，并与 `dccwpp calibration inspect --display <display-id> --json` 核对。验证 Windows 选用旧的 DCCW++ ICC 时定位对应保存，选用其他 ICC 时显示与最近一次保存的差异。切换显示器后重新检查，核对结果中的预设名、ICC 文件名及检查时间。[按显示器核对](CLI.md)

重启后先核对当前默认 ICC 和曲线，再对重启前的校准运行 `calibration undo <calibration-id> --dry-run --json`。Windows 可为同一适配器分配新的 LUID。核对撤回计划采用当前路径，且只有匹配原显示器与配置设备时才允许预览。然后分别核对取消、确认恢复和其他屏幕的状态。[Windows 显示适配器 ID](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displayadapter.id)

## M2 · 0.4 · DCCW 行为对齐与 WinUI 体验

在同一台测试机和同一目标屏对照 Windows 设置中的 `Calibrate display` 流程，记录 Windows build、入口、默认值、跳过行为和保存结果。Windows 设置会针对运行校准工具所在的显示器创建配置文件，多屏流程需明确选屏和移动画布。[Windows 显示颜色设置](https://support.microsoft.com/en-us/windows/hardware/display-graphics/change-display-brightness-and-color-in-windows)

在本机交互桌面进行逐页对照。Windows Sandbox 的远程会话中，原版 `dccw.exe` 会在向导打开前拒绝校准。DCCW++ 在该会话中提示登录本机，暂缓显示状态写入，保留静态参考图案与预设编辑功能。[Windows 远程桌面会话检测](https://learn.microsoft.com/en-us/windows/win32/termserv/detecting-the-terminal-services-environment)

| 原版任务 | DCCW++ 验收 |
| --- | --- |
| 准备与选屏 | 标明目标、位置和色彩模式。把校准画布放在所选屏上。跨屏或拓扑变化时重新验证目标。 |
| Gamma | 可辨识过低、合适、过高的视觉参考。图案在 100%、150%、200% 缩放和全屏按物理像素呈现。向导和全屏均支持数值微调和重置。 |
| 亮度与对比度 | 分别用纯黑与纯白作背景，各有八个相邻色阶供辨认可见细节。指引用户操作显示器后，验证亮度和对比度可跳过，并能按记录恢复手动调整过的 OSD 设置。 |
| 灰阶色彩平衡 | RGB 单独调整，保留灰阶层次。向导和全屏均可用键盘微调和恢复默认。切换步骤保留当前草稿。 |
| 前后对比 | 比较同一目标屏的原始状态和候选状态。从全屏图案或全屏参考照片切换曲线、查看原会话倒计时或取消并恢复。确认全屏图案随步骤更新，切到参考照片后保持原预览期限，并在其他页面验证取消反馈。 |
| 完成、取消与 ClearType | 确认后可在 Windows 中识别新 ICC，取消恢复原状态。完成后可按用户选择打开 ClearType。 |

保存后核对 ICC 确认状态和本地绑定的进度。模拟确认回执丢失、预设刷新失败和 ClearType 启动失败，确认提示仍显示已选用的 ICC。ClearType 的重试应只重开调谐器。

用英文、简体中文和繁体中文预设名验证 ICC 描述。确认保存提示列出预设名和可用于 CLI 的 ICC 文件名，并可打开 Windows 显示设置。分别核对 ICC v2 普通描述与 Unicode 描述、ICC v4 Unicode 描述和已有文件的撤回。只读取 v2 ASCII 字段的应用以短编号显示非 ASCII 配置。[ICC v2](https://www.color.org/v2spec/) · [ICC v4](https://www.color.org/icc-1_specification/) · [Windows 颜色配置页](https://support.microsoft.com/en-us/windows/hardware/display-graphics/change-display-brightness-and-color-in-windows)

选择非主屏后刷新列表。核对原屏重建路径且持久身份唯一时保留选中，原屏掉线或身份不明确时清空选中。向导运行期间记住重复的持久身份。已见重复时，即使只剩一个匹配设备，也要核对完整的原活动路径和配置设备。移动、全屏和启动预览前重新核对活动路径。[Windows 显示器设备路径](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_target_device_name)

将全屏图案移到另一屏或使其退回普通窗口。核对图案在实际可见边界不再覆盖所选屏时关闭，同屏缩小时可重新打开。活动预览中若图案越出所选屏，应使用原会话的取消与回滚路径。向导发送新的曲线更新前再核对窗口仍在目标屏。[窗口变更通知](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindowchangedeventargs) · [可见窗口边界](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect)

向导空闲时接收 Windows 显示设置通知，并在返回校准页或重新激活窗口时刷新。核对刷新后显示模式、按钮、选屏和全屏参考图的状态。下拉列表打开或显示事务进行时应延后刷新。[Windows 显示设置通知](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.systemevents.displaysettingschanged)

用 UI Automation 检查选屏控件展开和收起时的名称。三种语言均应读到显示器名称、主屏标识和可用的桌面坐标，刷新和切换语言后保留选中。用两台同型号显示器验证坐标能区分桌面位置。可见标签相同时核对各自附加的 ID 与 `displays list` 一致。[Windows 无障碍名称指引](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information)

在准备页选中非主屏后执行「识别显示器」。核对标识完整位于所选屏、显示器 ID 与 CLI 一致、键盘焦点留在向导。核对标识自动关闭，并在切换步骤、选屏或拓扑变化时立即消失。复制模式及向导预览期间应停用该按钮。

依照 WinUI Gallery 的控件和交互习惯完善布局。实测浅色、深色、高对比度、窄窗口、键盘导航、Narrator、焦点和三种语言。新文案同步维护三个 catalog，CLI 的字段和原因码保持语言无关。[WinUI Gallery](https://github.com/microsoft/WinUI-Gallery)

切换 Windows 对比主题时核对向导和全屏说明的文字、按钮及背景都使用系统配色。以每种主题下的画面采样比对 Gamma、亮度和对比度图案的渲染像素，另行在人工作业中验收物理显示效果。[Windows 对比主题指引](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes)

在未启动预览时，用 Tab 聚焦 Gamma 和 RGB 的滑块与数值框。核对方向键每次分别改变 0.02 和 0.01，数值框的 Page Up／Page Down 分别改变 0.20 和 0.10，且滑块同步变化。验证到达上下限、重置及返回上一步后保留草稿数值。[WinUI 滑块步长](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.primitives.rangebase.smallchange?view=windows-app-sdk-1.8) · [数值框步长](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.numberbox.largechange?view=windows-app-sdk-1.8)

在亮度或对比度页用键盘执行跳过，核对焦点移到灰阶页的红色滑块，并可立即用方向键微调。在 Gamma 页用 Tab 进入全屏按钮后打开参考图，分别用 Escape、退出按钮和 Alt+F4 返回，核对焦点回到全屏按钮并保留所选步骤。[Windows 键盘焦点指引](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions)

在亮度和对比度页分别记录调整前的显示器菜单值。核对普通与全屏窗口同步、跳过后仍可查看、切换显示器与语言时记录归属正确。取消预览后可从「显示器原始设置」查看或复制原值，然后在显示器菜单手动还原。记录在关闭向导窗口时清除。

在 Gamma 和灰阶页全屏调整滑块及数值框，退出全屏核对向导中的值。分别用草稿和活动预览验证调值、重置与取消。进入亮度、对比度或比较时隐藏曲线编辑控件。验证显示器更换时沿用原路径校验，全屏调值与向导共享同一预览会话。

订阅 Windows UI Automation 的 `LiveRegionChanged`，核对前台向导的步骤说明和比较状态。全屏比较使用当前全屏说明作为事件来源。同一段文字在界面定时刷新时应保持安静，隐藏的向导页也不应播报。另行用 Narrator 核对实际播报内容和时机。[Windows 实时区域示例](https://learn.microsoft.com/en-us/accessibility-tools-docs/items/uwpxaml/text_livesetting)

验证滚动参考内容时，步骤选择、全屏和底部操作始终可用。实际预览时另行确认窄窗口中的取消、确认和倒计时可见。通过上一步返回时应保留草稿值，切换页面后应回到原来的校准步骤。[Windows 响应式布局](https://learn.microsoft.com/en-us/windows/apps/develop/ui/layouts-with-xaml)

调整 RGB 后立即进入比较，核对首次显示的候选曲线含最后一次输入。分别在进入比较前让原预览到期、在比较中反复切换视图，并从原始视图返回编辑。记录比较按钮启用时的读回和截止时间。

数学图案优先用于校准。用户提供的照片作为视觉复核的补充，首批覆盖中性灰、肤色、暗部、高光、饱和色与渐变。每张图片记录色彩配置、作者、许可及署名。验收自带照片时连续切换图片，确认画面始终对应最新选择。文件无法打开时核对三语提示，并切回有效文件继续比较。[选片要求](PHOTOGRAPHY.md)

从“对比与保存”打开参考照片。核对照片按比例在所选屏全屏显示，切换原始和候选曲线时保持同一照片及原预览期限。分别验证退出全屏、切换照片、取消和超时。将全屏照片移出目标屏时核对预览进入原有回退流程。[WinUI 全屏窗口](https://learn.microsoft.com/en-us/windows/apps/develop/ui/windowing-overview)

## M3 · 0.5 · 登录、唤醒与重连

- 从 MSIX StartupTask 接入共享的绑定/恢复用例。将“Windows 允许登录启动”和“此屏启用预设恢复”作为两个独立状态。默认关闭，遵循 Windows 的用户和组织策略状态。
- 用装包别名启停 Windows 登录任务并重复调用。在设置页确认切换结果仍与 Windows 一致，核对通过系统禁用或组织策略控制时的原因码，并保持各屏绑定独立。
- 绑定目标使用持久身份及 Windows 配置设备摘要，并校验已确认 ICC 的名称和文件摘要。重新保存时核对原恢复偏好是否属于同一配置设备。恢复已确认的绝对曲线，避免重复叠加相对参数。设备路径是较弱的回退身份，换端口或同型号匹配不明确时需要重新确认。
- 处理登录、续睡、屏幕重连、扩展/复制切换和驱动重置。先对账未完成预览，再检查已确认绑定。合并重复事件，限制重试，避免与 GUI、CLI 的活动预览争用。没有系统通知时，轮询活动路径、布局和色彩模式的变化。[Windows 显示路径与模式](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/relationship-of-mode-information-to-path-information)
- 验收恢复事件发生时已有活动预览的情况。预览结束后，后台在有上限的等待期内重新评估同一请求。等待期间关闭 Windows 自启动或对应显示器的绑定也需验证。
- 在旧恢复事件即将用完重试次数时重连显示器，核对驱动就绪前仍有短间隔重试。在忙碌期限结束前唤醒系统，核对旧期限结束后仍会尝试这次唤醒。重复发送相同布局的通知时，核对原有等待上限。
- 模拟登录时 Windows 启动任务查询超时或不可用，核对后台保留待处理事件及原因。等待期间触发唤醒，分别验证 Windows 重新确认启用后处理该事件，以及确认禁用后退出。
- 通过 CLI 或向导启动中断恢复时，用 `observe` 核对执行中的回退与待处理状态。让另一个进程持有会话锁，核对后台及手动批量恢复报告忙碌，保留原日志，释放后才能尝试恢复。
- 验收路径短暂不可读后恢复到相同布局且系统未发通知的情况。核对后台重新评估绑定，ICC 或逻辑曲线暂时不可读时按限次重试，同名 ICC 已更改时保留现有设置。
- 恢复前重新核对 SDR、用户选用的配置、曲线及目标。用户改默认配置或系统改变当前模式时暂停并记录原因。提供暂停、移除绑定和最近一次恢复结果。
- 在设置页核对最近一次自动恢复的时间和触发方式。选择不同的显示器，核对跳过和失败原因。刷新或切换语言后仍应保留所选显示器。
- 验收覆盖首次启用、Windows 禁用后再次登录、重启、睡眠唤醒、拔插、换端口、两个同型号屏和升级。登录不会弹出向导，失败有可查询原因。[StartupTask 状态](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptaskstate)

## M4 · 0.6 · 预设与 Agent 接口稳定

- 补齐预设重命名、删除、重复导入的处理、绑定状态和本地数据迁移。共享 JSON 仅携带校准数据和描述，显示器身份与已安装 ICC 引用留在本机。说明他人的预设需要当前显示器重新预览。
- 固定 CLI v1 JSON 封装、退出码、状态和原因码。维护 `capabilities --json` 的命令效果、预演和请求 ID 支持字段。定义超时和相同请求的处理，验证绑定查询和恢复诊断。保留 0.2 命令兼容，变更时记录迁移方式。
- 从装包 CLI 用 `wizard open` 把目标显示器和预设交给 WinUI。核对默认和显式选屏、草稿名称与数值、目标在交接途中断开或被替换，以及启动失败和回执超时。验证打开向导时没有新校准会话，也没有改动现有向导里的草稿。[Agent 交接](CLI.md#从-agent-交接到校准向导)
- GUI、CLI 和后台恢复共用预设校验、会话所有权和恢复路径。验证 CLI 中断的曲线和 ICC 预览都可在向导中发现并尝试恢复。覆盖同一目标并发、不同显示器并发、会话重启及非交互环境。包内 GUI 和 CLI 使用同一数据存储。
- 模拟工作进程启动时遇到过期的预览日志、未完成日志和迟到的错误回执。核对过期预览等待回退，未完成日志报告需要恢复，故障原因传到 CLI。
- 验收覆盖旧版预设、无效/超大 JSON、陌生 schema 版本、导入后未应用、非 ASCII 和含空格的路径，以及本地日志的可读性。装包后验证无窗口进程的 JSON 和退出码、缺少 WindowsApps `PATH` 时的完整路径，以及别名关闭和重新启用后保留的应用数据。[现有 CLI 契约](CLI.md) · [装包验收步骤](STORE.md)

## M5 · 1.0 RC → Microsoft Store

商店身份、素材和提交材料可与 M1–M4 并行准备。候选包以 M1–M4 的首发验收结果为准。

1. 参考 RunOnce 已上架的单项目 MSIX、清单资源和 Store 关联流程，为 DCCW++ 在 Partner Center 预留名称并关联独立产品身份。开发身份与商店身份分别构建。关联文件和签名材料留在本地。[RunOnce](https://github.com/Water-Run/RunOnce)
2. 从候选版本源码构建 x64 和 ARM64，验证对应 GUI/CLI、运行时、图标、启动任务、别名和语言资源均随包发布。核对版本递增、架构选择和 `runFullTrust` 说明。提交优先使用生成并验证过的上传包。[上传 MSIX](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages)
3. 在干净的 Windows 11 x64 和原生 ARM64 设备上验证普通用户安装、冷启动、命令行、登录启动、升级保留数据和卸载。验证已保存 ICC 的移除和恢复指引。可用 WACK 辅助本地检查，正式认证以 Partner Center 为准。[MSIX 打包与验收](https://learn.microsoft.com/en-us/windows/msix/package/packaging-uwp-apps)
4. 审阅已起草的英文、简中和繁中商店说明，补充实际应用截图。根据验收更新 SDR 范围和已知限制。归档相同版本完整对应源码、构建方式、依赖许可和照片许可。[商店文案及认证说明](STORE_LISTING.md)
5. 首次发布可用 Private audience 做 Store 签名包的安装验证。已有正式版本后，评估使用 package flights 验证更新。跟踪 Partner Center 认证和 Store 签名安装结果，公开发布后再增加 README 的正式商店入口。[Private audience](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/visibility-options) · [Package flights](https://learn.microsoft.com/en-us/windows/apps/publish/package-flights)

CI 在托管 Windows Server 2025 x64 和 Windows 11 ARM64 上分别测试该次构建的开发包。核对运行报告中的提交、包摘要、CLI 别名、WinUI 进程架构和卸载状态。在 Windows 11 x64 普通用户交互会话中验收从 CLI 交接到向导。[运行记录](STORE.md)

## 发布后的 HDR 与 Advanced Color

首发对 HDR、Advanced Color 和包含 MHC2 的配置显示明确的状态及可用的 Windows 校准入口。检测到 MHC2 的 SDR 曲线预览会在写入前结束。独立实现先验证能力探测、HDR 图案呈现、MHC2 配置和关联恢复，再决定支持组合。用仪器或可复核元数据核对亮度、色域与白点。分别测试 SDR 预设和 HDR 配置在模式切换后的归属、应用及恢复。[Windows MHC2 校准管线](https://learn.microsoft.com/en-us/windows/win32/wcs/display-calibration-mhc)

## 验收记录

每个实机案例记录构建版本、Windows build、架构、GPU 和驱动、显示器类型、HDR/Advanced Color 状态、连接方式和拓扑。保存步骤、预期、实际、恢复结果与证据。基础矩阵包含笔记本内屏、直连外屏、扩展双屏、同型号双屏、扩展坞/热插拔、复制、HDR 切换和远程会话。覆盖 Windows 11 最低声明版本的运行能力和当前发布版本。发布前检查每项失败是否已修复，或已通过能力检测明确限制在支持范围外。

构建、核心测试、CLI 只读/预演检查和 MSIX 静态检查用于回归验证。端到端实机报告单独记录视觉结果、逻辑曲线读回、ICC 关联和重启后加载结果。当前交接见 [STATUS](STATUS.md)，打包步骤见 [STORE](STORE.md)。
