using System.Windows.Controls;

namespace HSA.Views;

public partial class PrintersView : UserControl
{
    public PrintersView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is ViewModels.PrintersViewModel vm)
                await vm.RefreshAsync();
        };
    }
}
