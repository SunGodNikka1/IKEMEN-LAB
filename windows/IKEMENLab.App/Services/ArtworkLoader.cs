using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Sprites;

namespace IKEMENLab.App.Services;

/// <summary>
/// UI-side bridge: runs Core artwork decoding off the UI thread (bounded parallelism), turns the cached
/// PNG bytes into frozen BitmapImages and memoises small thumbnails. Core never sees WPF types.
/// </summary>
public sealed class ArtworkLoader
{
    public const int ThumbnailDecodeHeight = 96;
    public const int StageThumbnailDecodeWidth = 360;

    private readonly SemaphoreSlim _gate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private ArtworkService? _service;
    private int _generation;

    public void SetRoot(string? root)
    {
        Interlocked.Increment(ref _generation);
        _thumbnails.Clear();
        try
        {
            _service = string.IsNullOrWhiteSpace(root) ? null : new ArtworkService(root, new ThumbnailCache(ThumbnailCache.DefaultDirectory));
        }
        catch (InvalidOperationException)
        {
            _service = null; // cache would land inside the installation; refuse rather than write there
        }
    }

    public Task<ImageSource?> CharacterThumbnailAsync(CharacterEntry character)
        => _thumbnails.GetOrAdd("c:" + character.DefPath,
            _ => LoadAsync(s => s.CharacterPortraitPng(character), decodeHeight: ThumbnailDecodeHeight));

    public Task<ImageSource?> CharacterPortraitAsync(CharacterEntry character)
        => LoadAsync(s => s.CharacterPortraitPng(character));

    public Task<ImageSource?> StageThumbnailAsync(StageEntry stage)
        => _thumbnails.GetOrAdd("s:" + stage.DefPath,
            _ => LoadAsync(s => s.StagePreviewPng(stage), decodeWidth: StageThumbnailDecodeWidth));

    public Task<ImageSource?> StagePreviewAsync(StageEntry stage)
        => LoadAsync(s => s.StagePreviewPng(stage));

    private async Task<ImageSource?> LoadAsync(Func<ArtworkService, byte[]?> produce, int decodeWidth = 0, int decodeHeight = 0)
    {
        var service = _service;
        if (service is null) return null;
        var generation = _generation;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != _generation) return null;
            return await Task.Run(() =>
            {
                var png = produce(service);
                return png is null ? null : ToImage(png, decodeWidth, decodeHeight);
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null; // unreadable artwork shows the neutral placeholder
        }
        finally
        {
            _gate.Release();
        }
    }

    public static ImageSource? ToImage(byte[] png, int decodeWidth = 0, int decodeHeight = 0)
    {
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        else if (decodeHeight > 0) image.DecodePixelHeight = decodeHeight;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
