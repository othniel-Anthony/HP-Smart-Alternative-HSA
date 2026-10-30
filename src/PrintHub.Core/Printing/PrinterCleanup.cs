using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PrintHub.Core.Printing;

/// <summary>An installed printer driver as Windows lists it.</summary>
public sealed record InstalledDriver(string Name, string Manufacturer, string Environment);

/// <summary>What a clean-up would remove and what it would leave.</summary>
public sealed record CleanupPlan(List<string> Remove, List<string> Keep);

/// <summary>
/// Removing every printer, and every vendor printer driver, from Windows (the "start from nothing" a repair shop wants between customers), and the per-user leftovers
/// Windows keeps in the registry (HKCU\Printers: ConvertUserDevModesCount, DevModePerUser, DevModes2 hold one entry per printer name, and are never cleaned up).
/// Removing a print queue or a driver needs administrator rights, so those two steps run in an elevated PowerShell that Windows asks the user to allow;
/// planning and the registry clean-up run in HSA itself.
/// </summary>
public static class PrinterCleanup
{
    // ports of Windows' own virtual printers (Microsoft Print to PDF, XPS Document Writer, OneNote, Fax): not printers anyone installed
    static readonly string[] VirtualPorts = { "PORTPROMPT:", "NUL:", "XPSPort:", "SHRFAX:", "FILE:" };

    /// <summary>Printers that are removed (every queue on a real port: USB, network, Bluetooth, WSD) and those left alone (Windows' built-in virtual printers).</summary>
    public static CleanupPlan PlanPrinters(IEnumerable<SpoolerPrinter> printers)
    {
        var plan = new CleanupPlan(new(), new());
        foreach (var p in printers)
            (VirtualPorts.Any(v => p.Port.Equals(v, StringComparison.OrdinalIgnoreCase)) ? plan.Keep : plan.Remove).Add(p.Name);
        plan.Remove.Sort(StringComparer.OrdinalIgnoreCase); plan.Keep.Sort(StringComparer.OrdinalIgnoreCase);
        return plan;
    }

