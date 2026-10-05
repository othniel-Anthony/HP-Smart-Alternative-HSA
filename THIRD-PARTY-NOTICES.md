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

## Epson model database

HSA's public releases do **not** contain any per-model Epson data, and the repository contains none either. The counter reset reads a user-supplied `epson-database.json` from HSA's data folder or from next to `HSA.exe` (or one chosen with *Use a different database file…*).

A build made on a machine that has `src/PrintHub.Core/Resources/epson-database.json` embeds that file in `HSA.exe`, unless it is built with `build.ps1 -NoDatabase`. The file is git-ignored. The database the maintainer uses privately is the `database.json` of the EWR 1.4.1 tool, whose licence terms are not stated, so builds that embed it are not published.
