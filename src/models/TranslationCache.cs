using System.Security.Cryptography;
using System.Text.Json;

namespace LiveCaptionsTranslator.models;

/// <summary>Thread-safe, byte-budgeted translation cache with absolute TTL and least-recently-used eviction.</summary>
public sealed class TranslationCache
{
    /// <summary>Default cache budget: 10 MiB, including serialized results, keys, and an allowance for entry metadata.</summary>
    public const long DefaultMaxBytes = 10 * 1024 * 1024;
    /// <summary>Results expire fifteen minutes after insertion; reads do not extend their lifetime.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);
    private const long EntryOverhead = 384;
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> recency = new();
    private readonly LinkedList<Entry> expiration = new();
    private readonly long maxBytes;
    private readonly TimeSpan ttl;
    private readonly TimeProvider clock;
    private long usedBytes;

    /// <summary>Creates an in-memory cache with a bounded accounting budget and absolute expiration.</summary>
    /// <param name="maxBytes">Maximum serialized payload/key bytes plus per-entry metadata allowance.</param>
    /// <param name="ttl">Absolute lifetime after insertion; defaults to fifteen minutes.</param>
    /// <param name="clock">Monotonic clock, injectable for deterministic expiration tests.</param>
    public TranslationCache(long maxBytes = DefaultMaxBytes, TimeSpan? ttl = null, TimeProvider? clock = null)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        this.ttl = ttl ?? DefaultTtl;
        if (this.ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        this.maxBytes = maxBytes;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Accounted bytes currently retained after removing expired entries; not total process memory.</summary>
    public long UsedBytes { get { lock (sync) { RemoveExpired(); return usedBytes; } } }
    /// <summary>Number of unexpired cached results.</summary>
    public int Count { get { lock (sync) { RemoveExpired(); return entries.Count; } } }

    /// <summary>Hashes a complete request identity so cache keys retain no plaintext credentials or source text.</summary>
    /// <param name="requestIdentity">Source/input, engine settings, context, and account scope defining the result.</param>
    public static string CreateKey(object requestIdentity) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(requestIdentity)));

    /// <summary>Reads an independent result snapshot and marks the entry as recently used without extending its TTL.</summary>
    /// <param name="key">Hashed request identity.</param>
    /// <param name="result">Decoded translation and replies, or null on a miss.</param>
    public bool TryGet(string key, out TranslationResult? result)
    {
        byte[] payload;
        lock (sync)
        {
            RemoveExpired();
            if (!entries.TryGetValue(key, out var entry)) { result = null; return false; }
            recency.Remove(entry.RecentNode);
            recency.AddLast(entry.RecentNode);
            payload = entry.Payload;
        }
        result = JsonSerializer.Deserialize<TranslationResult>(payload)!;
        return true;
    }

    /// <summary>Stores only successful complete results, evicting least-recently-used entries to stay within the byte budget.</summary>
    /// <param name="key">Hashed request identity.</param>
    /// <param name="result">Completed translation before display latency prefixes are added.</param>
    public void Store(string key, TranslationResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Text) || result.Text.Contains("[ERROR]") || result.Text.Contains("[WARNING]") ||
            (result.RepliesRequested && result.Replies.Count != 3)) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(result);
        var size = payload.LongLength + 2L * key.Length + EntryOverhead;
        lock (sync)
        {
            RemoveExpired();
            if (size > maxBytes) return;
            if (entries.TryGetValue(key, out var old)) Remove(old);
            while (usedBytes + size > maxBytes && recency.First != null) Remove(recency.First.Value);
            var entry = new Entry(key, payload, size, clock.GetTimestamp());
            entries.Add(key, entry);
            recency.AddLast(entry.RecentNode);
            expiration.AddLast(entry.ExpiryNode);
            usedBytes += size;
        }
    }

    private void RemoveExpired()
    {
        var now = clock.GetTimestamp();
        while (expiration.First is { } node && clock.GetElapsedTime(node.Value.StoredAt, now) >= ttl) Remove(node.Value);
    }

    private void Remove(Entry entry)
    {
        entries.Remove(entry.Key);
        recency.Remove(entry.RecentNode);
        expiration.Remove(entry.ExpiryNode);
        usedBytes -= entry.Size;
    }

    /// <summary>Immutable serialized result indexed by independent LRU and insertion-order nodes.</summary>
    private sealed class Entry
    {
        internal string Key { get; }
        internal byte[] Payload { get; }
        internal long Size { get; }
        internal long StoredAt { get; }
        internal LinkedListNode<Entry> RecentNode { get; }
        internal LinkedListNode<Entry> ExpiryNode { get; }
        internal Entry(string key, byte[] payload, long size, long storedAt)
        {
            Key = key; Payload = payload; Size = size; StoredAt = storedAt;
            RecentNode = new(this); ExpiryNode = new(this);
        }
    }
}
