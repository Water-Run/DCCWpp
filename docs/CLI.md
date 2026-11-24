# DCCW++ CLI 0.2

安装后的入口为 `dccwpp.exe`，应用名为 DCCW++。控制台宿主与 WinUI 使用同一套核心校准事务。

## Agent 调用

`dccwpp capabilities --json` 返回版本化的命令清单，可在无需打开 WinUI 时调用。每条命令的 `name` 是稳定标识，`usage` 是参数说明，`supportsDryRun` 和 `supportsRequestId` 表示该命令接受的选项。当前 `contractVersion` 为 1。命令清单反映程序提供的接口。当前显示器是否可执行校准，由 `calibration diagnose` 和 `apply --dry-run` 查询。

| `effect` | 执行命令时可能产生的效果 |
| --- | --- |
| `read` | 查询数据或验证输入 |
| `local-write` | 保存或修改本地文件、预设、显示观察或恢复偏好 |
| `display-preview` | 开始或更新限时预览 |
| `display-change` | 确认、取消或恢复显示状态 |

Agent 从 `displays list` 选择明确的显示器，从 `presets list` 取得预设 UUID，再执行 `apply --dry-run`。实际预览返回会话 ID 与 `expiresAt`。通过 `calibration status` 读回状态，并在得到用户选择后调用 `calibration confirm` 或 `calibration revert`。显示会话来自当前 Windows 用户和桌面。Agent 应读取退出码、`ok`、状态和原因码。

## 命令参考

查询与预设操作：

```text
dccwpp --help --json
dccwpp capabilities --json
dccwpp status --json
dccwpp displays list --json
dccwpp displays snapshot --json
dccwpp displays snapshot show --json
dccwpp displays restore-plan --display <display-id> --json
dccwpp calibration diagnose --display <display-id> --json
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
dccwpp startup last-restore --json
dccwpp recovery status --json
dccwpp bindings list --json
dccwpp bindings enable <persistence-key> --json
dccwpp bindings disable <persistence-key> --json
dccwpp bindings remove <persistence-key> --json
dccwpp bindings restore --dry-run --json
```

`--data-dir <directory>` 指定存储目录，默认是 `%LOCALAPPDATA%/DCCW++`。包内 GUI 与 CLI 使用同一存储环境。导入只加入预设库，导出会保护已有文件和预设库中的记录。

`presets create` 可用 `--gamma`、`--red-gain`、`--green-gain`、`--blue-gain`、`--author` 和 `--description` 创建草稿。数值使用英文小数点。重复导入同一文档会返回已有的本地预设，包括本地改名后的条目。旧版库中的精确副本也可复用。需要独立条目时加 `--copy`。已有命令返回的预设字段保持原样。

`presets rename` 保留 ID 和校准值。删除前可用 `presets bindings` 查询引用。即使已停用自动恢复，有绑定的预设也会返回 `preset_in_use`。用 `bindings remove` 解除引用后可以删除。解除绑定只停止管理本机恢复记录，已选用的 ICC 和当前显示状态继续保留。

