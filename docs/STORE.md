# Microsoft Store 发布基础

## 已有工程结构

- 单项目 MSIX，Windows.Desktop，最低 Windows 11 build 22000。
- GUI 和控制台分别声明 Application；CLI 使用 `dccwpp.exe` 执行别名和 console 子系统。
- StartupTask 默认关闭，使用 Windows API 启用/禁用并读取用户或策略禁用状态。
- 界面在普通用户权限下运行；manifest 仅声明桌面应用所需的 `runFullTrust`，没有管理员启动要求。
- 自包含 .NET 和 Windows App SDK；源码固定版本，构建不依赖机器上的应用运行时预装状态。
- 英语、简中、繁中描述资源；多尺度 PNG、任务栏 targetsize 和 ICO；未签名包由脚本生成。

## 本地开发包

```powershell
./scripts/Build.ps1 -Configuration Release -Package -Test
```

输出目录：`artifacts/packages/`。CLI 先发布为独立自包含目录，再打入同一 MSIX 的 `Cli/`。包内必须包含 `Cli/dccwpp.exe` 和完整依赖。

这是未签名开发包，不能直接当作已发布产品安装。开发时可在 Visual Studio 的部署流程中注册 loose package；需要侧载时使用你自己的开发签名流程。仓库不存储证书私钥或密码。

## 专属商店身份

清单中的 `DCCWPlus.Development`、`CN=DCCWPlus-Development` 是开发占位值。DCCW++ 需要在 Partner Center 预留名称并取得自己的 Package Identity Name、Publisher、PublisherDisplayName。使用 Visual Studio 的 Associate App with the Store，或将核实后的值填入清单。

参考 RunOnce 的工程方式，但不得复用它的 Identity Name、产品 ID、StoreAssociation、证书或 Shell Extension 注册项。

关联后：

```powershell
./scripts/Build.ps1 -Configuration Release -Package -Store -Test
```

`-Store` 会阻止使用开发占位身份生成提交包。Windows App SDK 的依赖版本固定；若更换工具链，应重新验证包布局与首次启动。

## 发布前验收

- 完成真正的校准和自动恢复功能，商店文案与已验证能力一致。
- 分别验证 x64 和 ARM64：安装、冷启动、CLI 别名、标准用户、升级保留数据、卸载。
- 使用 Windows App Certification Kit 检查最终提交包。
- 在安装包中验证用户关闭/启用 StartupTask，任务管理器禁用和组织策略的反馈。
- 包内 GUI/CLI 的预设互通；输出、退出码、空格/中文路径正常。
- 登录恢复不会弹出向导，不会在显示器不匹配时误套用预设；恢复失败有可查询状态。
- 准备真实截图、产品描述、支持网址和隐私政策。当前实现本地存储，没有遥测、账号或联网功能；后续有变化需要更新说明。
- 提供该版本完整对应源码与构建说明、GPL 文本、依赖许可清单。摄影素材许可与代码许可分开记录。

截至本轮：开发身份、未签名打包路径已建立。真实商店身份、提交、WACK 和最终发布验证仍待进行。

参考：[MSIX 单项目](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/single-project-msix)、[商店分发](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-distribute-your-win32-app-through-microsoft-store)、[执行别名 schema](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap5-appexecutionalias)。
