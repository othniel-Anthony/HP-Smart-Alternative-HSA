using System.Windows.Controls;

namespace HSA.Views;

public partial class SuppliesView : UserControl
{
    public SuppliesView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is ViewModels.SuppliesViewModel vm)
                await vm.LoadPrintersAsync();
        };
    }
}
