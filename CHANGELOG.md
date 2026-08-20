# Changelog

All notable changes to HSA (HP Smart Alternative) are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Planned
- PWG 5100.11 IPP System Services firmware push (where supported by the printer)
- Windows Update catalog integration for automatic driver fetch
- Network printer auto-discovery (mDNS / WS-Discovery)

---

## [0.1.3] - 2026-08-20

### Changed — Modern controls (theme-wide)
- The default `CheckBox` style is now a Material 3 square (18×18, rounded 4, surface
  fill, primary fill when checked, white check path). Affects every checkbox in the
  app (HP-only filter, Supplies/Drivers/Printers/Settings) automatically.
- The default `ToggleButton` style is now a Material 3 filter chip (pill background,
  secondary-container fill when checked). Replaces the old Windows toggle used by
  the Supplies filter chips.
- `M3Switch` (used by the Settings dark-mode toggle) is now defined in both theme
  files instead of inline in `SettingsView.xaml` — same look, single source of truth.
- `TabControl` template now wraps the `TabPanel` in a horizontal `ScrollViewer` so
  the 5 tabs can never overflow off-screen on narrow windows.
- Bump to 0.1.3.

---

## [0.1.2] - 2026-08-20

### Added — Supplies chips in the Printers list
- Each row in the Printers list now shows a horizontal row of small color-coded
  chips for ink/toner (K / C / M / Y / etc.) with the level percent. Network HP
  printers are queried in the background right after a refresh; chips appear as
  data arrives, the row stays empty for USB-only printers.

### Added — Settings tab + Dark mode
- New `Settings` tab with two preference rows: Dark mode (Material You dark
  scheme) and "Start with HP-only filter".
- Settings are persisted to `%LOCALAPPDATA%\HSA\settings.json` via the new
  `SettingsService` and re-applied on next launch.
- The theme dictionary is installed **before** `MainWindow` is created, so the
  app boots in the right theme with no flash.
- A new M3 switch control template (`M3Switch`) drives the toggle.

### Added — About content moved into Settings
- Removed the "About" button from the top app bar. About content (app name,
  version, description, publisher, build date, repo link) now lives in the
  Settings tab alongside the local-data paths (settings file, logs, app data).
- The top app bar now shows the running version (`v0.1.2`) instead.

---

## [0.1.1] - 2026-08-20

### Added — Ink & toner management (new "Supplies" tab)
- `IConsumableService` reads each network HP printer's `prtMarkerSuppliesTable` (RFC 3805 Printer MIB) via SNMP
- Parses description, class, color, max capacity, level, and part number (regex over the description: e.g. `HP CF258A` → `CF258A`)
- Cross-references `prtMarkerColorantTable` for color names; falls back to description-text heuristics
- Computes a rolled-up health status (OK / Low / Replace soon / Replace now / Empty) with Material You thresholds
- New `Supplies` tab in the UI with:
  - Card list per consumable: printer name, description, part number, color, level % with a horizontal progress bar, color-coded health pill
  - Filter chips: All / Low or below / Replace
  - Per-printer refresh with progress percent
  - Network HP printers only (USB-only printers don't expose SNMP)

### Added — Model-specific printer icons
- New `IModelImageService` resolves an icon for each printer with this priority:
  1. Exact match in `Resources/printers/<normalized-model>.png`
  2. Family keyword match (LaserJet mono/color, OfficeJet, ENVY, Smart Tank, Neverstop, PageWide, DeskJet, DesignJet)
  3. Generic HP icon
- Six procedural family icons shipped in `Resources/printers/`
- `Generate-PrinterIcons.ps1` regenerates the procedural set (no real product images bundled)
- Printers list now shows a 48×48 rounded icon + a model-family subtitle (e.g. "LaserJet Pro (color)")
- Adding a real product photo is a 1-step process: drop `<normalized-model>.png` into `Resources/printers/`

### Added — Supporting infrastructure
- `SnmpClient.WalkTableAsync` for enumerating RFC 3805 tables
- `Converters/Converters.cs`: `IntEqualsConverter`, `CountToVisibilityConverter`, `PercentToWidthConverter`
- `ConsumableStatus` model with `ConsumableClass` and `ConsumableHealth` enums

### Changed
- Tab order updated: Printers, **Supplies**, Drivers, Firmware
- `PrinterInfo` model gains settable `ModelImageUri` and `ModelFamily` (with `INotifyPropertyChanged`)

---

## [0.1.0] - 2026-08-19

### First public release.
- Consume ink / toner level display
- Localization (Spanish, French to start)
- Code signing certificate for unattended installs

---

## [0.1.0] - 2026-08-19

### First public release.

HSA is a free, open-source Windows utility for managing HP printers, drivers, and
firmware without HP Smart or the HP App. Built by Circuit & Ink.

### Highlights

#### Printers
- Enumerate every printer visible to Windows (local, network, shared) via WMI + spooler
- Live status, default-printer indicator, port / driver / location info
- Open Printing Preferences and Advanced properties dialogs (the same dialogs HP Smart hides)
- Print test page
- Pause, resume, purge the print queue per printer
- Per-job control: pause, resume, cancel individual jobs

#### Drivers
- List every driver package in the Windows driver store
- Show which installed printers use which driver
- Remove a specific driver (with safety check for in-use drivers)
- **Remove ALL HP drivers in one shot** — force-remove loop with progress, audit log, and confirmations
- Install a driver from an INF file (`pnputil /add-driver /install`)

#### Firmware
- Detect current firmware version on network printers via SNMP (RFC 3805 + HP enterprise OIDs)
- Detect current firmware version via IPP `get-printer-attributes`
- Build a deep link to `https://support.hp.com/drivers?pattern=<model>` pre-filtered to the detected model
- Wire-up in place for the PWG 5100.11 IPP System Services Update operation (v0.2)

#### App
- Material You (Material Design 3) light theme, Circuit & Ink navy source
- Top app bar + tabbed navigation (Printers / Drivers / Firmware)
- Single-instance with `--new-instance` recovery
- Per-action admin elevation (UAC prompt only when needed, not whole-app elevation)
- Serilog to `%LOCALAPPDATA%\HSA\Logs\hsa-YYYYMMDD.log` (7-day retention)
- Global exception handlers — startup errors land in the log instead of failing silently
- Self-contained 64-bit Windows build, ~70 MB single-file `.exe`, no .NET install required

### Known limitations
- IPP System Services firmware push is wired at the transport layer but the Update
  operation body is a v0.2 deliverable
- Driver install relies on user-supplied INFs (no Windows Update integration yet)
- Consume ink/toner levels are not yet displayed
- Dark theme is not yet implemented (light theme only)

### Security
- Initial release is private on GitHub during early feedback; will be flipped public
  in v0.2 once the Windows code-signing flow is sorted out
