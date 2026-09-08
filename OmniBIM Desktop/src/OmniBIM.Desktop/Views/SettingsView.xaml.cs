using System.Windows.Controls;
using OmniBIM.Desktop.ViewModels;

namespace OmniBIM.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void ApiKeyBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.SetApiKey(ApiKeyBox.Password);
    }
}
