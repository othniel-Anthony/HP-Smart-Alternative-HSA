# Changelog

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
