# 开发交接

## 2026-11-25 · ICC 与撤回预演的显示协调

- Gamma、ICC 和撤回预演使用同一个显示操作锁及待恢复检查。实际工作进程再次检查。向导的撤回操作可显示本地化的忙碌或待恢复提示。[CLI 契约](CLI.md)
- 用隔离的 Gamma 和 ICC 日志核对预演会报告 `recovery_required`，也核对请求 UUID 未被占用。332 项单元及模拟测试、157 项本机 CLI 进程检查和 x64 开发 MSIX 内容校验通过。实际显示写入仍待实机验收。

## 2026-11-25 · 未完成预览时的已保存校准核对

- `calibration verify` 持有显示操作锁再检查待恢复日志。Gamma 或 ICC 预览可能已经写入时，报告 `recovery_required` 和 `inconclusive`。仅准备的预览和待补写的绑定仍可继续只读核对。[CLI 契约](CLI.md)
- 使用隔离日志复现旧版尝试核对未回滚的状态。修正后，Gamma 和 ICC 的未结束日志保持原样。332 项单元及模拟测试、153 项本机 CLI 进程检查和 x64 开发 MSIX 内容校验通过。实机显示写入仍待验收。

## 2026-11-25 · 显示采样覆盖范围

- `calibration capture` 记录探测到的活动显示器总数，所选屏采样也保留总数。`calibration compare` 的 `fullDesktopCoverage` 标明前后采样是否均覆盖所有活动显示器。旧采样仍可读取，缺少计数时可能无法确定覆盖范围。验收未选屏时同时检查覆盖字段和逐屏结果。[CLI 契约](CLI.md)
- 332 项单元及模拟测试、149 项本机 CLI 进程检查与 x64 开发 MSIX 内容校验通过。Windows Sandbox build 26100 普通用户通过装包别名执行只读采样及比对，探测到零台显示器时返回 `inconclusive` 和 `fullDesktopCoverage: false`。实机多屏校准仍待验收。

## 2026-11-25 · 窗口退出与预览写入

- Gamma 和 ICC 工作进程在授权 Windows 写入前复核窗口进程的 ID 与启动时间。窗口已退出时以 `owner_exited` 结束，已开始写入时尝试回滚。Gamma 确认在提交 ICC 前也复核窗口。CLI 发起的预览仍由独立工作进程按会话期限处理，调用方退出后可继续查询或取消。[CLI 契约](CLI.md)
- 使用实际子进程退出与模拟显示后端复现两条多余写入路径。修正后核对两种预览的启动回执、无窗口归属的会话及写入后回滚。332 项单元及模拟测试、144 项本机 CLI 进程检查及 x64 开发 MSIX 内容校验通过。实机显示写入和回滚仍待验收。

## 2026-11-25 · Gamma 驱动写入前复核期限

- Gamma 后端在设备核对完成后、调用驱动前复核期限。未获准写入的首次预览以 `preview_expired` 结束，读回标志为 `false`。更新或确认被拒绝时按此前获准的曲线判断能否回退，保留恰好与未获准参数相同的外部曲线。[CLI 契约](CLI.md)
- 七个回归场景在修正前均失败，涉及过期写入、读回标志和外部曲线归属。修正后用例通过，也核对了确认跨过期限时没有重复写入候选曲线或提交 ICC。325 项单元及模拟测试通过。实机驱动写入和回滚仍待验收。

## 2026-11-25 · 恢复尚未写入的预览

- Gamma 和 ICC 的 `prepared` 会话只关闭日志，标记为 `reverted` 和 `preview_not_applied`。显示器无需可读，也不会记录为已验证的显示恢复。Windows 核对拒绝尚未授权的 ICC 写入时，保留当前配置并记录原失败原因。[CLI 契约](CLI.md)
- 四个模拟回归覆盖断开显示器及其他应用选用同名候选 ICC。修正前，两项发出了不应有的原配置写入，另两项尝试查询断开的显示器。修正后 317 项单元及模拟测试、144 项本机 CLI 进程检查和 x64 开发 MSIX 内容校验通过。CLI 检查覆盖两个单会话命令和批量恢复，核对读回标志及原请求记录。
- Windows Sandbox build 26100 的普通用户从向导处理两份模拟准备日志。装包 CLI 核对两份日志均以 `preview_not_applied` 结束，读回标志均为 `false`，测试前后的虚拟桌面采样相同。处理按钮已改用涵盖结案和回滚的文案。

## 2026-11-25 · ICC 预览启动超时

- ICC 预览在日志写入后和 Windows 写入前复核期限。写入前到期时保留现有配置。若已开始切换且写入或读回跨过期限，尝试回滚并阻止该会话报告预览已就绪。未就绪便到期的会话在首次调用报告 `preview_expired`，相同请求 ID 仍可查询终态。[CLI 契约](CLI.md)
- 六个回归用例复现了原实现超时写入或提前报告就绪。修正后均通过，也核对了日志无法写入时保留外部配置、工作进程回执和请求重试。独立子进程复测七个启动场景，验证已落盘的迟到回执返回 `preview_expired`，尚无日志的启动仍返回 `worker_start_timeout`。报告位于 `artifacts/icc-startup-probe/fixed-results/result.json`。本地 SDR 显示器上的 ICC 写入及回滚仍待验收。

## 2026-11-25 · 保存、撤回和登录恢复核对 ICC 作用域

- 最后一次读回同时核对 ICC 文件名、有效作用域、Windows 选用的作用域和逻辑曲线。保存将两个作用域核对为当前用户。撤回按原关联记录检查作用域，兼容回退到系统 ICC 或无默认 ICC。登录恢复沿用校准事务的最终检查。[CLI 契约](CLI.md)
- 五个回归场景均复现了原实现报告成功，覆盖保存和撤回的作用域变化，以及自动恢复时同名配置切换为系统作用域。修正后用例通过，并核对回滚原因及不同作用域下的正常撤回。305 项单元及模拟测试通过。实机作用域切换和完整校准仍待验收。

## 2026-11-25 · ICC 保存中断后的线性曲线恢复

- ICC 切换后若已读到线性曲线，回滚会在验证事务的 ICC 关联可回退、原显示路径与拓扑保持一致时，恢复保存前的曲线。写回前先把线性起点记入日志，写回失败可重新读日志恢复。遇到其他曲线或缺少关联证据时，保留当前曲线。[CLI 回滚说明](CLI.md)
- 新增的两个回归场景在修正前都错误记录 `superseded`。修正后 12 项 ICC 回滚模拟均通过，包含首次写回失败后的重试、未发生关联回退、拓扑变化和外部曲线。298 项单元及模拟测试、140 项本机 CLI 进程检查与 x64 开发 MSIX 内容校验通过。实机确认 Windows 重载 ICC 时的曲线表现仍待验收。

## 2026-11-25 · 装包 CLI 调用与别名

- 设置页在三种语言中显示 `dccwpp.exe capabilities --json` 和 Windows 执行别名排查提示。英文 README 和 CLI 参考说明 `PATH` 缺少 WindowsApps 时如何用完整别名路径调用，以及如何确认包身份。[CLI 参考](CLI.md)
- Windows Sandbox build 26100 的 x64 开发包以普通用户运行。移除该进程的 WindowsApps `PATH` 项后，完整别名路径仍以相同包身份运行。通过 Windows 设置关闭别名后命令无法启动，WinUI 仍可打开。重新启用后原有预设可查。含中文及空格的预设路径也已通过导出和验证。校准会话始终为零，包已卸载。报告位于 `artifacts/alias-sandbox/432dbc39-f836-4afe-b809-c07a25263561/results/activation.json`。
- 单独运行的普通用户装包检查验证了无窗口子进程通过标准输出接收 JSON，查询不存在的校准会话时返回退出码 3 和 `not_found`，标准错误为空。报告位于 `artifacts/sandbox-smoke/85302d7f-f900-4dbe-b35d-ffd028885731/results/result.json`。293 项单元及模拟测试、140 项本机 CLI 进程检查及 x64 MSIX 内容校验通过。

## 2026-11-25 · 设置页恢复结果

