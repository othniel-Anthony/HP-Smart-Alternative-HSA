# Changelog

## 0.13.2
- **The release build includes the Epson model database again**, so the waste-ink counter reset works without choosing a file. It is the Apache-2.0 `database.json` from the EWR project; its licence text and the credits for the projects it builds on (reinkpy, ez-reset, reink, Gutenprint) are in `THIRD-PARTY-NOTICES.md` and the `licenses` folder of the zip. A file in HSA's data folder or next to `HSA.exe` still overrides it.

## 0.13.1
- **The public release has no Epson model database inside it.** `build.ps1 -NoDatabase` leaves it out; the counter reset then needs an `epson-database.json` that you provide (data folder, or next to `HSA.exe`, or *Use a different database file…*).
- **Tidier Epson maintenance page.** Cleaning is now a single "Clean print head" drop-down (levels 1-3, black only, colours only, power ink flush), next to the nozzle check and a "print a nozzle check afterwards" tick box. The printer reset is one line. The waste ink counters are in a collapsed section, with the database status and "use a different database file" tucked at the bottom of it. The long explanations moved into the confirmation dialogs, where they matter.

## 0.13.0
- **Power ink flush** on the Epson maintenance page: Epson's own power cleaning (the standard cleaning command with its "power" flag), which pushes far more ink through the print head to clear stubborn clogs. It asks for confirmation, runs once, and can print a nozzle check afterwards.
- **The Epson model database is built into the app** when it is present at build time, so the counter reset needs no file to be chosen. A file in HSA's data folder or next to `HSA.exe` still overrides it. The database is not part of the source repository.
- **Cleaning now tells you when a printer ignores the command.** A cleaning or power flush that leaves the printer idle is reported ("the printer did not react") instead of being counted as done, with the driver's own Maintenance tab as the fallback.
- Checked on a real L3150: a normal cleaning runs for about 2.5 minutes (status 07) and then returns to idle.

## 0.12.3
- **Epson nozzle check prints one pattern and pushes the sheet out.** It used to send three commands and then a reset, which left the page stuck half way. It now sends a single nozzle-check command, waits until the printer is idle again, leaves remote mode without a reset and sends the end-of-page (form feed). Checked on real Epson L3250 and L3150 printers; adding a "job end" command made the pattern print twice, so it is not used.
- If a printer reports its error state during a nozzle check or cleaning, HSA stops waiting after 15 seconds and says so (check paper and jams) instead of waiting for minutes.

## 0.12.1
- **Cleaning levels.** Head cleaning now comes in three levels (1 = one full cleaning, 2 = two in a row, 3 = three in a row, each waiting for the printer to finish first), with an option to print a nozzle check afterwards. The independent *Black only* and *Colours only* cleanings are unchanged.
- **The model database is picked up automatically** from HSA's data folder, or from a file named `epson-database.json` next to `HSA.exe`; the page shows where it was loaded from, and an updated file is re-read without restarting.

## 0.12.0
- **Epson waste-ink counter reset.** On the *Epson maintenance* page: HSA identifies the selected model (from what the printer reports and its Windows name, and refuses if they disagree), reads the counters and shows how full they are, saves a backup of the printer's memory, writes the model's reset values, reads each one back, and undoes everything if the printer refuses a write. *Undo last reset* restores the backup. It uses the printer's USB control channel (IEEE 1284.4 / EPSON-CTRL) and needs a model database file that you provide; HSA does not ship one (see THIRD-PARTY-NOTICES.md for the protocol credit).
- The Epson nozzle-check and cleaning fix from 0.11.1 and the Epson maintenance page from 0.11.0 are included.

## 0.11.1
- **Epson nozzle check and head cleaning no longer cut the page short.** HSA used to send the "leave remote mode" reset about a second and a half after the command, which stopped the printer mid-page. It now asks the printer for its status and only releases it once it has finished and is idle again. The Maintenance page tells you to wait until the page is out.

