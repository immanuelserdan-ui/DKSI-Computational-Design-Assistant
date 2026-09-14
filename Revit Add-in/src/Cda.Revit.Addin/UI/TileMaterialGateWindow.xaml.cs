using System.Windows;

namespace Cda.Revit.Addin.UI;

/// <summary>
/// Two Yes/No questions asked before "Painted Surface Area" runs. Answering Yes to either
/// puts that room type through <see cref="Finishes.RoomMaterialCodeGate"/> before the takeoff
/// is allowed to execute - see PaintedSurfaceAreaGateCommand.
/// </summary>
public partial class TileMaterialGateWindow : Window
{
    public TileMaterialGateWindow()
    {
        InitializeComponent();
    }

    public bool AlrumKokkenHasTile { get; private set; }

    public bool BadToiletHasTile { get; private set; }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        AlrumKokkenHasTile = AlrumKokkenBox.IsChecked == true;
        BadToiletHasTile = BadToiletBox.IsChecked == true;
        DialogResult = true;
    }
}
