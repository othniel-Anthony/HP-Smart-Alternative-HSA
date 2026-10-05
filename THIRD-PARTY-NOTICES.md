# Third-party notices

## epson-usb

The Epson USB control protocol in `src/PrintHub.Core/Printing/EpsonD4.cs` (the IEEE 1284.4 "D4" session, the EPSON-CTRL message layout and the EEPROM read/write frames) follows the open-source **epson-usb** library, which was verified on real Epson L3250 / L3251 printers over Windows' USB print interface. HSA contains its own C# implementation, written from that library's documented byte layouts.

epson-usb is licensed under the MIT License:

```
MIT License

Copyright (c) 2026 Onur Kesim
Copyright (c) 2026 Ircama (his modifications and additions to this extraction,
relicensed under the MIT License with his permission:
https://github.com/Ircama/epson_print_conf/issues/35#issuecomment-5805756995)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Epson model database (EWR)

The Epson model database built into release builds of `HSA.exe` (per-model memory addresses and access keys for the waste-ink counter reset) is the `database.json` of **EWR - Epson Waste Reset** by RxNaison, taken unmodified from EWR 1.4.1.

- Project: https://github.com/RxNaison/Epson-Waste-Reset
- Licence: Apache License 2.0, Copyright 2026 RxNaison. The full text is in `docs/licenses/EWR-LICENSE.txt` (and in the `licenses` folder of the release zip).

EWR states that its database is assembled by an automated pipeline from four upstream open-source projects, whose work this therefore also builds on:

- reinkpy (https://codeberg.org/atufi/reinkpy)
- ez-reset (https://github.com/CiRIP/ez-reset)
- reink (https://github.com/lion-simba/reink)
- Gutenprint (https://gutenprint.sourceforge.net/)

The file is kept out of the source repository (it is git-ignored); release builds embed it when `src/PrintHub.Core/Resources/epson-database.json` is present at build time. A file named `epson-database.json` in HSA's data folder or next to `HSA.exe` overrides the built-in one.
