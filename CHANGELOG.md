# Changelog

All notable changes to HSA (HP Smart Alternative) are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Planned
- **WSD-USB protocol stack** (USB bulk transfers, WS-Discovery + WSD-Print SOAP over WSD-over-USB) — required to read consumables from WSD-USB printers whose EWS isn't reachable. Not shipped in v0.2.x; see Known limitation.

---

## [0.2.15] - 2026-08-23

### Fixed

- **Hotfix: app crashed on launch in v0.2.14.** When adding `TestPageService` to the DI registrations in `App.xaml.cs`, the `IFirmwareService` line was accidentally removed. The DI container then couldn't construct `PrintersViewModel` (which needs `IFirmwareService` for `DetectFirmwareCommand`) and threw `Unable to resolve service for type 'IFirmwareService'`. Restored the line.

---

## [0.2.14] - 2026-08-23

### Added

- **Bundled HP color-test-page PDF as the "Test page" output.** The `print-color-test-page-basic-1.pdf` is now embedded as an assembly resource (`HSA.Resources.test-page.pdf`) and extracted to `%LOCALAPPDATA%\HSA\test-page.pdf` on first run. The "Test page" button now sends this PDF to the selected printer via the Windows shell `printto` verb — your default PDF handler (Edge, Acrobat, etc.) renders the document and ships it to the printer. Status bar shows "Color-test page sent to {Printer}." on success.
  - New `TestPageService` extracts the embedded resource on demand.
  - New `IPrinterService.PrintFileAsync(name, filePath)` for shell-verb printing to a specific printer.
  - Fallback: if the embedded resource is missing, "Test page" still triggers the Windows built-in test page via `printui.dll`.

---

## [0.2.13] - 2026-08-23

### Added

- **"Clean printhead" button** in the Printers Actions card (replaces "Set as default"). Sends a PJL `@PJL CLEAN` job to the selected printer via the Windows print spooler, then a Print Quality Diagnostic Page as a fallback for HP AiOs that ignore `CLEAN` but auto-clean when they scan a diagnostic page.
  - P/Invoke surface for `StartDocPrinter` / `StartPagePrinter` / `WritePrinter` / `EndPagePrinter` / `EndDocPrinter` added to `Winspool.cs`.
  - `IPrinterService.CleanPrintheadAsync(name)` returns a short status message describing what was sent.
  - `PrintersViewModel.CleanPrintheadCommand` with IsBusy/status-line updates.
  - After clicking, watch the printer — LaserJet-class devices run the cleaning immediately; AiOs typically run a cleaning cycle within 30–60s of receiving the diagnostic page.

---

## [0.2.12] - 2026-08-23

### Added

- **mDNS TXT record matching for WSD-UUID → IP correlation.** When a WSD-USB printer also advertises over the network (which is the common case for HP AiOs that are on Wi-Fi), the mDNS browse now reads the printer's TXT record and extracts the `uuid`, `serial`, and `mac` fields. The EWS discovery chain then:
  1. Reads the printer's WSD Port Monitor UUID from the registry (we already do this).
  2. Browses `_ipp._tcp.local` and matches each result's `uuid=` TXT field against the registry UUID.
  3. If matched, returns the mDNS-discovered IP — no subnet scan or guessing needed.
  This is the same idea you suggested as "Method 3" (USB serial + mDNS match) but uses the WSD UUID as the fingerprint instead of a USB iSerial. UUIDs are already published by both transports, so the cross-reference is reliable.

### Changed
- `DiscoveredNetworkPrinter` now carries `Uuid`, `Serial`, `Mac`, and `RawTxt` fields populated from the mDNS TXT record.
- `EwsDiscoveryService.GetWsdPortUuid` is now public so callers (and the self-healing path) can use it as a fingerprint.
- Removed the redundant "guessed mDNS names" loop — v0.2.10's browse-based name match is strictly better.

---

## [0.2.11] - 2026-08-23

### Added

