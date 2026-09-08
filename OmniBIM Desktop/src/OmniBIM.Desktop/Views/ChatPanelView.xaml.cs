using System.Windows.Controls;
using System.Windows.Input;
using OmniBIM.Desktop.ViewModels;

namespace OmniBIM.Desktop.Views;

public partial class ChatPanelView : UserControl
{
    public ChatPanelView()
    {
        InitializeComponent();
    }

    private void DraftBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not ChatViewModel vm) return;
        if (vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
    }
}
