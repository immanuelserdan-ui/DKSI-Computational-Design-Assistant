using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OmniBIM.Desktop.ViewModels;

/// <summary>
/// Minimal INotifyPropertyChanged base. Written by hand rather than pulling in
/// CommunityToolkit.Mvvm so this Phase 1 scaffold has zero NuGet dependencies and builds
/// offline; swap in the toolkit once the project has network-restore in its build pipeline.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
