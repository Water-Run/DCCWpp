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

当前构建基线为 .NET SDK 10.0.404。验包会分别检查 GUI 和 CLI 内实际随附的 .NET 运行时，最低版本为 10.0.15。[微软安全公告](https://github.com/advisories/GHSA-p3jp-9v83-76w9) · [.NET 10.0.15 发布说明](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.15/10.0.15.md)

开发包未签名。需在测试设备侧载时，以与开发包 Publisher 匹配的开发签名签包，并在测试设备上信任相应证书。测试签名私钥、密码和正式 Store 关联文件保存在本地。

在支持 Windows Sandbox CLI 的 Windows 11 上，可以验证开发包签名和系统预配。脚本只映射当次的构建输入及报告目录。临时签名证书及信任只存在于沙盒中。[Windows Sandbox CLI](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli) · [MSIX 系统预配](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/sideload-apps-with-dism-s14)

```powershell
./scripts/Test-PackageInSandbox.ps1 -Package $x64DevelopmentMsix
```

加上 `-StandardUser` 可验证沙盒普通用户直接安装、CLI 别名、无窗口子进程接收 JSON 和退出码、默认登录启动状态和卸载。可用 DCCW++ 的 `vcgt` 写入器生成临时 ICC，再通过 `-IccProfile` 验证普通用户安装该文件、校验安装后的摘要并移除。GUI 冷启动、登录触发及显示器关联在交互桌面和实机单独验收。

GUI 冷启动验收需要已登录的交互桌面。用沙盒复测时，先通过 `wsb connect` 建立该次沙盒的交互会话。随后可用 `wsb exec --run-as ExistingLogin` 在同一会话中启动验收。200% 缩放的 x64 沙盒已检查默认页面、紧凑布局、从 CLI 创建的预设、语言切换及全屏图案。普通用户已验收 WinUI 冷启动和 GUI 导入后通过 CLI 查验。扩展桌面仍需核对移屏和全屏图案的位置。[Windows Sandbox CLI 的交互会话要求](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli#exec)

验收 CLI 别名时，用普通用户运行 `dccwpp.exe status --json`，核对 `data.packaged`。在 Windows 设置的“应用执行别名”关闭 DCCW++，核对命令不可用且开始菜单仍能打开应用。重新启用后核对包身份和原有预设。另在缺少 WindowsApps `PATH` 项的终端复测通过完整别名路径启动。[排查步骤](CLI.md#windows-执行别名)

登录启动复测从安装后的 `Disabled` 状态开始。在设置启用任务并关闭主窗口，注销测试用户并重新登录。先检查桌面是否弹出向导，再运行 `dccwpp startup last-restore --json`。报告应晚于新会话的登录时间，`trigger` 应为 `sign_in`，系统进程列表应有后台实例。没有启用绑定时检查 `no_bindings_enabled`。如有针对断开显示器的测试绑定，检查其状态为 `skipped`，原因为 `display_disconnected`。随后用设置关闭启动任务，确认后台退出；再次注销并登录，核对后台没有重启且报告时间未变化。[桌面应用 StartupTask](https://learn.microsoft.com/en-us/windows/uwp/porting/desktop-to-uwp-extensions#start-an-executable-file-when-users-log-into-windows)

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
python ./scripts/build_release_evidence.py review-01 --commit HEAD
```

结果位于 `artifacts/release-evidence/review-01/`。`--commit` 从指定 Git 提交提取源码并在同一个快照上构建。省略它时可验收当前工作区中的改动。脚本运行单元测试、x64 CLI 进程检查以及两种架构的包内容和版本校验。全部通过才写入 `evidence.json`，其中记录源码、MSIX 和依赖声明的 SHA-256。该命令使用开发身份，可用 `--architecture x64` 只重验 x64。

GitHub Actions 的 **Build** 使用触发构建的 Git 提交生成 `DCCWPlus-development-evidence`。该工件包含 x64 和 ARM64 MSIX、对应源码压缩包和 `evidence.json`。`DCCWPlus-build-logs` 包含构建及校验日志。可在 Actions 中用 **Run workflow** 重建所选分支上的开发工件。

同一次构建的 **Windows ARM64 package runtime** 作业获取 ARM64 开发 MSIX，核对提交和未签名包的 SHA-256。它用托管 Windows 11 ARM64 虚拟机中的临时证书签名测试副本，再检查安装、`dccwpp.exe` 别名、JSON 和退出码、禁用的初始登录启动状态、WinUI 冷启动及卸载。`DCCWPlus-arm64-runtime` 工件记录运行结果。商店签名、普通用户部署和物理显示效果在发布设备上单独验收。[GitHub Windows ARM64 runner](https://docs.github.com/en/actions/reference/runners/github-hosted-runners) · [MSIX 开发签名](https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide)

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
python ./scripts/build_release_evidence.py store-rc-01 --store
```

默认从已关联的工作区取一次源码快照，用其中的清单构建 x64 和 ARM64 上传包。结果写入 `artifacts/release-evidence/store-rc-01/`，包含 `DCCWPlus-source.zip`、两份 `.msixupload`、构建日志和 `evidence.json`。证据记录上传归档和内嵌 MSIX 的 SHA-256。源码归档保存实际用于构建的清单。本机身份 JSON 和 Store 关联文件留在本地。若指定 `--commit`，该提交中的清单须与本机身份一致。

单独重建或独立复核上传包时可运行以下命令。

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
- 在 Partner Center 核对包身份、架构、版本、上架范围和认证反馈。首次发布可用 Private audience 让选定测试人员获得 Store 签名包，完成实际安装和升级验证后再公开可见性。[首次发布的可见性选项](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/visibility-options)

### Windows 应用认证工具包预检

在测试设备上安装 Windows SDK 的应用平台元数据和 Windows 应用认证工具包（WACK），登录交互桌面并以管理员身份打开 PowerShell。用该设备信任的测试证书签署开发 MSIX。将 `$signedMsix` 设为签名包的绝对路径，`$reportXml` 设为本次检查的新报告路径。在同一用户会话中运行以下命令。[微软命令行说明](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit)

```powershell
$kit = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit\appcert.exe'
& $kit reset
if ($LASTEXITCODE) { throw 'Could not reset the Windows App Certification Kit.' }
& $kit test -appxpackagepath $signedMsix -reportoutputpath $reportXml
$report = [System.Xml.XmlDocument]::new()
$report.Load($reportXml)
$report.DocumentElement.GetAttribute('OVERALL_RESULT')
$report.DocumentElement.GetAttribute('PARTIAL_RUN')
$report.SelectNodes('/REPORT/REQUIREMENTS/REQUIREMENT/TEST[RESULT != "PASS"]') |
    ForEach-Object {
        [pscustomobject]@{
            Test = $_.GetAttribute('NAME')
            Optional = $_.GetAttribute('OPTIONAL')
            Result = $_.SelectSingleNode('RESULT').InnerText
        }
    }
```

即使命令退出码为 `0`，也要读取 XML 的 `OVERALL_RESULT`、`PARTIAL_RUN` 和具体失败项目。记录待测提交、原始包 SHA-256、Windows build、工具包版本及报告中的各项结果。按报告核对失败、警告和未运行的项目。微软将 Desktop Bridge 的可选项目作为兼容性参考，正式认证结果以 Partner Center 为准。[桌面应用检查项目](https://learn.microsoft.com/windows/uwp/debug-test-perf/windows-desktop-bridge-app-tests)
