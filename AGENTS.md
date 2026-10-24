# DCCW++

- Product name: DCCW++. Native WinUI 3 Windows 11 application; Microsoft Store is the release target. GPL-3.0-only.
- Read README.md and docs/ROADMAP.md before extending calibration behavior. Track real implementation status; never report a saved draft or successful API call as verified display calibration.
- GUI, CLI and startup restoration must share core use cases. Keep platform-independent preset parsing and storage in Core.
- Add user-facing strings to all catalogs. CLI machine fields remain stable and language-independent.
- Imported presets are data, never executable commands. Validate versions, sizes, values and paths. Do not automatically apply on import.
- Display changes need a bounded preview and recovery path. Treat HDR, disconnected monitors and changed topology explicitly.
- Reference RunOnce packaging and Clash# localization concepts without copying their product identity or licensing incompatible code.
- Build: `./scripts/Build.ps1 -Test`; package: `./scripts/Build.ps1 -Configuration Release -Package`.
- Keep signing keys and real Store association artifacts out of version control. Do not change real user display settings as part of automated tests.
