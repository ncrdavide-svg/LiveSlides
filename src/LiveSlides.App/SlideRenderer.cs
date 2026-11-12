using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace LiveSlides.App;

/// <summary>
/// Turns PDF pages and image files into frozen bitmaps sized for the output screen, using the
/// PDF renderer built into Windows (Windows.Data.Pdf). Everything runs off the UI thread and the
/// results are frozen, so they can be handed straight to WPF Image controls.
/// </summary>
public static class SlideRenderer
{
    // One render at a time: PdfDocument is not documented as safe for concurrent page renders,
    // and serializing also keeps a burst of thumbnail work from starving the on-air slide.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static Task<PdfDocument> OpenPdfAsync(string path) => Task.Run(async () =>
    {
        // Loaded from memory, not from the file: the file stays unlocked, so it can be replaced
        // or deleted while the show is running without affecting what is already loaded.
        var bytes = await File.ReadAllBytesAsync(path);
        var stream = new MemoryStream(bytes).AsRandomAccessStream();
        return await PdfDocument.LoadFromStreamAsync(stream);
    });

    public static async Task<BitmapSource> RenderPdfPageAsync(PdfDocument document, int pageIndex, int maxWidth, int maxHeight)
    {
        await Gate.WaitAsync();
        try
        {
            return await Task.Run(async () =>
            {
                using var page = document.GetPage((uint)pageIndex);
                var size = page.Size;
                var scale = Math.Min(maxWidth / size.Width, maxHeight / size.Height);
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * scale)),
                    DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * scale)),
                    // BMP instead of the default PNG: no compression work on either side, which
                    // matters for a 4K page that must be ready before the operator presses next.
                    BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId
                };
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, options);
                stream.Seek(0);

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream.AsStream();
                bitmap.EndInit();
                bitmap.Freeze();
                return (BitmapSource)bitmap;
            });
        }
        finally
        {
            Gate.Release();
        }
    }

    public static Task<BitmapSource> LoadImageAsync(string path, int maxWidth, int maxHeight) => Task.Run(() =>
    {
        // Read once to learn the real size, then decode no larger than needed.
        var bytes = File.ReadAllBytes(path);
        int width, height;
        using (var probe = new MemoryStream(bytes))
        {
            var frame = BitmapFrame.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            width = frame.PixelWidth;
            height = frame.PixelHeight;
        }
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(bytes);
        if (scale < 1.0) bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Round(width * scale));
        bitmap.EndInit();
        bitmap.Freeze();
        return (BitmapSource)bitmap;
    });
}
