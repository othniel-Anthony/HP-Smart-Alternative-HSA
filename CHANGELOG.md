# Changelog

All notable changes to HSA (HP Smart Alternative) are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Planned
- PWG 5100.11 IPP System Services firmware push (where supported by the printer)
- Windows Update catalog integration for automatic driver fetch
- Network printer auto-discovery (mDNS / WS-Discovery)
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
