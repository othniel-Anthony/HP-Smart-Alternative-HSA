# Changelog

## 0.17.3
- **A second printer of the same model is now its own entry in the printer list.** HSA used to treat every Windows queue of one model as one printer, so a second L3250 (or any second printer of a model you already had) plugged in next to, or after, the first never appeared, and the entry shown could carry the queue of a printer that was not even plugged in. HSA now follows each queue's USB port to the physical printer it was installed for: two printers that are both plugged in stay two entries, and queues left over from printers that are not plugged in fold into the one that is (as before), which now carries the queue that works. A printer of the same model that is plugged in after you have chosen a printer by hand is switched to like any newly plugged-in printer, and the printer you are using is matched by its exact name before a similar one. **Checked** with unit tests for the merging, the picking and the matching, and on this computer's real queues (three L3250 queues, one printer plugged in: one entry, with the right queue). **Not yet checked with two real printers of one model plugged in at the same time.**
- **Epson counter and cleaning commands go to the right printer when two of one model are plugged in.** The command used to go to the first Epson of that model found; it now goes to the printer the selected queue was installed for.

## 0.17.2
- **HSA holds back its own background work while an Epson counter read or cleaning has the printer's USB port.** The automatic printer searches and the 20-second status request now wait until the counter read, reset, clean, nozzle check or power flush has finished, so they cannot get in the way of it. This is a precaution: the "about a minute" counter read could not be reproduced here (on a real L3250 the read takes about 1 second, also while HSA kept searching because the printer list changed every few seconds), so it is not known whether this is what slowed it on your computer.
## 0.17.1
- **Closing HSA during an Epson clean no longer leaves the printer stuck busy.** The clean, power flush and nozzle check put the printer into remote-command mode and told it to leave only when the run ended normally. Closing HSA (or a run that failed or was cancelled) skipped that, and the printer went on waiting for more commands: it showed busy long after its cleaning had finished and ignored the next command. HSA now always tells the printer to leave remote mode when a run ends early or the window closes (without a reset, so the cleaning in progress carries on). Checked on a real Epson L3250: with the old release, closing HSA 10 seconds into a cleaning left the printer ignoring commands (and busy until it was switched off and on); with the fix, the printer finished the cleaning and accepted and ran the next command two minutes later.
- **The Home page no longer repaints itself while you leave it open.** The printer's status is refreshed every 20 seconds, and each refresh tore down and rebuilt the tiles, the supply rows and the alerts even when nothing had changed, which showed as a flicker. Each part is now rebuilt only when what it shows has changed. Checked with a test that leaves the Home page alone through two status refreshes and finds the very same tiles and supply rows afterwards (it fails on the old code).
- **The update check runs on every start, and no longer gives up.** It used to be skipped when the previous check was less than 12 hours old (my doing: I thought twice a day was enough), it waited until the "Install HSA?" question was answered, and one failed attempt (no network yet right after logging in, GitHub busy) was never repeated. It now checks on every start without waiting for that question, and when GitHub cannot be reached it tries again after 30 seconds, 2 minutes and 10 minutes. Checked against a stand-in for GitHub: the check happens on each of several starts, while the install question is open, and after the stand-in comes back; the open-question and retry checks fail on the old code. The "every start" part could not be shown failing on the old code, because the old code skipped its own limit whenever the test address was set; the limit is plain in the code.
- **Print several copies of the colour test page.** The Home screen's "Print test page" tile now asks how many copies (1 to 99) and remembers the last number. Checked against the simulated printer (it is asked for the right number of copies); not yet checked on a real printer.
- **A printer added, removed, plugged in or switched on while HSA is open now appears (or disappears) by itself** within a few seconds, instead of only after pressing Search or restarting. HSA compares a cheap summary of the Windows printers and the USB printers every four seconds and searches again when it changes; a search for network printers also runs every three minutes. These searches are silent: the printer you are using stays connected and the list only changes when something changed. Checked by adding and removing a Windows printer while HSA was open (shown after 8 seconds, gone after 8 seconds).
- **The search for network printers asks three times instead of once.** A single question can be lost (it is a UDP packet) or a sleeping printer can answer late, which left a printer out of the list now and then. Checked with a stand-in printer that ignores the first question: found now, missed before. Whether this or the previous item was what you saw is not known.

