using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HSA.ViewModels;

/// <summary>
/// Minimal INotifyPropertyChanged base. CommunityToolkit.Mvvm provides
/// [ObservableProperty] source generators but we keep the surface hand-written here
/// for clarity and to avoid extra build tooling surface.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