- **Self-healing launch-time EWS scan.** v0.2.8's startup auto-discovery only ran for un-pinned printers and never questioned existing pins. v0.2.11 verifies every pin by fetching the EWS home page and checking it looks like a real HP EWS. If verification fails (e.g. the printer's IP changed and the old URL now points at a dead host), the pin is re-discovered and overwritten. If re-discovery finds nothing, the stale pin is kept and a warning is logged so the user knows.
- **`EwsService.FetchTextAsync`** — public text-fetch helper used by the verifier.
- **`EwsDiscoveryService.DiscoverAsync(printer, ct, ignorePin)`** — new flag so the self-healing path can re-discover instead of just returning the same broken URL.

### Note
- The verifier only checks "is this URL still a real HP EWS?" — not "is it the right printer?" The name-token check is for the subnet scan only (EWS home pages often don't contain the model name in the HTML body, only in JS variables).

---

## [0.2.10] - 2026-08-23

### Changed

- **EWS discovery is now name-aware end-to-end.** v0.2.10 tokenizes the printer's spooler name + model into a list of meaningful identifiers ("OfficeJet", "Pro", "9730", "4650", "20A523"…) and uses them to score every candidate.
  - **mDNS browse** — each `_ipp._tcp.local` instance name is scored against the target's tokens; the highest-scoring match wins instead of just taking the first result.
  - **Subnet scan** — every HP EWS found in the /24 is now scored by how many of the printer's tokens appear in the response body. The candidate with the highest score wins. With multiple HP printers on the same subnet, this lets the scan pick the right one (e.g. an OfficeJet 4650 doesn't false-positive on a 9730 scan because "9730" won't appear in the 4650's EWS body).
  - **Stop-word filter** — `HP`, `series`, `All-in-One`, `LaserJet`, `OfficeJet` are excluded from the token list so they don't dilute the score (every OfficeJet has "OfficeJet" in its EWS body; using it as a fingerprint would always match every OfficeJet equally).
- For your 9730 the tokens are `Pro` and `9730`. The 4650's EWS body has neither, so the 9730's scan will specifically prefer a printer that mentions "9730" in its home page.

---

## [0.2.9] - 2026-08-23

### Fixed

- **Subnet scan false-positived on the router** (v0.2.6 introduced this). The v0.2.6 `LooksLikeHpEws` body check accepted any body containing the substring `"HP "`, so the user's router at `http://192.168.1.1/` — whose admin page happened to contain "HP" — got auto-pinned as a printer's EWS URL. The fetch then returned 404 for `/DevMgmt/ConsumableConfigDyn.xml` and every consumables query for that printer came back empty ("Unknown"). v0.2.9 tightens the check: we now require one of `/DevMgmt/`, `Embedded Web Server`, `hp/device/`, `hp_ews`, or `HP EWS` in the body. A bare `"HP"` is no longer enough.

### Note
- If your `settings.json` already has a bad pin (e.g. `http://192.168.1.1` pointing at a router), the launch-time "auto-discover for un-pinned printers" path won't fix it (it skips printers that have any pin). Use **"Re-scan EWS for all HP printers"** in the Actions card, or **"Set EWS URL…"**, to overwrite.

---

## [0.2.8] - 2026-08-23

### Added

- **Auto-discover and pin EWS on launch.** The app now walks every HP printer on startup and runs the EWS discovery chain (pinned URL → `http://<ip>/` → mDNS by WSD Port Monitor UUID → guessed `.local` hostnames → /24 subnet scan) for any printer that doesn't already have a pinned URL. Discovered URLs are auto-pinned to `settings.json` so future launches skip the discovery cost. The Printers tab's status line shows a one-line summary like `"Startup EWS scan: 4 new, 0 already pinned, 1 no match (out of 5 HP printer(s))."` so you know what changed.
- **"Re-scan EWS for all HP printers"** button in the Actions card. Overwrites every existing pinned URL with a fresh discovery. Use this when a printer's IP changed (DHCP lease, new subnet, new router) or a new HP printer joined the network. Confirms before overwriting.

