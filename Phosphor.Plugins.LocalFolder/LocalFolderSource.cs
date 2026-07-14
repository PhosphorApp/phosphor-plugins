using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.LocalFolder;

/// <summary>
/// A configured local-folder source instance. Scans the configured folders for media files and
/// exposes them for browse, search, and playback. The "catalog" is an in-memory list built by
/// <see cref="RefreshAsync"/> (and lazily on first use); Phase-2 metadata extraction (tags,
/// durations, thumbnails) can enrich each entry later without changing the contract surface.
/// </summary>
/// <remarks>
/// Pure data producer — no UI, no host internals. It talks only to <c>Phosphor.Plugin.Abstractions</c>
/// and the file system, which is exactly what makes it a valid third-party plug-in template.
/// </remarks>
public sealed class LocalFolderSource :
    IPhosphorSource, IBrowsable, ITextSearchCapable, IPlayableResolver, IRefreshable, IGaplessCapable
{
    // File extensions we treat as playable media.
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v", ".wmv", ".flv" };
    private static readonly HashSet<string> AudioExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma" };

    private readonly object _gate = new();
    private List<MediaEntry> _catalog = [];
    private bool _catalogBuilt;

    private IPluginHost? _host;
    private List<string> _folders = [];
    private bool _recursive = true;

    public LocalFolderSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => LocalFolderSourceProvider.LocalFolderTypeId;
    public string DisplayName { get; set; } = "Local Folders";

    /// <summary>Configured once at least one existing folder is set.</summary>
    public bool IsConfigured => _folders.Any(Directory.Exists);

    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        return Task.CompletedTask;
    }

    public void ApplySettings(IReadOnlyDictionary<string, string?> values) => ApplySettingsInternal(values);

    private void ApplySettingsInternal(IReadOnlyDictionary<string, string?> values)
    {
        _folders = (Get(values, LocalFolderSourceProvider.KeyFolders) ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _recursive = !bool.TryParse(Get(values, LocalFolderSourceProvider.KeyRecursive), out var r) || r;

        // Settings changed — the catalog is stale.
        lock (_gate)
        {
            _catalog = [];
            _catalogBuilt = false;
        }
        _host?.Log($"LocalFolderSource: {_folders.Count} folder(s), recursive={_recursive}");
    }

    // ── IRefreshable ───────────────────────────────────────────────────────────

    public bool CanRefresh => _folders.Count > 0;

    public Task<RefreshResult> RefreshAsync(
        IProgress<RefreshProgress>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var existing = _folders.Where(Directory.Exists).ToList();
            if (existing.Count == 0)
                return new RefreshResult(false, 0, "No configured folders exist on disk.");

            var catalog = new List<MediaEntry>();
            int folderIndex = 0;
            foreach (var folder in existing)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new RefreshProgress(
                    (double)folderIndex / existing.Count, folder));

                var option = _recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder, "*", option);
                }
                catch (Exception ex)
                {
                    _host?.Log($"LocalFolderSource: skipping '{folder}': {ex.Message}");
                    folderIndex++;
                    continue;
                }

                foreach (var path in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(path);
                    var isVideo = VideoExtensions.Contains(ext);
                    var isAudio = AudioExtensions.Contains(ext);
                    if (!isVideo && !isAudio) continue;

                    catalog.Add(new MediaEntry(
                        Path: path,
                        Title: Path.GetFileNameWithoutExtension(path),
                        Folder: folder,
                        IsAudioOnly: isAudio));
                }
                folderIndex++;
            }

            catalog.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            lock (_gate)
            {
                _catalog = catalog;
                _catalogBuilt = true;
            }

            progress?.Report(new RefreshProgress(1.0));
            return new RefreshResult(true, catalog.Count,
                $"Scanned {catalog.Count} file(s) in {existing.Count} folder(s).");
        }, ct);
    }

    private List<MediaEntry> EnsureCatalog()
    {
        lock (_gate)
        {
            if (_catalogBuilt) return _catalog;
        }
        // Build synchronously on first access if a rescan hasn't run yet.
        RefreshAsync().GetAwaiter().GetResult();
        lock (_gate) return _catalog;
    }

    // ── IBrowsable ─────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        // One tile per configured folder.
        foreach (var folder in _folders.Where(Directory.Exists))
        {
            yield return new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = folder,
                Title = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : folder,
                HasSubCategories = false,
                SourceState = folder,
            };
        }
    }

    public Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        var folder = category.SourceState as string ?? category.CategoryId;
        var items = EnsureCatalog()
            .Where(e => PathIsUnder(e.Path, folder))
            .Select(ToSourceItem)
            .ToList();
        return Task.FromResult(new BrowseResult { Items = items });
    }

    // ── ITextSearchCapable ─────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var catalog = EnsureCatalog();
        foreach (var e in catalog)
        {
            ct.ThrowIfCancellationRequested();
            if (e.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                yield return ToSourceItem(e);
        }
    }

    // ── IPlayableResolver ──────────────────────────────────────────────────────

    public Task<ResolvedStream?> ResolveAsync(SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        var path = item.SourceState as string ?? item.ItemId;
        if (!File.Exists(path))
            return Task.FromResult<ResolvedStream?>(null);

        var layout = item.IsAudioOnly ? StreamLayout.AudioOnly : StreamLayout.Muxed;
        return Task.FromResult<ResolvedStream?>(
            new ResolvedStream(StreamTransport.File, layout, path));
    }

    public Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
    {
        // Phase 1: no tag/duration probing yet. Return an empty (but non-null) metadata so the host
        // treats the item as valid; Phase 2 can read tags/duration here.
        return Task.FromResult<SourceMetadata?>(new SourceMetadata(null, null, []));
    }

    // ── IGaplessCapable ────────────────────────────────────────────────────────

    public string? GetGaplessStreamUrl(SourceItem item)
    {
        // Local audio files are stable, direct paths — ideal for gapless priming.
        var path = item.SourceState as string ?? item.ItemId;
        return item.IsAudioOnly && File.Exists(path) ? path : null;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private SourceItem ToSourceItem(MediaEntry e) => new()
    {
        SourceInstanceId = InstanceId,
        ItemId = e.Path,
        Title = e.Title,
        Subtitle = Path.GetFileName(e.Folder.TrimEnd(Path.DirectorySeparatorChar)),
        IsAudioOnly = e.IsAudioOnly,
        SourceState = e.Path,
    };

    private static bool PathIsUnder(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(folder);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var v) ? v : null;

    /// <summary>One catalog entry (a media file on disk).</summary>
    private sealed record MediaEntry(string Path, string Title, string Folder, bool IsAudioOnly);
}
