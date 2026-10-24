# CLI 与 Agent 接口 v1

安装后的入口为 `dccwpp.exe`。应用名仍然是 DCCW++。命令行使用独立控制台宿主，避免 GUI 子系统导致 stdout/退出码不可用。

## 命令

```text
dccwpp --help --json
dccwpp status --json
dccwpp displays list --json
dccwpp displays snapshot --json
dccwpp displays snapshot show --json
dccwpp presets list --json
dccwpp presets create "Daylight" --json
dccwpp presets show <preset-uuid> --json
dccwpp presets validate ./shared.dccw.json --json
dccwpp presets import ./shared.dccw.json --json
dccwpp presets export <preset-uuid> ./export.dccw.json --json
dccwpp apply <preset-uuid> --display <display-id> --dry-run --json
dccwpp startup status --json
```

`--data-dir <directory>` 可为隔离测试指定存储目录；默认是 `%LOCALAPPDATA%/DCCW++`。包内 GUI 与 CLI 共享包身份和目录。直接运行开发产物与商店安装包之间不承诺自动迁移。

JSON 模式成功和失败均在 stdout 输出一个 JSON 文档，无进度文字、ANSI 色码或交互提示。非 JSON 模式失败写入 stderr。输入 UUID、数值和机器字段不依赖 UI 语言。

```json
{"schemaVersion":1,"ok":true,"data":{}}
```

```json
{"schemaVersion":1,"ok":false,"error":{"code":"calibration_backend_pending","message":"..."}}
```

| 退出码 | 含义 |
| --- | --- |
| 0 | 命令完成；预演成功仅代表生成计划，检查 `canApply` |
| 1 | 未分类的内部错误 |
| 2 | 参数或预设无效 |
| 3 | 文件、预设或显示器不存在 |
| 4 | 功能尚不可用 |
| 5 | 文件访问、权限或 I/O 错误 |

`presets export` 不覆盖现有文件。`import` 每次生成新的本地 ID，不应用效果。`presets list` 在 `invalidFiles` 中报告损坏文件，其余预设仍可读取。现在没有批量删除命令。

`displays snapshot` 把当前只读探测写入数据目录，并覆盖上一次观察。它不改变显示设置。`snapshot show` 只读取这份观察。观察的 `capability` 固定为 `read-only`，`applied` 固定为 `false`；文件里不能携带命令或“已应用”标记。

## 校准边界

此版本没有实际校准引擎。`apply --dry-run` 验证预设和显示器并返回 `canApply: false`；`apply` 不更改系统并以代码 4 结束。

`colorState` 是 Windows 报告的当前颜色模式：`sdr`、`wcg`、`hdr`、`advanced-color` 或 `unknown`。`unknown` 和 `advanced-color` 都不是 SDR。`hdrActive: false` 只在模式明确不是 HDR 时出现；探测失败时该字段为空。`profileName` 是当前默认 ICC 配置文件的文件名，不是测量结果，也不证明查找表已经生效。`identityScope` 固定为 `topology-query`：`id` 和 `targetKey` 只标识这次活动拓扑，换端口后不能继续拿来认显示器。

未来 GUI、CLI 和登录恢复共用事务服务：读取原状 → 临时预览 → 确认或超时恢复 → 验证。非交互 Agent 获得有期限的事务 ID；重试和重复确认需幂等处理。
