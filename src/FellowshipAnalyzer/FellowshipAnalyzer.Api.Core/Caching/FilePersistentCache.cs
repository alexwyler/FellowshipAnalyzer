using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace FellowshipAnalyzer.Api.Core.Caching;

public sealed class FilePersistentCacheOptions
{
    public const string SectionName = "PersistentCache";

    /// <summary>Directory that stores the cache partitions. Created on demand.</summary>
    public string BasePath { get; set; } = "persistent-cache";
}

/// <summary>
/// Filesystem-backed <see cref="IPersistentCache"/> for single-instance hosts without Azure
/// Storage. Each entry is a directory under <c>{BasePath}/{partition}/{key path}</c> holding the
/// raw bytes (<c>entry.bin</c>) and a <c>meta.json</c> sidecar with expiry and content headers.
/// TTL is stored as ISO-8601 ("O") and evaluated on read; expired entries are lazily deleted.
/// Read failures surface as misses; write failures are logged and swallowed so cache problems
/// never fail a request.
/// </summary>
public sealed class FilePersistentCache : IPersistentCache
{
    private const string ContentFileName = "entry.bin";
    private const string MetaFileName = "meta.json";

    private static readonly JsonSerializerOptions MetaJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FilePersistentCacheOptions _options;
    private readonly ILogger<FilePersistentCache> _logger;

    public FilePersistentCache(FilePersistentCacheOptions options, ILogger<FilePersistentCache> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<PersistentCacheEntry?> GetAsync(CachePartition partition, string key, CancellationToken ct)
    {
        try
        {
            var entryDirectory = ResolveEntryDirectory(partition, key);
            var contentPath = Path.Combine(entryDirectory, ContentFileName);
            var metaPath = Path.Combine(entryDirectory, MetaFileName);

            if (!File.Exists(contentPath) || !File.Exists(metaPath))
            {
                return null;
            }

            FileCacheMetadata? meta;
            await using (var metaStream = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                meta = await JsonSerializer.DeserializeAsync<FileCacheMetadata>(metaStream, MetaJsonOptions, ct);
            }

            if (meta is null)
            {
                return null;
            }

            if (meta.ExpiresAt is { } expiresAt && DateTimeOffset.UtcNow >= expiresAt)
            {
                _ = DeleteEntryAsync(entryDirectory);
                _logger.LogInformation("File cache GET partition={Partition} key={Key} expired", partition, key);
                return null;
            }

            var content = new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (content.Length != meta.ContentLength)
            {
                await content.DisposeAsync();
                _ = DeleteEntryAsync(entryDirectory);
                _logger.LogInformation("File cache GET partition={Partition} key={Key} length mismatch", partition, key);
                return null;
            }

            _logger.LogInformation("File cache GET partition={Partition} key={Key} bytes={Bytes}", partition, key, content.Length);

            return new PersistentCacheEntry(
                Content: content,
                ContentLength: content.Length,
                ExpiresAt: meta.ExpiresAt,
                ContentEncoding: meta.ContentEncoding,
                Metadata: meta.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "File cache GET failed partition={Partition} key={Key}", partition, key);
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(
        CachePartition partition, string key,
        ReadOnlyMemory<byte> bytes,
        PersistentCacheWriteOptions options,
        CancellationToken ct)
    {
        try
        {
            var entryDirectory = ResolveEntryDirectory(partition, key);
            Directory.CreateDirectory(entryDirectory);
            var contentPath = Path.Combine(entryDirectory, ContentFileName);

            await WriteFileAsync(contentPath, bytes, ct);
            await WriteMetaAsync(entryDirectory, new FileInfo(contentPath).Length, options, ct);

            _logger.LogInformation("File cache SET partition={Partition} key={Key} bytes={Bytes}", partition, key, bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "File cache SET failed partition={Partition} key={Key}", partition, key);
        }
    }

    /// <inheritdoc />
    public async ValueTask SetStreamAsync(
        CachePartition partition, string key,
        Stream payload,
        PersistentCacheWriteOptions options,
        CancellationToken ct)
    {
        try
        {
            var entryDirectory = ResolveEntryDirectory(partition, key);
            Directory.CreateDirectory(entryDirectory);
            var contentPath = Path.Combine(entryDirectory, ContentFileName);

            await using (var temp = new FileStream(contentPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await payload.CopyToAsync(temp, ct);
            }

            File.Move(contentPath + ".tmp", contentPath, overwrite: true);
            await WriteMetaAsync(entryDirectory, new FileInfo(contentPath).Length, options, ct);

            _logger.LogInformation("File cache SET partition={Partition} key={Key} committed (stream)", partition, key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "File cache SET failed partition={Partition} key={Key}", partition, key);
        }
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(CachePartition partition, string key, CancellationToken ct)
    {
        _ = DeleteEntryAsync(ResolveEntryDirectory(partition, key));
        return ValueTask.CompletedTask;
    }

    private static async Task WriteFileAsync(string contentPath, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await using (var temp = new FileStream(contentPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await temp.WriteAsync(bytes, ct);
        }

        File.Move(contentPath + ".tmp", contentPath, overwrite: true);
    }

    private static async Task WriteMetaAsync(
        string entryDirectory, long contentLength, PersistentCacheWriteOptions options, CancellationToken ct)
    {
        var meta = new FileCacheMetadata(
            options.ExpiresAt,
            contentLength,
            options.ContentType,
            options.ContentEncoding,
            options.Metadata);
        var metaPath = Path.Combine(entryDirectory, MetaFileName);

        await using (var temp = new FileStream(metaPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(temp, meta, MetaJsonOptions, ct);
        }

        File.Move(metaPath + ".tmp", metaPath, overwrite: true);
    }

    private Task DeleteEntryAsync(string entryDirectory)
    {
        try
        {
            if (Directory.Exists(entryDirectory))
            {
                Directory.Delete(entryDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "File cache delete failed path={Path}", entryDirectory);
        }

        return Task.CompletedTask;
    }

    private string ResolveEntryDirectory(CachePartition partition, string key)
    {
        var segments = key
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeSegment)
            .ToArray();

        if (segments.Length == 0)
        {
            throw new ArgumentException("Cache key must contain at least one non-empty segment.", nameof(key));
        }

        return Path.Join(_options.BasePath, partition.ToString(), Path.Join(segments));
    }

    private static string SanitizeSegment(string segment) =>
        segment is "." or ".."
            ? "_"
            : new string(segment.Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());

    private sealed record FileCacheMetadata(
        DateTimeOffset? ExpiresAt,
        long ContentLength,
        string? ContentType,
        string? ContentEncoding,
        Dictionary<string, string>? Metadata);
}
