using CleanTag.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace CleanTag.Services;

public sealed class ThumbnailCache
{
    private readonly SemaphoreSlim _slots = new(2);
    private readonly Dictionary<string, byte[]> _cache = new();
    private readonly Queue<string> _order = new();
    public async Task<byte[]?> GetAsync(FileEntry file, CancellationToken token, int size = 1200)
    {
        string key = $"{file.Identity}|{size}";
        lock (_cache) if (_cache.TryGetValue(key, out var found)) return found;
        await _slots.WaitAsync(token);
        try
        {
            var storage = await StorageFile.GetFileFromPathAsync(file.FullPath).AsTask(token);
            using var thumbnail = await storage.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)size, ThumbnailOptions.ResizeThumbnail).AsTask(token);
            if (thumbnail == null || thumbnail.Type != ThumbnailType.Image || thumbnail.Size > 8_000_000) return null;
            using var reader = new DataReader(thumbnail.GetInputStreamAt(0));
            var length = (uint)thumbnail.Size;
            if (await reader.LoadAsync(length).AsTask(token) != length) return null;
            byte[] bytes = new byte[length]; reader.ReadBytes(bytes);
            lock (_cache)
            {
                if (!_cache.ContainsKey(key)) { _cache.Add(key, bytes); _order.Enqueue(key); }
                while (_order.Count > 8) _cache.Remove(_order.Dequeue());
            }
            return bytes;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { _slots.Release(); }
    }
}