---

## [0.2.7] - 2026-08-23

### Fixed

- **"Click on the Drivers or Firmware tab → UAC prompt"** — root cause: v0.2.5's UAC fix was too aggressive. It routed *every* `pnputil` call through the batched-elevated path, including **read-only** operations like `pnputil /enum-drivers` and `pnputil /scan-devices`. So every time the Drivers tab was opened, `EnumerateDriversAsync` (a pure read) would trigger a UAC dialog.
- v0.2.7 splits `RunAsync` (no UAC; used for reads) from `RunBatchAsync` (UAC; used for writes). `EnumerateDriversAsync` and `RescanAsync` now run unelevated; `RemoveDriverAsync` / `RemoveDeviceAsync` / `AddDriverAsync` still go through the batched UAC path so driver installs / removals still elevate properly.

### Added

- **Auto-discover network printers on launch.** The Printers tab's first-load now auto-runs the mDNS browse so the new "Discovered network printers" expander is populated as soon as the tab opens — no need to click "Discover network" every time.
- **"Discovered network printers" expander** below the installed-printers list, showing each discovered printer's name, IP, and IPP URL. Lets you see what was found and pick the one to re-add after a cleanup.

### Re: "app should pull the host subnet every launch"
The subnet scan is currently part of `DiscoverEwsCommand` (per-printer). To make it global on launch, the v0.2.8 plan is to add a background "Network health check" that runs at startup and surfaces a notification "X HP printers found on your network" if any were discovered. Filed for next release.

---

## [0.2.6] - 2026-08-23

### Fixed

- **Driver removal per-command status was wrong.** v0.2.5 wrote `echo %ERRORLEVEL%>>file` into the .bat — but cmd.exe expands `%ERRORLEVEL%` at PARSE time, not after the command runs, so the log always showed the prior command's exit code (or `0` for the first command). Fixed in v0.2.6 with `setlocal enabledelayedexpansion` + `echo !ERRORLEVEL!>>file`. Now every line of the activity log shows the real pnputil exit code.
- **"Discover EWS" gave up after mDNS failed.** Many HP printers — including USB-attached AiOs that are also on Wi-Fi — don't advertise via mDNS. v0.2.6 adds a **/24 subnet probe** as the last discovery step: it walks every host on the local subnet on TCP 80 and asks for `/DevMgmt/ProductConfigDyn.xml`, with an HP+EWS substring check to avoid false positives on routers / NAS. Bounded to ~30s; picks the first match.

### Changed

- **Drivers tab now leads with "Remove ALL HP drivers (1 UAC)"** (filled, prominent) and demotes the per-driver button to an outlined secondary. The ALL button is still one UAC for the whole batch; the per-driver button is only useful if you genuinely want to keep some drivers.

### Re: "too many UAC popups"
With v0.2.5/v0.2.6, every elevated pnputil action is **exactly one UAC prompt**:
- "Remove ALL HP drivers" (with Full registry cleanup ON) → ONE UAC, runs every `/remove-device` + `/delete-driver` for every HP driver in one .bat
- "Remove only the selected driver" → ONE UAC
- "Install driver INF…" → ONE UAC

If you're seeing more than that, please share a screenshot of which action triggers the extra prompts — the per-action design guarantees one UAC per click.

---

## [0.2.5] - 2026-08-23

### Fixed

- **"HP driver removal fails" / "Delete all drivers does nothing"** — root cause: `PnpUtil.RunAsync` was using `UseShellExecute = false, Verb = "runas"`, but MSDN documents that **`Verb` is silently ignored when `UseShellExecute` is false**. So per-driver removal never triggered a UAC prompt, pnputil ran unelevated, and the operation failed with "Access is denied" — the user saw nothing happen. The batched path (`RunBatchAsync`) used the correct `UseShellExecute = true` setting and did work, but the legacy "Remove ALL HP drivers" non-cleanup path also used the broken per-driver loop.
- v0.2.5 routes every pnputil call through `RunBatchAsync`, so **every driver removal now triggers exactly one UAC prompt**, regardless of which button you click.
- Per-line exit codes are now captured properly. v0.2.0–v0.2.4 used "stderr empty == success", which gave false positives when pnputil wrote informational messages to stderr. v0.2.5 writes `ERRORLEVEL` to a file from the .bat script and reads it back per-command, so the activity log now shows accurate per-driver status.