- 设置页显示最近一次自动恢复检查的本地时间、触发方式和所选显示器的结果。跳过、失败和仍待恢复的结果带有本地化原因。刷新结果会重新读取报告并保留所选显示器。
- 在 Windows Sandbox build 26100 的普通用户会话中使用 x64 开发包和预置报告，核对英语、简中与繁中界面。覆盖尚无报告、断开、ICC 文件变化、忙碌、未知原因码和刷新后保留选择。校准会话计数始终为零，登录启动保持关闭，测试包已卸载。报告位于 `artifacts/restore-status-sandbox/a96f91b1-99ae-425f-960a-10bfe3800bbe/results/activation.json`。
- 293 项单元及模拟测试、140 项本机 CLI 进程检查和 x64 MSIX 内容校验通过。实机唤醒、重连和自动恢复仍待验收。

## 2026-11-25 · 商店上传包与对应源码

- 发布证据脚本新增 `--store`。同一份源码快照用于构建双架构上传包和生成对应源码归档。证据记录各上传包、内嵌 MSIX、第三方声明索引和源码的 SHA-256。[发布手册](STORE.md)
- 在隔离工作区使用测试身份生成 x64 和 ARM64 的 `1.0.0.0` 上传包，独立复核两种架构、清单身份及全部 148 个归档源码条目。缺少身份文件或身份与清单不符时已验证构建前停止。本机身份 JSON 和 Store 关联文件留在工作区。演练通过 293 项单元及模拟测试和 140 项本机 CLI 进程检查，报告位于 `artifacts/store-rehearsal/evidence-fixture/artifacts/release-evidence/synthetic-final/evidence.json`。正式身份和 Partner Center 认证待验收。

## 2026-11-25 · 预览工作进程就绪

- Gamma 和 ICC 启动客户端会核对工作锁和 `expiresAt`。过期预览等候回退结果。进程退出后，如已写下启动错误，则返回该原因。如只留下未完成日志，则报告 `recovery_required`。[CLI 契约](CLI.md)
- 用独立子进程模拟正常启动、到期回退、中断、启动报错及回退后报错。基线有五项结果不符，修正后十项均通过。报告位于 `artifacts/preview-start-probe/fixed-final-results/result.json`。
- 293 项单元及模拟测试、140 项本机 CLI 进程检查及 x64 MSIX 内容校验通过。Windows Sandbox 普通用户通过装包别名运行两个真实工作进程。不存在的目标分别报告 `not_found` 和 `display_disconnected`，两次都写入错误回执但未创建预览日志。报告位于 `artifacts/preview-start-probe/artifacts/sandbox-smoke/91822e64-a66a-48d3-9d79-e3cc55b64a62/results/result.json`。

## 2026-11-25 · Gamma 预览期限复核

- 正常显示状态读回、预览日志写入和向导比较倒计时落盘后都会再核对期限。操作期间到期时尝试恢复原曲线，成功回退记录 `preview_expired`。读回发现外部 ICC 选择时继续保留外部设置。[CLI 契约](CLI.md)
- 新增模拟覆盖开始预览时的慢读、更新和进入比较时的慢写，以及到期与外部 ICC 选择同时发生。WinUI、CLI 构建、292 项单元及模拟测试和 139 项本机 CLI 进程检查通过。实机显示效果仍待验收。

## 2026-11-25 · 跨入口恢复的会话锁

- 批量恢复、单会话 ICC 恢复和绑定补写会持有相应的工作锁。CLI 可观察正在执行的回退。遇到另一个进程持有的会话时，恢复保留该会话并报告 `preview_busy`，后台沿用现有的限时重试。ICC 确认和取消会等待已经在执行的回退。[CLI 契约](CLI.md)
- 288 项单元及模拟测试、139 项本机 CLI 检查及 x64 开发 MSIX 内容校验通过。Windows Sandbox 普通用户通过装包别名核对了跨进程锁、忙碌反馈、观察等待、锁释放后识别中断，以及回退中确认不会写入新的请求。使用隔离日志，包已卸载。报告位于 `artifacts/cli-outcome-check/artifacts/sandbox-smoke/3d1a90c9-119e-4331-9aac-cd3cd28f141a/results/result.json`。

## 2026-11-25 · Agent CLI 事务退出码

- `calibration update` 在预览已结束或进入回退时返回退出码 4。`profiles confirm` 与 `profiles revert` 按请求的目标状态判断成功，`profiles recover` 在恢复仍待处理时也返回退出码 4。失败响应保留会话原因码，`status` 和 `observe` 仍可查看完整状态。[CLI 契约](CLI.md)
- 286 项单元及模拟测试、139 项本机 CLI 进程检查及 x64 开发 MSIX 内容校验通过。Windows Sandbox build 26100 普通用户通过装包别名验证了过期 Gamma 更新与 ICC 确认、重复 ICC 取消、断开目标的恢复及卸载。使用隔离数据和断开的目标。报告位于 `artifacts/cli-outcome-check/artifacts/sandbox-smoke/e539a25b-cd8c-44af-95c4-0dfcf7f592e8/results/result.json`。

## 2026-11-25 · 参考照片切换

- 选择新照片时立即清除旧图，只有当前选中的文件可以更新页面。选中的文件无法打开时，在照片下拉框旁显示本地化提示。切回有效照片后恢复显示。[参考照片验收项](ROADMAP.md)
- 在 Windows Sandbox 的 x64 诊断包中以普通用户打开实际 PNG 文件，通过控制加载顺序验证较晚完成的旧图和旧解码错误均不影响当前照片。核对损坏文件的三语提示、选回有效文件及清空选择。沙盒卸载成功，前后均无校准会话。报告位于 `artifacts/reference-photo-probe/b7cf2262-c662-474f-96b2-ad8e62eb1699/results/activation.json`。
- WinUI 与 CLI 构建、286 项单元及模拟测试、127 项本机 CLI 进程检查和 x64 开发 MSIX 内容校验通过。

## 2026-11-25 · x64 开发包 WACK 预检

