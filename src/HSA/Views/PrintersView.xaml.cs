using System.Windows.Controls;

namespace HSA.Views;

public partial class PrintersView : UserControl
{
    private bool _initialDiscoverDone;

    public PrintersView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is ViewModels.PrintersViewModel vm)
            {
                await vm.RefreshAsync();
                // v0.2.7: on first open, auto-run "Discover network" so the
                // Discovered list is always fresh on launch. The user no
                // longer has to click the button manually to see what's
                // out there. Subsequent tab visits don't re-run it.
                if (!_initialDiscoverDone)
                {
                    _initialDiscoverDone = true;
                    if (vm.DiscoverNetworkPrintersCommand.CanExecute(null))
                        await vm.DiscoverNetworkPrintersCommand.ExecuteAsync(null);
                }
            }
        };
    }
}
