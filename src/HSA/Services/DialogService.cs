using System.Windows;
using Microsoft.Extensions.Logging;

namespace HSA.Services;

/// <summary>
/// WPF implementation of <see cref="IDialogService"/>. All dialogs marshal to the UI thread.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly ILogger<DialogService> _log;

    public DialogService(ILogger<DialogService> log) => _log = log;

    public void ShowInfo(string title, string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
            MessageBox.Show(Application.Current.MainWindow!, message, title,
                MessageBoxButton.OK, MessageBoxImage.Information));
    }

    public bool Confirm(string title, string message, string okText = "OK", string cancelText = "Cancel")
    {
        var result = MessageBoxResult.No;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            result = MessageBox.Show(Application.Current.MainWindow!, message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No);
        });
        return result == MessageBoxResult.Yes;
    }

    public bool ConfirmDestructive(string title, string message, string actionLabel)
    {
        var result = MessageBoxResult.Cancel;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            result = MessageBox.Show(Application.Current.MainWindow!, message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        });
        return result == MessageBoxResult.Yes;
    }

    public void ShowError(string title, Exception ex)
    {
        _log.LogError(ex, "Dialog error: {Title}", title);
        Application.Current?.Dispatcher.Invoke(() =>
            MessageBox.Show(Application.Current.MainWindow!, ex.ToString(), title,
                MessageBoxButton.OK, MessageBoxImage.Error));
    }

    public void ShowError(string title, string message)
    {
        _log.LogError("Dialog error: {Title}: {Message}", title, message);
        Application.Current?.Dispatcher.Invoke(() =>
            MessageBox.Show(Application.Current.MainWindow!, message, title,
                MessageBoxButton.OK, MessageBoxImage.Error));
    }
}
