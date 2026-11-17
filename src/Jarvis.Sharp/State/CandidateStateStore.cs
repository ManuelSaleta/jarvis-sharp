namespace Jarvis.Sharp.State;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

public class CandidateStateStore
{
    private readonly TimeSpan _ttl;
    private readonly string? _stateFilePath;
    private readonly ILogger<CandidateStateStore>? _logger;
    private readonly ConcurrentDictionary<string, CandidateBatch> _batches = new(StringComparer.Ordinal);
    private string? _lastBatchId;
    private readonly object _fileLock = new();

    public CandidateStateStore(
        TimeSpan? ttl = null,
        string? stateFilePath = null,
        ILogger<CandidateStateStore>? logger = null)
    {
        _ttl = ttl ?? TimeSpan.FromHours(24);
        _stateFilePath = stateFilePath;
        _logger = logger;
        LoadFromDisk();
    }

    public void SaveBatch(string batchId, IEnumerable<CandidateItem> items, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(items);

        var timestamp = now ?? DateTimeOffset.UtcNow;
        var batch = new CandidateBatch(batchId.Trim(), timestamp, items.ToList().AsReadOnly());

        _batches[batch.BatchId] = batch;
        _lastBatchId = batch.BatchId;

        EvictExpired(timestamp);
        PersistToDisk();
    }

    public CandidateBatch? GetBatch(string? batchId = null, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        EvictExpired(timestamp);

        var targetId = !string.IsNullOrWhiteSpace(batchId) ? batchId.Trim() : _lastBatchId;
        if (targetId != null && _batches.TryGetValue(targetId, out var batch))
        {
            if (timestamp - batch.CreatedAt <= _ttl)
            {
                return batch;
            }
            _batches.TryRemove(targetId, out _);
        }

        return null;
    }

    public CandidateBatch? GetLatestBatch(DateTimeOffset? now = null) => GetBatch(null, now);

    public IReadOnlyList<CandidateItem> ResolveIndices(string? batchId, IEnumerable<int> indices, DateTimeOffset? now = null)
    {
        var batch = GetBatch(batchId, now);
        if (batch == null)
        {
            return Array.Empty<CandidateItem>();
        }

        var indexSet = new HashSet<int>(indices);
        return batch.Items.Where(item => indexSet.Contains(item.Index)).ToList().AsReadOnly();
    }

    public int EvictExpired(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var expiredKeys = _batches
            .Where(kvp => current - kvp.Value.CreatedAt > _ttl)
            .Select(kvp => kvp.Key)
            .ToList();

        var evicted = 0;
        foreach (var key in expiredKeys)
        {
            if (_batches.TryRemove(key, out _))
            {
                evicted++;
            }
        }

        return evicted;
    }

    private void PersistToDisk()
    {
        if (string.IsNullOrWhiteSpace(_stateFilePath))
        {
            return;
        }

        lock (_fileLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(_batches.Values, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_stateFilePath, json);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to persist candidate state to '{StateFilePath}'.", _stateFilePath);
                if (_logger == null)
                {
                    Console.Error.WriteLine($"[CandidateStateStore] Failed to persist candidate state to '{_stateFilePath}': {ex.Message}");
                }
            }
        }
    }

    private void LoadFromDisk()
    {
        if (string.IsNullOrWhiteSpace(_stateFilePath) || !File.Exists(_stateFilePath))
        {
            return;
        }

        lock (_fileLock)
        {
            try
            {
                var json = File.ReadAllText(_stateFilePath);
                var loaded = JsonSerializer.Deserialize<List<CandidateBatch>>(json);
                if (loaded != null)
                {
                    foreach (var batch in loaded)
                    {
                        _batches[batch.BatchId] = batch;
                        _lastBatchId = batch.BatchId;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load candidate state from '{StateFilePath}'; starting with empty store.", _stateFilePath);
                if (_logger == null)
                {
                    Console.Error.WriteLine($"[CandidateStateStore] Failed to load candidate state from '{_stateFilePath}': {ex.Message}");
                }
            }
        }
    }
}
