# Printer drivers

Windows drivers for printers that HSA works with. They are kept here, apart from the program itself: the HSA app and its releases do not contain them, and the program never installs a driver by itself. The **Download drivers** link on HSA's maintenance pages opens this page.

The installers are the manufacturers' own files, unchanged, and are too large for the repository itself, so they are attached to the release named [`drivers`](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/tag/drivers). Check the SHA-256 after downloading (`Get-FileHash <file>` in PowerShell) against the table.

## HP

| Printer | File | Size | SHA-256 |
|---|---|---|---|
| DeskJet 2700 | [DeskJet2700.16.2.4.exe](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/download/drivers/DeskJet2700.16.2.4.exe) | 213 MB | `28006fc82e47d15231e8badc40b3114d3089651395f56891d74c1318a4f40e80` |
| DeskJet 2800 | [DeskJet2800.15.8.1.exe](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/download/drivers/DeskJet2800.15.8.1.exe) | 339 MB | `55c932f7d688422e170ad192603f6e581363a1ba9ad0ab99a852f1a5c1a4ef51` |
| DeskJet 2900 | [DeskJet2900.17.2.17.exe](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/download/drivers/DeskJet2900.17.2.17.exe) | 402 MB | `86a3318edfd5b7e3f28fa3f8a2cd9df5f9ba32de2ea3ba87f276bcf8085c6dcc` |
| Laser MFP 131 / 133 / 135-139 | [HP_Laser_MFP_131_133_135-139.exe](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/download/drivers/HP_Laser_MFP_131_133_135-139.exe) | 92 MB | `4805fd694ab966aef36d3107ce75856c2ff444fb6efc83e909ea96c3ed9804a5` |
| LaserJet 4301-4303 | [LaserJet4301-4303.16.2.4.exe](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/download/drivers/LaserJet4301-4303.16.2.4.exe) | 373 MB | `76afeeba65a5ac53b27800cd4314bd2c35fc1c4506af98898a442a2ee0d63982` |
| OfficeJet Pro 9130 | [OfficeJetPro9130.16.2.4.exe](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/download/drivers/OfficeJetPro9130.16.2.4.exe) | 363 MB | `92b1925cadabde54a4e78766b8a6c7e759846e44261f1ff8c039ccea5f663421` |
| Smart Tank 520 / 540 | [SmartTank520_540.15.8.1.exe](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/download/drivers/SmartTank520_540.15.8.1.exe) | 347 MB | `03b2c1a85cc253ae17f7ddef3f18788c7bc0ffeea03fb7c3a002b28d39a55220` |
| Smart Tank 580-590 | [SmartTank580-590.15.8.1.exe](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/download/drivers/SmartTank580-590.15.8.1.exe) | 356 MB | `eda1ed288ecf022379692bcadddcec9a5464c556dd62a39c4cb5885fb2d33041` |
| Smart Tank 750 | [SmartTank750.13.6.5.exe](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/download/drivers/SmartTank750.13.6.5.exe) | 221 MB | `34f06f877777f562143a676846cf3b2a550652ca953d81677522e024fcb68ea4` |

## Epson

| Printer | File | Size | SHA-256 |
|---|---|---|---|
| L5190 | [Epson.L5190.exe](https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/releases/download/drivers/Epson.L5190.exe) | 68 MB | `185d315cc3aad390b23577da0a208646a5ad34294129f46c67d65eef7dae00f9` |

## Notes

- The version number in a file name is the version of the manufacturer's installer, as it was named when it was added here.
- A driver that is not listed for your printer can usually be found on the manufacturer's support site.
- The drivers are the property of their manufacturers (HP Inc., Seiko Epson) and are under their licences. HSA is an independent project and is not affiliated with them.