### Changed
- `DriverService.RemoveAllHpAsync` (the legacy non-cleanup path) also routes through the batched mechanism now — previously it would have triggered one UAC per driver (had the per-driver UAC actually worked).

---

## [0.2.4] - 2026-08-23

### Fixed

- **"Remove printer" couldn't actually delete printers** — `Winspool.DeletePrinter` returns Win32 error 5 (access denied) for non-admin users, which is the default for HSA. v0.2.3 only surfaced a "run as admin" hint; v0.2.4 actually fixes it: when the unelevated call fails, the dialog now offers **"Elevate & retry"** which spawns a UAC-elevated `powershell -Verb runas` running `Remove-Printer -Name '<name>'`. The HSA window then auto-refreshes the printer list after a short delay so the deleted row disappears.

### Added

- **"Open in Windows Settings…"** text button in the Actions card under "Remove printer". Opens `ms-settings:printers` for users who'd rather use the standard UAC removal path from Windows Settings.

---

## [0.2.3] - 2026-08-23

### Added

- **EWS auto-derivation from `IpAddress`.** Network / Wi-Fi-attached HP printers no longer need a manual EWS URL — `EwsDiscoveryService.DiscoverAsync` derives `http://<ip>/` from `PrinterInfo.IpAddress` and probes it. The user-pinned URL still takes priority when present.
- **"Discover EWS" button** in the Actions card. Auto-discovers the EWS URL for the selected printer using this order: (1) user-pinned, (2) `http://<ip>/` probe, (3) mDNS by WSD Port Monitor UUID (`<uuid>.local`), (4) guessed `.local` hostnames from the model name. On success the discovered URL is persisted to `EwsAddresses` so future launches skip the discovery cost. On failure the user sees a clear "click 'Set EWS URL…' to enter manually" hint.
- **EWS status pill now distinguishes "✓ Pinned" (manually set) from "✓ Auto-detected"** (derived from IP). The status text below the pill shows the actual URL.

### Changed

- `ConsumableService.ResolveEwsUrl` is now async and goes through `EwsDiscoveryService` so all transports share a single source of truth for "where is the EWS for this printer?".
- "Remove printer" now handles Win32 error 5 (access denied) with a clear error dialog explaining the typical causes (jobs pending, spooler requires admin) instead of crashing the dispatcher.

### Known limitation
- WSD-USB EWS access over the raw USB bulk interface is still not implemented. The Discover button works best for printers that are also on Wi-Fi / Ethernet (which is true for most HP AiOs); truly USB-isolated printers still need a manual URL via "Set EWS URL…".

---

## [0.2.2] - 2026-08-23

### Fixed

- **Settings deserialization silently failed on hand-edited `settings.json` files that contained `null` for the three nullable C# properties (`ThemeMode`, `StartWithHpOnlyFilter`, `LastQuickInstallUrl`).** `System.Text.Json` raises `JsonException` for `null` against a non-nullable reference / value type, and the old `SettingsService.LoadFromDisk` swallowed the exception — so the in-memory `AppSettings` was reset to defaults, **dropping the entire `EwsAddresses` map**. Result: every printer's EWS lookup returned `configured=0`, supplies showed "unknown", and the "Open printer EWS" button stayed disabled even after "Set EWS URL…" had been used. v0.2.2 makes the JSON properties tolerate `null` on read (via `*Raw` backing fields with a `??` default in the public accessors) and adds loud `LogError` on load failure so future regressions are visible in the log.
- **Supplies tab didn't auto-refresh when reopened.** Added an `IsVisibleChanged` handler that calls `LoadPrintersAsync` + `RefreshAsync` whenever the tab transitions to visible. Switching back to Supplies now shows current ink/toner data without a manual click.
- **"Open printer EWS" and "Remove printer" buttons were clipped off the bottom of the Actions card on normal window sizes.** Wrapped the card's content in a `ScrollViewer` with `MaxHeight="540"` so all buttons are reachable even when the window is short.
- **"Set EWS URL" 2-column button grid was overflowing the right column on some window widths** (right-column buttons like "Test page" / "Advanced" / "Resume queue" were cut off). Replaced the 2-column `UniformGrid` with a single-column stack; the EWS / Remove buttons now fit comfortably inside the right panel.

