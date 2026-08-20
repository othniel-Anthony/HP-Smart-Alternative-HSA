# HSA

A Windows desktop utility that manages HP printers, drivers, and firmware — without HP Smart or the HP App.

Built as a C# WPF app on .NET 8 with native P/Invoke into the Windows print spooler and WMI, plus a network-aware firmware detection layer (SNMP + IPP).

> **Status:** v0.1.0 — working MVP. See "Feature status" below.

---

## Why this exists

HP Smart and the HP App are buggy, slow, and lock useful features (firmware updates, advanced settings, full driver cleanup) behind their telemetry-driven flow. This tool gives you direct, scriptable access to the same Windows plumbing HP Smart uses under the hood, plus a few things HP Smart *can't* do — like removing **all** HP driver packages from the driver store in one shot.

## Feature status

| Feature                              | v0.1.0 | Notes |
|--------------------------------------|:------:|-------|
| Enumerate printers (WMI + Spooler)   |   ✅   | Local, network, shared. Status via `ExtendedPrinterStatus`. |
| Set as default                       |   ✅   | |
| Rename / delete printer              |   ✅   | |
| Pause / resume / purge queue         |   ✅   | |
| View + control print jobs            |   ✅   | Pause / resume / cancel individual jobs. |
| Open Printing Preferences dialog     |   ✅   | Wraps `rundll32 printui.dll`. |
| Open Advanced properties dialog      |   ✅   | Wraps `DocumentProperties` with `DM_PROMPT`. |
| Print test page                      |   ✅   | |
| Detect firmware (SNMP)               |   ✅   | RFC 3805 + HP enterprise OIDs. |
| Detect firmware (IPP)                |   ✅   | `get-printer-attributes` for `printer-firmware-version`. |
| Deep-link to HP support              |   ✅   | Auto-built from model name. |
| Push firmware (PWG 5100.11 Update)   |   🚧   | v0.2 — IPP System Services Update op. |
| Enumerate driver store               |   ✅   | Wraps `pnputil /enum-drivers`. |
| Show which printer uses which driver |   ✅   | Joins `Win32_Printer` ↔ `Win32_PrinterDriver` ↔ `pnputil`. |
| Remove a specific driver             |   ✅   | Wraps `pnputil /delete-driver`. |
| Remove ALL HP drivers                |   ✅   | Force-remove loop, progress, audit log. |
| Install driver from INF              |   ✅   | Wraps `pnputil /add-driver /install`. |
| Driver install from Windows Update   |   🚧   | v0.2 — WU API integration. |
| Per-action admin elevation           |   ✅   | `pnputil` and INF install trigger UAC as needed. |
| Auto-elevate the whole app           |   ❌   | Deliberate choice — see "Elevation model". |

---

## Architecture

```
HSA.sln
└── src/HSA/                       (single WPF project, ~30 files)
    ├── App.xaml / App.xaml.cs     Serilog + Microsoft.Extensions.Hosting/DI bootstrap
    ├── MainWindow.xaml            Three tabs: Printers · Drivers · Firmware
    ├── Models/                    POCOs: PrinterInfo, PrintJob, DriverInfo, FirmwareInfo
    ├── ViewModels/                Hand-rolled MVVM (ObservableObject, RelayCommand, AsyncRelayCommand)
    ├── Views/                     UserControls per tab
    ├── Services/                  Business logic (IPrinterService, IDriverService, IFirmwareService, IDialogService)
    ├── Native/                    P/Invoke + WMI + IPP + SNMP
    │   ├── Winspool.cs            P/Invoke for winspool.drv
    │   ├── WmiHelper.cs           WMI queries against root\cimv2
    │   ├── PnpUtil.cs             Wraps pnputil.exe
    │   ├── ElevationHelper.cs     runas-verb relaunch helper
    │   ├── SnmpClient.cs          v1/v2c GET over UDP/161 using Lextm.SharpSnmpLib
    │   └── IppClient.cs           Minimal IPP get-printer-attributes client over TCP/631
    ├── Themes/AppTheme.xaml       Material You (M3) light theme, Circuit & Ink navy
    └── Helpers/                   JobControlHelper, WindowHelper
```

### Elevation model

The app runs **asInvoker**. Privileged actions (`pnputil` calls, INF install, spooler control on a system printer) are spawned in a child process with the `runas` verb, which triggers a UAC prompt **per action**. This is deliberately finer-grained than running the whole UI elevated.

### Firmware update policy (hybrid)

HP locks raw firmware push behind their signed update service. Pushing unsigned firmware bypasses signing on a few models and fails on most. Instead, this app implements two paths and picks per printer:

1. **Modern network printers (post-2018)**: detect capability via IPP. If the printer exposes the **PWG 5100.11 System Services** endpoint, we can push firmware updates over IPP using the standardized Update operation.
2. **All other printers**: detect current firmware via SNMP or IPP, then build a deep link to `https://support.hp.com/drivers?pattern=<model>`.

---

## Build & run

### Prerequisites

- Windows 10 1809+ or Windows 11
- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)

### From the command line

```powershell
cd "C:\Users\Delta\Documents\Minimax\Hp Software alternative"
dotnet build -c Release
.\src\HSA\bin\Release\net8.0-windows\HSA.exe
```

### From Visual Studio / Rider

Open `HSA.sln` and F5.

### Self-contained single-file publish

```powershell
dotnet publish src/HSA -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output lands in `src/HSA/bin/Release/net8.0-windows/win-x64/publish/`.

---

## Single instance + recovery

HSA uses a named mutex so only one copy runs at a time. Subsequent launches signal the existing window to come to the foreground instead of starting a second process.

If the previous instance was force-killed (e.g. by `Stop-Process -Force` from a script or Task Manager "End Task") and the mutex got orphaned, launch with:

```powershell
.\HSA.exe --new-instance
```

This finds and kills the stranded process, waits for the kernel to release the mutex, then starts a new primary instance.

---

## Logging

Serilog writes rolling daily files to:
```
%LOCALAPPDATA%\HSA\Logs\hsa-YYYYMMDD.log
```
Kept 7 days by default.

---

## License

Internal tool, no license declared yet. Add one before distributing.
