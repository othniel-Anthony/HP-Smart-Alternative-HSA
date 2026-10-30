namespace PrintHub.Core;

/// <summary>Web addresses the app sends people to.</summary>
public static class AppLinks
{
    /// <summary>
    /// Where printer drivers are kept: the drivers folder of the project's repository (a list with checksums; the installers are attached to the release named "drivers").
    /// It is apart from the program itself: the program's releases and source do not contain the drivers, so they can be added, replaced and removed without touching the software.
    /// </summary>
    public const string DriversUrl = "https://github.com/othniel-Anthony/HP-Smart-Alternative-HSA/tree/main/drivers";
}
