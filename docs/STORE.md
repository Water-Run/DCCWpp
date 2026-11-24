# Microsoft Store 发布手册

当前基线为 0.2。x64 和 ARM64 开发 MSIX 已构建并通过包内容检查。正式提交依赖 DCCW++ 自己的 Partner Center 关联及首发验收。[开发路线图](ROADMAP.md)

## 工程和身份

DCCW++ 采用 RunOnce 使用的 WinUI 3 单项目 MSIX 方式。应用面向 Windows Desktop，清单最低版本为 Windows 11 build 22000。包内包含自包含的 GUI 和 CLI、三语言资源、图标、默认关闭的 StartupTask，以及 `dccwpp.exe` 执行别名。清单声明桌面应用所需的 `runFullTrust`，应用入口以普通用户运行。[RunOnce](https://github.com/Water-Run/RunOnce) · [单项目 MSIX](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/single-project-msix)

仓库中的 `DCCWPlus.Development` 和 `CN=DCCWPlus-Development` 用于本地构建。使用 Partner Center 为 DCCW++ 预留名称，从 Visual Studio 的 **Publish → Associate App with the Store** 关联本产品，并核对清单中的 Package Identity Name、Publisher 和 PublisherDisplayName。商店关联文件保存在本地。使用 DCCW++ 的身份、Store ID 和清单扩展。

## 构建与检查开发包

```powershell
./scripts/Build.ps1 -Configuration Release -Architecture x64 -Package -Test
./scripts/Build.ps1 -Configuration Release -Architecture ARM64 -Package
```

包输出在 `artifacts/packages/`，两个架构各有一个 MSIX。脚本先发布各自的自包含 CLI，再把相应工具和依赖放入 `Cli/`。Python 3 可用于检查包结构和当前架构的 CLI。

如果上一次构建的包仍在使用，可指定新的输出子目录，保留原包供验收对照。

```powershell
./scripts/Build.ps1 -Configuration Release -Package -PackageRun review-01
```

此命令会将结果写入 `artifacts/packages/review-01/`。

```powershell
python ./scripts/smoke_cli.py ./artifacts/cli/win-x64/dccwpp.exe
python ./scripts/verify_package.py ./artifacts/packages/DccwPlus.App_0.2.0.0_x64_Test/DccwPlus.App_0.2.0.0_x64.msix
```

包内的 `ThirdParty/index.json` 记录已解析的 SDK 和运行时版本、许可证来源及随包声明的摘要。验包脚本会核对 GUI 和 CLI 的依赖条目及声明内容。项目自身的许可证在 `LICENSE.txt`。

开发包未签名。需在测试设备侧载时，以与开发包 Publisher 匹配的开发签名签包，并在测试设备上信任相应证书。测试签名私钥、密码和正式 Store 关联文件保存在本地。

在支持 Windows Sandbox CLI 的 Windows 11 上，可以验证开发包签名和系统预配。脚本只映射当次的构建输入及报告目录。临时签名证书及信任只存在于沙盒中。预配检查使用沙盒系统会话，普通用户安装、别名和 GUI 激活需要登录桌面后单独验收。[Windows Sandbox CLI](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli) · [MSIX 系统预配](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/sideload-apps-with-dism-s14)

```powershell
./scripts/Test-PackageInSandbox.ps1 -Package $x64DevelopmentMsix
```

在同一份源码上重建双架构开发包和对应源码压缩包。

```powershell
python ./scripts/build_release_evidence.py review-01
```

结果位于 `artifacts/release-evidence/review-01/`。脚本先归档 Git 跟踪文件及源码目录中未忽略的新文件，再使用同一快照构建。校验通过才写入 `evidence.json`，其中记录源码、MSIX 和依赖声明的 SHA-256。该命令使用开发身份，可用 `--architecture x64` 只重验 x64。

## 生成提交包

在已关联并核对 DCCW++ 身份的本地工作区中生成提交候选包。

```powershell
./scripts/Build.ps1 -Configuration Release -Architecture x64 -Package -Store -Test
./scripts/Build.ps1 -Configuration Release -Architecture ARM64 -Package -Store
```

`-Store` 拒绝开发占位身份，切换为 `StoreUpload` 构建模式。归档 x64 产物后再构建 ARM64，核对两者的归档文件名、版本、身份与架构，并检查包中的两个入口和对应运行时。使用 Visual Studio 生成的 Store 上传文件，或上传已核对的两份独立 MSIX。Partner Center 支持独立 MSIX 并推荐 `.msixupload` 或 `.appxupload`。Store 在发布流程中签署 MSIX。[上传 MSIX](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages) · [商店分发与签名](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/choose-distribution-path)

## 验收与提交

- 在干净的 Windows 11 x64 和原生 ARM64 设备上用普通用户验证安装、启动、别名、升级数据迁移和卸载，并核对 Windows 颜色设置能找到或移除已保存的 ICC。
- 验证登录任务启用、用户或组织禁用、登录后后台恢复及无匹配显示器的处理。关闭可见窗口后检查睡眠唤醒和热插拔。用 `dccwpp startup last-restore --json` 核对触发原因和逐屏结果，并验证关闭启动任务后后台退出。用装包版本检查 GUI/CLI 数据互通及进程返回值。
- 准备三种语言的商店描述、真实界面截图和产品支持地址。按提交时的产品能力核对隐私说明，并为认证人员记录选屏、预览、取消恢复和命令行测试步骤。说明 `runFullTrust` 用于桌面显示、文件选择和预览工作进程。[隐私说明](PRIVACY.md)
- 归档候选版对应的源码、构建说明、GPL 文本及第三方许可。保存照片作者和许可。GitHub 源码版本与商店包版本保持可追溯。
- 在 Partner Center 核对包身份、架构、版本、上架范围和认证反馈。WACK 可作为本地辅助检查。首次发布可用 Private audience 让选定测试人员获得 Store 签名包，完成实际安装和升级验证后再公开可见性。[MSIX 本地检查](https://learn.microsoft.com/en-us/windows/msix/package/packaging-uwp-apps#validate-your-app-package-locally) · [首次发布的可见性选项](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/visibility-options)