## 0.17.0
- **The embedded web server of more HP printers works over USB.** Many HP printers (LaserJet and Color LaserJet MFPs among them) have the interface that carries their embedded web server handled by Windows' scanner driver instead of WinUSB, and HSA could not open those. HSA now opens them through the scanner driver's own device file, so the printer's embedded web server, IPP printing, eSCL scanning and supply levels all work over the USB cable without changing any driver. Interfaces bound to WinUSB are still tried first.
- Tested on a real **HP Color LaserJet MFP M283fdw** (the web page "HP Color LaserJet MFP M283fdw" with its System, Print, Fax, Scan, Copy, Networking and HP Web Services tabs, plus toner levels, over USB) and a real **HP Color LaserJet Pro MFP 4303** (its Cartridges page and toner levels over USB), including 200 requests with five at a time on each of the 4303's two interfaces and a read that times out or is cancelled. Not tried: models whose web-server interface Windows gives to some other driver, and printers that have both a WinUSB and a scanner-driver interface on the same cable (the Smart Tank 580 does; it was not plugged in for this test, and its WinUSB interfaces are still tried first).
- On the M283, HP's driver locks its WinUSB web-services interface to administrators (access denied); HSA skips it and uses the scanner-driver one, which an ordinary user can open.
- **Buttons on a printer's web page that post a form no longer break the connection.** On the M283, pressing "Print" for a report on the embedded web page showed "not connected" and left the printer's USB web server answering 404 to everything until it was switched off and on; nothing printed. A browser that opened the page at the local address (127.0.0.1) sends that address as the form's Origin and Referer, while HSA tells the printer its name is "localhost"; the printer evidently took that for a request from another site. HSA now presents those two headers to the printer as "localhost" as well. After the change the same request prints the report and the web server keeps working. The earlier failure was not reproduced again on purpose, so the cause is the one difference between the failing and the working request (the Referer), not something demonstrated by switching it back.
- **A multifunction printer is now one entry.** Its USB interfaces were listed as a separate printer named after a driver label ("HP LEDM", "HP Smart Universal Printing(REST)"). The model name is now taken from the interface that carries it (function labels such as "(REST)" are dropped), so the USB web server, the scanner and the Windows print queue join the printer they belong to; "M282M285" in a scanner's name is recognised as the models M282 and M285. The Windows queue of a printer's fax function ("Fax - HP ...") is no longer taken for its print queue.

## 0.16.4
- **The "update is ready" step no longer waits behind thousands of progress reports.** The update download used to report progress after every 64 KB, about 2,600 times for the 163 MB file, and each report queued work on the window's thread. When that thread was busy (in testing, a tool that constantly queries the window; an accessibility tool could do the same), the "download finished" step queued up behind all of them: the file was downloaded in about 8 seconds but HSA took over 160 seconds to say so. Progress is now reported once per whole percent. Checked on the real exe against the real GitHub release with the window being polled the whole time: about 9 seconds, against over 160 seconds before.

## 0.16.3
- **A download that stops receiving data is given up and tried again.** If an update download receives nothing for 30 seconds it is abandoned (no half-finished file is kept) and tried again automatically, up to three attempts, before the bar offers "Try again". Tested with a server that sends half the file and goes silent. This is a safety net; it has not been seen to matter in real use.
- Correction: this release was prompted by a download that seemed to hang for over a minute in automated tests, which these notes first called a stall. It was not: the download took about 8 seconds, and the delay was the app finishing the step slowly while a test tool queried its window (see the entry above).

## 0.16.2
- **Uninstall fix.** Removing HSA from *Apps & features* could leave its install folder (with `HSA.exe`) behind: the shortcut and the Apps & features entry were removed, but the folder was deleted only once, about three seconds in, while HSA was still open showing its "was removed" message and the exe was locked. If you took longer than that to click OK, the folder stayed. The clean-up now keeps retrying, once a second, until the folder is gone. A copy installed by 0.16.0 or 0.16.1 gets the fix when it updates to this version.
- Tested: a unit test that locks a file in the folder for longer than the old wait and then releases it, and a run of the uninstaller on a real single-file exe.

## 0.16.1
- **HSA picks the printer that is plugged in by USB and online, by itself.** Until you choose a printer by hand, the printer connected by USB at that moment is selected when HSA starts, instead of the remembered printer or the Windows default. "Plugged in" is checked against the USB bus, not the Windows printer list (Windows keeps a queue for every USB printer you ever installed and still shows it as normal when it is unplugged), and a queue set to "use printer offline" does not count. With several USB printers connected, the remembered one wins, then the Windows default. Once you pick a printer yourself, a search never takes the choice away; only a printer plugged in since the last search is switched to, with a notice.
- Tested on a real Epson L3250 on USB with seven unplugged USB queues installed (including the Windows default) and with a different network printer remembered: the USB printer was selected in both cases. Not yet tried with two USB printers plugged in at once.

