using System.Windows;
using System.Windows.Controls;

namespace HSA.Views;

public partial class SuppliesView : UserControl
{
    public SuppliesView()
    {
        InitializeComponent();
        // First-time load: populate the printer list (consumables are queried on Refresh).
        Loaded += async (_, _) =>
        {
            if (DataContext is ViewModels.SuppliesViewModel vm)
                await vm.LoadPrintersAsync();
        };
        // v0.2.2: auto-refresh whenever the tab becomes visible. The user expects
        // the Supplies tab to show current data when they switch to it, without
        // having to click Refresh.
        IsVisibleChanged += async (_, e) =>
        {
            if (!((bool)e.NewValue)) return;   // ignore IsVisible=False transitions
            if (DataContext is not ViewModels.SuppliesViewModel vm) return;
            // Make sure the printer list is fresh too — covers the case where a
            // printer was added/removed since the last visit.
            await vm.LoadPrintersAsync();
            // Then query the consumables.
            _ = vm.RefreshAsync();
        };
    }
}
