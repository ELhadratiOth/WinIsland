using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinIsland.Core.Media;

namespace WinIsland.Platform.Windows.Media;

/// <summary>Turns a media session thumbnail into a small square PNG plus an accent colour.</summary>
public static class ArtworkLoader
{
    private const uint Size = 160;

    public static async Task<MediaArtwork?> LoadAsync(IRandomAccessStreamReference? thumbnail, CancellationToken cancellationToken = default)
    {
        if (thumbnail is null)
        {
            return null;
        }

        using IRandomAccessStreamWithContentType source = await thumbnail.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(source).AsTask(cancellationToken).ConfigureAwait(false);

        // Aspect-fill into a square: scale the short side to Size, then centre-crop.
        double scale = (double)Size / Math.Min(decoder.PixelWidth, decoder.PixelHeight);
        uint scaledWidth = Math.Max(Size, (uint)Math.Round(decoder.PixelWidth * scale));
        uint scaledHeight = Math.Max(Size, (uint)Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = scaledWidth,
            ScaledHeight = scaledHeight,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = (scaledWidth - Size) / 2, Y = (scaledHeight - Size) / 2, Width = Size, Height = Size },
        };

        PixelDataProvider pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
        byte[] bgra = pixels.DetachPixelData();

        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(cancellationToken).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, Size, Size, 96, 96, bgra);
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

        byte[] png = new byte[output.Size];
        using (var reader = new DataReader(output.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)output.Size).AsTask(cancellationToken).ConfigureAwait(false);
            reader.ReadBytes(png);
        }

        return new MediaArtwork(png, AccentPicker.Pick(bgra));
    }
}
