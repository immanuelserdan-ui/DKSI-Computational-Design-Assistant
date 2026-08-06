using System.Windows;

namespace Cda.Revit.Addin.UI;

public partial class DiagnoseParamsWindow : Window
{
    public DiagnoseParamsWindow(string material, string type, string? selectionHint)
    {
        InitializeComponent();

        MaterialBox.Text = material;
        TypeBox.Text = type;

        if (!string.IsNullOrWhiteSpace(selectionHint))
        {
            SelectionHint.Text = selectionHint;
            SelectionHint.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => MaterialBox.Focus();
    }

    public string MaterialName => MaterialBox.Text.Trim();

    public string TypeName => TypeBox.Text.Trim();

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (MaterialName.Length == 0 && TypeName.Length == 0)
        {
            MessageBox.Show(this,
                "Enter a material name, a type name, or both.",
                "Diagnose Parameters",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
