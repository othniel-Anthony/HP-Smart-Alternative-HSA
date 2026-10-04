# Code signing: getting rid of the "are you sure?" prompt

When someone downloads `HSA.exe` and runs it, Windows asks *"The publisher could not be verified. Are you sure you want to run this software?"* (or shows the blue *Windows protected your PC* box). That is Windows telling you the file has no digital signature from a publisher it trusts. It doesn't mean anything is wrong with the app.

The only real fix is to **sign the exe with a code-signing certificate**. This guide covers what that involves and how the build script uses one. (Details below were checked in October 2026; prices and eligibility change, so confirm before paying for anything.)

## What signing does and doesn't do

- A signed exe shows your (or your certificate authority's) name as the *verified publisher* instead of "Unknown publisher".
- It does **not** instantly stop SmartScreen. SmartScreen also looks at how many people have run the file. A new signed app can still get a reputation warning for the first few weeks; the signature is what lets that reputation build up and stay attached across releases.
- A self-signed certificate (one you make yourself) changes nothing for other people. Only certificates that chain to a root Windows already trusts count.

## Options

| Route | Cost | Good for | Catch |
|---|---|---|---|
| **[SignPath Foundation](https://signpath.org/terms.html)** | Free | Open-source projects like this one (MIT, public repo) | You apply and they review it by hand; they want some existing reputation, releases built by CI (GitHub Actions), and the signature names "SignPath Foundation" as publisher |
| **A certificate from a certificate authority** (Sectigo, DigiCert, SSL.com, Certum and others) | Roughly $100 to $400 a year | Anyone, in any country | Identity checks; the private key must live on a hardware token or cloud service by industry rule |
| **[Microsoft Artifact Signing](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)** (formerly Trusted Signing) | About $10 a month | Individuals in the US and Canada, and organisations in a list of countries | Not available to individuals elsewhere |
| **[Microsoft Store](https://learn.microsoft.com/en-us/windows/apps/publish/whats-new-individual-developer)** | Free for individuals | Reaching people with no warning at all (Microsoft signs it) | The app must be packaged as MSIX and pass Store review, which is a project of its own |

If you're open source and not in the US or Canada, **SignPath Foundation** is the best first thing to try. The Store is the cleanest long-term result.

## Signing a build once you have a certificate

The build script can sign the exe for you:

```powershell
# certificate in a .pfx file
.\build.ps1 -PfxPath C:\certs\hsa.pfx -PfxPassword "your password"

# certificate already installed in Windows (or on a token): use its thumbprint
.\build.ps1 -CertThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

It signs `HSA.exe` with SHA-256, adds a timestamp from DigiCert's free timestamp server (so the signature stays valid after the certificate expires), checks the result, and then makes the zip from the signed file. If you leave both options off, the build is unsigned as before.

Check any exe afterwards with:

```powershell
Get-AuthenticodeSignature .\HSA-0.10.4-win-x64.exe | Format-List Status, SignerCertificate
```

`Status : Valid` is what you want.

## Until then: for people who download the exe

- **Easiest:** use the installer in the zip (`Install-HSA.ps1`). It clears the "downloaded from the internet" mark on the installed copy, so the Start menu shortcut starts the app straight away.
- **For the standalone exe:** right-click the file, choose *Properties*, tick **Unblock** at the bottom, and press OK. Or in PowerShell: `Unblock-File .\HSA-0.10.4-win-x64.exe`.
- Only do this for a file you downloaded from this project's Releases page. Compare its SHA-256 with the one in the release notes if you want to be sure.
