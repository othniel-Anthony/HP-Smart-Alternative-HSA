using System.Windows.Controls;

namespace HSA.Views;

public partial class FirmwareView : UserControl
{
    public FirmwareView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is ViewModels.FirmwareViewModel vm)
                await vm.LoadPrintersAsync();
        };
    }
}
