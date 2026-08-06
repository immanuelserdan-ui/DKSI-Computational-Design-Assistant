using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Loads ribbon icons from embedded PNG resources.
///
/// Revit expects 32x32 for <c>LargeImage</c> and 16x16 for <c>Image</c>. Supplying the
/// wrong size does not fail, it just looks blurry. A null image is legal - the button
/// renders as text only - so a missing icon never breaks the ribbon.
/// </summary>
internal static class Icons
{
    private static readonly Assembly Asm = typeof(Icons).Assembly;
    private static readonly string Prefix = $"{typeof(Icons).Namespace!.Replace(".Infrastructure", string.Empty)}.Resources.Icons.";

    /// <summary>Returns the named PNG (without extension), or null if it is not embedded.</summary>
    public static ImageSource? Load(string name)
    {
        try
        {
            using var stream = Asm.GetManifestResourceStream(Prefix + name + ".png");
            if (stream is null) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;   // read now so the stream can close
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();                                  // safe to hand to the UI thread
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn($"Icon '{name}' could not be loaded: {ex.Message}");
            return null;
        }
    }
}
