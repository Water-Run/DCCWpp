# DCCW++ privacy notice

DCCW++ performs display calibration on your Windows device. It does not require an account. The app does not send your settings, photos, presets, or diagnostics to the DCCW++ project.

- **Display information.** DCCW++ reads display configuration, color mode, and ICC profile information. It stores local identifiers and recovery records so calibration and restoration target the intended display.
- **Presets and photos.** Presets are stored locally. Exported presets include their name, author, description, and calibration values. Share them only with the people you choose. Reference photos are opened from files you select.
- **Local records.** DCCW++ stores preferences, presets, calibration sessions, display bindings, and diagnostic records in its application data directory. The CLI exposes that directory and supports an alternate `--data-dir` for inspection.
- **Windows and Microsoft Store.** Windows applies ICC profile associations. Microsoft Store handles installation and updates when the app is distributed there. The app can open Windows Display Settings, ClearType, and external links at your request.

For privacy questions or support, contact the maintainers through the [DCCW++ repository](https://github.com/Water-Run/DCCWpp). Avoid posting private photos or machine diagnostics in a public issue.
