namespace HSA.Services;

public interface IDialogService
{
    void ShowInfo(string title, string message);
    bool Confirm(string title, string message, string okText = "OK", string cancelText = "Cancel");
    bool ConfirmDestructive(string title, string message, string actionLabel);
    void ShowError(string title, Exception ex);
    void ShowError(string title, string message);
}
