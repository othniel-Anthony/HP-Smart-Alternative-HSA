using HSA.Models;

namespace HSA.Services;

public interface IConsumableService
{
    /// <summary>
    /// Reads ink/toner levels from the printer's prtMarkerSuppliesTable via SNMP.
    /// Returns an empty list if the printer is not reachable, has no network address,
    /// or doesn't expose the table.
    /// </summary>
    Task<IReadOnlyList<ConsumableStatus>> GetConsumablesAsync(
        PrinterInfo printer, CancellationToken ct = default);

    /// <summary>
    /// Reads consumables for every reachable network printer in the list and returns
    /// a flat list with PrinterName populated.
    /// </summary>
    Task<IReadOnlyList<ConsumableStatus>> GetAllConsumablesAsync(
        IEnumerable<PrinterInfo> printers, IProgress<(int Done, int Total, string Current)>? progress = null,
        CancellationToken ct = default);
}
