using HSA.Services;
using HSA.Views;

namespace HSA.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public PrintersViewModel Printers { get; }
    public DriversViewModel Drivers { get; }
    public FirmwareViewModel Firmware { get; }

    public MainViewModel(PrintersViewModel printers, DriversViewModel drivers, FirmwareViewModel firmware)
    {
        Printers = printers;
        Drivers = drivers;
        Firmware = firmware;
    }
}
