using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HSA.Helpers;

internal static class WindowHelper
{
    public static IntPtr GetMainWindowHandle()
    {
        var w = Application.Current?.MainWindow;
        if (w is null) return IntPtr.Zero;
        return new WindowInteropHelper(w).Handle;
    }
}
