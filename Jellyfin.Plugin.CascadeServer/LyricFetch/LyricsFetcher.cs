using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CascadeServer.LyricStore;
using Jellyfin.Plugin.CascadeServer.Status;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeServer.LyricFetch;

/// <summary>
/// Fetches lyrics for a single track and stores them. Two separate steps:
///
/// Fetch (<see cref="FetchAsync"/>): Kugou for word-level karaoke, LRCLIB for line-synced
/// and plain. Sources are queried independently, and in parallel: finding karaoke on Kugou
/// does not stop the LRCLIB lookup. The goal is preservation, so we keep everything each
/// source will give us.
///
/// Store (<see cref="EnsureAsync"/>): by default, sidecars next to the audio file
/// (see <see cref="Sidecars"/>). With DisableSidecarWrites on, the
/// <see cref="LiveLyricsCache"/> in the data folder instead.
///
/// Nothing already on disk is ever overwritten - a source is only queried when its own
/// output file is missing. That keeps hand-edited lyrics safe and stops repeat runs from
/// re-downloading the whole library.
///
/// Shared by the scheduled download task, the status Recheck, and the lyrics GET so they
/// cannot drift apart.
/// </summary>
public class LyricsFetcher
{
    private readonly ILogger _logger;
    private readonly KugouLyricsClient _kugou;
    private readonly LrclibClient _lrclib;
    private readonly SpicyLyricsClient _spicy;
    private readonly LiveLyricsCache _cache;
    private readonly SpicyLyricsCache _spicyCache;

    /// <summary>Initialises a new instance of <see cref="LyricsFetcher"/>.</summary>
    public LyricsFetcher(ILogger logger, IApplicationPaths appPaths)
    {
        _logger = logger;
        _kugou = new KugouLyricsClient(logger);
        _lrclib = new LrclibClient(logger);
        _spicy = new SpicyLyricsClient(logger);
        _cache = new LiveLyricsCache(appPaths);
        _spicyCache = new SpicyLyricsCache(appPaths);
    }

    /// <summary>What one fetch found. <c>QueriedNetwork</c> is false when nothing was asked.</summary>
    public record FetchedLyrics(string? Karaoke, string? Synced, string? Plain, bool QueriedNetwork);

    /// <summary>
    /// What one <see cref="EnsureAsync"/> call stored, plus the resulting status row.
    /// <c>QueriedNetwork</c> is false when everything was already stored and no source was
    /// contacted, which lets callers skip their inter-track rate-limit delay - on a fully
    /// populated library a repeat run is otherwise almost entirely sleeping.
    /// </summary>
    public record EnsureResult(
        LyricsStatusEntry Entry, bool WroteKaraoke, bool WroteSynced, bool WrotePlain, bool QueriedNetwork);

