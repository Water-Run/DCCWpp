# DCCW++ CLI 0.2

安装后的入口为 `dccwpp.exe`，应用名为 DCCW++。控制台宿主与 WinUI 使用同一套核心校准事务。

## Windows 执行别名

在安装了 DCCW++ 的 Windows 用户账户下运行 CLI。`dccwpp.exe status --json` 的 `data.packaged` 为 `true` 时，进程以 MSIX 包身份运行。

如果 Agent 进程的 `PATH` 缺少 WindowsApps，可用同一系统别名的完整路径。它随安装注册，不随包版本变动。

```powershell
$cli = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\dccwpp.exe'
& $cli status --json
& $cli capabilities --json
```

如果完整路径也不存在，请确认应用已安装在该账户。打开 Windows 设置并搜索“应用执行别名”（App execution aliases），启用 DCCW++ 后重试。别名关闭时，程序尚未启动，命令找不到的错误由调用方处理。[Microsoft 的执行别名说明](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-extensions#start-your-application-by-using-an-alias)

## Agent 调用

`dccwpp capabilities --json` 返回版本化的命令清单，可在无需打开 WinUI 时调用。每条命令的 `name` 是稳定标识，`usage` 是参数说明，`supportsDryRun` 和 `supportsRequestId` 表示该命令接受的选项。当前 `contractVersion` 为 1。命令清单反映程序提供的接口。当前显示器是否可执行校准，由 `calibration diagnose` 和 `apply --dry-run` 查询。

| `effect` | 执行命令时可能产生的效果 |
| --- | --- |
| `read` | 查询数据或验证输入 |
| `local-write` | 保存或修改本地文件、预设、显示观察、恢复偏好或 Windows 登录启动设置 |
| `ui-launch` | 打开交互式 WinUI 窗口并准备草稿 |
| `display-preview` | 开始或更新限时预览 |
| `display-change` | 确认、取消或恢复显示状态 |

Agent 从 `displays list` 选择明确的显示器，再用 `calibration preview --dry-run` 检查直接输入的参数，或从 `presets list` 取得预设 UUID 后执行 `apply --dry-run`。实际预览返回会话 ID 与 `expiresAt`。通过 `calibration observe` 查询工作进程和会话结果，并在得到用户选择后调用 `calibration confirm` 或 `calibration revert`。显示会话来自当前 Windows 用户和桌面。Agent 应读取退出码、`ok`、状态和原因码。`--request-id` 适用于 `calibration preview`、`apply`、`calibration undo` 和 `profiles preview`，可在客户端超时或响应丢失时查回同一个会话。

`calibration observe` 和 `profiles observe` 支持 `--wait-seconds`，范围为 0–60，默认 0。命令只读取会话与预览进程的锁。等待期间不会提交命令、改变显示状态或延长预览。响应的 `data.session` 与相应 `status` 命令的 `data` 同形，`data.outcome` 表示本次观察结果。

| `outcome` | 含义 |
| --- | --- |
| `in_progress` | 即时查询时，未完成的会话仍由工作进程持有 |
| `wait_timeout` | 本次等待用完，工作进程仍在处理会话 |
| `recovery_required` | 会话或已确认的本地绑定仍待处理，预览工作进程已释放锁或退出 |
| `finished` | 已观察到终态，具体结果见 `session.status` 和 `session.reasonCode` |

四种观察结果都返回退出码 0。会话不存在返回 `not_found` 和退出码 3。`finished` 时分别检查 `confirmed`、`reverted` 或 `superseded`。`in_progress` 时同时检查 `session.status` 和 `session.expiresAt`，工作进程可能正在保存或回退。需要恢复时，用相应的 `calibration recover` 或 `profiles recover`。

## 命令参考

查询与预设操作：

```text
dccwpp --help --json
dccwpp capabilities --json
dccwpp status --json
dccwpp displays list --json
dccwpp wizard open --display <display-id> --preset <preset-uuid> --dry-run --json
dccwpp wizard open --display <display-id> --preset <preset-uuid> --json
dccwpp displays snapshot --json
dccwpp displays snapshot show --json
dccwpp displays restore-plan --display <display-id> --json
dccwpp calibration diagnose --display <display-id> --json
dccwpp calibration capture [--display <display-id>] --json
dccwpp calibration compare <before.json> <after.json> --json
dccwpp calibration verify <calibration-id> --json
dccwpp calibration inspect --display <display-id> --json
dccwpp presets list --json
dccwpp presets create "Daylight" --json
dccwpp presets create "Daylight edited" --gamma 2.35 --red-gain 0.92 --author "Studio" --json
dccwpp presets show <preset-uuid> --json
dccwpp presets validate ./shared.dccw.json --json
dccwpp presets import ./shared.dccw.json --json
dccwpp presets import ./shared.dccw.json --copy --json
dccwpp presets rename <preset-uuid> "Work display" --json
dccwpp presets bindings <preset-uuid> --json
dccwpp presets delete <preset-uuid> --json
dccwpp presets export <preset-uuid> ./export.dccw.json --json
dccwpp startup status --json
dccwpp startup enable --json
dccwpp startup disable --json
dccwpp startup last-restore --json
dccwpp recovery status --json
dccwpp bindings list --json
dccwpp bindings enable <persistence-key> --json
dccwpp bindings disable <persistence-key> --json
dccwpp bindings remove <persistence-key> --json
dccwpp bindings restore --dry-run --json
```

`--data-dir <directory>` 指定存储目录，默认是 `%LOCALAPPDATA%/DCCW++`。包内 GUI 与 CLI 使用同一存储环境。导入只加入预设库，导出会保护已有文件和预设库中的记录。

### 从 Agent 交接到校准向导

`wizard open` 可选用 `displays list` 的显示器 ID 和 `presets list` 的预设 UUID，分别通过 `--display` 和 `--preset` 传入。直接调用会打开新向导，明确传入显示器时将窗口放到该屏。预设进入草稿，显示状态等到用户在向导开始预览时才会变更。使用装包的执行别名或开发版 WinUI 输出目录内的 `Cli/dccwpp.exe`。向导读取默认数据目录，传入不同的 `--data-dir` 会报告 `wizard_data_directory_mismatch`。非交互式 Windows 会话会报告 `wizard_desktop_unavailable`。

`--dry-run` 查询目标、预设和启动入口是否可用，返回 `canOpen`、`reasonCode` 及 `changesDisplay: false`。实际打开成功返回 `status: opened`、`processId`、`displayId`、`presetId` 和 `changesDisplay: false`。省略的 ID 字段为 `null`。成功需要新 WinUI 进程回执。传入的显示器或预设一开始就找不到时退出码为 3。打开过程中目标变化、窗口定位失败或回执超时会返回退出码 4 和对应的 `error.code`。超时后可先查看当前桌面是否已经打开窗口。此命令每次调用都会请求新窗口。[Windows 进程创建](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)

`presets create` 可用 `--gamma`、`--red-gain`、`--green-gain`、`--blue-gain`、`--author` 和 `--description` 创建草稿。数值使用英文小数点。重复导入同一文档会返回已有的本地预设，包括本地改名后的条目。旧版库中的精确副本也可复用。需要独立条目时加 `--copy`。已有命令返回的预设字段保持原样。

`presets rename` 保留 ID 和校准值。删除前可用 `presets bindings` 查询引用。即使已停用自动恢复，有绑定的预设也会返回 `preset_in_use`。用 `bindings remove` 解除引用后可以删除。解除绑定只停止管理本机恢复记录，已选用的 ICC 和当前显示状态继续保留。

`calibration diagnose` 只读取指定显示器和 Windows 校准加载器状态。输出包含 `windowsCalibrationLoaderEnabled`、`driverSupportsVcgt`、`logicalRampReadable`、`profileMhc2Present` 和 `reasonCode`。无法查询或不适用时对应字段为 `null`。能读取逻辑曲线表是实机验收的前置证据，实际写入及登录后加载需要分别验证。[Windows 校准加载器](https://learn.microsoft.com/en-us/windows/win32/wcs/display-calibration-mhc#enhanced-windows-display-calibration-loader)

当 Windows 报告当前会话为远程桌面时，`calibration diagnose` 返回 `remote_session_blocked`，逻辑曲线可读性为 `null`。Gamma 预演返回退出码 4 及相同原因码，ICC 预演计划返回 `canPreview: false`。在远程会话中发现中断的显示事务时，保留恢复记录供本地会话继续处理。显示列表、ICC 查询、采样和预设操作仍可调用。[Windows 远程桌面会话检测](https://learn.microsoft.com/en-us/windows/win32/termserv/detecting-the-terminal-services-environment)

`displays list` 对明确的 SDR、WCG 和 HDR 查询对应模式的默认 ICC。HDR 使用扩展关联，SDR 和 WCG 使用标准关联。对无法区分 SDR 与 HDR 的模式，配置文件字段为 `null`。可读配置的 `profile.mhc2Present` 表示标签存在，标签目录无法读取时为 `null`。如果当前 SDR 配置检测到 MHC2，`apply --dry-run` 返回 `canApply: false` 和 `mhc2_profile_blocked`，实际预览在写入曲线前返回相同原因码。[Windows Advanced Color 配置关联](https://learn.microsoft.com/en-us/windows/win32/wcs/advanced-color-icc-profiles)

`displays list` 和 `calibration diagnose` 同时报告 `selectedProfileScope` 和 `profileQueryStatus`。前者是 Windows 选用的作用域，`profileScope` 是实际返回的 ICC 所在作用域。后者可以是 `read`、`not-found` 或 `unavailable`。系统明确无默认关联时使用 `not-found`；查询未执行时用 `null`。读取失败会以 `profile_state_unavailable` 阻止 SDR 预览或暂缓恢复。[Windows 作用域查询](https://learn.microsoft.com/en-us/windows/win32/api/icm/nf-icm-colorprofilegetdisplayuserscope)

`calibration capture` 只读采集当前显示状态并输出到 stdout。默认采集全部活动显示器，可用 `--display` 只返回指定目标。报告记录 Windows build、架构、拓扑、每屏 ICC 和作用域。可读取时，`profileSha256` 是 ICC 文件的 SHA-256，`logicalRampSha256` 是按 `vcgt` 编码的逻辑 Gamma 曲线的 SHA-256。摘要可用于核对前后是否逐项相同。即使摘要相同，光学结果和驱动行为仍需单独验收。采集期间检测到状态变化时返回 `capture_state_changed`，待状态稳定后可重试。不可读的曲线用 `curveStatus` 解释，摘要为 `null`。[Windows 逻辑曲线读回](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getdevicegammaramp)

Windows 提供足够的活动路径和桌面几何信息时，采样还会记录 `topologySha256`。摘要涵盖全桌面的目标和源、分辨率、位置、刷新率及色彩模式，即使只返回某一屏的采样。旧采样没有该字段时仍可比较逐屏证据。

`activeDisplayCount` 是此次探测到的活动显示器总数。使用 `--display` 时，`displays` 只包含指定目标，`activeDisplayCount` 仍记录总数。请用新版比较命令读取含此字段的采样。新版也支持缺少此字段的旧采样。

采样的 `isRemoteSession` 记录 Windows 是否将当前会话识别为远程桌面。远程采样保留可读取的 ICC 关联、作用域及文件摘要，曲线状态为 `remote_session_blocked`，曲线摘要为 `null`。如果采样期间会话类型改变，命令返回 `capture_state_changed`。

`calibration compare` 只读取两份采样文件。它支持 `--json` 输出的成功封装和未封装的采样，单个文件上限为 256 KiB，接受 UTF-8 和带 BOM 的 UTF-16 LE。无效采样返回退出码 2 和 `invalid_capture`。可按以下方式保存样本并比对。

```powershell
dccwpp.exe calibration capture --json > before.json
# Capture after.json after the test action has completed.
dccwpp.exe calibration capture --json > after.json
dccwpp.exe calibration compare before.json after.json --json
```

比对按 `displayId` 配对，逐屏返回 `profileStatus`、`logicalRampStatus` 和前后原始条目。只有一侧有条目时报告 `before-only` 或 `after-only`。检测到持久显示身份变化时报告 `identity-changed`。色彩模式或身份变化时不比较该屏的 ICC 和曲线。两次都明确无默认关联且作用域一致时，ICC 状态可以匹配。仅缺少摘要或无法查询时返回 `inconclusive`。

退出码 0 表示成功生成比较报告。`status: matched` 需要拓扑摘要、Windows build 与架构一致，并且两侧采样的显示器均有匹配的 ICC 和曲线证据。`changed` 表示采样发现明确变化，其余情况为 `inconclusive`。若一份报告来自远程桌面而另一份来自本地，`sameSessionType` 为 `false`，整体比较为 `inconclusive`。旧报告缺少会话字段时，`sameSessionType` 为 `null`，其余字段沿用原比较规则。`colorAccuracyVerified` 固定为 false。

验收未选中屏幕时，请用同一 Windows 用户和桌面采集全桌面，核对 `status: matched` 和 `fullDesktopCoverage: true`。只有两份采样都包含本次探测到的每台活动显示器且至少有一台时，覆盖字段才为 `true`。任一份只采集部分显示器或未探测到显示器时为 `false`。缺少旧报告的总数时，无法确定覆盖范围的情况返回 `null`。覆盖字段只表示采样范围，逐屏证据仍以对应的比较结果为准。

`calibration verify <calibration-id>` 只读核对已确认的 SDR 校准。`calibration-id` 是已确认会话的 `sessionId`。命令将历史 `savedStatus` 与实时 `status` 分开报告。保存记录含持久显示身份时，命令用唯一匹配的当前路径进行核对。没有持久身份的旧记录继续要求原显示路径。

会话或 ICC 关联备份记录的 Windows 配置设备也必须唯一匹配。设备不符、不可读或被其他显示路径共用时，返回 `inconclusive` 和 `profile_device_unavailable`，目标 ID 与比较标志为空。较早的会话可以从关联备份获得设备信息。[Windows 默认 ICC 查询](https://learn.microsoft.com/en-us/windows/win32/api/icm/nf-icm-colorprofilegetdisplaydefault)

如果本机正在执行其他显示操作，已确认校准的核对返回退出码 4 和 `preview_busy`。显示操作结束后，若还有可能已经写入的 Gamma 或 ICC 预览等待回滚，核对返回 `inconclusive` 和 `recovery_required`，`capturedAt` 为空。用 `recovery status` 查找未结束的会话。只留下 `prepared` 日志或只待补写本地绑定时，仍可核对当前显示状态。

实时报告分别给出 `profileSelectionMatches`、`profileFileMatches` 和 `logicalCurveMatches`。`null` 表示该项缺少证据或不可比较。`matched` 需要三项均为 `true`，其中逻辑曲线使用与保存验证相同的读回容差。在本地独立 SDR 路径发现差异时报告 `changed`。证据不足时报告 `inconclusive`，通过 `reasonCode` 指明远程会话、显示器断开、身份不唯一或缺失的证据。远程匹配的 ICC 关联和文件摘要仍可单独报告。`windowsCalibrationLoaderEnabled` 是当前 Windows 加载器设置，`colorAccuracyVerified` 固定为 `false`。退出码 0 表示已生成实时报告，仍需读取 `status`。

`calibration inspect --display <display-id>` 与向导第一步的「检查已保存的校准」使用同一条只读路径。用 `displays list` 取得当前显示 ID。按显示器与配置设备定位已确认的校准，优先选择 ICC 文件名与当前 Windows 选用名称相同的记录，其次选择该设备最近一次确认的保存。缺少配置设备证据的旧日志只可在原活动路径下用作比对。命令输出 `displayId`、`reasonCode`、`capturedAt`、`presetName` 和 `verification`。可比较时，`verification` 提供与 `calibration verify` 相同的报告。`no_saved_calibration`、`display_identity_ambiguous`、`profile_device_unavailable` 和 `recovery_required` 均无比对报告，`verification` 为 `null`。目标未连接时返回退出码 3，显示操作正在进行或采样状态发生变化时返回退出码 4。命令只读取所选屏的 Gamma 表，仍检查活动拓扑以判断身份和配置设备是否唯一。

已确认校准的逐屏绑定可在设置或 `bindings` 命令中启用。启用绑定仅保存偏好。Windows 登录任务由设置页单独管理。确认会话的 `bindingStatus` 表示本机绑定保存情况。旧日志中该字段可能为空。

`bindingStatus: pending` 表示显示事务已经确认，本地预设或自动恢复绑定仍待完成。`calibration recover <session-id>` 可再次处理。常规保存会核对目标、当前 ICC、文件摘要和确认时保存的旧绑定。记录已存在时保留其恢复开关和保存时间。用户通过 GUI 或 CLI 移除绑定、删除预设或更改待清理的撤回绑定时，之前的写入意图会结束。旧日志缺少意图且没有已写入的记录时，以 `binding_intent_unavailable` 结束检查。若显示器断开或仍有中断预览，命令返回退出码 4，确认状态仍可通过 `calibration status` 查询。

确认回执错误或超时后，`calibration confirm` 会核对会话日志。日志已记录 `confirmed` 时，返回该结果及当前 `bindingStatus`。需要再次检查本地记录时，使用同一会话 ID 查询或恢复。

保存、撤回和逐屏恢复都在确认日志写入临时文件后、发布 `confirmed` 前核对目标、逻辑曲线、有效 ICC 及作用域，再核对预览期限与当前权限。核对还包括保存或恢复所用 ICC 的摘要。撤回同时核对每用户默认关联，即使 Windows 当前选用系统作用域。发现关联、文件或曲线变化时进入回滚。保存和撤回的 CLI 返回退出码 4，可能报告 `profile_selection_changed`、`profile_file_changed` 或 `profile_file_unavailable`。用 `calibration status` 查看是否仍需恢复。[Windows 的作用域查询](https://learn.microsoft.com/windows/win32/api/icm/nf-icm-colorprofilegetdisplayuserscope)

Windows 重新分配活动路径后，本地记录恢复按唯一的持久显示身份查找设备，并核对 Windows 配置设备、有效 ICC、选用作用域及文件摘要。撤回后的补写同样会核对恢复到的 ICC，再移除旧绑定。该步骤用已有确认记录中的曲线保存或清理本地数据。实际重新应用曲线由独立的显示恢复步骤执行并读回验证。登录时若遇到正在加载的显示配置或 ICC，后台最多追加两次短间隔检查。

`startup status` 从当前 MSIX 包查询登录任务。未安装版返回 `unpackaged`，Windows 未报告任务时返回 `unavailable`。[Windows 包范围的 StartupTask 查询](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask.getforcurrentpackageasync)

`startup enable` 和 `startup disable` 显式启停当前 Windows 用户的 DCCW++ 登录任务。成功返回与 `startup status` 相同的字段，可重复调用。命令使用 Windows 的包任务接口，作用域由当前用户和包标识决定。逐屏恢复开关通过 `bindings enable` 和 `bindings disable` 控制。`--data-dir` 只影响应用数据目录。[Windows StartupTask](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask?view=winrt-26100)

若所请求状态未达成，命令返回退出码 4 和原因码。`startup_unavailable` 表示未装包或 Windows 未报告任务。`startup_disabled_by_user` 表示需通过 Windows 的启动应用设置重新启用。`startup_managed_by_policy` 表示组织策略控制，`startup_state_unchanged` 表示 Windows 未确认请求。用 `startup status` 可查看当前状态。[Windows 启动任务启用限制](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask.requestenableasync?view=winrt-26100)

成功响应反映查询时有效的 Windows 状态。管理员禁用任务时，`startup disable` 成功返回 `DisabledByPolicy`，原有用户偏好仍由 Windows 保留。若策略随后解除，请用 `startup status` 重新查询。[Windows StartupTask 状态](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptaskstate?view=winrt-26100)

DCCW++ 设置页在状态为 `DisabledByUser` 时提供直达 Windows 启动应用设置的链接。返回 DCCW++ 时可查看更新后的任务状态。[Windows 设置入口](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)

`bindings restore --dry-run` 生成恢复报告。去掉 `--dry-run` 会先尝试恢复中断预览，再为已启用且符合条件的绑定写回已确认的绝对曲线。只有原目标可唯一识别、当前为独立 SDR 路径、已确认的用户 ICC 仍被选用且摘要一致、当前曲线匹配已知基线时才写入。最后一次读回若发现 ICC 或作用域变化，所选显示器报告 `skipped` 和 `profile_selection_changed`。报告通过 `reason` 和每屏 `status` 表示结果，退出码 0 表示已生成报告。Windows 登录任务使用同一路径。`startup last-restore` 返回后台最近一次报告及触发原因，尚无报告时为 `null`。

新保存的绑定还记录 Windows 配置设备的摘要。若当前配置设备不同或暂不可读，恢复及预演以 `profile_device_unavailable` 跳过该屏。旧绑定可从匹配的确认会话和 ICC 关联备份读取该摘要。缺少这些记录时，旧绑定仍按既有的显示身份和 ICC 核对规则恢复。保存新的校准时，原绑定的恢复开关只有在能核对为同一配置设备时才会沿用。[Windows 配置关联作用域](https://learn.microsoft.com/en-us/windows/win32/api/icm/nf-icm-wcsgetuseperuserprofiles)

后台定期只读检查活动路径、分辨率、桌面位置、刷新率和色彩模式。即使系统未发通知，检测到变化也会触发 `display_changed` 并重新核对绑定。路径查询短暂不可用后重新可读时，原布局也会重新检查。SDR 路径检测到 MHC2 时，预演和实际恢复都会报告 `mhc2_profile_blocked`。[Windows 显示路径与模式](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/relationship-of-mode-information-to-path-information)

逐屏报告以 `profile_file_unavailable` 标识 ICC 文件无法读取，以 `restore_data_unavailable` 标识预设等本机数据无法读取。后台遇到 ICC 文件、关联状态或逻辑曲线暂时不可读时，每隔 3 秒重试，最多两次。确认摘要不匹配后保留 `profile_file_changed`，等待后续显示事件或手动操作。

后台收到 `preview_busy` 后每 15 秒重试，同一批请求最多等待 31 分钟。等待期间合并显示通知，保留 `resume` 作为优先触发原因。每次尝试前重新读取 Windows 自启动状态和当前绑定。关闭自启动会结束待处理请求，最近一条后台报告可显示 `startup_disabled`。手动执行 `bindings restore` 仍立即返回本次结果。[Windows 登录任务状态](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptaskstate?view=winrt-26100)

开始处理待恢复事件前，若 Windows 自启动状态读取失败或超过 3 秒，后台会报告 `startup_unavailable` 并保留事件。3 秒后重新查询，仍不可读时转为每 15 秒查询。等待期间可合并唤醒及显示器变更通知。Windows 确认启用后重新评估，确认禁用后停止后台。[Windows 启动任务查询](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask.getforcurrentpackageasync?view=winrt-26100)

登录恢复在驱动写入前和最终读回后复核 Windows 权限。发现自启动关闭时停止当前批次并报告 `startup_disabled`。若复核超时或 Windows 的任务状态暂不可读，报告 `startup_unavailable`，后台按短间隔规则重试。在处理显示器期间停止时，尚未写入的显示器报告 `skipped`，已写入且仍由当前事务持有的曲线按原日志回滚。回滚未完成时，恢复报告仍显示 `recovery_required`。手动 `bindings restore` 使用逐屏恢复开关。

校准操作：

```text
dccwpp calibration preview --display <display-id> --gamma 2.30 --red-gain 0.97 --dry-run --json
dccwpp calibration preview --display <display-id> --gamma 2.30 --red-gain 0.97 --seconds 60 --request-id <request-uuid> --json
dccwpp apply <preset-uuid> --display <display-id> --dry-run --json
dccwpp apply <preset-uuid> --display <display-id> --seconds 30 --json
dccwpp apply <preset-uuid> --display <display-id> --seconds 30 --request-id <request-uuid> --json
dccwpp calibration sessions --json
dccwpp calibration status <session-id> --json
dccwpp calibration observe <session-id> --wait-seconds 15 --json
dccwpp calibration update <session-id> --preset <preset-uuid> --json
dccwpp calibration update <session-id> --green-gain 0.96 --json
dccwpp calibration update <session-id> --original --json
dccwpp calibration update <session-id> --adjusted --json
dccwpp calibration confirm <session-id> --json
dccwpp calibration revert <session-id> --json
dccwpp calibration recover <session-id> --json
dccwpp calibration undo <calibration-id> --dry-run --json
dccwpp calibration undo <calibration-id> --seconds 30 --request-id <request-uuid> --json
```

`apply --dry-run` 与向导、CLI 的实际预览共用启动检查。检查未完成的显示事务、SDR 路径、逻辑曲线、默认 ICC 及作用域，并在内存中生成候选 ICC。`canPreview: true` 表示这些检查通过。驱动写入和 Windows 默认关联在实际操作后验证。HDR 或 MHC2 继续返回 `canApply: false` 与原因码。其他阻碍通过错误封装返回，例如 `recovery_required`、`profile_device_unavailable`、`profile_file_unavailable` 和 `icc_base_unsupported`。预演不占用请求 ID，也不生成磁盘 ICC。

Gamma 校准要求目标屏的 Windows 配置设备在活动路径中唯一。多个活动路径共用设备时，预演返回 `profile_device_unavailable`。确认保存或恢复时，会在写入 ICC 关联和作用域前重新核对设备。[Windows WCS 配置作用域](https://learn.microsoft.com/en-us/windows/win32/wcs/wcs-registry-keys)

Gamma、ICC 和撤回预演都先检查显示操作是否忙碌，以及是否有待处理的预览日志。忙碌时原因码为 `preview_busy`，有未结束的预览时为 `recovery_required`。撤回预演通过 `canPreview: false` 和 `reasonCode` 报告，Gamma 和 ICC 预演通过退出码 4 和 `error.code` 报告。实际预览会重新检查。

`calibration preview` 使用同一套检查。Gamma 范围为 1–3，默认 2.2。`--red-gain`、`--green-gain` 和 `--blue-gain` 范围为 0.5–1.5，默认 1。预演通过或因色彩模式被阻止时，`data` 含 `displayId`、`calibration`、`canPreview`、`reasonCode`、`timeoutSeconds` 和 `changesDisplay`。直接输入只在事务中暂存，在确认保存后可出现在预设库。

**未带 `--dry-run` 的 `apply` 和 `calibration preview` 会实际写入所选 SDR 显示器的临时 Gamma 曲线。** 命令返回 `sessionId` 和固定的 `expiresAt`。CLI 时限为 10–60 秒，更新参数、切回原曲线和确认都沿用同一时限。`confirm` 保存 ICC 并在 Windows 中选用，`revert` 取消尚未确认的预览，`recover` 尝试恢复中断会话。

新生成的 ICC 描述包含会话短编号和预设名。自动化查找仍使用 `installedProfile` 的 `DCCWPlus-<会话 UUID 去除连字符>.icc` 文件名。ICC v2 中，无法用 ASCII 表示的预设名保存在 Unicode 描述里，ASCII 字段保留短编号。[ICC 描述格式](https://www.color.org/icc_specs2/)

Gamma 工作进程会在读取显示状态、写入预览日志后以及调用驱动前复核期限。首次预览若在任何写入获准前到期，日志以 `reverted` 和 `preview_expired` 结束，`curveReadbackVerified` 为 `false`。更新或确认跨过期限时，工作进程只按已获准的曲线写入判断能否回退。若其他应用写入了恰好与未获准更新相同的曲线，会保留外部曲线。如果驱动写入已开始，则在返回后到期时尝试回退。[Windows Gamma Ramp 文档](https://learn.microsoft.com/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp)

保存或撤回时，在安装 ICC、修改关联与作用域、重新写入曲线以及发布确认日志之前复核期限和窗口所有者。若 Windows 调用已开始，则等该次调用返回后尝试回退，保留 `preview_expired` 或 `owner_exited` 原因。目标或配置冲突时，会话仍按恢复规则记录结果。ICC 安装和选用是独立操作，已经安装的文件可能留在 Windows 中。用 `calibration status` 查询回退或待恢复的状态。[安装 ICC](https://learn.microsoft.com/en-us/windows/win32/api/icm/nf-icm-installcolorprofilew) · [关联 ICC](https://learn.microsoft.com/en-us/windows/win32/api/icm/nf-icm-colorprofileadddisplayassociation)

`calibration update` 可通过数值选项调整当前普通预览，仅替换传入的参数。也可用 `--preset` 切换到已保存的预设。`--original` 查看原始曲线，`--adjusted` 查看当前候选曲线，单独使用只切换视图。成功响应中 `calibration` 是候选预设参数，`showingOriginal` 表示当前视图。若预览已经结束或在更新时进入回退，命令返回退出码 4，`error.code` 记录原因，例如 `preview_expired`。确认提交候选曲线。撤回已保存校准使用 `calibration undo`，它的候选曲线来自原校准备份。

Agent 可以在第一次实际预览前生成一个 UUID 并传入 `--request-id`。该 UUID 同时是会话 ID。发生客户端超时或输出丢失时，使用同一个 UUID、预设 ID、显示器 ID 和超时值重试。`apply` 会返回持久日志中的现有会话，包括已超时、已确认或待恢复的会话，以上次保存的状态为准。原显示器断开或原预设已删除时仍可查询已经创建的会话。复用 UUID 提交其他目标、预设或时限会返回 `request_id_conflict`。需要发起新的预览时使用新 UUID。`--dry-run` 不占用请求 ID。

直接预览重试时使用同一 UUID、显示器、时限和最初的有效数值。重试返回该会话的当前状态及当前候选值。直接预览、预设应用与撤回共用校准会话 ID 范围，跨命令复用 UUID 会返回 `request_id_conflict`。

同一会话的命令由 GUI 和 CLI 共同串行发送。遇到 `command_busy`、`worker_start_timeout` 或 `worker_not_responding` 时，先查询会话状态。状态为 `recovery-pending` 时，在目标显示器重新可用后执行 `calibration recover`。恢复会保留最初的请求记录。应用在同一个 Windows 会话内串行执行实际显示写入，忙时返回 `preview_busy`。

新启动的 Gamma 或 ICC 预览只有在期限未过且会话仍由工作进程持有时才会返回可用的 `previewing`。若启动时已经到期，客户端等待回退结果。工作进程退出时，优先返回该次启动的错误回执。若进程已退出且只留下未完成日志，返回退出码 4 和 `recovery_required`。以相同请求 ID 重查已存在的会话时，仍返回当前持久状态。

工作进程已经退出时，`calibration status` 仍会显示最后保存的状态，可能是 `previewing` 或 `committing`。等待或提交命令时如确认工作进程已退出，CLI 返回退出码 4 和 `recovery_required`。此时使用 `calibration recover <session-id>` 检查并恢复原目标。向导也会退出操作中的预览并显示恢复入口。

`recovery status` 列出尚未结束的曲线及 ICC 预览和待处理的本地绑定记录，`kind` 分别为 `calibration`、`profile` 和 `binding`。`recovery run` 尝试处理这些会话，也可在向导里点击恢复。处理 `binding` 记录只更新本机状态。`bindings restore` 和登录任务也会先处理这些记录，再按已启用的绑定恢复显示。命令返回完整报告，调用方可用 `reason` 判断是否为忙碌 `preview_busy`、仍需恢复 `recovery_required`、处理完毕 `recovery_finished` 或无待处理会话 `no_recovery_needed`。`recovery_finished` 表示检查结束，各条目的 `status` 和 `reasonCode` 记录结果。仍在运行的预览由原工作进程处理。

中断时只留下 `prepared` 日志的预览尚未开始写入。恢复会结案为 `reverted`，原因是 `preview_not_applied`，并将 `curveReadbackVerified` 或 `associationVerified` 保持为 `false`。这一步只写入会话结果，显示器断开或处于远程桌面也可以完成。若最后的日志已记录 `applying`，则继续按可能发生过写入的事务检查和恢复。

批量恢复和单会话恢复会持有被处理会话的工作锁，包括补写已确认的绑定。`observe` 可以等待正在进行的回退或补写结果。已有工作进程持有该会话时，恢复保留日志和当前显示状态并报告 `preview_busy`。对正在回退的 ICC 会话执行确认或取消时，命令等待该次回退的结果。

GUI 在校准页编辑时使用心跳维持预览，单次会话最多 30 分钟。切到其他页面会暂停心跳，可使用该页面的取消入口。进入比较页后，原始与调整后视图共用最多 30 秒的确认倒计时，且受单次会话总时限约束。`showingOriginal` 记录比较视图选择。对于由窗口拥有的 Gamma 或 ICC 预览，工作进程会在写入前核对进程 ID 与启动时间。窗口进程已退出时会结束预览并记录 `owner_exited`。已经开始写入时仍尝试回滚。CLI 发起的预览继续由工作进程按原期限处理，调用方退出后仍可用会话 ID 查询。工作进程自身中断后，需要读取持久日志并恢复。显示器不匹配或不可用时保留待恢复状态。

向导切换到比较时，工作进程先在当前预览期限内应用尚未提交的控件值，成功读回后才进入比较倒计时。如果原预览先到期，则恢复原曲线。切换原始与调整后视图沿用当前比较期限。CLI 发起的预览继续使用最初设定的截止时间。

拓扑变化会结束活动预览。恢复会核对原始显示器身份、目标和源路径、SDR 模式、ICC 关联和曲线。兼容旧版会话日志。检测到其他程序选择了新配置或写入新曲线时，状态可变为 `superseded`。

Windows 提供完整活动路径与桌面几何信息时，Gamma 和 ICC 预览日志的 `original.activeTopologySha256` 使用与 `calibration capture` 相同的全桌面摘要。只改变刷新率也会结束活动预览。原显示器身份及独立 SDR 路径仍匹配时，可按回滚检查恢复。会话记录了该摘要但缺少可验证的持久身份时，需要原拓扑可再次验证。旧会话沿用原有拓扑检查。

恢复中断的预览时，如果日志同时保存了持久显示身份和 Windows 配置设备摘要，可在同一显示器的新活动路径上处理。恢复要求这两项在当前桌面均能唯一匹配，继续核对独立 SDR、ICC 作用域和相关文件摘要或逻辑曲线。失败可能报告 `display_disconnected`、`display_identity_ambiguous`、`profile_device_unavailable` 或 `recovery_target_changed`。旧日志缺少设备证据时继续使用原路径。会话 ID 和日志中的原始目标保持原样。

`calibration undo` 接收已确认的原校准会话 ID。先用 `--dry-run` 查看 `canPreview` 和 `reasonCode`。去掉预演选项后会启动一个新会话并临时显示之前的曲线。对新会话执行 `confirm` 才会恢复之前的 ICC、作用域和曲线。`revert`、确认时限结束或预览进程退出会触发回退。撤回会话会在 JSON 中记录 `undoOf`。撤回成功会清除仍指向被撤回配置的自动恢复绑定，已保存的预设和 ICC 文件仍可保留。

撤回可用 `--request-id` 指定新会话的 UUID。以相同 UUID、原校准 ID 和时限重试会返回已有会话，不会再启动预览或延长时限。`apply` 和 `calibration undo` 共用会话 ID 范围。尝试复用已被另一条校准请求占用的 UUID 会返回 `request_id_conflict`。预演不占用 UUID。

撤回前会核对实际选中的已确认 ICC、曲线、显示器身份和配置文件摘要。Windows 重建活动路径时，撤回以唯一的持久显示身份查找当前路径，并要求同一个 Windows 配置设备仍独占该作用域。预演可用时，`displayId` 为当前目标的 ID。无持久身份的旧记录要求原路径。旧版会话缺少摘要时报告 `undo_backup_unavailable`。撤回开始后固定当前路径，后续拓扑变化仍按活动预览规则处理。

保存时会在设置默认关联前后比对新 ICC 的安装摘要。回滚会检查事务已记录的 ICC 摘要和将要恢复的系统默认关联。`profile_file_changed` 表示同名文件的内容有变化，`profile_file_unavailable` 表示无法读取，`previous_system_profile_changed` 表示原来的系统默认关联已更改。无法核对的回滚保留为 `recovery-pending`，可通过 `recovery status` 查看。旧中断日志仍使用其中已有的证据恢复。

切换 ICC 后，保存或撤回只会从已记录的原曲线或线性曲线重新写入候选曲线。发现未知曲线会返回 `external_curve_change` 或 `undo_curve_changed`。回滚时，如果验证了事务的 ICC 关联回退且原显示路径与拓扑始终一致，也可恢复在回滚开始前已重置为线性的曲线。如果曲线在 ICC 回退时才变为线性，则切换前必须能确认曲线归属，且切换过程中显示路径与拓扑要保持一致。写回前，日志会记录此前未登记的线性回滚起点。发现无法确认归属的曲线时会停止曲线写入，并在日志记录 `superseded`。恢复完成仍以读回为准。

如果回滚在确认了原路径、已知曲线和事务 ICC 后即将切换关联，工作进程会先记录切换可能导致线性重置。若切换后读回前中断，重试会核对原路径、拓扑和原 ICC 选择，再判断是否可从线性曲线继续恢复。原路径不匹配或读到未知曲线时，仍按现有恢复规则处理。

关联回滚在写入前再次读取活动路径、有效 ICC、选用的作用域和每用户默认 ICC。路径或拓扑在本次恢复期间变化时保留待恢复状态。检测到外部选用的 ICC 或作用域改变时，保留新的关联并记录 `external_profile_change`。若回退依赖原系统默认 ICC，也会再次核对该默认配置。若最后核对确认本次未写入，因目标或系统默认配置变化而待恢复的日志只保留更早尝试的线性重置依据。

Gamma 参数的中性位置为 2.2，曲线按 `x^(2.2/gamma)` 和通道增益调整后，再与开始时捕获的曲线组合。这是视觉调整参数，不是对面板物理 Gamma 的测量。确认的绝对曲线保存在 ICC `vcgt` 中；再次恢复已确认曲线不能重复叠加预设。

`curveReadbackVerified` 指驱动逻辑表读回检查，容差为一个 8 位级别；`colorAccuracyVerified` 始终为 false。数值、预设与会话字段使用固定机器名称，不依赖界面语言。

已有配置的高级操作：

```text
dccwpp profiles list --display <display-id> --json
dccwpp profiles preview <profile-name> --display <display-id> --dry-run --json
dccwpp profiles preview <profile-name> --display <display-id> --seconds 30 --request-id <request-uuid> --json
dccwpp profiles status <session-id> --json
dccwpp profiles observe <session-id> --wait-seconds 15 --json
dccwpp profiles confirm <session-id> --json
dccwpp profiles revert <session-id> --json
dccwpp profiles recover <session-id> --json
```

这条路径限于已关联的、可验证原默认配置的当前用户 SDR 显示器。它试用默认 ICC 关联，不生成新的校准曲线。

`profiles list` 中的 `profiles[].mhc2Present` 表示是否检测到 MHC2 标签。读不到标签目录或记录来自旧版本时可能缺少该字段。`canSelect` 表示文件满足结构检查，实际预览还会检查显示器、作用域和 MHC2。当前或候选 ICC 含 MHC2 时，`--dry-run` 给出 `canPreview: false` 和 `mhc2_profile_blocked`，实际预览在建立日志和写入默认关联前返回退出码 4。旧会话仍可核对原配置、候选文件摘要和目标后回滚。[Windows 硬件色彩校准加载器](https://learn.microsoft.com/en-us/windows/win32/wcs/display-calibration-mhc#enhanced-windows-display-calibration-loader)

确认前会再次核对关联。若核对跨过截止时间，工作进程会尝试恢复原 ICC。回退成功时记录 `preview_expired`。`profiles confirm` 和 `profiles revert` 分别在日志为 `confirmed` 或 `reverted` 时返回成功，重复提交同一操作可读取同一结果。其他状态返回退出码 4 和记录的 `error.code`。若工作进程已经退出且日志仍处于进行状态，原因是 `recovery_required`。用 `profiles status` 或 `profiles observe` 查看完整会话。

首次启动 ICC 预览时，工作进程在写入日志后和调用 Windows 切换配置前检查期限。写入前到期时保留当前 ICC，日志记录 `reverted` 和 `preview_expired`，且 `associationVerified` 为 `false`。如果写入已经开始，则在返回或读回后发现到期时尝试回滚。预览尚未就绪且事务以 `preview_expired` 结束时，首次调用返回退出码 4 和该原因码。已有会话日志时，CLI 的启动等待上限为启动阶段 10 秒、所请求的预览秒数以及回滚宽限 5 秒。尚无日志时仍以 10 秒为上限。

Windows 写入前的状态核对失败时，ICC 预览会保留当前选用的配置，即使该配置恰好与预览候选相同。会话记录核对失败的原因，`associationVerified` 保持为 `false`。

桌面布局改变会结束正在进行的 ICC 预览。新会话记录了可唯一识别的显示器时，恢复可在同一显示器、同一目标和源路径上继续，条件是当前仍为独立 SDR 且作用域一致。缺少该身份的旧日志仍要求原拓扑。回滚前核对当前候选 ICC 与将恢复的原 ICC 摘要。任务仍需处理时，`profiles recover` 返回退出码 4，`error.code` 提供原因，例如 `display_disconnected`、`recovery_target_changed` 或 `profile_file_changed`。状态查询仍提供 `recovery-pending` 及完整日志。检测到其他配置已被选中时，恢复以 `superseded` 结束。

`profiles preview --request-id` 的 UUID 用于 `profiles status`，也会出现在响应的 `data.id`。用相同 UUID、显示器、ICC 文件名和时限重试会返回已有会话。文件名比较忽略大小写。参数冲突时返回 `request_id_conflict`。ICC 预览有独立的会话 ID 范围，预演不占用 UUID。

JSON 模式在 stdout 输出一个文档，成功为 `{"schemaVersion":1,"ok":true,"data":...}`，失败为 `{"schemaVersion":1,"ok":false,"error":{"code":"...","message":"..."}}`。非 JSON 模式失败写入 stderr。

| 退出码 | 含义 |
| --- | --- |
| 0 | 请求完成；预演需检查能力字段，会话需检查状态 |
| 1 | 未分类内部错误 |
| 2 | 参数或文档无效 |
| 3 | 文件、预设、显示器或会话不存在 |
| 4 | 不支持、预览/确认/恢复未完成 |
| 5 | 文件访问或 I/O 错误 |

自动检查脚本只运行查询和 `--dry-run`，不应通过测试脚本修改真实显示设置。
