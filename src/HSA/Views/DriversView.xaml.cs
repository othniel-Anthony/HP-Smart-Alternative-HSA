using System.Windows.Controls;

namespace HSA.Views;

public partial class DriversView : UserControl
{
    public DriversView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is ViewModels.DriversViewModel vm)
                await vm.RefreshAsync();
        };
    }
}