    /// <summary>
    /// Drivers that are removed: the ones a manufacturer supplied (HP, Epson, Canon, Brother...). Left alone: Microsoft's own, the "Generic" ones that come with Windows, and
    /// anything that is not for this PC's 64-bit Windows.
    /// </summary>
    public static CleanupPlan PlanDrivers(IEnumerable<InstalledDriver> drivers)
    {
        var plan = new CleanupPlan(new(), new());
        foreach (var d in drivers)
        {
            bool windows = d.Manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || d.Manufacturer.Equals("Generic", StringComparison.OrdinalIgnoreCase)
                || d.Name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) || d.Name.StartsWith("Generic ", StringComparison.OrdinalIgnoreCase)
                || d.Name.StartsWith("Send to Microsoft", StringComparison.OrdinalIgnoreCase) || d.Name.StartsWith("Universal Print", StringComparison.OrdinalIgnoreCase);
            bool x64 = d.Environment.Equals("Windows x64", StringComparison.OrdinalIgnoreCase);
            if (windows || !x64) { if (!plan.Keep.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) plan.Keep.Add(d.Name); }
            else if (!plan.Remove.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) plan.Remove.Add(d.Name);
        }
        plan.Remove.Sort(StringComparer.OrdinalIgnoreCase); plan.Keep.Sort(StringComparer.OrdinalIgnoreCase);
        return plan;
    }

    /// <summary>
    /// The exemptions to remember after the user confirmed a list. An item that was offered ticked and is unticked now becomes exempt; an exempt item that is ticked now stops being
    /// exempt; an item that was offered unticked for another reason (a driver in use) and an exempt item that is not in the list today (a printer that is not installed now)
    /// keep their state.
    /// </summary>
    public static List<string> UpdateExemptions(IEnumerable<string> before, IEnumerable<string> offeredTicked, IEnumerable<string> selected, IEnumerable<string> shown)
    {
        var exempt = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);
        var sel = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        var offered = new HashSet<string>(offeredTicked, StringComparer.OrdinalIgnoreCase);
        foreach (var name in shown)
        {
            if (sel.Contains(name)) exempt.Remove(name);
            else if (offered.Contains(name)) exempt.Add(name);
        }
        return exempt.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------------------------------------------------------- the elevated part

    static string Q(string s) => "'" + s.Replace("'", "''") + "'";      // a PowerShell single-quoted string: nothing inside is expanded

    /// <summary>
    /// A script that removes the named printers and reports "OK\tname" or "FAIL\tname\treason" per printer to <paramref name="resultFile"/>. It looks each printer up in the list
    /// Windows returns and removes that object: a name is never used as a pattern (printer names can contain [ ] which are wildcards).
    /// </summary>
    public static string BuildRemovePrintersScript(IEnumerable<string> names, string resultFile, bool previewDevicesOnly = false) =>
        "$ErrorActionPreference = 'Continue'\r\n" +
        $"$targets = @({string.Join(", ", names.Select(Q))})\r\n" +
        "$lines = New-Object System.Collections.Generic.List[string]\r\n" +
        $"$removedAny = ${(previewDevicesOnly ? "true" : "false")}\r\n" +
        $"$preview = ${(previewDevicesOnly ? "true" : "false")}\r\n" +
        "foreach ($p in @(Get-Printer)) {\r\n" +
        "  if (-not $preview -and $targets -contains $p.Name) {\r\n" +
        "    try { Remove-Printer -InputObject $p -ErrorAction Stop; $lines.Add(\"OK`t$($p.Name)\"); $removedAny = $true }\r\n" +
        "    catch { $lines.Add(\"FAIL`t$($p.Name)`t$($_.Exception.Message -replace '[\\r\\n]+',' ')\") }\r\n" +
        "  }\r\n" +
        "}\r\n" +
        DeviceCleanupScript +
        $"[System.IO.File]::WriteAllLines({Q(resultFile)}, $lines)\r\n";

    /// <summary>
    /// Part of the removal script, run only when a printer was removed. Removing a queue from the spooler leaves Windows' own device entries for it behind (the "print queue"
    /// devices under SWD\PRINTENUM, WSD printer entries, and the scanner entries a printer's driver registered: ROOT\SCANNER, ESCLUSB, WSD scan services), and Windows Settings
    /// builds its Printers and scanners list from them. Removed here: the entries of print queues that no longer exist (not present), and the scanner entries named like a
    /// removed printer (the name without " (USB)", " (NET)" or " (Copy n)"). Never touched: the root print queue, the entry of any printer that still exists in the
    /// spooler (an exempted printer that is switched off has an entry that is not "present"), and scanner entries named like a printer that was kept.
    /// </summary>
    const string DeviceCleanupScript = """
        if ($removedAny) {
          function Base([string]$n) { if (-not $n) { return '' }; (($n -replace '\s*\((USB|NET|WSD|Network|Copy \d+)\)\s*$','' -replace '\s*\(Copy \d+\)\s*$','').Trim()).ToLowerInvariant() }
          $stillThere = @(Get-Printer | ForEach-Object { $_.Name.ToLowerInvariant() })
          $removedBases = @($targets | ForEach-Object { Base $_ } | Where-Object { $_ })
          $keptBases = @(Get-Printer | ForEach-Object { Base $_.Name })
          foreach ($d in @(Get-PnpDevice -ErrorAction SilentlyContinue)) {
            $id = [string]$d.InstanceId
            if ($id -match 'PRINTQUEUES$') { continue }
            $name = [string]$d.FriendlyName
            $hit = $false
            if ($id -like 'SWD\PRINTENUM\*' -or $d.Class -in 'PrintQueue','Printer','WSDPrintDevice' -or $id -like 'SWD\WSDPRINT\*') {
              $hit = (-not $d.Present) -and ($stillThere -notcontains $name.ToLowerInvariant())
            }
            elseif ($d.Class -eq 'Image' -or $id -like 'ROOT\SCANNER\*' -or $id -like 'SWD\ESCLUSB\*' -or $id -like 'SWD\DAFWSDPROVIDER\*') {
              $b = Base $name
              if ($b) { $hit = ($removedBases -contains $b) -and ($keptBases -notcontains $b) -and -not ($id -like 'USB\*' -and $d.Present) }   # a USB device that is plugged in now is left alone
              elseif ($id -like 'ROOT\SCANNER\*') { $hit = $true }          # a nameless leftover of a network scanner entry
            }
            if ($hit -and $preview) { $lines.Add("DEVWOULD`t$name`t$id") }
            elseif ($hit) {
              $out = & pnputil.exe /remove-device $id 2>&1 | Out-String
              if ($LASTEXITCODE -eq 0) { $lines.Add("DEVOK`t$name`t$id") } else { $lines.Add("DEVFAIL`t$name ($id)`t$(($out -replace '[\r\n]+',' ').Trim())") }
            }
          }
        }

        """;

    /// <summary>The same for drivers: each is removed from Windows and from its driver store. A driver a printer still uses is refused by Windows and reported as FAIL.</summary>
    public static string BuildRemoveDriversScript(IEnumerable<string> names, string resultFile) =>
        "$ErrorActionPreference = 'Continue'\r\n" +
        $"$targets = @({string.Join(", ", names.Select(Q))})\r\n" +
        "$lines = New-Object System.Collections.Generic.List[string]\r\n" +
        "foreach ($d in @(Get-PrinterDriver | Where-Object { $_.PrinterEnvironment -eq 'Windows x64' })) {\r\n" +
        "  if ($targets -contains $d.Name) {\r\n" +
        "    try { Remove-PrinterDriver -Name $d.Name -PrinterEnvironment $d.PrinterEnvironment -RemoveFromDriverStore -ErrorAction Stop; $lines.Add(\"OK`t$($d.Name)\") }\r\n" +
        "    catch { $lines.Add(\"FAIL`t$($d.Name)`t$($_.Exception.Message -replace '[\\r\\n]+',' ')\") }\r\n" +
        "  }\r\n" +
        "}\r\n" +
        $"[System.IO.File]::WriteAllLines({Q(resultFile)}, $lines)\r\n";

    public sealed record Outcome(List<string> Done, List<(string Name, string Reason)> Failed)
    {
        /// <summary>Leftover Windows device entries (print queues, scanners) that were removed so Windows Settings no longer lists the printers.</summary>
        public List<string> Devices { get; } = new();
        public List<(string Name, string Reason)> DeviceFailed { get; } = new();
    }

    public static Outcome ParseResult(IEnumerable<string> lines)
    {
        var o = new Outcome(new(), new());
        foreach (var l in lines)
        {
            var f = l.Split('\t');
            if (f.Length >= 2 && f[0] == "OK") o.Done.Add(f[1]);
            else if (f.Length >= 2 && f[0] == "FAIL") o.Failed.Add((f[1], f.Length > 2 ? f[2] : ""));
            else if (f.Length >= 2 && f[0] == "DEVOK") o.Devices.Add(f[1]);
            else if (f.Length >= 2 && f[0] == "DEVWOULD") o.Devices.Add(f[1] + " [" + (f.Length > 2 ? f[2] : "") + "]");
            else if (f.Length >= 2 && f[0] == "DEVFAIL") o.DeviceFailed.Add((f[1], f.Length > 2 ? f[2] : ""));
        }
        return o;
    }

    /// <summary>
    /// Runs a script as administrator (Windows shows its permission prompt) and returns what the script reported. Throws <see cref="OperationCanceledException"/> when the
    /// user declines the prompt.
    /// </summary>
    public static async Task<Outcome> RunElevatedAsync(Func<string, string> buildScript, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "hsa-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string result = Path.Combine(dir, "result.txt"), script = Path.Combine(dir, "run.ps1");
        File.WriteAllText(script, buildScript(result), new UTF8Encoding(true));      // with a BOM: Windows PowerShell 5 reads it as UTF-8
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            Process? p;
            try { p = Process.Start(psi); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Administrator permission was not given."); }   // the user said No
            if (p is null) throw new InvalidOperationException("Windows did not start the administrator PowerShell.");
            using (p) await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (!File.Exists(result)) throw new InvalidOperationException("The administrator step ended without a result. Nothing may have been removed: check the list of printers or drivers and try again.");
            return ParseResult(File.ReadAllLines(result));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- what is installed

    /// <summary>Printer drivers Windows lists (no administrator rights needed to read them).</summary>
    public static async Task<List<InstalledDriver>> ListDriversAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; ConvertTo-Json -Compress -InputObject @(Get-PrinterDriver | Select-Object Name,Manufacturer,PrinterEnvironment)\"")
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell to list the drivers.");
        string json = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return ParseDrivers(json);
    }

    public static List<InstalledDriver> ParseDrivers(string json)
    {
        var list = new List<InstalledDriver>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
            foreach (var e in items)
            {
                string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                if (S("Name").Length > 0) list.Add(new InstalledDriver(S("Name"), S("Manufacturer"), S("PrinterEnvironment")));
            }
        }
        catch (JsonException) { }
        return list;
    }

    // ---------------------------------------------------------------- the registry leftovers

    /// <summary>
    /// Saves <paramref name="registryKey"/> (HKCU\Printers) to a .reg file in <paramref name="folder"/> before it is cleaned, so it can be put back by double-clicking the file.
    /// Returns the file's path, or null when the key does not exist or could not be saved.
    /// </summary>
    public static string? BackupRegistry(string folder, string registryKey = @"HKCU\Printers")
    {
        try
        {
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"printers-registry-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
            var psi = new ProcessStartInfo("reg.exe", $"export \"{registryKey}\" \"{file}\" /y") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(15000);
            return p.ExitCode == 0 && File.Exists(file) ? file : null;
        }
        catch (Exception ex) { Diag.Log("Printer clean-up: could not back up the registry: " + ex.Message); return null; }
    }

    /// <summary>The per-printer lists under HKCU\Printers that gather an entry for every printer name and keep it after the printer is gone.</summary>
    public static readonly string[] PerPrinterLists = { "ConvertUserDevModesCount", "DevModePerUser", "DevModes2" };

    /// <summary>
    /// Deletes the entries in those lists for printers that are not in <paramref name="keepPrinters"/> (the printers that still exist), including leftovers with cut-off names
    /// and the placeholder "NULL". Entries of printers that still exist, other keys and the lists' own default values are not touched. Returns the number of entries deleted.
    /// <paramref name="printersPath"/> is HKCU\Printers (a different key can be given for a test).
    /// </summary>
    public static int CleanRegistry(IEnumerable<string> keepPrinters, RegistryKey? hive = null, string printersPath = "Printers")
    {
        var keep = new HashSet<string>(keepPrinters, StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        foreach (var list in PerPrinterLists)
        {
            using var key = (hive ?? Registry.CurrentUser).OpenSubKey($"{printersPath}\\{list}", writable: true);
            if (key is null) continue;
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0 || keep.Contains(name)) continue;
                try { key.DeleteValue(name, throwOnMissingValue: false); removed++; }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { Diag.Log($"Printer clean-up: could not delete {list}\\{name}: {ex.Message}"); }
            }
        }
        return removed;
    }
}
