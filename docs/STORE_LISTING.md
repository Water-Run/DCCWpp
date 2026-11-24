# DCCW++ Microsoft Store listing

Draft for the first submission. Review the copy against the candidate's hardware acceptance results. The product name is **DCCW++** in all three listings. Leave **What's new in this version** empty for the first submission. [MSIX listing fields](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info)

- Privacy policy · [DCCW++ privacy notice](https://github.com/Water-Run/DCCWpp/blob/main/docs/PRIVACY.md)
- Support · [DCCW++ issues](https://github.com/Water-Run/DCCWpp/issues)
- Website and source · [DCCW++ on GitHub](https://github.com/Water-Run/DCCWpp)

## English (United States) · en-US

### Short description

Tune the color of an SDR display with a guided preview, keep your adjustments as presets, and restore an earlier calibration when you need it.

### Description

DCCW++ is a display color calibration tool for Windows 11. Pick a display and follow the visual patterns to adjust gamma and grayscale color balance. The wizard also guides you through your monitor's brightness and contrast controls.

Compare the original and adjusted curves before saving. A timed preview gives you a chance to restore the original state. Save the result as a Windows ICC profile, keep presets for later, or import a preset someone has shared for you to preview on your own display.

Choose which displays may restore their saved calibration when you sign in. The included dccwpp command line supports scripts and agents. You can also open your own reference photos in the app.

Calibration requires an independent SDR display and a compatible graphics driver. For HDR and Advanced Color displays, the app points you to the available Windows settings.

### Product features

- Guided gamma, brightness, contrast, and grayscale adjustments
- Timed previews with before-and-after comparison
- Windows ICC profile saving and calibration undo
- Local preset import and export
- Optional per-display restoration at sign-in
- JSON commands for scripts and agents

## 简体中文（中国） · zh-CN

### 简短描述

用分步向导和限时预览调整 SDR 显示器色彩。将调整保存为预设，需要时恢复上一次校准。

### 描述

DCCW++ 是一款 Windows 11 显示器色彩校准工具。选定显示器后，可按图案调整 Gamma 和灰阶色彩平衡。向导也会指导你使用显示器自身的亮度与对比度控件。

保存前可以比较原始与调整后的曲线，并在预览结束时恢复原状态。结果可保存为 Windows ICC 配置文件。你可以保留自己的预设，也可以导入他人共享的预设，在自己的显示器上预览。

在设置中选择哪些显示器可以在登录时恢复校准。随附的 dccwpp 命令行可供脚本和 Agent 调用。你也可以在应用中打开自己的参考照片。

校准需要独立 SDR 显示器和兼容的图形驱动。对于 HDR 与高级颜色显示器，应用会指引你打开相应的 Windows 设置。

### 产品功能

- 分步调整 Gamma、亮度、对比度与灰阶色彩平衡
- 限时预览与前后比较
- 保存 Windows ICC 配置并撤回校准
- 导入及导出本地预设
- 可按显示器启用登录恢复
- 面向脚本和 Agent 的 JSON 命令

## 繁體中文（台灣） · zh-TW

### 簡短描述

使用分步精靈與限時預覽調整 SDR 顯示器色彩。將調整儲存為預設，需要時回復上一次校準。

### 描述

DCCW++ 是一款 Windows 11 顯示器色彩校準工具。選取顯示器後，可依照圖案調整 Gamma 與灰階色彩平衡。精靈也會引導你使用顯示器本身的亮度與對比控制項。

儲存前可以比較原始與調整後的曲線，並在預覽結束時回復原始狀態。結果可儲存為 Windows ICC 描述檔。你可以保留自己的預設，也可以匯入他人分享的預設，在自己的顯示器上預覽。

在設定中選擇哪些顯示器可以在登入時回復校準。隨附的 dccwpp 命令列可供指令碼與 Agent 呼叫。你也可以在應用程式中開啟自己的參考照片。

校準需要獨立的 SDR 顯示器與相容的圖形驅動程式。若使用 HDR 與進階色彩顯示器，應用程式會引導你開啟相應的 Windows 設定。

### 產品功能

- 分步調整 Gamma、亮度、對比與灰階色彩平衡
- 限時預覽與前後比較
- 儲存 Windows ICC 描述檔並撤銷校準
- 匯入及匯出本機預設
- 可依顯示器啟用登入回復
- 供指令碼與 Agent 使用的 JSON 命令

## Screenshots

Capture the installed candidate on a clean Windows 11 desktop in each listing language. Use desktop PNGs at 1366 × 768 or larger. At least one current screenshot is required. Use the build's actual UI and a neutral preset name or the built-in calibration patterns. [Screenshot requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images)

| View | en-US caption | zh-CN caption | zh-TW caption |
| --- | --- | --- | --- |
| Display and gamma | Choose a display and match the gamma reference pattern | 选择显示器并按参考图调整 Gamma | 選取顯示器並依參考圖調整 Gamma |
| Comparison | Compare the original and adjusted curves before saving | 保存前比较原始和调整后的曲线 | 儲存前比較原始與調整後的曲線 |
| Presets | Import and export presets from your library | 在预设库导入和导出预设 | 在預設庫匯入及匯出預設 |
| Sign-in settings | Choose which displays may restore a saved calibration | 选择登录时可恢复校准的显示器 | 選擇登入時可回復校準的顯示器 |

## Notes for certification

The packaged WinUI desktop app and its console host use `runFullTrust` to query display configuration, apply and verify temporary SDR gamma curves, manage the Windows ICC association under the current user, and run a separate process that can restore an interrupted preview. The sign-in task is off on fresh installation. The app has no sign-in screen or test account requirement.

1. Install as a standard user on an interactive Windows 11 desktop. Open DCCW++ from Start and choose a display. In Settings, verify that sign-in startup is off.
2. On an independent SDR display, run `dccwpp.exe displays list --json`. Use its display ID with `dccwpp.exe calibration diagnose --display <id> --json`. For the preview test, use a display whose driver reports a readable logical curve.
3. In the Calibration wizard, start a preview and adjust gamma slightly. Use the original comparison, then choose **Cancel & restore**. Repeat and let the preview expire. Confirm that the prior association and logical curve are restored. Use `dccwpp.exe calibration capture --json` before and after to record the states.
4. On a test display, start a new preview and choose **Save & apply**. Confirm that the new ICC appears as the selected profile in Windows. Run the wizard's undo preview and confirm it to restore the previous association and curve.
5. Run `dccwpp.exe capabilities --json` to check command discovery. Import a preset in the UI and verify it appears in `dccwpp.exe presets list --json`. Switch app language in Settings and repeat the preset check.
6. On a saved test calibration, enable the Windows sign-in task and the per-display restore switch. Sign out and back in. Use `dccwpp.exe startup last-restore --json` to check the background result. Disable both settings once the check is complete.

Microsoft accepts scenario details in Notes for certification. Record the tested build, architecture, Windows build and graphics hardware alongside the candidate. [Certification process](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-certification-process)
