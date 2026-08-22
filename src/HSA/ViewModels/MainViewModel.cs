using HSA.Services;
using HSA.Views;

namespace HSA.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public PrintersViewModel Printers { get; }
    public SuppliesViewModel Supplies { get; }
    public DriversViewModel Drivers { get; }
    public FirmwareViewModel Firmware { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(
        PrintersViewModel printers,
        SuppliesViewModel supplies,
        DriversViewModel drivers,
        FirmwareViewModel firmware,
        SettingsViewModel settings)
    {
        Printers = printers;
        Supplies = supplies;
        Drivers = drivers;
        Firmware = firmware;
        Settings = settings;
    }
}
