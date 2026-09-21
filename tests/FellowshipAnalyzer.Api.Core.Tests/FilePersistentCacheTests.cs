using FellowshipAnalyzer.Api.Core.Caching;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

namespace FellowshipAnalyzer.Api.Core.Tests;

public class FilePersistentCacheTests : IDisposable
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), "fa-cache-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FilePersistentCache _cache;

    public FilePersistentCacheTests()
    {
        _cache = new FilePersistentCache(
            new FilePersistentCacheOptions { BasePath = _basePath },
            NullLogger<FilePersistentCache>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
        {
            Directory.Delete(_basePath, recursive: true);
        }
    }

    [Fact]
    public async Task SetAsync_RoundTripsBytesWithMetadata()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(1);
        var options = new PersistentCacheWriteOptions(
            ExpiresAt: expiresAt,
            ContentType: "application/json",
            ContentEncoding: null,
            Metadata: new Dictionary<string, string> { ["hero"] = "Rime" });

        await _cache.SetAsync(CachePartition.Metadata, "character/127512", "hello"u8.ToArray(), options, CancellationToken.None);

        var entry = await _cache.GetAsync(CachePartition.Metadata, "character/127512", CancellationToken.None);
        entry.ShouldNotBeNull();
        await using (entry!.Content)
        {
            using var reader = new StreamReader(entry.Content);
            (await reader.ReadToEndAsync()).ShouldBe("hello");
        }

        entry.ContentLength.ShouldBe(5);
        entry.ExpiresAt.ShouldBe(expiresAt);
        entry.ContentEncoding.ShouldBeNull();
        entry.Metadata["hero"].ShouldBe("Rime");
    }

    [Fact]
    public async Task GetAsync_ReturnsNullOnMiss()
    {
        var entry = await _cache.GetAsync(CachePartition.Events, "events/abc/1/2", CancellationToken.None);
        entry.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_LazilyDeletesExpiredEntries()
    {
        var options = new PersistentCacheWriteOptions(
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            ContentType: "application/json",
            ContentEncoding: "gzip");

        await _cache.SetAsync(CachePartition.Events, "events/abc/1/2", new byte[] { 1, 2, 3 }, options, CancellationToken.None);

        var entry = await _cache.GetAsync(CachePartition.Events, "events/abc/1/2", CancellationToken.None);
        entry.ShouldBeNull();
        Directory.Exists(Path.Combine(_basePath, CachePartition.Events.ToString(), "events", "abc", "1", "2")).ShouldBeFalse();
    }

    [Fact]
    public async Task SetStreamAsync_RoundTripsContent()
    {
        using var payload = new MemoryStream([10, 20, 30, 40]);
        var options = new PersistentCacheWriteOptions(
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(2),
            ContentType: "application/json",
            ContentEncoding: "gzip");

        await _cache.SetStreamAsync(CachePartition.Events, "deaths/abc/3", payload, options, CancellationToken.None);

        var entry = await _cache.GetAsync(CachePartition.Events, "deaths/abc/3", CancellationToken.None);
        entry.ShouldNotBeNull();
        await using (entry!.Content)
        {
            using var memory = new MemoryStream();
            await entry.Content.CopyToAsync(memory);
            memory.ToArray().ShouldBe([10, 20, 30, 40]);
        }

        entry.ContentEncoding.ShouldBe("gzip");
    }

    [Fact]
    public async Task RemoveAsync_DeletesEntry()
    {
        var options = new PersistentCacheWriteOptions(DateTimeOffset.UtcNow.AddDays(1), "application/json", null);
        await _cache.SetAsync(CachePartition.Metadata, "character/1", new byte[] { 9 }, options, CancellationToken.None);

        await _cache.RemoveAsync(CachePartition.Metadata, "character/1", CancellationToken.None);

        var entry = await _cache.GetAsync(CachePartition.Metadata, "character/1", CancellationToken.None);
        entry.ShouldBeNull();
    }

    [Fact]
    public async Task Keys_WithTraversalSegments_StayInsideBasePath()
    {
        var options = new PersistentCacheWriteOptions(DateTimeOffset.UtcNow.AddDays(1), "application/json", null);
        await _cache.SetAsync(CachePartition.Metadata, "../outside/escape", new byte[] { 7 }, options, CancellationToken.None);

        Directory.Exists(Path.Combine(_basePath, "Metadata", "_", "outside")).ShouldBeTrue();
        Directory.Exists(Path.GetFullPath(Path.Combine(_basePath, "..", "outside"))).ShouldBeFalse();

        var entry = await _cache.GetAsync(CachePartition.Metadata, "../outside/escape", CancellationToken.None);
        entry.ShouldNotBeNull();
        await entry!.Content.DisposeAsync();
    }
}
