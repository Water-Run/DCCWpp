# DCCW++

![DCCW++ app icon](design/branding/logo-preview.png)

DCCW++ is a native WinUI 3 display color calibration app for Windows 11. It aims to be a standalone alternative to Windows Display Color Calibration (`dccw.exe`) for SDR displays, with clearer controls, per-display presets, and a command line for automation. Microsoft Store is the planned distribution channel.

## Development status

**0.2 is a development milestone.** The SDR wizard, timed preview and recovery, and ICC save flow are implemented. Physical display behavior, ICC installation and restoration on supported hardware, and packaged end-to-end behavior still need acceptance testing. The Microsoft Store release is pending.

- Select a display and step through gamma, brightness, contrast, grayscale color balance, and before/after comparison. Inspect near-black and near-white patches, compare the curves in full screen, or skip hardware controls. The app can open Windows ClearType tuning after calibration.
- Preview an SDR curve with a bounded timer and an independent recovery process. Confirm to generate an ICC profile with the new curve, verify the installed file, and associate it with the selected display. Driver readback can verify the logical curve. Optical results require separate validation.
- Undo a saved calibration on the selected display. A timed preview shows the previous curve before you confirm the change to the previous Windows profile and its scope.
- Find and recover interrupted curve or ICC previews from the wizard or the CLI. If confirmation completed before the worker exited, recovery can finish the saved preset and automatic restore records after checking the current display and the recorded binding.
- Create, rename, and delete local presets. Import versioned JSON, reuse a previously imported preset, or make a separate copy. Import places presets in the library for review. Display bindings stay local and protect referenced presets from deletion.
- Inspect displays and profiles, manage presets, and run calibration transactions from `dccwpp.exe`. The CLI provides stable JSON fields, exit codes, and request IDs for retrying a preview safely. It shares calibration logic with the GUI.
- Use the app in English, Simplified Chinese, or Traditional Chinese. View local PNG, JPEG, or TIFF photos as visual references. You can switch to the References page during a preview. The current deadline still applies, and you can cancel from that page.

The SDR curve backend requires an independent display in SDR and checks for MHC2 hardware calibration before previewing. The wizard links to Windows display settings for Advanced Color and MHC2, and to Microsoft's HDR calibration tool for active HDR displays. In the installed MSIX, enable the Windows sign-in task and opt in for each calibrated display in Settings. The background monitor checks saved bindings at sign-in, on resume, and after a display change. Its periodic display check also detects layout and color mode changes when no notification arrives. If a preview is active, the monitor retries at 15-second intervals within a bounded wait.

The x64 development MSIX has been exercised in an interactive Windows Sandbox at 200% scaling. The check covered cold launch, the compact layout, language switching, full-screen reference patterns, and viewing a preset created through the packaged CLI. Physical calibration and Store-signed builds still need acceptance testing.

## Build from source

Use Windows 11, PowerShell 7, the .NET SDK selected by [global.json](global.json), and Visual Studio with Windows application development tools and Windows SDK 26100. The application pins Windows App SDK `1.8.260317003`.

```powershell
./scripts/Build.ps1 -Test
./scripts/Build.ps1 -Configuration Release -Package
```

The first command builds the unpackaged app and CLI and runs core and simulated worker tests. The second produces an unsigned development MSIX under `artifacts/packages/`. It uses a development identity. See the [Microsoft Store runbook](docs/STORE.md) for the packaging and submission steps.

## Command line

The MSIX declares the `dccwpp.exe` execution alias. For local development, run the built CLI directly or use `dotnet run --project src/DccwPlus.Cli --` followed by the arguments.

```powershell
dccwpp.exe capabilities --json
dccwpp.exe displays list --json
dccwpp.exe presets validate examples/neutral.dccw.json --json
dccwpp.exe calibration diagnose --display $displayId --json
dccwpp.exe calibration capture --json
dccwpp.exe calibration preview --display $displayId --gamma 2.3 --red-gain 0.97 --dry-run --json
dccwpp.exe apply $presetId --display $displayId --dry-run --json
dccwpp.exe bindings restore --dry-run --json
dccwpp.exe startup last-restore --json
dccwpp.exe recovery status --json
```

Agents can use `capabilities --json` to discover command effects, dry-run support, and request ID support. For a preset dry run, set `$presetId` to a saved preset ID and `$displayId` to an ID from `displays list`. The dry run checks for interrupted previews, reads the current curve and ICC association, and builds a candidate ICC in memory. Calling `apply` without `--dry-run` repeats those checks and starts a timed SDR preview if they pass. Use `calibration status`, `calibration confirm`, or `calibration revert` with its session ID.

For direct numeric control, use `calibration preview` with a display ID and Gamma or RGB gain values. Start with `--dry-run` and check `data.canPreview`. Once the user is ready to review, run the preview with a request UUID and use its `sessionId` with `calibration update` to tune individual values or switch between `--original` and `--adjusted`. The 10–60 second deadline is fixed for a CLI preview. Ask the user to inspect the display before confirming. The preset can enter the library as part of the confirmed save.

If a confirmed session reports `bindingStatus: pending`, query it with `calibration status <session-id> --json` and try `calibration recover <session-id> --json` to finish the local records. Use `recovery status --json` to find outstanding work across sessions.

Generate a UUID before starting a preview with `calibration preview`, `apply`, `calibration undo`, or `profiles preview` and pass it as `--request-id`. If the response is lost, use that UUID to check the session or retry the same command and arguments. Check the exit code and JSON `ok` field, then read the outcome fields in `data`. The [CLI reference](docs/CLI.md) covers command effects, transaction states, and exit codes.

Save `calibration capture --json` output before and after a test. Use `dccwpp.exe calibration compare before.json after.json --json` to compare the captured ICC, scope, logical curve, and display topology offline.

## Project layout

| Project | Responsibility |
| --- | --- |
| `DccwPlus.Core` | Calibration transactions, presets, storage, and localization |
| `DccwPlus.Windows` | Display discovery, ICC and gamma backends, recovery hosts, and startup integration |
| `DccwPlus.App` | WinUI 3 wizard, settings, and MSIX manifest |
| `DccwPlus.Cli` | Command-line entry point for users, scripts, and agents |
| `DccwPlus.Core.Tests` | Tests for data validation, calibration state, and recovery |
| `DccwPlus.Windows.Tests` | Preview-worker and command coordination tests with a simulated display backend |

- [Roadmap and acceptance criteria](docs/ROADMAP.md)
- [Development status and handoff](docs/STATUS.md)
- [Reference photography requirements](docs/PHOTOGRAPHY.md)
- [Privacy notice](docs/PRIVACY.md)

## License and references

DCCW++ is licensed under [GPL-3.0-only](LICENSE). Development MSIX packages include third-party notices. The [Store runbook](docs/STORE.md) describes how to build both architectures from an archived source snapshot and record their checksums. Bundled photographs will have separate attribution and license records.

The UI follows [WinUI Gallery](https://github.com/microsoft/WinUI-Gallery) conventions. [RunOnce](https://github.com/Water-Run/RunOnce) is the reference for Microsoft Store packaging, and Clash# is a reference for language selection and fallback. DCCW++ uses its own implementation and runs independently of these apps and `dccw.exe`.