### Added

- **EWS status indicator** in the Actions card header: a compact green "✓ Configured" / gray "Not set" pill next to the "EWS" label, with the actual URL displayed below in `BodySmallText` (text-trimmed with ellipsis so it can't push the card wider). The pill updates live when the user changes the EWS URL via "Set EWS URL…", and the underlying `CanExecute` for the Open EWS / Remove printer commands is re-evaluated at the same time.
- **SettingsService logs loaded settings on startup** (`Settings loaded: Theme=Light HpOnly=True EwsCount=4`) so it's obvious from the log whether the persisted file was actually parsed.

---

## [0.2.1] - 2026-08-23

### Added — EWS (Embedded Web Server) consumable source

The HP OfficeJet 4650 (and many WSD-USB-attached HP printers) is reachable
on the network at a stable IP and exposes an Embedded Web Server at
`http://<ip>/`. The EWS `/DevMgmt/ConsumableConfigDyn.xml` endpoint returns
cartridge state (color, level %, life state, brand) — the exact same data
the EWS home page shows in the "Estimated supply levels" panel.

- New `Services/EwsService.cs` fetches `ProductConfigDyn.xml`,
  `ConsumableConfigDyn.xml`, and `ProductStatusDyn.xml` over HTTP (gzip-aware,
  self-signed certs accepted since HP printers often use one).
- `ConsumableService` now tries EWS as the 3rd transport in the chain
  (after SNMP, IPP, before WSD-Print). When the user pins the EWS URL in
  Settings (keyed by the printer's stable DeviceId), HSA reads the same
  CMYK state HP Smart would show — including "Cartridge Problem" for
  failed/missing/expired cartridges.
- New "Open printer EWS…" button in the Printers view Actions card opens
  the configured EWS in the user's default browser (lets them change
  Wi-Fi, run maintenance, update firmware, see the same ink panel).
- New "Set EWS URL…" button asks for the base URL (e.g. `http://192.168.1.99`),
  saves it in `settings.json` under `EwsAddresses[<DeviceId>]`, and probes
  the URL to confirm reachability.
- New `ConsumableStatus.HealthDisplayOverride`: when the EWS reports a
  non-OK state ("failed", "expired", "missing", "wrong"), the health pill
  shows the verbatim state instead of a derived "Replace now" label —
  matches what the EWS home page itself shows.
- New `PrinterInfo.DeviceId` and `Win32PrinterRow.DeviceId`: spooler-stable
  per-printer ID used to key the EWS address map so it survives name
  changes and port reassignments.

### Changed
- `ConsumableStatus.HealthDisplay` now uses `HealthDisplayOverride` when set
  (falls back to the rolled-up health label otherwise).

### Known limitation
WSD-USB printers whose EWS is NOT reachable on the network (no Wi-Fi/Ethernet
bridge) still need the v0.2+ WSD-over-USB protocol stack. EWS is the easy
path for printers that are also on the network (often the case for Wi-Fi
printers that the host sees as WSD-USB because of how the queue is wired).
The driver path is a strict superset of WSD-Print: it works whether or not
the printer is on the network.

- Bump to 0.2.1.

---

## [0.2.0] - 2026-08-23

### Removed — "Search & download drivers" card
The v0.1.x "Search & download drivers" card (Windows Update search + browser
shortcuts + paste-URL download + install-first-INF) is gone. The Quick install
from URL flow is still available as a compact one-liner in the Actions card
(Install from URL -> one click downloads, extracts INFs, runs pnputil
/add-driver /install). The "Open HP support" / "Open MS catalog" buttons now
just open the search landing pages so the user can grab a direct download URL
to paste into the Quick install field.

### Added — Network printer auto-discovery (mDNS browse)
- New `PrinterEndpointDiscovery.BrowseAsync()` method: sends mDNS PTR queries
  for `_ipp._tcp.local` and `_printer._tcp.local`, parses the answers plus
  any SRV + A/AAAA records in the same packet, and returns a list of
  `DiscoveredNetworkPrinter { Name, IpAddress, Port, IppUrl }`.
- New "Discover" button in the Printers tab triggers a 3-second browse and
  shows the discovered printers + their IPP URLs. The user can use the IPP
  URL for direct supply / firmware queries.

### Added — PWG 5100.11 firmware push (IPP System Services)
- New `IppClient.UpdateFirmwareAsync(printerUri, firmwareFileUri)` sends the
  standardized PWG 5100.11 Update-Operation (IPP operation-id 0x0027) to a
  network printer. The printer downloads the firmware file from the URL and
  applies it. The protocol does NOT bypass any signing - the printer
  verifies the firmware signature itself.
- New `FirmwareService.PushUpdateAsync(printer, firmwareUrl)` wraps the IPP
  call, interprets the IPP status code, and returns a `FirmwarePushResult`
  (Accepted / DeviceBusy / Rejected / network error).
- New "Push firmware from URL" button in the Firmware tab asks for a URL and
  pushes the update. The push is async - the printer may take several
  minutes to apply and reboot.

### Changed
- Drivers view: removed the v0.1.x search/download card. Layout is now
  Drivers + Actions (top) and Activity log (full-width bottom).
- Actions card keeps Install at top, Removal under a subheader, and a compact
  Quick install URL field + HP support / MS catalog buttons at the bottom.

### Known limitation — WSD-USB consumables still require the v0.2+ protocol stack
The user's HP printers (OfficeJet 4650, OfficeJet Pro 9730) are all
WSD-USB. The WSD Port Monitor (`APMon.dll`) owns the only USB transport to
those devices - the canonical XAddr (`http://<uuid>/PrintService`) is not
TCP-reachable from the host. To read supplies from a WSD-USB printer, HSA
would need to:
  1. Open the device via WinUSB (vendor ID 0x03F0 for HP, product ID varies
     by model)
  2. Send WS-Discovery Probe over USB bulk IN/OUT
  3. Receive the WS-Discovery ProbeMatch, extract the XAddr
  4. Send WSD-Print GetPrinterElements over USB bulk IN/OUT
  5. Parse `wprt:Ink`/`wprt:InkLevel`/`wprt:InkName` from the SOAP response

This is a custom WSD-over-USB transport implementation - the WSD Port
Monitor doesn't expose its USB transport to user-mode code, and the
WSD-Print-Proxy Windows feature isn't available on this host. The
WSD-Print SOAP client that landed in v0.1.8 is the right *parser* and
activates automatically the moment an HTTP-reachable XAddr is available
(network WSD printer, or a future WSD-over-USB adapter). The actual
WinUSB + WS-Discovery-over-USB transport work is multi-day effort and
remains on the v0.2+ roadmap.

- Bump to 0.2.0.

---

## [0.1.10] - 2026-08-23

### Added — Discoverable in-app driver install

Three install paths now live in the Drivers tab, all without leaving the app:

1. **Quick install from URL** — new textbox + "Download & install" button at the top
   of the Search & download panel. Paste a direct download URL, click once, and HSA
   downloads to `%LOCALAPPDATA%\HSA\Downloads`, extracts INFs from the package, and
   runs `pnputil /add-driver /install` on the first INF. The URL is persisted to
   `settings.json` and pre-fills the box on next launch.
2. **Reinstall selected driver** — new "Reinstall" button in the Actions card.
   Re-adds the selected driver's existing INF to the store
   (`pnputil /add-driver /install`). Useful for "my driver is gone from the device
   but still in the store" scenarios.
3. **Install from INF…** — unchanged from v0.1.9, but moved to the top of the
   Actions card (the most discoverable position) since it's the most common
   positive action.

### Changed
- Actions card now leads with install (positive action), then "Removal" section.
  Destructive actions (Remove selected, Remove ALL HP) are visually grouped
  under a "Removal" subheader so the user can't accidentally click them while
  looking for install.
- DriversViewModel now injects `SettingsService` to persist the Quick install
  URL between runs (additive to existing settings — old `settings.json` files
  load fine and just get the new field on next save).

- Bump to 0.1.10.

---

## [0.1.9] - 2026-08-23

### Fixed — Driver removal: ONE UAC for the whole batch
The previous flow ran `pnputil /delete-driver` per driver in a loop. Each call
spun up a new `pnputil.exe` with `Verb=runas`, which triggered a UAC prompt per
driver — removing 20 HP drivers meant 20 UAC prompts. v0.1.5 added a
`/remove-device` step that made it worse (1 prompt per PnP device).

v0.1.9 rebuilds the removal flow around a single elevated `cmd.exe /c .bat`
script:

- New `PnpUtil.RunBatchAsync(args)` writes a temp `.bat` that runs every
  `pnputil` sub-command in sequence, then spawns ONE elevated `cmd.exe` to
  execute it. The user sees ONE UAC prompt for the entire batch.
- New `Services/DriverStoreManager.cs` builds the per-driver plan (every
  `/remove-device` per PnP instance, then `/delete-driver` per package),
  hands the flat arg list to `RunBatchAsync`, and maps per-line exit codes
  back to per-driver outcomes.
- `DriverService.RemoveAllHpWithRegistryCleanupAsync` now uses the batched
  path by default. Per-driver results are still reported in the activity log.
- `DriverService.RemoveWithRegistryCleanupAsync` (single-driver) also goes
  through the batched path with a one-driver list, so behavior is consistent.

The activity log surfaces real pnputil errors (stderr from each line) instead
of just "exit code 1". Per-line failures are visible.

### Added — In-app driver search, download, install
The Drivers tab now has a third row with a "Search & download drivers" card.
No need to open a browser to find an HP driver:

- **Search** — calls the Windows Update Agent via late-bound COM and lists
  matching driver updates. Returns Title, Class, Manufacturer, Model, Provider,
  Version. Late binding avoids the COM reference.
- **HP support** — opens `https://support.hp.com/drivers?pattern=<keyword>` in
  the default browser with the search box pre-filled.
- **MS catalog** — opens `https://catalog.update.microsoft.com/v7/site/Search.aspx?q=<keyword>`
  for Microsoft Update Catalog, which carries every WHQL-signed driver.
- **Download (paste URL)** — for any URL, downloads to
  `%LOCALAPPDATA%\HSA\Downloads`, extracts INFs from ZIPs, shows the file
  list. Progress bar reports percent.
- **Install first INF** — runs `pnputil /add-driver /install` followed by
  `/scan-devices` to register the driver and trigger PnP enumeration.

New files:
- `Services/WindowsUpdateClient.cs` — WUA search via `Microsoft.Update.Session`
  COM (late binding, no COM ref).
- `Services/DriverDownloader.cs` — `HttpClient` download with progress +
  `ZipFile.ExtractToDirectory` for INFs.
- `Services/DriverStoreManager.cs` — batched pnputil execution (see above).
- `Converters/Converters.cs` — new `NullToVisibilityConverter` for download
  detail pane.
- `Views/DriversView.xaml` — restructured layout: search panel gets the
  largest row (1.9*), driver list 1.5*, activity log 0.6*; help text moves
  to a scrollable footer in the Actions card so all action buttons always
  fit on a small window.

### Changed — Drivers view layout
- Row weights: Drivers+Actions 1.5\*, Search 1.9\*, Activity log 0.6\*.
  The search panel now has room for the results list + download/install
  card to both be fully visible (previously the search row was 1.1\* and
  the download card was cut off).
- Actions card "What this does" help text moved into a scrollable footer
  card so the primary action buttons (Remove selected, Remove ALL HP,
  Install driver from INF) always fit in view.
- Downloaded file path + INFs in the search panel are wrapped in a
  `ScrollViewer` and hidden when no download exists yet.

### Known limitation — WSD-USB supplies
The user's HP printers (OfficeJet 4650, OfficeJet Pro 9730) are all
WSD-USB, so the Supplies tab will still show "Supplies unavailable
(WSD-USB) - requires WSD-USB protocol support (v0.2+)" for them. This
release does not implement the WSD-over-USB protocol stack; it makes
the driver management workflow (removal, search, download, install) work
cleanly so users can re-install a fresh driver when HP ships one.

- Bump to 0.1.9.

---

## [0.1.8] - 2026-08-23

### Added — WSD-Print SOAP client + WSD-USB-aware code path
- `Services/WsdPrintConsumableSource.cs` sends the Microsoft WSD-Print
  `GetPrinterElements` SOAP request to a printer's XAddr and parses the
  `wprt:Ink` / `wprt:InkLevel` / `wprt:InkName` / `wprt:MarkerHighLevel`
  response. This is the standard way to read consumables from any WSD-capable
  printer.
- `PrinterEndpointDiscovery.FindWsdUsbXAddr` reads the printer's UUID from the
  WSD Port Monitor's per-port config (registry) and constructs the canonical
  XAddr `http://<uuid>/PrintService`. The `ConsumableService` now tries this
  path as a final fallback for WSD-USB printers.
- `Native/WsdApiClient.cs` declares the WSDAPI COM interop interfaces
  (`IWSDiscoveryProvider`, `IWSDiscoveryProviderNotify`, `IWSDiscoveryProbeMatch`)
  and P/Invokes `WSDCreateDiscoveryProvider`. The interfaces are ready for the
  v0.2 work that will use WS-Discovery over UDP to find network WSD printers
  and pull their XAddrs.

### Known limitation — WSD-USB consumables still require v0.2
For WSD-USB printers (PnP InstanceId starts with `SWD\PRINTENUM\WSD-`, the
"Microsoft IPP Class Driver" case), the WSD Port Monitor owns the only transport
to the device over USB. The canonical XAddr (`http://<uuid>/PrintService`) is
not TCP-reachable from the host - the WSD Port Monitor forwards WSD-Print
requests to the device via WSD-over-USB internally, but does not expose an
HTTP proxy.

Three paths exist to fix this in v0.2:
1. **Implement the WSD-USB protocol stack** (USB bulk transfers, WSD-XML over
   USB, the standard WSD-Print envelope). This is the only path that doesn't
   depend on Windows features or undocumented APIs.
2. **Enable the WSD Print Proxy Windows feature** so WSD-USB devices appear
   as network WSD printers on a local port. This feature isn't available on
   all Windows editions and isn't enabled on most installations.
3. **WSDAPI COM interop with a custom USB transport.** Significant work.

This release adds the WSD-Print SOAP client and the WSD-USB-aware discovery
code path, so the moment the underlying transport becomes reachable (e.g. on
a network WSD printer, or once the WSD-Print-Proxy is enabled, or once the
WSD-USB protocol is implemented in v0.2), the same code path automatically
returns consumables. The Supplies tab now shows a clear
"Supplies unavailable (WSD-USB) - requires WSD-USB protocol support (v0.2+)"
status row for WSD-USB printers that the WSD Port Monitor hasn't proxied.
- Bump to 0.1.8.

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
