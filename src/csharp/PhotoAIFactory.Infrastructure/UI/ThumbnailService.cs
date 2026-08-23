using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PhotoAIFactory.Application.UI;

namespace PhotoAIFactory.Infrastructure.UI;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ThumbnailService : IThumbnailService
{
    private sealed class CacheEntry(byte[] bytes, int width, int height)
    {
        public byte[] Bytes { get; } = bytes;
        public long SizeBytes => Bytes.Length;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public DateTime LastAccessedUtc { get; set; } = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object evictionLock = new();
    private long currentMemoryBytes;

    // UI Thumbnail cache budget: 128 MB and max 500 items
    public long MaxMemoryBytes { get; }
    public int MaxItemCount { get; }

    public ThumbnailService(long maxMemoryBytes = 128 * 1024 * 1024, int maxItemCount = 500)
    {
        MaxMemoryBytes = Math.Max(1024 * 1024, maxMemoryBytes);
        MaxItemCount = Math.Max(10, maxItemCount);
    }

    public long CurrentMemoryUsageBytes => Interlocked.Read(ref currentMemoryBytes);
    public int CachedItemCount => cache.Count;

    public async Task<byte[]?> GetThumbnailBytesAsync(
        string imagePath,
        int maxWidth = 256,
        int maxHeight = 256,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return null;

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return null;

        var key = $"{imagePath}:{maxWidth}x{maxHeight}:{File.GetLastWriteTimeUtc(imagePath).Ticks}";

        if (cache.TryGetValue(key, out var entry))
        {
            entry.LastAccessedUtc = DateTime.UtcNow;
            return entry.Bytes;
        }

        try
        {
            var thumbnailBytes = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Open with Read / ReadWrite share so file is not locked
                using var fileStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var originalImage = Image.FromStream(fileStream, useEmbeddedColorManagement: false, validateImageData: false);

                cancellationToken.ThrowIfCancellationRequested();

                int srcW = originalImage.Width;
                int srcH = originalImage.Height;

                int targetW = srcW;
                int targetH = srcH;

                if (targetW > maxWidth || targetH > maxHeight)
                {
                    double ratioX = (double)maxWidth / srcW;
                    double ratioY = (double)maxHeight / srcH;
                    double ratio = Math.Min(ratioX, ratioY);
                    targetW = Math.Max(1, (int)Math.Round(srcW * ratio));
                    targetH = Math.Max(1, (int)Math.Round(srcH * ratio));
                }

                cancellationToken.ThrowIfCancellationRequested();

                using var thumbnailBitmap = new Bitmap(targetW, targetH, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(thumbnailBitmap))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(originalImage, 0, 0, targetW, targetH);
                }

                cancellationToken.ThrowIfCancellationRequested();

                using var ms = new MemoryStream();
                // Save with standard JPEG format
                thumbnailBitmap.Save(ms, ImageFormat.Jpeg);
                return (ms.ToArray(), targetW, targetH);
            }, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || thumbnailBytes.Item1.Length == 0)
                return null;

            var newEntry = new CacheEntry(thumbnailBytes.Item1, thumbnailBytes.Item2, thumbnailBytes.Item3);

            lock (evictionLock)
            {
                // Evict oldest entries until within memory and count budgets
                while ((Interlocked.Read(ref currentMemoryBytes) + newEntry.SizeBytes > MaxMemoryBytes || cache.Count >= MaxItemCount) && !cache.IsEmpty)
                {
                    var oldest = cache.OrderBy(kv => kv.Value.LastAccessedUtc).FirstOrDefault();
                    if (oldest.Key is not null && cache.TryRemove(oldest.Key, out var removed))
                    {
                        Interlocked.Add(ref currentMemoryBytes, -removed.SizeBytes);
                    }
                    else
                    {
                        break;
                    }
                }

                if (cache.TryAdd(key, newEntry))
                {
                    Interlocked.Add(ref currentMemoryBytes, newEntry.SizeBytes);
                }
            }

            return newEntry.Bytes;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}