## 0.11.0
- **New: Epson maintenance** (only appears in the menu while an Epson printer is selected). Print a nozzle check, clean the print head (everything, black only, or colours only) and reset the printer (cancels the jobs waiting in Windows and sends the printer a standard reset). The commands go straight to the printer's USB port using Epson's documented remote-mode commands, with the printer's answer checked first, so nothing is sent to the wrong printer when several are connected. The page also opens the Epson driver's settings.
- Not included on purpose: resetting the waste-ink / "service required" counter. It means writing to the printer's memory with commands that differ per model, and it hides a real wear warning; the page explains this instead.

## 0.10.6
- **One printer, not three.** A USB printer used to show up as separate entries when Windows, the USB cable and the scanner driver spelled its name differently ("HP DJ 1110 series" for the print queue, "DeskJet 1110 series" over USB). Those entries are now recognised as one printer, so the web page, ink levels and scanner reach the printer you actually select, and Copy (which needs both a scanner and a printer) works.
- **HP ink levels over USB and the network** for printers that have no IPP, such as many USB-only HP inkjets (read from HP's web services).
- **Feeder scanning with a Windows scanner driver:** fixed "The parameter is incorrect". HSA now picks the driver's feeder source, sets the options in the order Windows expects, keeps to the values the driver allows, and retries with fewer options if the driver still refuses. If the printer also has a network scan service, a failed network scan falls back to the scanner driver.
- **USB transfers:** a print or scan request that was already sent is never sent a second time after a hiccup (this could print twice). The app waits longer for slow replies and closing the connection no longer hangs. Printers whose USB interface doesn't speak HTTP are given up on in seconds instead of minutes.
- **PDF printing over USB** goes straight to the printer when it has an IPP-over-USB connection and accepts PDF, instead of being turned into a huge image by the driver first. The driver is still used if the printer refuses.
- Driver printing no longer fails because one optional setting (paper size, quality, two-sided) is rejected by the driver.
- Clearer messages when a printer has no USB web page or ink levels, and the support report now lists every USB interface and which driver owns it, plus recent USB / scanner / print timing lines.

## 0.10.5
- **Faster start-up.** The printer you used last time is connected the moment the window opens; the network search (about 3 seconds) now finishes in the background instead of holding everything up. Previously the window waited for that search before it connected to any printer.
- The installer unpacks the app once while installing, so the first launch from the Start menu no longer spends several seconds extracting ~160 MB (`HSA.exe --prewarm`, exits straight away).
- The exe is 44 MB smaller: on-device AI / machine-learning / widgets parts of the Windows App SDK that HSA never uses are left out.

## 0.10.4
- The installer now clears Windows' "downloaded from the internet" mark on the installed copy, so the Start menu shortcut opens the app without the "Windows protected your PC" box.
- The build script can sign the exe when you have a code-signing certificate (`-PfxPath` / `-CertThumbprint`). New guide: docs/code-signing.md, covering the free, paid and Microsoft Store routes.
- README explains the first-run box and how to unblock the exe.

## 0.10.3
- The printer card on Home is now plain too (no tint, no accent bar). The remaining colour is the icons, headings and navigation highlight.

## 0.10.2
- Home tiles no longer have a tint or coloured outline: plain tiles with only the coloured icon.

## 0.10.1
- Toned down the pastel colours (softer icon colours, lighter tints, fainter outlines) and removed every gradient: the page backdrop, the printer card background and the line under the printer bar are now flat.

## 0.10.0
- **New: "Print test page" on the Home screen.** Prints the colour test page `print-color-test-page-basic-1.pdf` (built into the app, sent to the printer byte-for-byte unchanged, in colour, via the Windows driver or directly over IPP). The Printer page's generated page is now called "Print diagnostic page".
- **Pastel colour accents:** each Home tile has its own tint and icon colour (sky, mint, lavender, butter, pink, peach), a gradient-edged printer card, lavender headings and navigation highlight, pastel card borders, a gradient line under the printer bar and a soft pastel backdrop. Works in light and dark themes.
- Fixed clipped tile borders on the Home screen.

## 0.9.1
- New app icon: the supplied HP logo (white disc on red), cropped to a square and rendered as a rounded-square icon at 16-256 px. The original and the 1024 px master are kept in `src/PrintHub.App/Assets/source`.

## 0.9.0
Verification release: every feature was exercised end to end (see "Testing" in the README) and the problems that turned up were fixed.
- **Fixed** ID-card copy never asked you to flip the card when no progress reporter was attached.
- **Fixed** "Crop to the document" failed together with "Straighten crooked pages" on a dark scanner lid (the straightened corners were filled with white).
- **Fixed** a page scanned sideways was saved as a "searchable" PDF with no text. The saved PDF now stands the page upright and carries the text layer; your on-screen edits are not changed.
- **Improved** "Copy text" reads sideways and upside-down pages (tries the other orientations when the first reading looks like gibberish).
- **Fixed (accessibility)** the main action buttons (Scan, Save, Print, Copy text, Camera, Start copy, Print test page, ...) had no accessible name; they are now named, and the Scan button's name follows its label.
- **Improved** Add-by-IP accepts `host:port`, probes all candidate addresses at once (a wrong address now fails after one timeout instead of up to nine) and tolerates `http://` prefixes.
- **New** `--print <file>` launch option; print-to-file for Windows queues; `HSA_DATA_DIR` environment variable to run with a separate data folder (used by the UI tests).
- **Tests** 77 automated tests (Windows-driver printing through "Microsoft Print to PDF", real Bonjour discovery over multicast sockets, a fake network MFP process for IPP/eSCL/web) plus 125 UI-automation checks that drive the real app (`tests/ui`).
## 0.8.0
- Renamed to **HP Smart Alternative (HSA)**: executable `HSA.exe`, window title, installer (`Install-HSA.ps1`), data folders
  (`%LOCALAPPDATA%\HP Smart Alternative`, `Documents\HSA Scans`) and package name (`HSA-<version>-win-x64.zip`).
- New app icon (red rounded square with white "hp" mark), generated at 16-256 px for the window, taskbar and installer.
## 0.7.0
- **Scan with a camera**: live preview, capture, optional automatic crop / straighten / whiten.
- **Share** a saved scan with the Windows share sheet.
- Self-contained release build (no Windows App SDK or .NET install needed) with per-user installer scripts.
- Simulated-USB test harness for the HTTP-over-USB proxy.

## 0.6.0
- Full UI: **Print** (drag and drop, previews, copies, colour, two-sided, paper, fit/fill/actual/photo size, quality, page range,
  driver or direct IPP), **Scan** (presets, flatbed/feeder/duplex, filmstrip, rotate, crop, auto-crop, straighten, filters,
  brightness/contrast, text, signature, OCR "copy text", save/save as, print), **Copy** (including ID card, both sides on one page),
  **Shortcuts** (create / edit / run one-tap workflows), **Settings**.

## 0.5.0
- App shell: navigation, persistent printer picker with live status, Home dashboard with ink/toner levels and alerts,
  Printer page (information, maintenance tools, support report, jobs on the printer), embedded printer web page (WebView2),
  extended title bar, dark/light/system theme.

## 0.4.0
- Printing engine: Windows driver queues (System.Drawing printing) and direct IPP; PDF rendering through the Windows PDF engine;
  photo sizes; diagnostic test page; copy engine; persisted settings and shortcuts.

## 0.3.0
- Scanning: eSCL client, WIA backend, non-destructive page edits, document detection and deskew, PDF writer with invisible OCR
  text layer, Windows OCR, multi-page TIFF.

## 0.2.0
- Discovery (mDNS + Windows queues + USB + WIA, merged per physical printer), IPP client with supply levels / state / jobs,
  `PrinterSession` that gives USB and network printers the same interface.

## 0.1.0
- Solution skeleton, USB interface scanner (IPP-USB and HP web-services interfaces), WinUSB bulk-pipe stream and the loopback
  HTTP proxy that exposes a USB printer's embedded web server, IPP and eSCL.