- 核对了 GitHub Actions 构建 `82caea8` 的 x64、ARM64 MSIX 及对应源码的 SHA-256。取该构建的 x64 `0.2.0.0` 开发包，在 Windows Sandbox build 26100 内临时签名，用 WACK `10.0.26100.8249` 完成 Desktop Bridge 检查。[对应 GitHub 构建](https://github.com/Water-Run/DCCWpp/actions/runs/45462477516)
- 首轮缺少 Windows SDK 平台元数据，元数据检查初始化失败。装齐 SDK 组件后复跑，同一开发包的 XML 报告为 `OVERALL_RESULT=PASS`、`PARTIAL_RUN=FALSE`，13 项必需检查全部通过。两项可选检查分别报告 SDK 自带的 WinUI 接口 GUID 与系统对应接口重复，以及应用和运行时引用进程启动 API。复跑报告位于 `artifacts/wack-sandbox/a8119ce0-ddd1-4742-b0a1-24ffbedbaf51/results/certification.xml`。[复测步骤](STORE.md)
- 此结果覆盖临时签名的 x64 开发包。Store 签名包、原生 ARM64 运行和 Partner Center 认证仍待验收。

## 2026-11-25 · 完成页保留 ICC 确认结果

- 确认回执报错或超时时，GUI 和 CLI 共用的客户端以持久日志的 `confirmed` 为准，并返回当前 `bindingStatus`。模拟的提交后回执错误与超时均覆盖修正前后的差异。WinUI 与 CLI 构建、173 项核心测试、113 项 Windows 测试及 127 项本机 CLI 进程检查通过。[CLI 契约](CLI.md)
- 完成页保留 ICC 文件名和绑定待处理状态。预设列表刷新出错时，保留已加载的列表，并提示可在预设页重试。ClearType 启动出错有独立反馈，可单独重开调谐器。真实 SDR 保存和原版逐页对照仍待实机验收。[验收项](ROADMAP.md)

## 2026-11-25 · 显示器选项的无障碍名称

- WinUI 下拉选项使用显示器名称、主屏标识和可用的桌面坐标。基线沙盒的 `SelectionPattern` 名称包含整条显示器数据记录。界面现在向 UI Automation 提供与可见选项一致的本地化名称，并在切换语言和刷新后保留可核对的原选择。[验收项](ROADMAP.md)
- WinUI 与 CLI 构建、173 项核心测试、110 项 Windows 测试和 127 项本机 CLI 进程检查通过。多物理显示器辨识和 Narrator 语音仍待验收。

## 2026-11-25 · 向导刷新与目标显示器

- 刷新优先保留匹配原身份的选择。原屏换路径时按唯一持久身份重新选中。掉线、身份不明或旧路径被其他显示器复用时，如果无法找回原屏，清空选择并提示重新选择。移动、打开全屏图案、开始和撤回校准前均重新读取活动路径。[验收项](ROADMAP.md)
- WinUI 与 CLI 构建、173 项核心测试、110 项 Windows 测试和 127 项本机 CLI 进程检查通过。测试覆盖旧路径被复用、唯一身份回连、重复身份和缺少持久身份时的源与配置设备核对。多物理显示器的切换及放置仍待实机验收。

## 2026-11-25 · ICC 关联预览识别 MHC2

- `profiles list` 可报告已关联 ICC 的 MHC2 标签。当前或候选配置包含该标签时，ICC 预演和工作进程均给出 `mhc2_profile_blocked`。工作进程在建日志和选默认关联之前结束，原有日志仍可按原目标、作用域和文件摘要回滚。[CLI 契约](CLI.md)
- 修正前两类配置都能通过预演。修正后，170 项核心测试、110 项 Windows 测试和 127 项本机 CLI 进程检查通过。用例覆盖标签读取、拒绝新预览、工作进程原因码，以及旧会话在检测到 MHC2 后的模拟回滚。MHC2 显示管线的实际行为及 SDR 实机校准仍待验收。[验收项](ROADMAP.md)

## 2026-11-25 · 登录恢复重新检查可用路径与 ICC

- 后台轮询记录路径暂不可用的状态，下一次读到相同布局时也会重新检查恢复。逐屏恢复区分已安装 ICC 文件不可读和本机预设不可读。ICC、关联或逻辑曲线暂不可读时，登录后台沿用最多两次、每次相隔 3 秒的重试。[CLI 契约](CLI.md)
- 修正前五个回归用例在路径重新可读、ICC 原因码和自动重试上失败。修正后 WinUI 与 CLI 构建、164 项核心测试、107 项 Windows 测试与 127 项本机 CLI 进程检查通过。模拟还核对了文件内容变化不触发短间隔重试，以及 ICC 恢复可读后才写入曲线。实际唤醒、重连与 SDR 曲线恢复仍待实机验收。[验收项](ROADMAP.md)

## 2026-11-25 · 显示路径重建后的预览恢复

- 向导、CLI 和登录恢复共用的中断恢复流程可按持久显示身份和 Windows 配置设备摘要查找新活动路径。能唯一匹配时，使用当前适配器和源执行回滚，并按原日志重新核对 ICC、作用域和曲线。旧日志缺少新证据时继续使用原路径。[CLI 契约](CLI.md)
- 模拟覆盖了旧路径被复用、重复身份或配置设备、HDR、外部 ICC 选择、同名文件替换，以及恢复途中路径再次改变。WinUI 与 CLI 构建、162 项核心测试、102 项 Windows 测试和 127 项本机 CLI 进程检查通过。重启和重连后的实机验证仍待进行。[验收项](ROADMAP.md)

## 2026-11-25 · 预览期间的刷新率变化

- Windows 提供完整活动路径时，Gamma 和 ICC 预览使用与采样报告相同的全桌面拓扑摘要。仅刷新率变化也会使确认停止。仍匹配原显示器身份和独立 SDR 路径时尝试回滚。证据不足时保留待恢复状态，旧日志仍按原证据检查。[CLI 契约](CLI.md)
- 模拟测试覆盖刷新率变化后回滚或等候原拓扑、确认落盘后刷新率变化导致绑定补写延后，以及只采样一屏时能检测另一屏的刷新率变化。WinUI 与 CLI 构建、153 项核心测试、95 项 Windows 测试和 127 项本机 CLI 进程检查通过。刷新率切换和 SDR 校准的实机验收仍待进行。[验收项](ROADMAP.md)

## 2026-11-25 · ICC 预览截止时间与工作进程恢复

- ICC 确认前重新读取关联。读取跨过截止时间时尝试恢复原 ICC，回退成功时记录 `preview_expired`。异常恢复期间，工作进程继续持有会话锁，Agent 可通过 `profiles observe` 观察进度。
- 工作进程已经中断且日志仍显示进行中时，`profiles confirm` 和 `profiles revert` 返回退出码 4 和 `recovery_required`。检测到中断时不会提交新的命令标记。已明确处于 `recovery-pending` 的日志保留原状态及原因。[CLI 契约](CLI.md)
- WinUI 与 CLI 构建、149 项核心测试、94 项 Windows 模拟测试和 127 项本机 CLI 进程检查通过。验证使用模拟后端与隔离数据。当前远程桌面会话没有枚举到活动显示器，本地 SDR 实机验证仍待进行。

## 2026-11-25 · 深色主题与向导无障碍通知

- 前台向导的步骤说明和比较状态在文字变化后发出 UI Automation 的 `LiveRegionChanged`。全屏窗口居于前台时，由全屏说明发出事件。同一段文字在界面反复刷新时不会重复发出事件。[Windows 实时区域示例](https://learn.microsoft.com/en-us/accessibility-tools-docs/items/uwpxaml/text_livesetting)
- Windows Sandbox build 26100 的普通用户运行 x64 诊断构建。按窗口订阅的 UI Automation 客户端收到六项预期通知，验证 Gamma、亮度及比较步骤的说明，读到了主窗口的原始和调整后状态，以及全屏的调整后状态。隐藏向导的对比度说明未发出事件，并核对了步骤选择、Gamma 滑块和数值框的英文无障碍名称。比较使用内存中的模拟会话，测试前后均无实际校准会话。[无障碍验收项](ROADMAP.md)
- 在 200% 缩放下实际切换 Windows 深色主题，逐像素检查主窗口和全屏的 Gamma、亮度、对比度共 12 张浅色与深色画面。图案均与预期像素相符，抽查的说明和按钮保持可读。报告位于 `artifacts/accessibility-sandbox/e1d93367-5681-402a-933c-1aa180290ccb/results/`。本机构建、237 项单元及模拟测试和 124 项 CLI 进程检查通过。Narrator 实际语音播报与 SDR 实机校准仍待验收。

## 2026-11-25 · 全屏图案的对比主题底栏

- 全屏图案底栏采用 WinUI 的 `SolidBackgroundFillColorBaseBrush`，随 Windows 主题更新。高对比黑色下，底栏空白区域由窗口默认的纯黑切换为与说明文字配对的系统窗口背景色。[Windows 对比主题指引](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes)
- Windows Sandbox build 26100 的普通用户运行 x64 诊断构建，在 200% 缩放下依次加载四个内置 Windows 对比主题。对 30 张画面逐像素检查，向导与全屏的 Gamma、亮度和对比度图案都与浅色基线及预期像素一致。对比主题下检查的说明、按钮和全屏底栏使用了各主题的系统配色。
- 沙盒恢复了原主题，诊断包已卸载，前后均无校准会话。报告位于 `artifacts/contrast-theme-sandbox/b7ebcd4c-f0ff-4169-bd27-0b17c71cf9d9/results/rendered-pixels.json`。本机构建、237 项单元及模拟测试和 124 项 CLI 进程检查通过。常规深色主题、Narrator 和实机 SDR 显示效果仍待验收。

## 2026-11-25 · 向导键盘与全屏焦点

- 普通用户在 Windows Sandbox build 26100 的 x64 诊断构建中通过 Windows 键盘输入测试。Gamma 和三个 RGB 通道都通过了滑块方向键、Tab 进入数值框、数值框方向键及 Page Up／Page Down 的检查。Gamma 也通过了上限、重置和返回后保留草稿的检查。
- 基线 `a478331` 中从亮度页按 Enter 跳过会把焦点送到侧栏。修正后，亮度和对比度两页都能将焦点送到红色滑块，方向键可立即微调，下一次 Tab 进入红色数值框。用 Escape、退出按钮和 Alt+F4 离开全屏参考图后，主窗口都恢复前台与全屏按钮焦点，并保留 Gamma 步骤。[键盘验收项](ROADMAP.md)
- 两次沙盒验证前后均无校准会话，诊断包已卸载。基线和修正报告分别位于 `artifacts/keyboard-interaction-sandbox/4b4ddf81-5c80-4ccd-91d0-1b808638bc44/results/keyboard-interaction.json` 和 `artifacts/keyboard-interaction-sandbox/ae4d587e-b9fb-4415-a294-3489a6dee7fe/results/keyboard-interaction.json`。237 项单元及模拟测试和 124 项本机 CLI 进程检查通过。Narrator、高对比度和 SDR 实机校准仍待验收。

## 2026-11-25 · Gamma 与 RGB 键盘步长

- Gamma 滑块的细调和粗调步长为 0.02 和 0.20，三个 RGB 滑块为 0.01 和 0.10。相应的数值框读取同一组步长，继续与滑块双向同步。[键盘验收项](ROADMAP.md)
- WinUI 与 CLI 构建、237 项单元及模拟测试和 124 项本机 CLI 进程检查通过。在 Windows Sandbox 的 x64 诊断构建中，通过读取实际加载的控件属性和程序化设置数值核对了四组步长及双向绑定。前后均无校准会话。报告位于 `artifacts/keyboard-xaml-sandbox/089b56cc-759d-4ced-b0a9-330fb2908aff/results/keyboard-steps.json`。实际按键和全屏返回焦点仍待交互验收。

## 2026-11-24 · Agent 会话观察

- `calibration observe` 和 `profiles observe` 通过 CLI v1 返回工作进程与会话状态，支持最多 60 秒的只读等待。观察未完成的曲线、ICC 预览或绑定补写时，检测工作进程是否仍持有锁。保留会话状态和原预览期限。[CLI 契约](CLI.md)
- 147 项核心测试、90 项 Windows 测试和本机独立执行的 124 项 CLI 进程检查通过。模拟覆盖工作进程持有预览、取消、等待超时、进程退出及确认后的绑定待处理。x64 开发 MSIX 通过内容检查，Windows Sandbox build 26100 的普通用户已执行新命令的发现与缺失会话检查。报告位于 `artifacts/sandbox-smoke/6abdbccd-ea7c-4752-85c0-f77f2a6d30d0/results/result.json`。实机曲线和 ICC 事务待验收。

## 2026-11-24 · 双架构 CI 证据与商店截图草稿

- GitHub Actions 从同一提交的对应源码生成 x64 和 ARM64 开发 MSIX、源码归档和 SHA-256 清单。提交 `c288968` 的两份 MSIX 都通过内容检查，下载后的源码和包与清单摘要一致。[GitHub Actions](https://github.com/Water-Run/DCCWpp/actions/runs/45428714828)
- 使用提交 `8aac9a7` 的 x64 `0.2.0.0` 开发包在沙盒拍摄三语各四张界面截图。图像为 2338×1197 PNG，附三语字幕和截图来源记录，草稿位于 `artifacts/store-screenshots/7020a608-c8b8-4705-b622-cf943a77001d/DCCWPlus-Store-screenshots-draft.zip`。正式签名候选和本地 SDR 比较图待采集。[商店素材](STORE_LISTING.md)

## 2026-11-24 · 显示路径重建后的本地记录恢复

- GUI、CLI 和登录恢复在补写已确认的保存或撤回记录时，可通过唯一的持久身份找到活动路径。补写前核对原 Windows 配置设备、当前有效 ICC、作用域和文件摘要。撤回后清理旧绑定时也核对恢复的 ICC。登录时遇到暂不可读的路径或 ICC，后台沿用最多两次短间隔检查。[CLI 本地记录恢复](CLI.md)
- 模拟测试覆盖原路径被复用、同屏迁移、设备或身份不符、作用域和文件变化、摘要读取期间关联变化、HDR，以及撤回到原来没有默认 ICC 的状态。147 项核心测试、90 项 Windows 测试、116 项 CLI 检查及双架构开发 MSIX 内容检查通过。
- x64 沙盒普通用户用装包 CLI 核对两份模拟确认记录。旧路径与当前 ID 不同时，同一持久身份得到 `remote_session_blocked`，缺失身份得到 `display_disconnected`。两条记录保持待处理，前后显示采样一致。报告位于 `artifacts/binding-replay-sandbox/c340362a-515b-43e7-a60b-83fe953737a8/results/binding-result.json`。本地 SDR 实机重启后的补写和曲线重新应用仍待验收。

## 2026-11-24 · 进入比较前同步最后一次调整

- 从向导进入比较时，工作进程用同一命令处理待提交的参数和步骤切换。先按编辑阶段的期限完成写入与读回，再开始比较倒计时。向导根据工作进程保存的阶段与曲线状态启用比较和保存。比较切换沿用截止时间，从原始视图返回编辑会恢复候选曲线。[比较流程](CLI.md)
- 模拟测试验证立即进入比较、更新期间到期、重复比较、返回编辑及 CLI 固定截止时间。147 项核心测试、79 项 Windows 测试和 116 项 CLI 进程检查通过。x64 与 ARM64 开发 MSIX 通过内容检查。
- Windows Sandbox 的 x64 装包向导在 200% 缩放下，将红色增益改为 0.93 后立即进入比较，再返回确认数值保留。远程会话的保存处于禁用状态，前后均为零个校准会话。真实 SDR 显示上的首次比较和驱动读回仍待本地验收。报告位于 `artifacts/review-sync-sandbox/73f10e91-3c99-4816-b4c9-a34534e7323a/results/review-result.json`。

## 2026-11-24 · ICC 切换后的曲线回退

- 保存和撤回在切换 ICC 后核对读回曲线。已匹配目标时直接继续，读到已记录的原曲线或线性曲线时可重新写入，遇到未知曲线则停止。回滚恢复原 ICC 时，只有原有曲线仍匹配、实际执行关联恢复且显示路径未变，才把新读到的线性曲线记入日志并尝试恢复原曲线。[CLI 曲线回退说明](CLI.md)
- 模拟测试覆盖线性重置后恢复非线性原曲线、首次回滚写入失败后重试、同一关联上的未知变化以及外部曲线和拓扑变化。实机仍需验证 Windows 触发重载及驱动写回。144 项核心测试、78 项 Windows 测试和 116 项 CLI 进程检查通过。
- x64 和 ARM64 开发 MSIX 通过内容检查。x64 沙盒普通用户通过包安装、CLI 别名和卸载检查，也安装、比对摘要并移除了测试 ICC。报告位于 `artifacts/sandbox-smoke/5482c452-8783-4868-967b-450576cf9aae/results/result.json`。

## 2026-11-24 · 预览工作进程异常退出

- 向导将工作进程退出与日志中的预览状态分开判断。进程退出后停止当前预览交互并显示恢复入口。CLI 提交或等待命令时返回 `recovery_required`，保留会话和原始请求。已完成确认而尚未写完本地绑定的会话仍返回 `confirmed` 和待处理的绑定状态。[CLI 恢复说明](CLI.md)
- Windows Sandbox build 26100 的普通用户用模拟预览日志和独立的锁持有进程测试 x64 装包 CLI。该进程退出后，等待确认的 CLI 返回退出码 4。随后从 WinUI 启动恢复，系统报告 `remote_session_blocked` 并保留待恢复记录。ICC 和拓扑采样前后一致。该测试没有写入显示曲线。报告位于 `artifacts/worker-exit-sandbox/eb073692-1e01-43be-b94a-e259657891f2/results/recovery-result.json`。
- 137 项核心测试、76 项 Windows 测试和 116 项 CLI 进程检查通过。x64 与 ARM64 开发 MSIX 通过内容检查。本地 SDR 实机仍需验证校准过程中结束工作进程后能恢复原逻辑曲线。

## 2026-11-24 · 撤回已保存校准时定位当前显示路径

- GUI 和 CLI 可用持久身份找回活动路径已变化的显示器。撤回要求同一个独占的 Windows 配置设备、当前用户选中已保存的 ICC、匹配的文件摘要和逻辑曲线。预览和确认使用当前适配器与源 ID，已开始的预览固定当前路径。无持久身份的旧记录继续要求原路径。[Windows 适配器 ID](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displayadapter.id) · [CLI 撤回说明](CLI.md)
- 137 项核心测试、74 项 Windows 测试及 115 项 CLI 进程检查通过。模拟覆盖适配器与源改变、共享配置设备、身份歧义、HDR、镜像和 CLI 重试。x64 与 ARM64 开发 MSIX 通过内容检查。x64 在 Windows Sandbox build 26100 通过普通用户安装、别名调用及卸载，报告位于 `artifacts/sandbox-smoke/40e05a03-f4c8-42f1-9fe1-f00d73a00027/results/result.json`。重启后在真实 SDR 显示器执行预览与撤回仍待本地验收。

## 2026-11-24 · 已保存校准的实时核对

- `calibration verify <calibration-id> --json` 用唯一的持久显示身份查找当前路径，将历史确认状态与 Windows 当前的默认 ICC、文件 SHA-256 和逻辑曲线分开报告。命令同时返回 Windows 校准加载器的开关状态，供 Agent 判断登录后需核对的项目。[CLI 核对契约](CLI.md)
- Windows Sandbox build 26100 的普通用户通过 x64 装包 CLI 核对测试 ICC。样本使用合成的历史确认记录，实时状态来自 Windows。更换当前用户默认 ICC 后，选择匹配由 `true` 变为 `false`。恢复原 ICC 后，选择与文件摘要重新匹配。注销并登录到会话 2 后，Windows 返回新的虚拟显示器身份，命令给出 `inconclusive`，没有将新显示路径视为原目标。本地 SDR 曲线和重登录后的实际重载仍待实机验收。
- 136 项核心测试、67 项 Windows 测试及 115 项 CLI 进程检查通过。x64 和 ARM64 开发 MSIX 通过内容检查。`verify-first.json`、`verify-second.json`、`relogin-after.json` 与 `capture-after-relogin.json` 位于本机 `artifacts/calibration-verification-sandbox/fde29374-b4fe-4d4d-bd03-d74ba48c668d/results/`。

## 2026-11-24 · ICC 关联回退与采样会话

- Windows Sandbox build 26100 的普通用户通过原生 Windows API 安装两个测试 ICC，将作用域切为当前用户并依次设为默认。随后恢复前一个默认值，移除两条测试关联并恢复最初的系统作用域。x64 装包 CLI 核对了每个阶段的 ICC 名称、作用域和 SHA-256。此次证据覆盖虚拟显示器的 ICC 关联及回退，实际校准曲线和物理显示效果仍待本地验收。[Windows ICC 关联文档](https://learn.microsoft.com/en-us/windows/win32/wcs/display-calibration-mhc#new-display-icc-profile-management-apis)
- 采样新增 `isRemoteSession`。远程报告保留 ICC 证据，曲线状态为 `remote_session_blocked`。跨本地与远程会话的比对返回 `inconclusive`。旧报告继续可用。当前工作会话也是远程桌面，早期 `curve_read_failed` 记录无法判断物理驱动能力。[CLI 采样契约](CLI.md)
- 136 项核心测试、61 项 Windows 测试和 113 项 CLI 进程检查通过。x64 和 ARM64 开发 MSIX 通过内容检查。沙盒原生和装包 CLI 报告位于 `artifacts/icc-association-sandbox/a675e1d8-f1ea-4e23-8f37-bee30cd9e588/results/`。

## 2026-11-24 · 高缩放下的向导操作区

- 步骤选择和全屏入口固定在上方，返回、前进、重置、跳过和校准事务操作固定在底部。较窄窗口中的底部操作分为两行。显示器选择位于准备步骤，图案高度随滚动区变化。Gamma 和 RGB 数值框按两位小数显示。[WinUI 响应式布局](https://learn.microsoft.com/en-us/windows/apps/develop/ui/layouts-with-xaml) · [NumberBox 数值格式](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/number-box#formatting-input)
- Windows Sandbox build 26100 普通用户在 200% 缩放下验证了宽窗和窄窗、滚动时操作区位置不变、三语跳过入口可见、下一步可获键盘焦点、切换页面与退出全屏后保留进度。参考浏览的校准会话数保持为零。实际预览的倒计时、取消和保存仍需在本机交互桌面验收。
- 135 项核心测试、59 项 Windows 测试、113 项 CLI 检查及 x64、ARM64 开发 MSIX 内容检查通过。包内交互测试报告与截图位于本机 `artifacts/wizard-navigation-sandbox/1e3512da-cb5a-4fc8-991a-885da5928655/results/`。

## 2026-11-24 · 原版 DCCW 的远程会话行为

- 在同一个 Windows Sandbox build 26100 普通用户会话运行原版 `dccw.exe` 和 DCCW++。原版版本 `10.0.26100.8875` 在打开向导前提示远程桌面无法校准。旧开发包在同一 SDR 目标上报告 `curve_read_failed`。新版在 GUI 禁用开始和撤回，保留参考图案及预设功能。装包 CLI 的 Gamma 诊断、Gamma 预演与 ICC 预演返回 `remote_session_blocked`。[Windows 远程会话检测](https://learn.microsoft.com/en-us/windows/win32/termserv/detecting-the-terminal-services-environment)
- 200% 缩放下浏览了新版六个参考步骤，验证跳过硬件调整、返回后保留 Gamma 草稿值，以及重置回 2.2。审核页的 ClearType 选项可见，保存处于禁用状态。测试前后均为零个校准会话。原版逐页对照和实际保存仍需在本地交互会话进行。
- 135 项核心测试、59 项 Windows 测试、113 项 CLI 检查及双架构开发 MSIX 内容检查通过。模拟测试覆盖预览切到远程后保留待恢复记录，以及回到原本地目标后恢复。`native-trace.json`、`remote-baseline.json`、`dccwpp-wizard-trace.json` 和截图位于本机 `artifacts/dccw-parity-sandbox/f28bf701-f080-4e07-acbc-64197b7cfdf5/results/`。

## 2026-11-24 · 普通用户登录启动与预设互通

- 在 Windows Sandbox build 26100 安装 x64 开发 MSIX `0.2.0.0`，输入 SHA-256 为 `74DECB3B075040F225F5039AE045D812533130BAB4B6B470939F3BE5D1EEA779`。同一普通用户在四个独立的交互会话中验证 WinUI 冷启动、设置页启停和实际重新登录。启用后的后台进程无可见窗口，CLI 先后读到 `sign_in` 的 `no_bindings_enabled` 和断开测试绑定的 `display_disconnected`。后者没有创建校准会话。
- 后台运行期间仍可打开前台设置。关闭登录任务后，后台退出。再次注销并登录，后台未重启，最近报告的时间保持原值。此次覆盖登录触发和拒绝不匹配目标，实际曲线恢复、睡眠唤醒和热插拔仍待实机验证。[登录验收步骤](STORE.md)
- 通过原生文件选择器导入示例预设，装包 CLI 核对新本地 UUID、Gamma、作者及说明。重复导入选中原条目，显式导入副本产生独立 UUID。导入前后都没有校准会话。`startup-summary.json` 与 `preset-import.json` 位于本机 `artifacts/gui-startup-sandbox/648ef88b-eafa-453b-a077-ad324ba4254a/results/`。

## 2026-11-24 · Agent 直接控制 SDR 预览

- `calibration preview` 接受显示器 ID、Gamma 和 RGB 增益，支持预演和请求 ID。`calibration update` 支持单项调参及切换原始和候选视图。CLI 创建的预览沿用首次设定的截止时间。三语文案和 CLI v1 命令发现已更新。[CLI 参考](CLI.md)
- 133 项核心测试、59 项 Windows 测试及 113 项 CLI 进程检查通过。模拟覆盖并发调参、请求重试、视图切换和取消后恢复。修复了 Windows 在 CLI 读取会话文件时偶发拒绝替换的问题，并验证已打开的读取仍取得旧版本，新读取取得完整的新版本。本机只读预演返回 `curve_read_failed`，没有生成事务。实际曲线写入及 ICC 保存仍待实机验证。
- x64 和 ARM64 开发 MSIX 通过内容检查。x64 包由 Windows Sandbox 普通用户安装，经 `dccwpp.exe` 别名发现新命令，并核对无目标预演返回 `not_found`，随后卸载。本机沙盒报告位于 `artifacts/sandbox-smoke/9c6ea1e8-9945-42e9-b76c-89d0f1f32550/results/result.json`。

## 2026-11-24 · WinUI 交互冷启动与 200% 缩放

- 在 Windows Sandbox build 26100 的交互桌面冷启动 x64 MSIX。初始窗口将逻辑尺寸按当前显示器 DPI 转为像素，导航标题使用 WinUI 页头。步骤和默认语言均显示已选项，折叠导航栏的版本和许可证信息移到设置页。[WinUI 导航页头](https://learn.microsoft.com/en-us/windows/apps/design/controls/navigationview) · [窗口 DPI 查询](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow)
- 在该沙盒的管理员账户中，经 UI Automation 验证了 200% 缩放下的冷启动、窄窗口、CLI 创建的预设在 GUI 可见、全屏图案打开和退出。切换三语时保留亮度步骤，重启后保留最后选择的繁体中文。三语界面和报告位于本机 `artifacts/gui-sandbox/1f8e6f12-2953-404e-9f36-d2dfc683c24c/results/`。
- 132 项核心测试、58 项 Windows 测试和 94 项 CLI 检查通过，x64 和 ARM64 开发 MSIX 通过内容检查。沙盒显示驱动返回 `curve_read_failed`。普通用户交互启动、实机曲线写入、默认 ICC 关联及多屏仍待验收。[发布手册](STORE.md)

## 2026-11-24 · 全屏校准图案与 .NET 运行时更新

- Gamma 圆点沿图案长边排列。暗部和高光阶梯分别以纯黑和纯白为背景，每个色块与背景有不同的数值。全屏图案显示本步说明，比较时可在原会话倒计时内切换曲线或取消并恢复。核对了生成图案的像素和横竖布局。全屏键盘操作、缩放及真实显示效果仍待交互验收。
- 使用 .NET SDK 10.0.404 重建。两个架构的 GUI 和 CLI 都随附 .NET 10.0.15，包内四份 `coreclr.dll` 的摘要与官方运行时包一致。验包脚本拒绝旧运行时。[微软安全公告](https://github.com/advisories/GHSA-p3jp-9v83-76w9)
- 132 项核心测试、58 项 Windows 测试和 94 项 CLI 检查通过。x64 和 ARM64 开发 MSIX 通过内容检查，x64 通过沙盒普通用户安装、别名调用和卸载。[向导验收条目](ROADMAP.md)

## 2026-11-24 · Microsoft Store 提交准备

- 新增英文、简体中文和繁体中文首发文案，核对了文本长度、隐私和支持链接。整理了截图清单及认证人员的预览、取消、保存、撤回、CLI 和登录恢复步骤。实际截图待交互桌面采集。[商店文案](STORE_LISTING.md)
- `-Store` 构建前核对本地 Partner Center 身份与源清单。验包脚本从 `.msixupload` 内读取实际 MSIX，核对版本、架构、入口、语言和依赖。验证拒绝开发身份、发布者不一致、无效版本和重复架构。
- 在隔离源码中以测试身份生成 x64 和 ARM64 的 `1.0.0.0` 上传归档，两份归档及独立 MSIX 均通过检查。130 项核心测试、58 项 Windows 测试和 94 项 CLI 检查通过。正式身份关联、实机验收和 Partner Center 认证仍待完成。[发布手册](STORE.md)

## 2026-11-24 · SDR 预演检查与 ICC 保存准备

- 向导与 CLI 的实际预览和 `apply --dry-run` 共用启动检查。在写入曲线前检查未结束的显示事务、逻辑曲线、配置设备、作用域和 ICC，并在内存中生成候选。正式保存会重新核对目标并使用同一生成流程。
- 130 项核心测试、58 项 Windows 测试和 94 项 CLI 检查通过。本次可访问的 SDR 显示器返回 `curve_read_failed`。只读预演没有创建会话或 ICC。采样缺少可用曲线，前后对比为 `inconclusive`。
- x64 和 ARM64 开发 MSIX 通过内容检查。Windows Sandbox 的普通用户通过 x64 包别名调用 CLI。生成的测试 ICC 可安装、通过 SHA-256 核对并移除。实际曲线写入和默认 ICC 关联仍待实机验收。[CLI 预演说明](CLI.md)

## 2026-11-24 · ICC 预览在布局变化后的恢复

- 恢复可在原显示器的目标和源路径上继续，即使桌面布局已经变化。新日志记录可唯一辨识的显示器，旧日志保留原拓扑检查。写入前核对当前候选 ICC 和原 ICC 的摘要，识别到不同的当前配置时保留系统选择。
- 129 项核心测试、51 项 Windows 测试和 92 项 CLI 检查通过。只读探测在本次会话的一台独立 SDR 屏上匹配了 ICC 查询和显示器列表的持久身份。x64 和 ARM64 开发 MSIX 均通过内容检查，x64 通过沙盒普通用户安装、别名调用和卸载。实际 ICC 写入和重连后的恢复仍待实机验收。[ICC 命令说明](CLI.md)

## 2026-11-24 · 已确认校准的本地记录恢复

- 校准确认前记录旧绑定。工作进程中断后，GUI、CLI 和登录恢复共用本地补齐流程，核对显示身份、当前 ICC 及摘要。恢复优先处理尚未结束的显示预览。补齐本地记录时保留之后的删除、改名和恢复开关更改。
- 119 项核心测试、51 项 Windows 测试和 92 项 CLI 检查通过。x64 和 ARM64 开发 MSIX 通过内容检查，x64 包由 Windows Sandbox 普通用户安装、通过别名调用并卸载。实际 ICC 关联和 Windows 登录、唤醒后的曲线恢复仍待实机验收。[CLI 恢复命令](CLI.md)

## 2026-11-24 · 预览占用时的后台恢复重试

- 后台遇到 `preview_busy` 时保留请求，每 15 秒重试，单批等待最多 31 分钟。等待时合并显示通知并保留唤醒原因。每次恢复前读取 Windows 自启动状态和最新逐屏绑定。
- 118 项核心测试、44 项 Windows 测试和 80 项 CLI 检查通过。模拟覆盖一分钟预览后补做恢复、禁用自启动、通知合并和等待上限。双架构开发 MSIX 通过内容检查，交互式登录和唤醒后的实际曲线恢复仍待验收。[恢复与诊断说明](CLI.md)

## 2026-11-24 · 显示路径解析与恢复触发

- 按 Windows 路径标志解码普通和虚拟源模式索引，核对返回模式的源 ID 与适配器。后台轮询覆盖活动路径、分辨率、位置、刷新率和色彩模式的变化。SDR 恢复预演与实际恢复均会跳过 MHC2。
- 在本机三屏扩展桌面，只读验证轮询和显示列表的分辨率、位置及 SDR 状态一致。代码变更前后的只读采样匹配。报告位于本机 `artifacts/sdr-validation/active-configuration-20261124/` 和 `artifacts/sdr-validation/source-mode-20261124-191947/`。
- 118 项核心测试、35 项 Windows 测试和 80 项 CLI 检查通过。双架构开发 MSIX 通过内容检查，x64 包通过沙盒普通用户安装、CLI 调用和卸载。仍需在交互桌面验收实际睡眠唤醒、色彩模式切换和驱动重置后的恢复。[恢复说明](ROADMAP.md)

## 2026-11-24 · SDR 采样比对

- `calibration compare` 可离线比较两份采样。新采样在 Windows 提供活动路径和桌面几何信息时记录全桌面的拓扑摘要。旧采样仍可做逐屏比较。缺少拓扑摘要时，逐屏匹配的结果保持可见，总体为 `inconclusive`。
- 在本机 Windows build 26340 上对三屏扩展桌面做两次只读采样。拓扑、ICC 关联和作用域、逻辑曲线证据均匹配。旧报告的三个逐屏结果匹配，拓扑判定为 `inconclusive`。报告位于本机 `artifacts/sdr-validation/compare-20261124-190915/`。
- 116 项核心测试、29 项 Windows 测试和 80 项 CLI 进程检查通过。双架构开发 MSIX 通过内容检查，x64 沙盒普通用户可通过安装后的别名比对采样。该沙盒有零个采样显示器，结果为 `inconclusive`。[采样和比对说明](CLI.md)

## 2026-11-24 · ICC 保存与回滚的文件校验

- 关联候选 ICC 前后比对 Windows 安装文件与事务记录的 SHA-256。登录绑定沿用已确认文件的摘要。回滚核对原有 ICC 和需要恢复的系统默认关联，证据不符时保留待恢复会话。旧中断日志按其中已有的摘要核对。
- 111 项核心测试、28 项 Windows 测试和 73 项 CLI 检查通过，双架构开发 MSIX 通过内容检查。模拟覆盖同名文件内容变化、文件缺失、系统默认配置更换和恢复重试。[原因码与回滚说明](CLI.md)
- Windows Sandbox build 26100 的普通用户安装测试 ICC 后，系统颜色目录中的文件与输入摘要一致，随后成功移除。实际显示器上的默认关联、系统加载和恢复仍待验收。

## 2026-11-24 · 向导预览与参考照片切换

- 切到参考照片、预设库或设置页时暂停预览续期。其他页面显示取消入口，主窗口的 Escape 使用同一条恢复路径。恢复待处理时提示返回校准页。
- 全屏图案跟随向导步骤，换屏时关闭旧图案窗口。比较期限从工作进程返回后立即显示，超时且已恢复时显示具体结果。
- 108 项核心测试、21 项 Windows 测试和 73 项 CLI 检查通过。x64 与 ARM64 开发 MSIX 通过内容检查。全屏切换、缩放、键盘焦点和实际显示恢复仍待交互验收。[验收矩阵](ROADMAP.md)

## 2026-11-24 · 多显示器窗口定位

- 主窗口启动时按当前显示器工作区居中并限制大小。移屏和全屏图案的定位使用 Windows 虚拟桌面坐标，包含副屏原点偏移和任务栏占用的空间。
- 用只读方式获取本机三屏的实际工作区。定位探针验证了正、负副屏偏移和小屏尺寸，探针只记录传给 WinUI 的边界。108 项核心测试、21 项 Windows 测试和 73 项 CLI 检查通过。x64 和 ARM64 开发 MSIX 通过内容检查。
- 普通用户的无头沙盒运行位于 Session 0。Windows 在创建应用激活对象时返回 `0x8000401A`。冷启动和实际窗口跨屏仍待交互桌面验收。[验收步骤](STORE.md)

## 2026-11-24 · Agent 重试撤回校准和 ICC 预览

- `apply`、`calibration undo` 和 `profiles preview` 均支持 `--request-id`。同一请求使用原会话和截止时间。冲突参数返回 `request_id_conflict`，预演不占用 UUID。
- 108 项核心测试、21 项 Windows 测试和 73 项 CLI 进程检查通过。模拟 ICC 工作进程验证重复启动只保留一个预览。实机显示写入仍待验收。
- x64 与 ARM64 开发 MSIX 通过内容检查。x64 开发包在 Windows Sandbox build 26100 中由普通用户安装，命令别名报告 CLI v1 及全部三条支持请求 ID 的命令，随后成功卸载。[CLI 参考](CLI.md)

## 2026-11-24 · 普通用户原地升级

- Windows Sandbox build 26100 的普通用户从 x64 开发包 `0.2.0.0` 原地升级到 `0.2.1.0` 测试包。新包保留同一数据目录、预设 UUID 及参数、已保存的显示观察、CLI 别名的包身份和登录任务的 `Disabled` 状态，随后成功卸载。
- `0.2.1.0` 是用于验收的清单版本。交互式冷启动、登录启动、原生 ARM64 运行与 Store 签名包升级仍待验收。[复测步骤](STORE.md)

## 2026-11-24 · 普通用户安装与登录任务查询

- 在 Windows Sandbox build 26100 中，普通用户安装了 DCCW++ 生成的 ICC。系统目录中的文件摘要与原件相符，随后由同一用户移除。
- 普通用户安装并卸载 x64 开发 MSIX，`dccwpp.exe` 别名以包身份运行。修复了 CLI 查不到登录任务的问题。两个可执行程序现归同一个应用 ID，冷安装后 `startup status` 返回 `Disabled`。
- 108 项核心测试、20 项 Windows 测试和 65 项 CLI 检查通过。双架构开发 MSIX 通过包内容检查。交互式 GUI 冷启动、登录触发、升级、原生 ARM64 运行及实际显示器 ICC 关联仍待验收。[复测步骤](STORE.md)

## 2026-11-24 · SDR 实机验收采样

- CLI `calibration capture` 可只读记录全屏幕或指定屏幕的 ICC、作用域、逻辑曲线摘要以及 Windows build 和拓扑。检测到采样期间变化时返回可重试的原因码。
- 在 build 26340 的三屏扩展桌面上保存两次样本，逐屏结果相同。三个逻辑曲线摘要与直接驱动读回相符，一个 ICC 摘要与系统目录中的实际文件相符。记录保存在本机 `artifacts/sdr-validation/`。这组样本用于后续校准前后比对。
- 108 项核心测试、20 项 Windows 测试和 65 项 CLI 检查通过。标准用户保存、取消、超时及撤回后的配对样本仍待实机验收。[验收路线图](ROADMAP.md)

## 2026-11-24 · 跨入口恢复预览

- 向导识别未结束的 Gamma 及 ICC 预览。CLI 可通过 `recovery status` 查询，也可通过 `recovery run` 调用与向导和登录恢复相同的逻辑。
- 原工作进程仍在执行显示操作时，恢复返回忙碌。单会话 ICC 恢复保留最初的请求文件。无法恢复的会话继续保留日志，待目标重新可用后重试。
- 108 项核心测试、17 项 Windows 测试和 63 项 CLI 检查通过。模拟检查覆盖活跃工作进程保护、两类日志恢复及断开后重试。
- x64 与 ARM64 开发 MSIX 通过内容检查，x64 通过沙盒预配和移除。装包后真实 ICC 的跨入口恢复仍待实机验收。

## 2026-11-24 · ICC 查询状态与作用域

- 查询默认 ICC 时区分 Windows 明确返回的无关联状态和读取失败。显示列表及 CLI 诊断分别报告 Windows 选用的作用域和实际 ICC 来源。
- 无法读取配置时，向导和 CLI 阻止新的 SDR 预览。恢复保留待处理日志，待状态可读后重试。预览日志记录的作用域发生外部变更时，会话标记为 `superseded`。
- 108 项核心测试、16 项 Windows 测试和 60 项 CLI 检查通过。本机只读核对了两台无显式默认 ICC 和一台有默认 ICC 的屏幕。普通用户实际保存、取消和撤回仍待实机验收。
- x64、ARM64 开发 MSIX 通过包内容检查，x64 通过沙盒预配和移除预配。

## 2026-11-24 · CLI 与向导的预览时限

- CLI 曲线会话沿用初次启动时发布的 `expiresAt`。更新参数、原始视图及重试不会重新计时。
- 向导的编辑心跳只对窗口拥有的编辑会话续期。比较倒计时和编辑都受会话启动后 30 分钟的上限约束。
- 新增回归用例覆盖跨截止时间的确认、迟到的心跳及多客户端更新。106 项核心测试、9 项 Windows 测试和 60 项 CLI 检查通过。双架构开发 MSIX 通过包内容检查，x64 通过沙盒预配及移除预配。

## 2026-11-24 · 色彩模式和硬件校准提示

- 采用 Windows 报告的活动色彩模式判断 SDR。明确报告 SDR 的普通屏可通过模式检查，信息冲突或模式未知时仍停在预览前。
- HDR 读取扩展 ICC 关联，SDR 和 WCG 读取标准关联。向导为 HDR 提供 Windows 设置和微软 HDR 校准入口，为 Advanced Color 提供系统设置指引。
- ICC 检查报告增加 MHC2 标签状态。检测到 MHC2 时，SDR 向导和 CLI 在写曲线前停止，并返回可查询的原因。
- 102 项核心测试、9 项 Windows 后端和模拟工作进程测试、60 项 CLI 检查通过。本机三个已连接目标的只读查询均报告 SDR。HDR 和 WCG 实机显示、跳转及校准行为仍待验收。
- x64 和 ARM64 开发 MSIX 已重新构建并通过内容检查。x64 包通过 Windows Sandbox 签名、预配及移除预配。

## 2026-11-24 · 商店开发包与对应源码

- x64 和 ARM64 开发 MSIX 已从归档源码构建并通过检查。包内 GUI、CLI 和 CLI 运行时的二进制架构与清单相符。两个包均包含依赖许可索引及相应声明。
- 100 项核心测试、2 项模拟工作进程测试及 x64 CLI 的 59 项进程检查通过。CLI 测试用隔离数据，只执行显示查询和预演。
- 在 build 26100 的 Windows Sandbox 中，x64 开发包用沙盒内临时证书签名，成功预配并移除预配。无交互登录环境未取得标准用户安装、装包别名或 GUI 冷启动结果。
- 后续候选版仍需通过 SDR 实机闭环、原生 ARM64 安装与运行、升级和商店认证。[商店发布手册](STORE.md)

## 2026-11-24 · 撤回已保存的校准

- 向导和 CLI 现在可为已确认的校准试用上一次曲线。确认后才恢复之前的 ICC、作用域和曲线。取消、超时或确认中断均走持久日志恢复。
- 撤回前检查原显示器、当前用户的已确认 ICC、实际曲线和配置文件摘要。旧记录缺少摘要时仍可查询和处理已有的中断预览，无法开启新的撤回事务。
- 撤回成功后清除仍引用该 ICC 的自动恢复绑定。预设库和系统中已安装的 ICC 文件保留。
- 100 项核心测试、2 项模拟工作进程测试和 59 项 CLI 进程检查通过。自动测试包括撤回取消、确认、无显式默认配置及 ICC 切换中断。真实 ICC 关联、标准用户权限及外接设备行为仍待实机验收。
- x64 与 ARM64 开发 MSIX 已在 `artifacts/packages/undo-preview-qa/` 重新构建并通过包内容校验。

## 2026-11-24 · Agent 请求重试与命令并发

- CLI `apply` 支持 `--request-id`。重试同一请求返回原会话状态，不会再次启动校准或延长时限。复用 UUID 提交其他参数有明确错误码。`--dry-run` 不占用请求 ID。
- 每个预览会话只有一个工作进程可以开始写入。GUI 和 CLI 命令等待前一条命令的回执，恢复走独立调用并保留请求原文。
- 91 项核心测试、2 项 Windows 模拟工作进程测试和 58 项 CLI 进程检查通过。覆盖重复工作进程、并发编辑、恢复原曲线和断开目标的请求重放。实际显示设置未用于自动化测试。
- 原输出目录在替换旧包时卡住。新增 `-PackageRun` 可写入独立子目录。x64 开发包已在 `artifacts/packages/agent-protocol/` 生成并通过内容校验。

## 2026-11-24 · 校准向导与预设库

- 向导新增显示模式和位置提示、目标屏验证、独立全屏测试图、三组 Gamma 示例、亮度和对比度跳过、RGB 数值输入。比较切换共用一次确认倒计时。
- GUI 和 CLI 支持重命名、删除、绑定查询和解除绑定。相同共享预设可复用本地副本，包括经本地重命名的条目。用户可显式导入独立副本。
- CLI 可直接提交 Gamma、RGB、作者和描述创建草稿。现有预设 JSON 的 schema 和导入结果字段保持兼容。
- 91 项核心测试和 48 项 CLI 进程检查通过，x64 开发 MSIX 已重新构建并通过内容校验。四个 CLI 进程并发导入同一文件，得到同一个本地 ID。真实校准写入、多屏定位与 DPI 视觉效果仍待验收。

## 2026-11-24 · 登录与显示事件恢复

- 安装版的 Windows 启动任务与逐屏恢复开关已接入设置页。启用后，后台在登录、唤醒和活动显示路径变化时使用同一恢复流程。停用 Windows 启动任务后，后台在下次检查时退出。
- 启动恢复与 GUI/CLI 的预览共用显示写入锁。先处理未完成事务，再核对当前显示器身份、SDR 路径、已确认 ICC 的选择和摘要，以及当前曲线是否属于已知基线。
- GUI 和 CLI 可分别查看绑定及最近的后台恢复结果。确认校准的结果会区分已选用的 ICC 和逐屏绑定保存情况。
- 81 项核心测试与 32 项 CLI 检查已通过。测试覆盖重复恢复、预演、启停绑定、目标重用、外部曲线替换和保留中断日志。x64 和 ARM64 开发 MSIX 均已构建，并验证包内包含后台监听所需依赖。
- 装包后的 StartupTask 激活、睡眠唤醒、热插拔及真实 ICC/曲线恢复仍待实机验收。

## 2026-11-24 · SDR 预览和提交恢复

- Gamma 日志记录驱动写入前后的曲线。恢复可识别中断前活动的预览，也可识别已经写入的新曲线。其他应用替换曲线或配置后，以当前状态为准。
- 拓扑变化会结束预览。原 SDR 目标、源路径和显示器身份仍一致时可以原路恢复，断开、镜像或切换颜色模式时保留待恢复状态。
- ICC 提交后读回同一显示器上的默认配置和逻辑曲线。中断回滚重新核对 ICC 作用域、关联目标和原始默认关联。
- 新增 CLI 只读命令 `calibration diagnose --display <id> --json`。本机三个已连接 SDR 目标均报告可读逻辑曲线表、支持 VCGT、系统校准加载器已启用。[CLI 文档](CLI.md)
- 71 项核心测试和 24 项 CLI 检查通过，x64 未签名开发 MSIX 已重新生成。实机写入、普通用户 ICC 安装、实际恢复和重启加载仍待验收。

## 2026-11-24 · 0.2 基线复验

- Release x64 构建通过，61 项核心测试和 22 项 CLI 进程检查通过。CLI 检查使用隔离数据，只执行查询或预演显示事务。
- x64 和 ARM64 未签名开发 MSIX 均已生成。核对了包身份、两种架构的 GUI 和 CLI 二进制、运行时、登录启动声明、执行别名、图标、PRI 和 GPL 文件。
- 英文 README、详细路线图和 Microsoft Store 发布手册已更新。下一个实现里程碑是 M1 的 SDR 实机校准与恢复闭环。[开发路线图](ROADMAP.md) · [商店发布手册](STORE.md)
- MSIX 安装验收、ARM64 真机运行、真实显示器写入和 ICC 恢复仍待验证。

## 2026-10-26 · 0.2 开发节点

按用户要求，在 0.2 SDR 校准向导与恢复事务节点收尾暂停。完整替代 DCCW 的目标仍未完成。

本轮新增代码：

- 原生 WinUI 六步向导：准备、Gamma、亮度、对比度、灰阶平衡、比较与保存。亮度/对比度使用显示器自身控制；支持步骤返回、数值微调、重置、全屏、原效果比较和 ClearType 入口。
- SDR Gamma 曲线组合、逻辑表读回检查、独立预览进程、编辑心跳、确认倒计时、取消、窗口进程退出恢复和持久中断日志。
- ICC 校准文件构建：保留原配置的颜色描述，替换校准与名称标签，写入 `vcgt`；包含 MHC2 的原配置会拒绝使用兼容写入路径。
- Windows ICC 安装、默认关联和作用域备份/恢复的代码；用户配置或显示目标发生变化时避免继续使用旧目标。
- GUI 与 Agent CLI 接入同一曲线事务。`apply` 现在会开启真实预览，`--dry-run` 才只查询计划。
- 确认后保存预设及本机绑定；EDID 序列号身份和设备路径回退身份。新增绑定保存和恢复服务代码，尚未接入登录、唤醒与重连入口。
- 本机参考照片选择与查看；简中、繁中、英语向导资源。
- 图标重做为正视薄边显示器与单个色彩立方体，包含 ImageGen 参考稿、SVG 和全套 PNG/ICO。旧版 SVG 作为历史参考保留。

已取得的验证证据：

- `./scripts/Build.ps1 -Configuration Release -Package -Test` 成功，61 项核心测试通过。
- 22 项 CLI 进程检查通过，只查询和预演，没有改动真实显示设置。
- 本机只读核对了 30 个 P/Invoke 函数导出；54 个 XAML 文案键在三个语言目录中均存在。
- 未签名开发 MSIX 的 GUI/CLI 入口、运行时、启动声明、执行别名、图标、PRI 和 GPL 文件校验通过。
- 产物为 `artifacts/packages/DccwPlus.App_0.2.0.0_x64_Test/DccwPlus.App_0.2.0.0_x64.msix`，约 127.0 MiB。
- 界面检查被用户按物理 Escape 键停止，本轮未继续进行界面自动化，未取得新版窗口截图或实际点击验收结果。

恢复开发时优先处理：

1. 完成 WinUI 页面、键盘、缩放及三语言实际交互验收；确认 Gamma 图案在各 DPI 下按物理像素显示。
2. 对原生写入进行受控硬件验收：临时曲线、实际 ICC 安装、作用域恢复、确认、取消、进程终止、驱动拒绝、外部配置变化和显示器断开。当前测试使用模拟后端，不能作为这些硬件行为已经通过的证明。
3. 审查 ICC 提交过程中失败的所有恢复分支，并补独立工作进程的跨进程故障测试。显示拓扑变化后的中断会话目前可能保留为待恢复，尚无完整重连修复流程。
4. 验证已接入的登录启动、唤醒、重连和逐屏恢复开关在装包后的实机行为。
5. 完成预设维护、正式摄影素材、HDR/MHC2 后端规划及原生 DCCW 行为对齐验收。
6. 完成 MSIX 安装/升级/卸载、WACK、ARM64、专属商店身份与签名发布准备。当前包使用开发身份，未签名，也未发布到 Microsoft Store。

后续实现以 [开发路线图](ROADMAP.md) 的里程碑和验收要求为准。
