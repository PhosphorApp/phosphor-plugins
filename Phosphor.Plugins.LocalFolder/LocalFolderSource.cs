using System.Text.Json;
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
    // File extensions we treat as playable media. These are configurable per instance (see the
    // video/audio extension settings) so a user can, for example, run one source over a mixed folder
    // filtering to video and a second over the same folder filtering to audio. Populated from
    // settings in ApplySettingsInternal; empty when the user selects "None" for that media kind.
    private HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _audioExtensions = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();
    private List<MediaEntry> _catalog = [];
    private bool _catalogBuilt;
    private DateTimeOffset? _catalogSavedUtc;

    private IPluginHost? _host;
    private List<string> _folders = [];
    private bool _recursive = true;
    private int _cacheMaxAgeHours;
    private string _organizeBy = LocalFolderSourceProvider.OrganizeByFolder;
    private bool _extractThumbnails;

    // On-disk cache format version. Bump when the persisted shape changes; a mismatch forces a
    // full rescan (the tester-only user base does not need migration shims).
    private const int CacheSchemaVersion = 4;
    private const string CacheFileName = "catalog.json";

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
        _cacheMaxAgeHours = int.TryParse(Get(values, LocalFolderSourceProvider.KeyCacheMaxAgeHours), out var h) && h > 0
            ? h : 0;
        _organizeBy = Get(values, LocalFolderSourceProvider.KeyOrganizeBy) switch
        {
            var v when string.Equals(v, LocalFolderSourceProvider.OrganizeByFolder, StringComparison.OrdinalIgnoreCase)
                => LocalFolderSourceProvider.OrganizeByFolder,
            var v when string.Equals(v, LocalFolderSourceProvider.OrganizeByMetadata, StringComparison.OrdinalIgnoreCase)
                => LocalFolderSourceProvider.OrganizeByMetadata,
            _ => LocalFolderSourceProvider.OrganizeByBoth,
        };
        _extractThumbnails = bool.TryParse(Get(values, LocalFolderSourceProvider.KeyExtractThumbnails), out var t) && t;

        _videoExtensions = ParseExtensions(
            Get(values, LocalFolderSourceProvider.KeyVideoExtensions),
            LocalFolderSourceProvider.RecommendedVideoExtensions);
        _audioExtensions = ParseExtensions(
            Get(values, LocalFolderSourceProvider.KeyAudioExtensions),
            LocalFolderSourceProvider.RecommendedAudioExtensions);

        // Settings changed — the catalog is stale.
        lock (_gate)
        {
            _catalog = [];
            _catalogBuilt = false;
            _catalogSavedUtc = null;
        }
        _host?.Log(LogLevel.Debug, $"LocalFolderSource: {_folders.Count} folder(s), recursive={_recursive}, cacheMaxAgeHours={_cacheMaxAgeHours}, organizeBy={_organizeBy}, thumbnails={_extractThumbnails}, video=[{string.Join(' ', _videoExtensions)}], audio=[{string.Join(' ', _audioExtensions)}]");
    }

    /// <summary>
    /// Turns a stored extension setting into a normalized set of "<c>.ext</c>" tokens. A null value
    /// (the key was never configured) falls back to the shipped <paramref name="recommended"/> list.
    /// Otherwise the text is tokenized on whitespace, commas, or semicolons and <em>only real
    /// extension tokens are kept</em> — any word that isn't a valid extension (e.g. the "None"
    /// convention, or an empty box) is simply ignored, yielding an empty set that indexes nothing of
    /// that media kind. The leading dot is optional.
    /// </summary>
    private static HashSet<string> ParseExtensions(string? value, IReadOnlyCollection<string> recommended)
    {
        // Never configured → ship the recommended defaults.
        if (value is null)
            return new(recommended, StringComparer.OrdinalIgnoreCase);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in value.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ext = token.StartsWith('.') ? token : "." + token;
            if (IsExtensionToken(ext))
                set.Add(ext);
        }
        return set;
    }

    /// <summary>
    /// True when a token looks like a real file extension (a dot followed by alphanumerics only), so
    /// reserved words like "None" or stray prose in the field are ignored rather than treated as an
    /// extension to match.
    /// </summary>
    private static bool IsExtensionToken(string ext)
    {
        if (ext.Length < 2 || ext[0] != '.') return false;
        for (int i = 1; i < ext.Length; i++)
            if (!char.IsLetterOrDigit(ext[i])) return false;
        return true;
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

            // Index the previous catalog by path so unchanged files can be reused verbatim — this
            // keeps rescans cheap and preserves already-extracted metadata and thumbnails for files
            // whose contents haven't changed on disk. Prefer the in-memory catalog, but fall back to
            // the on-disk cache: the Settings "Rescan library" runs on a fresh transient source whose
            // in-memory catalog is empty, so without this every file would be miscounted as "new".
            List<MediaEntry> priorEntries;
            lock (_gate)
                priorEntries = _catalog;
            if (priorEntries.Count == 0 && TryLoadCache(out var cachedPrior, out _))
                priorEntries = cachedPrior;
            var previous = priorEntries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);

            var catalog = new List<MediaEntry>();
            int folderIndex = 0, reused = 0, scanned = 0;
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
                    _host?.Log(LogLevel.Warning, $"LocalFolderSource: skipping '{folder}': {ex.Message}");
                    folderIndex++;
                    continue;
                }

                foreach (var path in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(path);
                    var isVideo = _videoExtensions.Contains(ext);
                    var isAudio = _audioExtensions.Contains(ext);
                    if (!isVideo && !isAudio) continue;

                    long size;
                    DateTimeOffset writeUtc;
                    try
                    {
                        var info = new FileInfo(path);
                        size = info.Length;
                        writeUtc = info.LastWriteTimeUtc;
                    }
                    catch (Exception ex)
                    {
                        _host?.Log(LogLevel.Warning, $"LocalFolderSource: skipping '{path}': {ex.Message}");
                        continue;
                    }

                    // Incremental reuse: keep the cached entry when size and last-write time match.
                    if (previous.TryGetValue(path, out var prior) &&
                        prior.FileSize == size && prior.LastWriteTimeUtc == writeUtc)
                    {
                        var reusedEntry = prior with { Folder = folder };
                        // The file is unchanged. Only (re)generate a thumbnail if we've never tried
                        // for this file (e.g. the setting was just enabled) or a previously-made image
                        // went missing. A file we already tried and that has no cover art / no ffmpeg
                        // frame is skipped, so it doesn't pay the extraction cost on every rescan.
                        if (_extractThumbnails && NeedsThumbnail(reusedEntry))
                            reusedEntry = EnsureThumbnail(reusedEntry, ct);
                        catalog.Add(reusedEntry);
                        reused++;
                        continue;
                    }

                    var entry = ReadMetadata(new MediaEntry(
                        Path: path,
                        Title: Path.GetFileNameWithoutExtension(path),
                        Folder: folder,
                        IsAudioOnly: isAudio,
                        FileSize: size,
                        LastWriteTimeUtc: writeUtc));
                    if (_extractThumbnails)
                        entry = EnsureThumbnail(entry, ct);
                    catalog.Add(entry);
                    scanned++;
                }
                folderIndex++;
            }

            catalog.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            lock (_gate)
            {
                _catalog = catalog;
                _catalogBuilt = true;
            }

            SaveCache(catalog);

            progress?.Report(new RefreshProgress(1.0));
            return new RefreshResult(true, catalog.Count,
                $"Scanned {catalog.Count} file(s) in {existing.Count} folder(s) " +
                $"({scanned} new/changed, {reused} unchanged).");
        }, ct);
    }

    private List<MediaEntry> EnsureCatalog()
    {
        lock (_gate)
        {
            if (_catalogBuilt) return _catalog;
        }

        // Try the on-disk cache first so we don't rescan every launch.
        if (TryLoadCache(out var cached, out var savedUtc))
        {
            bool stale = _cacheMaxAgeHours > 0 &&
                DateTimeOffset.UtcNow - savedUtc > TimeSpan.FromHours(_cacheMaxAgeHours);
            lock (_gate)
            {
                _catalog = cached;
                _catalogBuilt = true;
                _catalogSavedUtc = savedUtc;
            }
            if (!stale)
                return cached;

            _host?.Log(LogLevel.Debug, "LocalFolderSource: catalog cache is stale — rescanning.");
        }

        // No usable cache (or it's stale): build synchronously on first access.
        RefreshAsync().GetAwaiter().GetResult();
        lock (_gate) return _catalog;
    }

    /// <summary>
    /// Async counterpart of <see cref="EnsureCatalog"/>. Loading the cache and (if needed) building it
    /// can touch disk and read tags for a large library, so it runs off the caller's thread. The host
    /// awaits browse/search on the UI thread, so doing this work inline would hang the UI; offloading
    /// it keeps the await truly asynchronous.
    /// </summary>
    private async Task<List<MediaEntry>> EnsureCatalogAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_catalogBuilt) return _catalog;
        }
        return await Task.Run(() => EnsureCatalog(), ct);
    }

    // ── metadata (tags) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads embedded tag metadata (artist/album/track/year/duration) for a freshly discovered or
    /// changed file. Defensive per-file: a corrupt or unsupported file simply yields an entry with
    /// <see cref="MediaEntry.MetadataRead"/> set but empty tags, so it is not re-probed on every
    /// rescan. Unchanged files skip this entirely via the incremental-reuse path.
    /// </summary>
    private MediaEntry ReadMetadata(MediaEntry entry)
    {
        try
        {
            using var file = TagLib.File.Create(entry.Path);
            var tag = file.Tag;
            var title = string.IsNullOrWhiteSpace(tag.Title) ? entry.Title : tag.Title.Trim();
            var artist = FirstNonEmpty(tag.Performers) ?? FirstNonEmpty(tag.AlbumArtists);
            var album = string.IsNullOrWhiteSpace(tag.Album) ? null : tag.Album.Trim();
            var duration = file.Properties?.Duration is { Ticks: > 0 } d ? d : (TimeSpan?)null;

            return entry with
            {
                Title = title,
                Artist = artist,
                Album = album,
                Track = tag.Track,
                Year = tag.Year,
                Duration = duration,
                MetadataRead = true,
            };
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"LocalFolderSource: no tags for '{entry.Path}': {ex.Message}");
            return entry with { MetadataRead = true };
        }
    }

    private static string? FirstNonEmpty(string[]? values)
        => values?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    // ── thumbnails ──────────────────────────────────────────────────────────────

    private const string ThumbsDirName = "thumbs";

    /// <summary>Per-instance directory where generated thumbnails are cached, or null if unavailable.</summary>
    private string? ThumbsDirectory =>
        _host is { InstanceCacheDirectory: { Length: > 0 } dir } ? Path.Combine(dir, ThumbsDirName) : null;

    /// <summary>
    /// True when a thumbnail should be (re)generated for an otherwise-unchanged, reused entry: either
    /// we've never attempted extraction for it, or a previously-created image is no longer on disk.
    /// Files we've already tried that yielded no image (no cover art / no ffmpeg) return false so they
    /// aren't reprocessed every rescan.
    /// </summary>
    private static bool NeedsThumbnail(MediaEntry entry)
    {
        if (!entry.ThumbnailTried) return true;
        return entry.ThumbnailPath is { Length: > 0 } p && !File.Exists(p);
    }

    /// <summary>
    /// Ensures a cached thumbnail exists for the entry and returns it with <see cref="MediaEntry.ThumbnailPath"/>
    /// set. Audio uses embedded cover art (TagLibSharp); video shells out to the host's ffmpeg for a
    /// single frame. All failure modes are non-fatal — the entry is returned unchanged so the catalog
    /// still builds. A stable hash of the path keys the file so unchanged files reuse the same thumb.
    /// The returned entry always has <see cref="MediaEntry.ThumbnailTried"/> set, so a file that has no
    /// artwork is not re-extracted on subsequent rescans.
    /// </summary>
    private MediaEntry EnsureThumbnail(MediaEntry entry, CancellationToken ct)
    {
        var dir = ThumbsDirectory;
        if (dir is null) return entry;

        // Reuse an already-generated thumbnail if it's still on disk.
        if (entry.ThumbnailPath is { Length: > 0 } existing && File.Exists(existing))
            return entry with { ThumbnailTried = true };

        // Mark the attempt up front so every return path (success or failure) records that we tried.
        entry = entry with { ThumbnailTried = true };
        try
        {
            Directory.CreateDirectory(dir);
            var key = StableHash(entry.Path);

            if (entry.IsAudioOnly)
            {
                var target = Path.Combine(dir, key + ".jpg");
                return ExtractCoverArt(entry, target) ? entry with { ThumbnailPath = target } : entry;
            }
            else
            {
                var target = Path.Combine(dir, key + ".jpg");
                return ExtractVideoFrame(entry, target, ct) ? entry with { ThumbnailPath = target } : entry;
            }
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"LocalFolderSource: thumbnail failed for '{entry.Path}': {ex.Message}");
            return entry;
        }
    }

    private bool ExtractCoverArt(MediaEntry entry, string target)
    {
        try
        {
            using var file = TagLib.File.Create(entry.Path);
            var pic = file.Tag.Pictures?.FirstOrDefault(p => p.Data?.Data is { Length: > 0 });
            if (pic?.Data?.Data is not { Length: > 0 } bytes) return false;
            File.WriteAllBytes(target, bytes);
            return true;
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"LocalFolderSource: no cover art for '{entry.Path}': {ex.Message}");
            return false;
        }
    }

    private bool ExtractVideoFrame(MediaEntry entry, string target, CancellationToken ct)
    {
        var ffmpeg = _host?.GetToolPath("ffmpeg");
        if (string.IsNullOrEmpty(ffmpeg))
            return false; // Host doesn't provide ffmpeg — skip video thumbnails gracefully.

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            // Grab a single frame a few seconds in, scaled to a reasonable tile size, overwrite output.
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add("00:00:05");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(entry.Path);
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale=480:-1");
            psi.ArgumentList.Add(target);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return false;
            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(true); } catch { /* best effort */ }
                return false;
            }
            return proc.ExitCode == 0 && File.Exists(target) && new FileInfo(target).Length > 0;
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"LocalFolderSource: ffmpeg failed for '{entry.Path}': {ex.Message}");
            return false;
        }
    }

    private static string StableHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── catalog cache (catalog.json) ────────────────────────────────────────────
    private string? CachePath =>
        _host is { InstanceCacheDirectory: { Length: > 0 } dir } ? Path.Combine(dir, CacheFileName) : null;

    private void SaveCache(List<MediaEntry> catalog)
    {
        var path = CachePath;
        if (path is null) return;
        try
        {
            var doc = new CacheDocument(
                Version: CacheSchemaVersion,
                SavedUtc: DateTimeOffset.UtcNow,
                Recursive: _recursive,
                Folders: _folders.ToList(),
                Entries: catalog);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(doc, CacheJsonOptions);
            File.WriteAllText(path, json);
            lock (_gate) _catalogSavedUtc = doc.SavedUtc;
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"LocalFolderSource: failed to write catalog cache: {ex.Message}");
        }
    }

    private bool TryLoadCache(out List<MediaEntry> catalog, out DateTimeOffset savedUtc)
    {
        catalog = [];
        savedUtc = default;
        var path = CachePath;
        if (path is null || !File.Exists(path)) return false;
        try
        {
            var doc = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path), CacheJsonOptions);
            if (doc is null || doc.Version != CacheSchemaVersion) return false;

            // Invalidate the cache if the folder set or recursion flag no longer matches the
            // current settings — those change which files belong in the catalog.
            if (doc.Recursive != _recursive ||
                !doc.Folders.SequenceEqual(_folders, StringComparer.OrdinalIgnoreCase))
                return false;

            catalog = doc.Entries;
            savedUtc = doc.SavedUtc;
            return true;
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"LocalFolderSource: failed to read catalog cache: {ex.Message}");
            return false;
        }
    }

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>Persisted catalog envelope written to <c>catalog.json</c>.</summary>
    private sealed record CacheDocument(
        int Version,
        DateTimeOffset SavedUtc,
        bool Recursive,
        List<string> Folders,
        List<MediaEntry> Entries);

    // ── IBrowsable ─────────────────────────────────────────────────────────────

    // Sentinel ids. A single root tile represents the instance; when both organizations are enabled
    // it opens into two view sub-tiles ("Folders" and "By Artist"). "By Folder" browses the on-disk
    // structure; "By Artist" browses the tag-metadata tree (Artist → Album → Track).
    private const string RootId = "__root__";
    private const string FolderRootId = "__folders__";
    private const string MetadataRootId = "__artists__";

    private bool ShowFolderView => _organizeBy != LocalFolderSourceProvider.OrganizeByMetadata;
    private bool ShowMetadataView => _organizeBy != LocalFolderSourceProvider.OrganizeByFolder;
    private bool ShowBothViews => ShowFolderView && ShowMetadataView;

    public async IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        if (!_folders.Any(Directory.Exists))
            yield break;

        // A single tile for the instance. Drilling in shows the "Folders"/"By Artist" view sub-tiles
        // when both are enabled, or opens directly into the one enabled view otherwise.
        // NOTE: do NOT build/scan the catalog here — the host enumerates root tiles during startup,
        // and a synchronous rescan (e.g. after a cache-schema bump) would block the splash. Use a
        // thumbnail only if the catalog is already in memory; otherwise the tile is fine without one.
        yield return new SourceCategory
        {
            SourceInstanceId = InstanceId,
            CategoryId = RootId,
            Title = DisplayName,
            ThumbnailUrl = TryGetLoadedThumbnail(),
            HasSubCategories = true,
            SourceState = RootId,
        };
    }

    /// <summary>A representative thumbnail if the catalog is already loaded, else null. Never builds.</summary>
    private string? TryGetLoadedThumbnail()
    {
        lock (_gate)
            return _catalogBuilt ? FirstThumbnail(_catalog) : null;
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        var node = category.SourceState as string ?? category.CategoryId;
        var catalog = await EnsureCatalogAsync(ct);

        // Root tile: with both views enabled, offer the two view sub-tiles; with one view pinned,
        // pass straight through to that view so there's no redundant middle tile.
        if (node == RootId)
        {
            if (ShowBothViews)
            {
                var views = new List<SourceCategory>
                {
                    new()
                    {
                        SourceInstanceId = InstanceId,
                        CategoryId = FolderRootId,
                        Title = "Folders",
                        Icon = "📁",
                        ThumbnailUrl = FirstThumbnail(catalog),
                        HasSubCategories = true,
                        SourceState = FolderRootId,
                    },
                    new()
                    {
                        SourceInstanceId = InstanceId,
                        CategoryId = MetadataRootId,
                        Title = "By Artist",
                        Icon = "🎵",
                        ThumbnailUrl = FirstThumbnail(catalog),
                        HasSubCategories = true,
                        SourceState = MetadataRootId,
                    },
                };
                return new BrowseResult { Categories = views };
            }

            node = ShowMetadataView ? MetadataRootId : FolderRootId;
        }

        // Metadata tree: the artist root and any artist:/album: node route to the metadata browser.
        if (node == MetadataRootId
            || node.StartsWith(ArtistPrefix, StringComparison.Ordinal)
            || node.StartsWith(AlbumPrefix, StringComparison.Ordinal))
            return BrowseByMetadata(node, catalog);

        // Folder tree — a file-explorer style drill-down: each level shows its immediate subfolders
        // and immediate files, one level at a time.
        if (node == FolderRootId)
        {
            var existingFolders = _folders.Where(Directory.Exists).ToList();

            // A single configured folder is the natural root — browse straight into it. With several
            // configured folders, list each as a top-level node (then drill into its own tree).
            if (existingFolders.Count == 1)
                return BrowseFolder(existingFolders[0], catalog);

            var roots = existingFolders
                .Select(f => new SourceCategory
                {
                    SourceInstanceId = InstanceId,
                    CategoryId = f,
                    Title = FolderLabel(f),
                    ThumbnailUrl = FirstThumbnail(catalog.Where(e => PathIsUnder(e.Path, f))),
                    HasSubCategories = true,
                    SourceState = f,
                })
                .ToList();
            return new BrowseResult { Categories = roots };
        }

        // Any other node in the folder view is a directory path — show its immediate contents.
        return BrowseFolder(node, catalog);
    }

    /// <summary>
    /// Lists the immediate contents of one directory (file-explorer style): subfolders that contain
    /// indexed media anywhere beneath them become drill-in sub-categories, and media files sitting
    /// directly in this directory become leaf items. Derived from the catalog, so it reflects exactly
    /// what was indexed (and honors the recursive setting).
    /// </summary>
    private BrowseResult BrowseFolder(string dir, List<MediaEntry> catalog)
    {
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);

        // First path segment below this directory → the full child-directory path (dedup, keep label).
        var childDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<MediaEntry>();

        foreach (var e in catalog)
        {
            var full = Path.GetFullPath(e.Path);
            var parent = Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar);
            if (parent is null) continue;

            if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            {
                files.Add(e); // sits directly in this directory
            }
            else if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var rel = full[(root.Length + 1)..];
                var sep = rel.IndexOf(Path.DirectorySeparatorChar);
                if (sep > 0)
                {
                    var childName = rel[..sep];
                    childDirs[Path.Combine(root, childName)] = childName;
                }
            }
        }

        var categories = childDirs
            .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = kv.Key,
                Title = kv.Value,
                ThumbnailUrl = FirstThumbnail(catalog.Where(e => PathIsUnder(e.Path, kv.Key))),
                HasSubCategories = true,
                SourceState = kv.Key,
            })
            .ToList();

        var items = files
            .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .Select(ToSourceItem)
            .ToList();

        return new BrowseResult { Categories = categories, Items = items };
    }

    // Node id prefixes for the metadata browse tree (Artist → Album → Tracks).
    private const string ArtistPrefix = "artist:";
    private const string AlbumPrefix = "album:";
    private const char AlbumSeparator = '\u001F';
    private const string UnknownArtist = "Unknown Artist";
    private const string UnknownAlbum = "Unknown Album";

    /// <summary>
    /// Browses the catalog arranged by tag metadata: root lists artists, an artist lists its albums,
    /// an album lists its tracks (ordered by track number). Tracks with no artist/album tags fall
    /// under "Unknown Artist"/"Unknown Album" so nothing is hidden. Category tiles (artists/albums)
    /// inherit a thumbnail from the first item under them (first-thumbnail-wins).
    /// </summary>
    private BrowseResult BrowseByMetadata(string node, List<MediaEntry> catalog)
    {
        if (node == MetadataRootId)
        {
            var artists = catalog
                .GroupBy(e => e.Artist is { Length: > 0 } a ? a : UnknownArtist,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new SourceCategory
                {
                    SourceInstanceId = InstanceId,
                    CategoryId = ArtistPrefix + g.Key,
                    Title = g.Key,
                    ThumbnailUrl = FirstThumbnail(g),
                    HasSubCategories = true,
                    SourceState = ArtistPrefix + g.Key,
                })
                .ToList();
            return new BrowseResult { Categories = artists };
        }

        if (node.StartsWith(ArtistPrefix, StringComparison.Ordinal))
        {
            var artist = node[ArtistPrefix.Length..];
            var albums = catalog
                .Where(e => ArtistOf(e).Equals(artist, StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => e.Album is { Length: > 0 } a ? a : UnknownAlbum,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new SourceCategory
                {
                    SourceInstanceId = InstanceId,
                    CategoryId = AlbumPrefix + artist + AlbumSeparator + g.Key,
                    Title = g.Key,
                    ThumbnailUrl = FirstThumbnail(g),
                    HasSubCategories = false,
                    SourceState = AlbumPrefix + artist + AlbumSeparator + g.Key,
                })
                .ToList();
            return new BrowseResult { Categories = albums };
        }

        if (node.StartsWith(AlbumPrefix, StringComparison.Ordinal))
        {
            var payload = node[AlbumPrefix.Length..];
            var sep = payload.IndexOf(AlbumSeparator);
            var artist = sep >= 0 ? payload[..sep] : payload;
            var album = sep >= 0 ? payload[(sep + 1)..] : "";
            var tracks = catalog
                .Where(e => ArtistOf(e).Equals(artist, StringComparison.OrdinalIgnoreCase) &&
                            AlbumOf(e).Equals(album, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Track == 0 ? uint.MaxValue : e.Track)
                .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                .Select(ToSourceItem)
                .ToList();
            return new BrowseResult { Items = tracks };
        }

        return new BrowseResult { Items = catalog.Select(ToSourceItem).ToList() };
    }

    private static string ArtistOf(MediaEntry e) => e.Artist is { Length: > 0 } a ? a : UnknownArtist;
    private static string AlbumOf(MediaEntry e) => e.Album is { Length: > 0 } a ? a : UnknownAlbum;

    /// <summary>
    /// First-thumbnail-wins inheritance for a category tile: returns the thumbnail of the first
    /// entry (in catalog order) under this node that has one on disk, or null. Cheap and predictable
    /// — a track's thumb becomes its album's, and an album's first thumb becomes the artist's.
    /// </summary>
    private static string? FirstThumbnail(IEnumerable<MediaEntry> entries)
        => entries.FirstOrDefault(e => e.ThumbnailPath is { Length: > 0 } p && File.Exists(p))?.ThumbnailPath;

    private static string FolderLabel(string folder)
        => Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : folder;

    // ── ITextSearchCapable ─────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var catalog = await EnsureCatalogAsync(ct);
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
        // Serve duration/year from the cached catalog (populated during rescan). If the item isn't
        // in the catalog yet, return empty-but-non-null so the host still treats it as valid.
        var path = item.SourceState as string ?? item.ItemId;
        MediaEntry? entry;
        lock (_gate)
            entry = _catalog.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

        var published = entry is { Year: > 0 and <= 9999 }
            ? new DateTimeOffset(new DateTime((int)entry.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            : (DateTimeOffset?)null;

        return Task.FromResult<SourceMetadata?>(
            new SourceMetadata(entry?.Duration, null, [], published));
    }

    // ── IGaplessCapable ────────────────────────────────────────────────────────

    public string? GetGaplessStreamUrl(SourceItem item)
    {
        // Local audio files are stable, direct paths — ideal for gapless priming.
        var path = item.SourceState as string ?? item.ItemId;
        return item.IsAudioOnly && File.Exists(path) ? path : null;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private SourceItem ToSourceItem(MediaEntry e)
    {
        // Prefer a metadata subtitle (artist / artist — album) when tags are present; otherwise fall
        // back to the containing folder name so folder-organized browsing still reads well.
        string? subtitle = (e.Artist, e.Album) switch
        {
            ({ Length: > 0 } artist, { Length: > 0 } album) => $"{artist} — {album}",
            ({ Length: > 0 } artist, _) => artist,
            (_, { Length: > 0 } album) => album,
            _ => Path.GetFileName(e.Folder.TrimEnd(Path.DirectorySeparatorChar)),
        };

        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = e.Path,
            Title = e.Title,
            Subtitle = subtitle,
            Duration = e.Duration,
            ThumbnailUrl = e.ThumbnailPath is { Length: > 0 } thumb && File.Exists(thumb) ? thumb : null,
            IsAudioOnly = e.IsAudioOnly,
            SourceState = e.Path,
        };
    }

    private static bool PathIsUnder(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(folder);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var v) ? v : null;

    /// <summary>One catalog entry (a media file on disk).</summary>
    /// <remarks>
    /// <see cref="FileSize"/> and <see cref="LastWriteTimeUtc"/> drive incremental rescans (an entry
    /// is reused as-is when both still match on disk). Phase 2/3 fields (metadata, thumbnail path)
    /// can be added here and will ride along in the cache automatically.
    /// </remarks>
    private sealed record MediaEntry(
        string Path,
        string Title,
        string Folder,
        bool IsAudioOnly,
        long FileSize = 0,
        DateTimeOffset LastWriteTimeUtc = default,
        string? Artist = null,
        string? Album = null,
        uint Track = 0,
        uint Year = 0,
        TimeSpan? Duration = null,
        bool MetadataRead = false,
        string? ThumbnailPath = null,
        bool ThumbnailTried = false);
}