## 0.16.0
- **HSA can install itself.** A copy that is not installed offers to install itself the first time it starts (Install, Not now, or Don't ask again); *Settings > Updates and installation* has the same button. It copies itself to your user folder, adds a Start menu shortcut and an *Apps & features* entry (so it can be removed from there), and restarts from the installed copy. No administrator rights and no extra installer program; your scans and settings are not touched. The location is the one the `Install-HSA.ps1` script in the zip already used, so both count as the same installation.
- **HSA checks GitHub for new versions** a few seconds after it starts (at most twice a day; switch it off in Settings) and shows a bar when there is one: **Download and install**, *What's new*, *Skip this version* or *Later*. By default the new version is downloaded in the background, and you choose when to **Restart and update**: HSA closes, replaces itself and opens again in the new version, keeping the old file until the swap worked.
- **Updates are checked before they are used.** The download must come from this project's GitHub releases, match the size and the SHA-256 listed in the release's `SHA256SUMS.txt`, and be a Windows program with the announced version; otherwise it is thrown away and nothing changes. A release without the checksum file is never offered. The checksum file sits next to the exe on GitHub, so this catches damaged or swapped downloads, not a compromised GitHub release: HSA is still not code-signed.
- Updates work for an installed copy and for an exe you simply keep in a folder; a copy in a folder HSA is not allowed to change gets a link to the download page instead. Only versions from 0.16.0 on can update themselves: 0.15.x users download this one by hand once.
- Tested with the real single-file exe against a local stand-in for GitHub: first-run install offer, update found and downloaded, install, restart-and-update, uninstall. **Not yet tried against the real GitHub release**, which this release itself is the first to be able to serve.

## 0.15.1
- **HP maintenance now works on current HP printers over USB.** Tested on a real HP Smart Tank 580-590 over USB: ink levels, cleaning levels 1 to 3, report pages and printhead alignment all ran and the printer reported success. These printers speak HP's newer JSON web services (CDM) rather than the older XML ones, so HSA now supports both and picks the one the printer answers to.
- **Printhead alignment on these printers is two steps**, as the printer requires: HSA prints the alignment page, you put it face down on the scanner glass, then HSA has the printer scan it and reports the result. (Starting the scan with nothing on the glass makes the printer fail it.)
- **USB fix:** some HP printers answer an idle read with an empty packet straight away instead of waiting. HSA looped on those without ever timing out, which made the USB connection appear to hang. Reads now time out properly, clearing leftover data finishes, and a failed USB connection is no longer mistaken for a working web server.
- A job the printer ends in failure (not just one that never starts) is now reported as a failure.

## 0.15.0
- **New: Canon maintenance** (in the menu only while a Canon printer is selected; over USB). **Clean print head** (cleaning, deep cleaning, black only, colours only), **Align print head** (automatic alignment) and **Print a report** (nozzle check pattern, head alignment check page). The commands are the ones Canon's own open-source Linux maintenance tool sends. Canon printers report no status over this channel, so the page cannot tell when a job has finished: it waits about as long as the job normally takes before allowing the next one. **Not tested on a real Canon printer**: none has been available.
- **Canon ink absorber (waste ink pad) counter reset is not included.** Canon only allows it from a service mode entered with a button sequence on the printer, and the commands are model specific and unpublished (the only open work covers one model, the G6020). The page says so.

## 0.14.0
- **New: HP maintenance** (in the menu only while an HP printer is selected; works over USB and the network). Ink levels, **Clean printhead** (levels 1 to 3), **Align printhead** and a **Print a report** menu, all taken from what the printer itself says it supports (HP's web services), as in HP Smart. Cleaning and reports wait for the printer to finish, stop with a clear message on a paper or door problem, and say so if a printer ignores a command. Alignment works on printers with automatic or semi-automatic alignment; printers that need you to pick patterns by number are pointed to the printer web page. This is built from HP's open-source printing project (HPLIP) and was tested against a simulated HP printer only: it has not yet run on a real HP printer.
- **Ink levels for HP tank and cartridge printers** are read the way HP's own software reads them (ink tanks, missing cartridges and unknown levels handled), and the support report now includes the raw HP web-services documents.

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
