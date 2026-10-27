using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace LLMAutocorrect.UI;

internal static class ApplicationIcon
{
    internal const string ResourceName = "LLMAutocorrect.Assets.Spellchecker.ico";

    internal static Stream OpenResource() => typeof(ApplicationIcon).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException("The embedded application icon is missing.");

    internal static Drawing.Icon CreateTrayIcon(Drawing.Size size)
    {
        using var stream = OpenResource();
        using var source = new Drawing.Icon(stream, size);
        // The shell retains this icon after the loading stream has been closed.
        return (Drawing.Icon)source.Clone();
    }

    internal static BitmapSource CreateWindowIcon()
    {
        using var stream = OpenResource();
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.MaxBy(image => image.PixelWidth)!;
        frame.Freeze();
        return frame;
    }
}
