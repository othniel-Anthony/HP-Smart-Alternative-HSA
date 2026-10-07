using System.Text;
using PrintHub.Core.Ipp;

namespace PrintHub.Core.Printing;

/// <summary>What an Epson printer says about itself in its "@BDC ST2" status block: its state, an error if it has one, and the ink left in each cartridge.</summary>
public sealed record EpsonStatusReading(int? StateCode, int? ErrorCode, List<int> WarningCodes, List<EpsonInkLevel> Ink, string? Serial, byte[] Raw)
{
    /// <summary>The state the printer reports (element 0x01). Codes seen on a real L3250 and the ones Epson documents for its ESC/P-R family.</summary>
    public string State => StateCode switch
    {
        0x00 => "Error",
        0x01 => "Self printing",
        0x02 => "Busy",
        0x03 => "Waiting",
        0x04 => "Ready",
        0x05 => "Paused",
        0x07 => "Cleaning",
        0x08 => "Not initialised",
        0x0A => "Shutting down",
        0x0F => "Nozzle check",
        0x11 => "Charging",
        null => "Unknown",
        _ => $"State {StateCode:X2}",
    };

    public string? Error => ErrorCode switch
    {
        null => null,
        0x00 => "Fatal error",
        0x01 => "Another interface is selected",
        0x02 => "Cover open",
        0x04 => "Paper jam",
        0x05 => "Ink out",
        0x06 => "Paper out",
        0x0C => "Paper size, type or path error",
        0x10 => "Waste ink counter overflow",
        0x12 => "Double feed",
        0x2C => "Ink cartridge overflow",
        0x41 => "Maintenance request",
        0x4A => "Maintenance box near end",
        _ => $"Printer error {ErrorCode:X2}",
    };

    public bool IsReady => StateCode is 0x03 or 0x04;

    /// <summary>The first complete status block in a reply, or null when it is not one (or is cut short).</summary>
    public static EpsonStatusReading? Parse(byte[]? reply)
    {
        if (reply is null) return null;
        int at = IndexOf(reply, Encoding.ASCII.GetBytes("BDC ST2\r\n"));
        if (at < 0) return null;
        int p = at + "BDC ST2\r\n".Length;
        if (p + 2 > reply.Length) return null;
        int declared = reply[p] | (reply[p + 1] << 8);
        p += 2;
        if (reply.Length - p < declared) return null;   // cut short: asking again is better than trusting half a block
        int end = p + declared;

        int? state = null, error = null; string? serial = null;
        var warnings = new List<int>(); var ink = new List<EpsonInkLevel>();
        while (p + 2 <= end)
        {
            int type = reply[p], len = reply[p + 1]; p += 2;
            if (p + len > end) return null;
            var item = reply.AsSpan(p, len);
            switch (type)
            {
                case 0x01 when len >= 1: state = item[0]; break;
                case 0x02 when len >= 1: error = item[0]; break;
                case 0x04: foreach (var w in item) warnings.Add(w); break;
                case 0x0F when len >= 1:   // ink: the first byte is the size of each entry; an entry is the cartridge id, the colour, and the percentage left
                {
                    int size = item[0];
                    if (size >= 3)
                        for (int o = 1; o + size <= len; o += size) ink.Add(new EpsonInkLevel(item[o], item[o + 1], item[o + 2]));
                    break;
                }
                case 0x1F when len > 0: serial = Encoding.ASCII.GetString(item).Trim('\0', ' '); break;
                case 0x40 when len > 0: serial ??= Encoding.ASCII.GetString(item).Trim('\0', ' ', '?'); break;
            }
            p += len;
        }
        return new EpsonStatusReading(state, error, warnings, ink, serial, reply);
    }

    static int IndexOf(byte[] data, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= data.Length; i++)
        {
            int k = 0; while (k < pattern.Length && data[i + k] == pattern[k]) k++;
            if (k == pattern.Length) return i;
        }
        return -1;
    }

    /// <summary>The status as the Home page shows it (the same shape an IPP printer gives).</summary>
    public PrinterStatus ToPrinterStatus(string fallbackName)
    {
        var st = new PrinterStatus
        {
            MakeAndModel = fallbackName,
            SerialNumber = Serial ?? "",
            State = Error is not null || StateCode == 0x00 ? PrinterState.Stopped : IsReady || StateCode is 0x05 or null ? PrinterState.Idle : PrinterState.Processing,
        };
        if (Error is not null) st.StateReasons.Add(System.Text.RegularExpressions.Regex.Replace(Error.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') + "-error");
        // A printer without ink sensors (the EcoTank L3250 sends 105 for every tank, and Epson says to check those tanks by eye) has no level to show: nothing is made up.
        foreach (var i in Ink.Where(i => i.HasLevel)) st.Supplies.Add(i.ToSupplyLevel());
        return st;
    }
}

/// <summary>One cartridge: the id the printer gives it, its colour code and the percentage left.</summary>
public sealed record EpsonInkLevel(int CartridgeId, int ColourCode, int Percent)
{
    /// <summary>Colour codes of the ink element; the four of an L3250 were read from a real printer.</summary>
    public string Name => ColourCode switch
    {
        0x00 => "Black",
        0x01 => "Cyan",
        0x02 => "Magenta",
        0x03 => "Yellow",
        0x04 => "Light cyan",
        0x05 => "Light magenta",
        _ => $"Ink {ColourCode:X2}",
    };

    /// <summary>0 to 100 is a real percentage; anything above it is the printer saying it does not measure the tank.</summary>
    public bool HasLevel => Percent is >= 0 and <= 100;

    string Hex => ColourCode switch { 0x00 => "#202020", 0x01 or 0x04 => "#00AEEF", 0x02 or 0x05 => "#EC008C", 0x03 => "#FFD400", _ => "#808080" };

    public SupplyLevel ToSupplyLevel() => new(Name, "ink", Hex, Math.Clamp(Percent, 0, 100), 10, 100);
}
