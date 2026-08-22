# Changelog

All notable changes to HSA (HP Smart Alternative) are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Planned
- PWG 5100.11 IPP System Services firmware push (where supported by the printer)
- Windows Update catalog integration for automatic driver fetch
- Network printer auto-discovery (mDNS / WS-Discovery)
- **WSD-Print SOAP for WSD-USB printers** (consumables over the WSD port monitor's transport)

---

## [0.1.7] - 2026-08-22

### Added — IPP consumable query + multi-transport discovery
Supplies used to come only from SNMP, which only works for HP printers with a known
IP. The Supplies tab is now wired to a multi-transport query that tries each available
channel per printer:

1. **SNMP** (RFC 3805 `prtMarkerSuppliesTable`) for direct network HP printers.
2. **IPP** (PWG 5100.13 `marker-levels` / `marker-names` / `marker-colors` /
   `marker-types`) for any HP printer reachable via IPP - including IPP-over-USB
   devices that advertise via mDNS (`_ipp._tcp.local` / `_printer._tcp.local`).
3. **Location URL fallback** (the `DEVPKEY_Device_LocationInfo` the spooler stores
   in the registry) for IPP-over-USB printers that the host network can still reach.

New code:
- `Native/IppClient.cs` rewritten with a proper IPP decoder that handles
  `1setOf integer(0..100)` (RFC 8010 value tag 0x21) for `marker-levels` /
  `marker-high-levels` / `marker-low-levels`, plus `1setOf name` and `1setOf keyword`
  for marker names/colors/types. The old decoder only handled string values.
- `Services/IppConsumableSource.cs` queries `marker-*` attributes and maps the IPP
  values into `ConsumableStatus` (color, class, level %, health, part number).
- `Services/PrinterEndpointDiscovery.cs` implements the Location URL / mDNS discovery
  chain above. Includes a minimal mDNS query/parser (no external dependencies).
- `Services/ConsumableService.cs` orchestrates the multi-transport strategy.
- `ViewModels/SuppliesViewModel.cs` now lists **every** HP printer (network, local,
  WSD-USB) and shows a clear "Supplies unavailable (WSD-USB) - WSD-Print support
  coming in v0.2" row for printers that return no data.
- `ViewModels/PrintersViewModel.cs` now queries all HP printers (not just network)
  via the new `ConsumableService`, so the consumable chips on the Printers list work
  for any HP printer that exposes IPP or SNMP.

### Changed
- The Supplies tab's empty-state message is now informative instead of silent: it tells
  the user which printers are HP, which connection type they have, and what to do
  for WSD-USB devices.
- `IppAttributeSet` switched from `Dictionary<string, string>` to a typed
  `Dictionary<string, List<IppValue>>` so callers can read integer attributes
  directly. `FirmwareService` updated to use the new API.

### Known limitation - WSD-USB consumables
USB-connected HP printers that the spooler manages via the **WSD port monitor**
(their PnP InstanceId starts with `SWD\PRINTENUM\WSD-` and the port name with
`WSD-`) are the only HP printers that won't return supplies in v0.1.7. The
Microsoft IPP Class Driver + WSD port monitor owns the only transport to those
devices, and it doesn't expose an HTTP-reachable IPP endpoint. WSD-Print SOAP
(via the WSDAPI + a SOAP client) is the standard way to query consumables on
those devices and is on the v0.2 roadmap. The Supplies tab now shows a clear
"Supplies unavailable (WSD-USB)" status row for those printers so the user
isn't left wondering what happened.
- Bump to 0.1.7.

---

## [0.1.6] - 2026-08-22

### Fixed — Critical: Printers tab loaded zero rows, action buttons stayed disabled

The Printers tab was always empty on the user's machine, and every action button
(Set as default, Test page, Preferences, etc.) stayed greyed out no matter what
was clicked. Three layered bugs:

- **DataContext inheritance (latent since v0.1.0).** `MainWindow.DataContext` is
  `MainViewModel`, so each `<views:XxxView />` was inheriting `MainViewModel` as
  its own DataContext. Every `{Binding Printers}`, `{Binding RefreshCommand}`,
  etc. in the child views silently resolved to nothing. Fix: each child view now
  sets its own `DataContext="{Binding Printers}"` (etc.) explicitly on the tag.
- **WMI "Invalid query" (0x80041017).** The WQL parser on this host refused the
  `Win32_Printer` / `Win32_PnPSignedDriver` queries. Switched the helper to direct
  CIM via `ManagementClass.GetInstances()` — the same path PowerShell's
  `Get-CimInstance` uses — so the WQL parser is bypassed entirely.
- **WMI "Not found" (0x80041002).** Some WMI instances don't expose every
  documented property. Reading a missing property through the indexer throws
  `ManagementException`. Added a `WmiGet<T>` safe-accessor that catches the
  exception and returns the default value, so one missing property doesn't fail
  the whole enumeration.

Also: `EnumerateInstances` is now eager + clones each `ManagementObject` (via
`mo.Clone()`) so the consumer is never handed a disposed object from a torn-down
collection.

### Fixed — Action buttons stayed disabled even with a printer selected

`RelayCommand.CanExecuteChanged` was a plain event with no hookup to
`CommandManager.RequerySuggested`, so `CommandManager.InvalidateRequerySuggested()`
was a no-op. Buttons were stuck in their initial `IsEnabled=False` state because
no requery was ever requested. Fix: the event now subscribes to
`CommandManager.RequerySuggested` and `RaiseCanExecuteChanged` posts a requery.
SelectedProperty setters in `PrintersViewModel`, `DriversViewModel`, and
`FirmwareViewModel` also call `InvalidateRequerySuggested()` explicitly.

### Changed
- `IsHp` on `PrinterInfo` now also checks the `Name` field (and `Hewlett`) — the
  OfficeJet's `Name` is `HPI02082C (HP OfficeJet Pro 9730 Series)` which contains
  "HP" but the previous check only looked at Manufacturer/Model/DriverName.
- Bumped version to 0.1.6.

---

## [0.1.5] - 2026-08-20

### Added — Full registry cleanup on driver removal
- New `pnputil /remove-device` sub-command wrapper (`PnpUtil.RemoveDeviceAsync`) -
  the only pnputil op that actually unregisters a PnP device AND cascades to clean
  up its `HKLM\…\Services\<svc>` and `HKLM\…\Enum\<inst>` entries.
- New WMI query `WmiHelper.QueryPnpInstanceIdsForInf(inf)` returns the PnP
  instance IDs bound to a driver package.
- New `DriverService.RemoveWithRegistryCleanupAsync(driver)` runs the full
  pipeline: enumerate PnP devices → `pnputil /remove-device /force` for each →
  `pnputil /delete-driver /force` on the package. Returns a
  `RegistryCleanupResult` that reports per-device success and overall status.
- New `DriverService.RemoveAllHpWithRegistryCleanupAsync(progress)` does the
  above for every HP driver package, with a per-driver progress callback.
- New **Full registry cleanup** toggle in the Drivers view Actions card
  (default ON). When on, "Remove selected driver" and "Remove ALL HP drivers"
  use the new pipeline; the per-driver PnP devices are unregistered, the driver
  package is deleted, and the result is logged per device. Turn it off for the
  old store-only behavior.
- The activity log now shows per-device success/failure for the cleanup flow,
  so a partial result is easy to diagnose.
- Bump to 0.1.5.

---

## [0.1.4] - 2026-08-20

### Fixed — Mojibake throughout
- Replaced all `â€¦` (UTF-8 mis-decoded ellipsis), `â€"` (em-dash), `â€"` (en-dash),
  `â€¢` (bullet), `â€™` / `â€˜` (smart quotes), and `Â·` (middle dot) with their
  proper Unicode characters. Buttons like "Install driver from INF…" now render
  correctly. Affects 12 source files.

### Added — M3 linear progress bar in Drivers view
- New `M3ProgressBar` style (4dp track, rounded ends, surface-container tint behind
  a primary fill).
- Long-running driver operations (single INF install and bulk "Remove ALL HP
  drivers") now show the bar under the status line. Single install uses
  indeterminate mode (pnputil has no progress), bulk remove uses determinate
  mode driven by `IProgress<T>`.
- A new `IsInstalling` flag (separate from `IsBusy`) means the rest of the app
  stays usable during an install — you can switch tabs, browse the driver
  list, etc. The Install button guards against re-entry.

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