`calibration diagnose` 只读取指定显示器和 Windows 校准加载器状态。输出包含 `windowsCalibrationLoaderEnabled`、`driverSupportsVcgt`、`logicalRampReadable`、`profileMhc2Present` 和 `reasonCode`。无法查询或不适用时对应字段为 `null`。能读取逻辑曲线表是实机验收的前置证据，实际写入及登录后加载需要分别验证。[Windows 校准加载器](https://learn.microsoft.com/en-us/windows/win32/wcs/display-calibration-mhc#enhanced-windows-display-calibration-loader)

`displays list` 对明确的 SDR、WCG 和 HDR 查询对应模式的默认 ICC。HDR 使用扩展关联，SDR 和 WCG 使用标准关联。对无法区分 SDR 与 HDR 的模式，配置文件字段为 `null`。可读配置的 `profile.mhc2Present` 表示标签存在，标签目录无法读取时为 `null`。如果当前 SDR 配置检测到 MHC2，`apply --dry-run` 返回 `canApply: false` 和 `mhc2_profile_blocked`，实际预览在写入曲线前返回相同原因码。[Windows Advanced Color 配置关联](https://learn.microsoft.com/en-us/windows/win32/wcs/advanced-color-icc-profiles)

`displays list` 和 `calibration diagnose` 同时报告 `selectedProfileScope` 和 `profileQueryStatus`。前者是 Windows 选用的作用域，`profileScope` 是实际返回的 ICC 所在作用域。后者可以是 `read`、`not-found` 或 `unavailable`。系统明确无默认关联时使用 `not-found`；查询未执行时用 `null`。读取失败会以 `profile_state_unavailable` 阻止 SDR 预览或暂缓恢复。[Windows 作用域查询](https://learn.microsoft.com/en-us/windows/win32/api/icm/nf-icm-colorprofilegetdisplayuserscope)

已确认校准的逐屏绑定可在设置或 `bindings` 命令中启用。启用绑定仅保存偏好。Windows 登录任务由设置页单独管理。确认会话的 `bindingStatus` 表示本机绑定保存情况。旧日志中该字段可能为空。

`bindings restore --dry-run` 生成恢复报告。去掉 `--dry-run` 会先尝试恢复中断预览，再为已启用且符合条件的绑定写回已确认的绝对曲线。只有原目标可唯一识别、当前为独立 SDR 路径、已确认的用户 ICC 仍被选用且摘要一致、当前曲线匹配已知基线时才写入。报告通过 `reason` 和每屏 `status` 表示结果，退出码 0 表示已生成报告。Windows 登录任务使用同一路径。`startup last-restore` 返回后台最近一次报告及触发原因，尚无报告时为 `null`。

校准操作：

```text
dccwpp apply <preset-uuid> --display <display-id> --dry-run --json
dccwpp apply <preset-uuid> --display <display-id> --seconds 30 --json
dccwpp apply <preset-uuid> --display <display-id> --seconds 30 --request-id <request-uuid> --json
dccwpp calibration sessions --json
dccwpp calibration status <session-id> --json
dccwpp calibration update <session-id> --preset <preset-uuid> --json
dccwpp calibration update <session-id> --preset <preset-uuid> --original --json
dccwpp calibration confirm <session-id> --json
dccwpp calibration revert <session-id> --json
dccwpp calibration recover <session-id> --json
dccwpp calibration undo <calibration-id> --dry-run --json
dccwpp calibration undo <calibration-id> --seconds 30 --json
```

**未带 `--dry-run` 的 `apply` 会实际写入所选 SDR 显示器的临时 Gamma 曲线。** 它返回会话 ID 和固定的 `expiresAt`。CLI 时限为 10–60 秒，更新参数或切回原曲线都沿用同一时限。未在到期前进入保存流程的会话会尝试恢复原曲线。`confirm` 保存 ICC 并在 Windows 中选用，`revert` 取消尚未确认的预览，`recover` 尝试恢复中断会话。

Agent 可以在第一次实际预览前生成一个 UUID 并传入 `--request-id`。该 UUID 同时是会话 ID。发生客户端超时或输出丢失时，使用同一个 UUID、预设 ID、显示器 ID 和超时值重试。`apply` 会返回持久日志中的现有会话，包括已超时、已确认或待恢复的会话，以上次保存的状态为准。原显示器断开或原预设已删除时仍可查询已经创建的会话。复用 UUID 提交其他目标、预设或时限会返回 `request_id_conflict`。需要发起新的预览时使用新 UUID。`--dry-run` 不占用请求 ID。

同一会话的命令由 GUI 和 CLI 共同串行发送。遇到 `command_busy`、`worker_start_timeout` 或 `worker_not_responding` 时，先查询会话状态。状态为 `recovery-pending` 时，在目标显示器重新可用后执行 `calibration recover`。恢复会保留最初的请求记录。应用在同一个 Windows 会话内串行执行实际显示写入，忙时返回 `preview_busy`。

`recovery status` 列出尚未结束的曲线与 ICC 预览，`kind` 分别为 `calibration` 和 `profile`。`recovery run` 尝试恢复这些会话，也可在向导里点击恢复。启用的登录绑定由 `bindings restore` 和登录任务管理。命令返回完整报告，调用方可用 `reason` 判断是否为忙碌 `preview_busy`、仍需恢复 `recovery_required`、处理完毕 `recovery_finished` 或无待处理会话 `no_recovery_needed`。仍在运行的预览由原工作进程处理。

GUI 编辑阶段使用心跳维持预览，单次会话最多 30 分钟。进入比较页后，原始与调整后视图共用最多 30 秒的确认倒计时，且受单次会话总时限约束。`showingOriginal` 记录比较视图选择。窗口进程退出会触发独立工作进程恢复。工作进程自身中断后，需要读取持久日志并恢复。显示器不匹配或不可用时保留待恢复状态。

拓扑变化会结束活动预览。恢复会核对原始显示器身份、目标和源路径、SDR 模式、ICC 关联和曲线。兼容旧版会话日志。检测到其他程序选择了新配置或写入新曲线时，状态可变为 `superseded`。

`calibration undo` 接收已确认的原校准会话 ID。先用 `--dry-run` 查看 `canPreview` 和 `reasonCode`。去掉预演选项后会启动一个新会话并临时显示之前的曲线。对新会话执行 `confirm` 才会恢复之前的 ICC、作用域和曲线。`revert`、确认时限结束或预览进程退出会触发回退。撤回会话会在 JSON 中记录 `undoOf`。撤回成功会清除仍指向被撤回配置的自动恢复绑定，已保存的预设和 ICC 文件仍可保留。

恢复前需核对实际选中的已确认 ICC、曲线、显示器身份和配置文件摘要。旧版会话缺少摘要时报告 `undo_backup_unavailable`。已确认的旧记录仍可查询。

Gamma 参数的中性位置为 2.2，曲线按 `x^(2.2/gamma)` 和通道增益调整后，再与开始时捕获的曲线组合。这是视觉调整参数，不是对面板物理 Gamma 的测量。确认的绝对曲线保存在 ICC `vcgt` 中；再次恢复已确认曲线不能重复叠加预设。

`curveReadbackVerified` 指驱动逻辑表读回检查，容差为一个 8 位级别；`colorAccuracyVerified` 始终为 false。数值、预设与会话字段使用固定机器名称，不依赖界面语言。

已有配置的高级操作：

```text
dccwpp profiles list --display <display-id> --json
dccwpp profiles preview <profile-name> --display <display-id> --dry-run --json
dccwpp profiles preview <profile-name> --display <display-id> --seconds 30 --json
dccwpp profiles status <session-id> --json
dccwpp profiles confirm <session-id> --json
dccwpp profiles revert <session-id> --json
dccwpp profiles recover <session-id> --json
```

这条路径限于已关联的、可验证原默认配置的当前用户 SDR 显示器。它试用默认 ICC 关联，不生成新的校准曲线。

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
