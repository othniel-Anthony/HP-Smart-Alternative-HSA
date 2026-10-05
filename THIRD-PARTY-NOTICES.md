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

HSA does **not** include the per-model data (memory addresses, access keys) that the waste-ink counter reset needs. The user supplies a database file, which HSA only reads; it is never part of this repository or its releases.
