# Microsoft Store 发布手册

当前基线为 0.2。x64 和 ARM64 开发 MSIX 已构建并通过包内容检查。正式提交依赖 DCCW++ 自己的 Partner Center 关联及首发验收。[开发路线图](ROADMAP.md)

## 工程和身份

DCCW++ 采用 RunOnce 使用的 WinUI 3 单项目 MSIX 方式。应用面向 Windows Desktop，清单最低版本为 Windows 11 build 22000。包内包含自包含的 GUI 和 CLI、三语言资源及图标。登录启动任务和 `dccwpp.exe` 执行别名属于同一应用 ID，分别启动 GUI 和控制台程序。登录任务默认关闭。清单声明桌面应用所需的 `runFullTrust`，应用入口以普通用户运行。[RunOnce](https://github.com/Water-Run/RunOnce) · [单项目 MSIX](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/single-project-msix)

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

在支持 Windows Sandbox CLI 的 Windows 11 上，可以验证开发包签名和系统预配。脚本只映射当次的构建输入及报告目录。临时签名证书及信任只存在于沙盒中。[Windows Sandbox CLI](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli) · [MSIX 系统预配](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/sideload-apps-with-dism-s14)

```powershell
./scripts/Test-PackageInSandbox.ps1 -Package $x64DevelopmentMsix
```

加上 `-StandardUser` 可验证沙盒普通用户直接安装、CLI 别名、默认登录启动状态和卸载。可用 DCCW++ 的 `vcgt` 写入器生成临时 ICC，再通过 `-IccProfile` 验证普通用户安装该文件、校验安装后的摘要并移除。GUI 冷启动、登录触发及显示器关联在交互桌面和实机单独验收。

GUI 冷启动验收需要已登录的交互桌面。在普通用户安装后，从开始菜单启动 DCCW++，记录首次出现的窗口、默认页和退出结果。随后在扩展桌面上选中原点偏离主屏的目标屏，核对移屏和全屏图案的位置。[Windows Sandbox CLI 的交互会话要求](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli#exec)

```powershell
$fixture = dotnet run --file ./scripts/New-IccTestFixture.cs -- ./artifacts/icc-test-fixtures
./scripts/Test-PackageInSandbox.ps1 -Package $x64DevelopmentMsix -StandardUser -IccProfile $fixture
```

升级验收可传入相同开发身份的两个未签名 x64 MSIX。第二个包的清单版本必须更高。检查会以同一沙盒用户安装旧包、在默认数据目录保存预设与显示观察、原地升级，再核对数据、执行别名、启动任务状态和卸载结果。报告记录两个原始包的版本与 SHA-256。[MSIX 更新要求](https://learn.microsoft.com/en-us/windows/msix/app-package-updates)

```powershell
./scripts/Test-PackageInSandbox.ps1 -Package $previousX64Msix -UpgradePackage $newerX64Msix -StandardUser
```

在同一份源码上重建双架构开发包和对应源码压缩包。

```powershell
python ./scripts/build_release_evidence.py review-01
```

结果位于 `artifacts/release-evidence/review-01/`。脚本先归档 Git 跟踪文件及源码目录中未忽略的新文件，再使用同一快照构建。校验通过才写入 `evidence.json`，其中记录源码、MSIX 和依赖声明的 SHA-256。该命令使用开发身份，可用 `--architecture x64` 只重验 x64。

## 生成提交包

在已关联并核对 DCCW++ 身份的本地工作区中生成提交候选包。

在本地 `store/identity.local.json` 保存 Partner Center **Product identity** 中的三个值。此文件已被 Git 忽略。先关联项目，再使用同一页面的信息独立核对清单。清单的开发版号可用于本地侧载。首发候选用递增的四段 MSIX 版本号，主版本非零，末段留为 `0`。[MSIX 包版本规则](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements) · [发布前检查](https://learn.microsoft.com/en-us/windows/apps/publish/store-readiness-checklist)

```json
{
  "name": "<Package/Identity/Name>",
  "publisher": "<Package/Identity/Publisher>",
  "publisherDisplayName": "<Package/Properties/PublisherDisplayName>"
}
```

```powershell
./scripts/Build.ps1 -Configuration Release -Architecture x64 -Package -Store -Test -PackageRun store-x64
./scripts/Build.ps1 -Configuration Release -Architecture ARM64 -Package -Store -PackageRun store-arm64
python ./scripts/verify_package.py $x64Upload $arm64Upload --store-identity ./store/identity.local.json
```

`-Store` 在构建前用 Python 核对本地身份、源清单和版本，再切换到 `StoreUpload` 模式。将两份生成的单架构 `.msixupload` 路径传给验包脚本。脚本会检查上传文件中的实际 MSIX，并核对身份、版本、CPU 架构、清单入口、语言和运行时。也可直接传入两份独立 MSIX。Store 在发布流程中签署 MSIX。[上传 MSIX](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages) · [商店分发与签名](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/choose-distribution-path)

## 验收与提交

- 在干净的 Windows 11 x64 和原生 ARM64 设备上用普通用户验证安装、启动、别名、升级数据迁移和卸载，并核对 Windows 颜色设置能找到或移除已保存的 ICC。
- 验证登录任务启用、用户或组织禁用、登录后后台恢复及无匹配显示器的处理。关闭可见窗口后检查睡眠唤醒和热插拔。用 `dccwpp startup last-restore --json` 核对触发原因和逐屏结果，并验证关闭启动任务后后台退出。用装包版本检查 GUI/CLI 数据互通及进程返回值。
- 按首发验收结果审阅三语文案与认证说明，并上传当前构建的真实界面截图。[商店文案及截图清单](STORE_LISTING.md) · [隐私说明](PRIVACY.md)
- 归档候选版对应的源码、构建说明、GPL 文本及第三方许可。保存照片作者和许可。GitHub 源码版本与商店包版本保持可追溯。
- 在 Partner Center 核对包身份、架构、版本、上架范围和认证反馈。WACK 可作为本地辅助检查。首次发布可用 Private audience 让选定测试人员获得 Store 签名包，完成实际安装和升级验证后再公开可见性。[MSIX 本地检查](https://learn.microsoft.com/en-us/windows/msix/package/packaging-uwp-apps#validate-your-app-package-locally) · [首次发布的可见性选项](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/visibility-options)
