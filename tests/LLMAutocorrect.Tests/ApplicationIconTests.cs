using System.Drawing;
using System.Windows.Media.Imaging;
using LLMAutocorrect.UI;

internal static class ApplicationIconTests
{
    internal static void IncludesHighDpiFrames()
    {
        using var stream = ApplicationIcon.OpenResource();
        using var reader = new BinaryReader(stream);
        Require(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1, "Expected an ICO directory.");
        var count = reader.ReadUInt16();
        var entries = new List<(int Size, uint Length, uint Offset)>();
        for (var index = 0; index < count; index++)
        {
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            Require(width == height, "Icon frames must be square.");
            reader.ReadByte(); reader.ReadByte();
            Require(reader.ReadUInt16() == 1 && reader.ReadUInt16() == 32, "Expected 32-bit alpha frames.");
            entries.Add((width == 0 ? 256 : width, reader.ReadUInt32(), reader.ReadUInt32()));
        }
        Require(entries.Select(entry => entry.Size).SequenceEqual(new[] { 16,20,24,32,40,48,64,96,128,256 }),
            "Missing a tray, desktop, or high DPI icon size.");
        foreach (var entry in entries)
        {
            stream.Position = entry.Offset;
            using var png = new MemoryStream(reader.ReadBytes((int)entry.Length));
            using var bitmap = new Bitmap(png);
            Require(bitmap.Width == entry.Size && bitmap.Height == entry.Size, "Frame size does not match its directory entry.");
            Require(bitmap.GetPixel(0, 0).A == 0, "Rounded icon corners must remain transparent.");
            Require(bitmap.GetPixel(entry.Size / 2, entry.Size / 2).A > 240, "The icon tile should be opaque.");
            // GDI+ may select the 128 frame for a request of 256. It is used
            // only for the small tray image; the WPF/shell high-resolution
            // frame is checked independently below and during publishing.
            if (entry.Size <= 128)
            {
                using var trayIcon = ApplicationIcon.CreateTrayIcon(new Size(entry.Size, entry.Size));
                using var retainedBitmap = trayIcon.ToBitmap();
                Require(retainedBitmap.Width == entry.Size, "Tray icon should select the requested size after closing its resource stream.");
            }
        }
    }

    internal static void WindowIconIsLoadedAndFrozen()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var image = ApplicationIcon.CreateWindowIcon();
                Require(image.IsFrozen && image.PixelWidth == 256 && image.PixelHeight == 256,
                    "Window icon must be a frozen high-resolution frame.");
                var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
                var converted = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                converted.CopyPixels(pixels, image.PixelWidth * 4, 0);
                Require(pixels[3] == 0, "The loaded window image must keep transparent corners after its stream closes.");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Window icon decoding timed out.");
        if (failure is not null) throw failure;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
