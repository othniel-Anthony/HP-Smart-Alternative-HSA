<div align="center">

# HSA — HP Smart Alternative

**A free, open-source Windows app for managing HP printers, drivers, and firmware — without HP Smart or the HP App.**

[![Latest Release](https://img.shields.io/github/v/release/othniel-Anthony/HP-Smart-Alternative-HSA?style=for-the-badge&color=05145C&label=Download)](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/latest)
[![License](https://img.shields.io/github/license/othniel-Anthony/HP-Smart-Alternative-HSA?style=for-the-badge)](LICENSE)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4?style=for-the-badge)](#-requirements)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge)](https://dotnet.microsoft.com/)
[![Release Date](https://img.shields.io/github/release-date/othniel-Anthony/HP-Smart-Alternative-HSA?style=for-the-badge)](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases)

[Download](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/latest) · [Features](#-features) · [Screenshots](#-screenshots) · [Install](#-installation) · [FAQ](#-faq) · [Report a Bug](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/issues)

</div>

---

## Why HSA?

You bought an HP printer. You didn't sign up for HP Smart. The HP App is slow, pushy with a Microsoft-account sign-in, and quietly uploads telemetry in the background. Worse, it **hides** useful features behind its own UI — features that Windows already supports.

HSA gives you the keys back:

- **🧹 Remove all HP driver packages** in one shot — HP Smart doesn't even let you remove *one* cleanly
- **🖨️ Full advanced settings** for every printer, with no Microsoft account required
- **📋 Per-job control** — pause, resume, cancel any individual print job
- **🔌 Works offline, no account, no telemetry, no ads**
- **⚙️ One-click clean install** when a Windows update breaks your HP driver

Built by **Circuit & Ink** (Georgetown, Guyana) for the people who actually service HP printers.

---

## 📸 Screenshots

> Coming soon — these are placeholders until the project goes fully public.

| Main window | Drivers tab | Firmware tab |
| :---: | :---: | :---: |
| _placeholder_ | _placeholder_ | _placeholder_ |

> Have a good screenshot? Open a PR to add it.

---

## ✨ Features

### 🖨️ Printers

- **Enumerate every printer** Windows can see — local, network, shared — with live status
- **Set as default, rename, delete** printers
- **Open advanced settings** — the same dialogs HP Smart hides
- **Open Printing Preferences** for per-document defaults
- **Print test pages** with one click
- **Pause, resume, or purge** an entire printer's queue
- **Per-job control** — pause, resume, or cancel any individual print job

### 🧹 Drivers

- **List every driver** in the Windows driver store
- **See which printers** use which driver (so you know what will break)
- **Remove a specific driver** with a safety check
- **Remove ALL HP drivers in one shot** — force-removes everything HP-branded from the driver store, with a progress bar and an audit log
- **Install a driver from an INF file** — useful when Windows Update gives you the wrong one

### 🔧 Firmware

- **Detect current firmware** on network printers via **SNMP** (RFC 3805 + HP enterprise OIDs) or **IPP**
- **Deep-link to HP's official firmware page** for the detected model — no more hunting for the right download
- _Coming in v0.2_: **Push firmware updates** to printers that support the standardized PWG 5100.11 IPP System Services Update operation — no reverse-engineering, no signing bypass

### 🎨 App

- **Material You (M3) themed** — clean, modern, brand-color aware
- **Single instance** with a `--new-instance` recovery flag
- **Per-action admin elevation** — UAC prompts only when you actually need it (driver install, spooler control), not the whole app
- **Crash-safe** — every startup error lands in a log file you can read, not a silent failure
- **Self-contained** — one `.exe`, no .NET install required

---

## 📦 Installation

### Requirements

- Windows 10 (1809 or later) or Windows 11
- That's it. HSA ships with the .NET runtime bundled.

### Install

1. Download **`HSA.exe`** from the [**Latest Release**](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/latest) page
2. Save it anywhere (Desktop is fine)
3. **Double-click** to run

> **First run on Windows SmartScreen?** Click *More info* → *Run anyway*. The binary is unsigned in v0.1; code signing is on the v0.2 roadmap.

### Update

HSA does not auto-update. To upgrade:

1. Download the new `HSA.exe` from the latest release
2. Replace your existing copy
3. Launch

Your settings and per-printer history are stored in `%LOCALAPPDATA%\HSA\` — they're not in the `.exe`, so they survive a copy-replace.

### Uninstall

1. Delete `HSA.exe`
2. (Optional) Delete `%LOCALAPPDATA%\HSA\` to remove logs

There's nothing else — no installer, no registry entries, no scheduled tasks, no services.

---

## ❓ FAQ

### Is this an official HP product?

No. HSA is an independent open-source project built by Circuit & Ink. "HP" in the name refers to the printers it manages, not the company. The HP logo is used to identify printer compatibility, the same way file managers show PDF or ZIP icons.

### Will this void my HP warranty?

No. HSA only talks to the same Windows APIs (print spooler, WMI, driver store) that HP Smart and every other printer utility uses. It doesn't modify printer firmware, doesn't bypass any signing, doesn't phone home.

### Why not just use HP Smart?

You're welcome to. HSA is for people who:

- Don't want a Microsoft account sign-in to print a test page
- Want to clean out **all** the HP drivers HP's own tool refuses to remove
- Need to see the actual Windows-level error message when something goes wrong
- Don't want their printer software updating itself in the background

### Is it safe?

HSA does three things and only three things, all through documented Windows APIs:

1. **Reads** printer / driver / firmware information (no write)
2. **Controls** the print spooler when you ask (pause, resume, cancel, purge)
3. **Adds or removes** driver packages in the driver store, but only when you explicitly click a button

Driver removal requires admin elevation (UAC prompt) every single time. You can't accidentally run it.

### Will it work with my non-HP printer?

Yes — the printer **enumeration** and **queue / job control** work with any printer. The driver cleanup features specifically target HP, but the app is happy to list, monitor, and manage printers from any vendor.

### Why is the binary 70 MB?

It's a self-contained `.exe` with the .NET 8 runtime, WPF, and all dependencies embedded. You don't need to install .NET. If size matters, you can build a framework-dependent version yourself (see [Development](#-development)) which is ~3 MB, but you'll need the .NET 8 Desktop Runtime installed.

### My antivirus flagged it. Is it a virus?

HSA v0.1.0 is **unsigned** because we don't have a code-signing certificate yet. Some antivirus products flag unsigned executables from unknown publishers. The full source is in this repository — you can audit it, build it yourself, or wait for the signed v0.2 release. If you have concerns, open an issue and we'll help verify.

### How do I report a bug?

[Open an issue](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/issues) on GitHub. Please include:

- Windows version (10 22H2? 11 23H2?)
- Printer model
- What you were trying to do
- The log file at `%LOCALAPPDATA%\HSA\Logs\hsa-YYYYMMDD.log` (it has the exception details)

---

## 🛠 Development

### Prerequisites

- Windows 10 1809+ or Windows 11
- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)
- Visual Studio 2022, Rider, or just `dotnet` on the command line

### Build from source

```powershell
git clone https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA.git
cd HP-Smart-Alternative-HSA
dotnet build HSA.sln -c Release
.\src\HSA\bin\Release\net8.0-windows\HSA.exe
```

### Build the self-contained release binary

```powershell
dotnet publish src/HSA/HSA.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
# Output: publish\HSA.exe (~70 MB)
```

### Project layout

```
HSA.sln
├── src/HSA/                        WPF app
│   ├── App.xaml / App.xaml.cs     DI + Serilog + single-instance bootstrap
│   ├── MainWindow.xaml            Top app bar + tabbed navigation
│   ├── Models/                    POCOs: PrinterInfo, PrintJob, DriverInfo, FirmwareInfo
│   ├── ViewModels/                Hand-rolled MVVM
│   ├── Views/                     UserControls for each tab
│   ├── Services/                  Business logic — IPrinterService, IDriverService, IFirmwareService
│   ├── Native/                    P/Invoke (winspool, IPP), WMI, SNMP, pnputil wrapper
│   ├── Themes/AppTheme.xaml       Material You (M3) light theme
│   ├── Resources/                 Icon assets + Build-Icon.ps1
│   └── Helpers/                   JobControlHelper, WindowHelper
├── .github/workflows/release.yml  Build + publish on tag push
├── CHANGELOG.md                   Per-version release notes
└── README.md                      You are here
```

### Cut a new release

1. Bump `<Version>` in `src/HSA/HSA.csproj`
2. Add a section to `CHANGELOG.md`
3. Commit + push to `main`
4. Tag: `git tag v0.2.0 && git push origin v0.2.0`
5. The GitHub Actions workflow (`.github/workflows/release.yml`) builds the binary and creates the Release automatically

### Coding conventions

- 4-space indent, LF line endings (enforced via `.gitattributes` — TODO)
- `_camelCase` private fields, `PascalCase` public members
- XML doc comments on all public service methods
- Async all the way down — no `.Result` or `.Wait()` in production code

---

## 🗺 Roadmap

**v0.2** (next)
- [ ] PWG 5100.11 IPP System Services firmware push
- [ ] Windows Update catalog integration for auto-driver fetch
- [ ] Code signing certificate (clean SmartScreen experience)
- [ ] Network printer auto-discovery (mDNS / WS-Discovery)
- [ ] Consume ink/toner level display

**v0.3+**
- [ ] Dark theme
- [ ] Localization (Spanish, French)
- [ ] Power user features: scheduled cleanups, driver profile export/import
- [ ] Plugin model for vendor-specific extensions

Have a feature request? [Open an issue](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/issues) with the `enhancement` label.

---

## 📄 License

HSA is released under the **MIT License**. See [LICENSE](LICENSE) for the full text.

TL;DR: do whatever you want with the code, just keep the copyright notice and don't blame us if it breaks your printer.

---

## 🙏 Acknowledgments

- **HP** for making printers that are perfectly serviceable without their software
- **The .NET team** for WPF, which is still the most productive way to build Windows desktop apps in 2026
- **Lextm's SharpSnmpLib** for the SNMP client
- **The PWG / IETF** for IPP and the standards HP pretends don't exist
- **You**, for using an alternative instead of complaining

---

<div align="center">

Built with ❤️ by [**Circuit & Ink**](https://circuitink.tech) in Georgetown, Guyana

[⬇ Download HSA](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/latest) · [⭐ Star this repo](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA) · [🐛 Report a bug](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/issues)

</div>
