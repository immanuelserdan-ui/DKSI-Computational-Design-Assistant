using System.Windows.Input;
using OmniBIM.Desktop.Models;

namespace OmniBIM.Desktop.ViewModels;

/// <summary>One row in the sidebar. IsSelected is set by ShellViewModel, not by the item itself, so exactly one is ever true.</summary>
public sealed class NavItemViewModel : ObservableObject
{
    public string Label { get; }
    public AppPage Page { get; }
    public ICommand SelectCommand { get; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    public NavItemViewModel(string label, AppPage page, Action<AppPage> onSelect)
    {
        Label = label;
        Page = page;
        SelectCommand = new RelayCommand(_ => onSelect(page));
    }
}
