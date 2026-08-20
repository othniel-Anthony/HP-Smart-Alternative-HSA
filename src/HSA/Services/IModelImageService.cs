using HSA.Models;

namespace HSA.Services;

public interface IModelImageService
{
    /// <summary>
    /// Returns a pack URI for the icon that best matches the printer, or null if no
    /// image can be resolved. Resolution order:
    ///   1. Exact normalized model name in <c>Resources/printers/&lt;name&gt;.png</c>
    ///   2. First family-keyword match in the catalog
    ///   3. Generic HP icon
    /// </summary>
    Uri? GetImageUri(PrinterInfo printer);

    /// <summary>Infers the printer family (e.g., "LaserJet (color)") for diagnostic display.</summary>
    string? GetFamily(PrinterInfo printer);
}