    /// <summary>The HttpClient every fetch path uses. Caller disposes it.</summary>
    public static HttpClient CreateHttpClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Cascade/1.0)");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    /// <summary>Queries Kugou and/or LRCLIB. Writes nothing.</summary>
    public async Task<FetchedLyrics> FetchAsync(
        Audio item, HttpClient client, bool wantKaraoke, bool wantLrclib, CancellationToken ct)
    {
        var (title, artist) = TitleArtist(item);

        // Without a title and artist there is nothing to search on.
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || !(wantKaraoke || wantLrclib))
            return new FetchedLyrics(null, null, null, false);

        var album = item.Album ?? string.Empty;
        var duration = item.RunTimeTicks.HasValue
            ? (int)(item.RunTimeTicks.Value / TimeSpan.TicksPerSecond)
            : 0;

        var karaokeTask = wantKaraoke
            ? _kugou.FetchKaraokeAsync(client, title, artist, duration * 1000, ct)
            : Task.FromResult<string?>(null);
        var lrclibTask = wantLrclib
            ? _lrclib.FetchAsync(client, title, artist, album, duration, ct)
            : Task.FromResult<(string?, string?)>((null, null));

        await Task.WhenAll(karaokeTask, lrclibTask);
        var (synced, plain) = lrclibTask.Result;

        return new FetchedLyrics(
            karaokeTask.Result,
            string.IsNullOrWhiteSpace(synced) ? null : synced,
            string.IsNullOrWhiteSpace(plain) ? null : plain,
            true);
    }

    /// <summary>
    /// Returns the cached fetch for <paramref name="item"/>, fetching and caching it first
    /// when there is no fresh entry or <paramref name="force"/> is set. Sources whose
    /// sidecar already exists are not queried.
    /// </summary>
    public async Task<(CachedLyrics Lyrics, bool QueriedNetwork)> GetOrFetchCachedAsync(
        Audio item, HttpClient client, bool force, CancellationToken ct)
    {
        if (!force && _cache.Get(item.Id) is { } cached) return (cached, false);

        var files = Sidecars.For(item.Path);
        var fetched = await FetchAsync(item, client, !File.Exists(files.Slrc), !File.Exists(files.Lrc), ct);
        var entry = new CachedLyrics(fetched.Karaoke, fetched.Synced, fetched.Plain, DateTime.UtcNow);
        _cache.Put(item.Id, entry);
        return (entry, fetched.QueriedNetwork);
    }

    /// <summary>
    /// Fetches whatever is missing for <paramref name="item"/>, stores it (sidecars, or the
    /// data-dir cache with DisableSidecarWrites on) and returns the status entry describing
    /// what is available afterwards. <paramref name="force"/> bypasses a fresh cache entry;
    /// existing sidecars are never refetched.
    /// </summary>
    public async Task<EnsureResult> EnsureAsync(Audio item, HttpClient client, bool force, CancellationToken ct)
    {
        var files = Sidecars.For(item.Path);
        var (title, artist) = TitleArtist(item);
        var hadKaraoke = File.Exists(files.Slrc);

        if (Plugin.Config.DisableSidecarWrites)
        {
            var (cached, queried) = await GetOrFetchCachedAsync(item, client, force, ct);
            var entry = BuildEntry(title, artist, hadKaraoke || cached.Karaoke is not null, files, cached);
            return new EnsureResult(
                entry,
                queried && cached.Karaoke is not null,
                queried && cached.Synced is not null,
                queried && cached.Plain is not null,
                queried);
        }

        var hadSynced = File.Exists(files.Lrc);
        var fetched = await FetchAsync(item, client, !hadKaraoke, !hadSynced, ct);
        var (wroteKaraoke, wroteSynced, wrotePlain) = await WriteSidecarsAsync(files, fetched, title, artist, ct);

        return new EnsureResult(
            BuildEntry(title, artist, hadKaraoke || fetched.Karaoke is not null, files, null),
            wroteKaraoke, wroteSynced, wrotePlain, fetched.QueriedNetwork);
    }

    /// <summary>
    /// SpicyLyrics, when a key is set and the item has a Spotify id: the cached raw response,
    /// or a fresh one (then cached for 25 days). <c>null</c> otherwise, including on any miss.
    /// </summary>
    public async Task<string?> TrySpicyAsync(BaseItem item, HttpClient client, CancellationToken ct)
    {
        var key = Plugin.Config.SpicyLyricsSecretKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        var spotifyId = SpotifyIdLookup.Find(item);
        if (!SpicyLyricsClient.IsValidTrackId(spotifyId)) return null;

        if (_spicyCache.Get(spotifyId!) is { } cached) return cached;
        if (SpicyLyricsCache.IsRecentMiss(spotifyId!)) return null;

        var result = await _spicy.FetchAsync(client, key, spotifyId!, ct);
        if (result.Status == SpicyLyricsStatus.Hit && result.RawJson is not null)
        {
            _spicyCache.Put(spotifyId!, result.RawJson);
            return result.RawJson;
        }

        if (result.Status == SpicyLyricsStatus.Miss) SpicyLyricsCache.MarkMiss(spotifyId!);
        return null;
    }

    private async Task<(bool Karaoke, bool Synced, bool Plain)> WriteSidecarsAsync(
        Sidecars files, FetchedLyrics fetched, string title, string artist, CancellationToken ct)
    {
        bool karaoke = false, synced = false, plain = false;

        if (fetched.Karaoke is not null && !File.Exists(files.Slrc))
        {
            await File.WriteAllTextAsync(files.Slrc, fetched.Karaoke, ct);
            _logger.LogDebug("Saved karaoke sidecar for \"{Title}\" by {Artist}", title, artist);
            karaoke = true;
        }

        // A track that already has only a .txt is still re-queried (FetchAsync ran because the
        // .lrc is missing), since LRCLIB may have gained a synced version since the last run.
        if (fetched.Synced is not null && !File.Exists(files.Lrc))
        {
            await File.WriteAllTextAsync(files.Lrc, fetched.Synced, ct);
            _logger.LogDebug("Saved synced sidecar for \"{Title}\" by {Artist}", title, artist);
            synced = true;
        }
        else if (fetched.Synced is null && fetched.Plain is not null && !File.Exists(files.Txt))
        {
            await File.WriteAllTextAsync(files.Txt, fetched.Plain, ct);
            _logger.LogDebug("Saved plain sidecar for \"{Title}\" by {Artist}", title, artist);
            plain = true;
        }

        return (karaoke, synced, plain);
    }

    private static (string Title, string Artist) TitleArtist(Audio item)
        => (item.Name ?? string.Empty,
            item.AlbumArtists?.FirstOrDefault() ?? item.Artists?.FirstOrDefault() ?? string.Empty);

    private static LyricsStatusEntry BuildEntry(
        string title, string artist, bool kugouAvailable, Sidecars files, CachedLyrics? cached)
        => new()
        {
            Name = title,
            Artist = artist,
            KugouAvailable = kugouAvailable,
            HasKaraoke = File.Exists(files.Slrc) || cached?.Karaoke is not null,
            HasSynced = File.Exists(files.Lrc) || cached?.Synced is not null,
            HasPlain = File.Exists(files.Txt) || cached?.Plain is not null,
            Cached = cached?.Any ?? false,
            LastChecked = DateTime.UtcNow,
        };
}
