using System.Globalization;
using System.Text;
using PrintHub.Core.Discovery;

namespace PrintHub.Core.Printing;

public enum CanonAction { NozzleCheck, Clean, CleanDeep, CleanBlack, CleanBlackDeep, CleanColour, CleanColourDeep, AutoAlign, AlignmentCheck }

/// <summary>
/// Maintenance for Canon inkjets over their USB print interface: nozzle check, head cleaning and automatic head alignment. The command
/// strings and the way a job is wrapped follow Canon's own open-source Linux maintenance tool (cnijfilter / maintenance, GPL-2, Canon Inc.):
/// each command travels as a small print job made of "BJLSTART ... BJLEND" blocks.
/// </summary>
public static class CanonMaintenance
{
    public const int CanonVendor = 0x04A9;

    /// <summary>ESC [ K 02 00 00 1F: switches the printer into Canon's command (BJ) language for the block that follows.</summary>
    static readonly byte[] BjlPrefix = { 0x1B, (byte)'[', (byte)'K', 0x02, 0x00, 0x00, 0x1F };

    public static string CommandFor(CanonAction a) => a switch
    {
        CanonAction.NozzleCheck => "@TestPrint=NozzleCheck\n",
        CanonAction.Clean => "@Cleaning=1ALL\n",
        CanonAction.CleanDeep => "@Cleaning=2ALL\n",
        CanonAction.CleanBlack => "@Cleaning=1K\n",
        CanonAction.CleanBlackDeep => "@Cleaning=2K\n",
        CanonAction.CleanColour => "@Cleaning=1CMY\n",
        CanonAction.CleanColourDeep => "@Cleaning=2CMY\n",
        CanonAction.AutoAlign => "@TestPrint=Regi_Auto1\n",
        CanonAction.AlignmentCheck => "@TestPrint=RegiCheck\n",
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };

    public static string Title(CanonAction a) => a switch
    {
        CanonAction.NozzleCheck => "Nozzle check pattern",
        CanonAction.Clean => "Cleaning (all colours)",
        CanonAction.CleanDeep => "Deep cleaning (all colours)",
        CanonAction.CleanBlack => "Cleaning: black only",
        CanonAction.CleanBlackDeep => "Deep cleaning: black only",
        CanonAction.CleanColour => "Cleaning: colours only",
        CanonAction.CleanColourDeep => "Deep cleaning: colours only",
        CanonAction.AutoAlign => "Automatic head alignment",
        CanonAction.AlignmentCheck => "Head alignment check page",
        _ => a.ToString(),
    };

    /// <summary>How long the printer is busy with it, roughly. Canon's printers give no status over this channel, so the page waits this long before it lets you start another.</summary>
    public static TimeSpan TypicalDuration(CanonAction a) => a switch
    {
        CanonAction.NozzleCheck or CanonAction.AlignmentCheck => TimeSpan.FromSeconds(30),
        CanonAction.Clean or CanonAction.CleanBlack or CanonAction.CleanColour => TimeSpan.FromSeconds(60),
        CanonAction.CleanDeep or CanonAction.CleanBlackDeep or CanonAction.CleanColourDeep => TimeSpan.FromSeconds(120),
        CanonAction.AutoAlign => TimeSpan.FromSeconds(90),
        _ => TimeSpan.FromSeconds(60),
    };

    static byte[] Block(string content)
    {
        var b = new List<byte>();
        b.AddRange(BjlPrefix);
        b.AddRange(Encoding.ASCII.GetBytes("BJLSTART\n" + content + "BJLEND\n"));
        return b.ToArray();
    }

    /// <summary>
    /// The bytes sent for one action: a block that sets the printer's clock (ControlMode=Common, SetTime=YYYYMMDDHHMMSS), a block with the command,
    /// and a closing NUL, exactly as Canon's own tool assembles them.
    /// </summary>
    public static byte[] BuildJob(CanonAction action, DateTime now) =>
        Block("ControlMode=Common\nSetTime=" + now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "\n")
            .Concat(Block(CommandFor(action)))
            .Concat(new byte[] { 0x00 })
            .ToArray();

    public static async Task SendAsync(PrinterDevice dev, CanonAction action, CancellationToken ct = default)
    {
        using var fs = EpsonMaintenance.OpenUsbStream(dev, out var path, CanonVendor);
        Diag.Log($"Canon: {action} on '{dev.Name}' via {path}");
        var job = BuildJob(action, DateTime.Now);
        await fs.WriteAsync(job, ct).ConfigureAwait(false);
        await fs.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>True when this printer's USB print interface is plugged in right now.</summary>
    public static bool IsOnUsb(PrinterDevice dev) => EpsonMaintenance.FindPrintInterface(dev, CanonVendor) is not null;
}
