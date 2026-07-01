using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeLyrics.ScheduledTasks;

/// <summary>
/// Scheduled task that iterates all audio items in the library and downloads
/// lyrics into sidecar files next to the audio file:
///   {audioFile}.slrc — karaoke (word-level Enhanced LRC, from Kugou KRC)
///   {audioFile}.lrc  — synced  (line-level LRC, from LRCLIB)
///
/// Items that already have either sidecar are skipped entirely.
/// If karaoke is found, synced is not fetched (karaoke is a superset).
/// </summary>
public partial class DownloadLyricsTask : IScheduledTask
{
    // Kugou KRC decryption key
    private static readonly byte[] KrcKey =
        [0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69];

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DownloadLyricsTask> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initialises a new instance of <see cref="DownloadLyricsTask"/>.</summary>
    public DownloadLyricsTask(
        ILibraryManager libraryManager,
        ILogger<DownloadLyricsTask> logger,
        IHttpClientFactory httpClientFactory)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc/>
    public string Name => "Download Cascade Lyrics";

    /// <inheritdoc/>
    public string Key => "CascadeLyricsDownload";

    /// <inheritdoc/>
    public string Description =>
        "Downloads karaoke lyrics from Kugou (.slrc) and synced lyrics from LRCLIB (.lrc) " +
        "as sidecar files next to each audio file. Skips items that already have a sidecar.";

    /// <inheritdoc/>
    public string Category => "Cascade Lyrics";

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Weekly on Sunday at 3am
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        };
    }

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true,
        };

        var items = _libraryManager.GetItemList(query)
            .OfType<Audio>()
            .Where(a => a.Path is not null)
            .ToList();

        var total = items.Count;
        if (total == 0)
        {
            progress.Report(100);
            return;
        }

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Cascade/1.0)");
        httpClient.Timeout = TimeSpan.FromSeconds(15);

        var completed    = 0;
        var skipped      = 0;
        var savedKaraoke = 0;
        var savedSynced  = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var (k, s, skip) = await ProcessItemAsync(item, httpClient, cancellationToken);
                if (skip) skipped++;
                if (k) savedKaraoke++;
                if (s) savedSynced++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to process lyrics for {Title}", item.Name);
            }

            completed++;
            progress.Report(100.0 * completed / total);

            // Be polite to the APIs
            await Task.Delay(300, cancellationToken);
        }

        _logger.LogInformation(
            "Cascade lyrics download complete. Saved {Karaoke} karaoke, {Synced} synced, " +
            "skipped {Skipped} (already had sidecar) out of {Total} tracks.",
            savedKaraoke, savedSynced, skipped, total);
    }

    private async Task<(bool savedKaraoke, bool savedSynced, bool skipped)> ProcessItemAsync(
        Audio item, HttpClient httpClient, CancellationToken ct)
    {
        var slrcSidecar = Path.ChangeExtension(item.Path, ".slrc");
        var lrcSidecar  = Path.ChangeExtension(item.Path, ".lrc");

        // Skip entirely if any sidecar already exists
        if (File.Exists(slrcSidecar) || File.Exists(lrcSidecar))
            return (false, false, true);

        var title    = item.Name ?? string.Empty;
        var artist   = item.AlbumArtists?.FirstOrDefault()
                    ?? item.Artists?.FirstOrDefault()
                    ?? string.Empty;
        var album    = item.Album ?? string.Empty;
        var duration = item.RunTimeTicks.HasValue
            ? (int)(item.RunTimeTicks.Value / TimeSpan.TicksPerSecond)
            : 0;

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            return (false, false, false);

        var durationMs = duration * 1000;

        // ── 1. Try Kugou for karaoke (.slrc) ─────────────────────────────────
        var karaoke = await FetchKugouAsync(httpClient, title, artist, durationMs, ct);
        if (karaoke is not null)
        {
            await File.WriteAllTextAsync(slrcSidecar, karaoke, ct);
            _logger.LogDebug("Saved karaoke sidecar for \"{Title}\" by {Artist}", title, artist);
            return (true, false, false);
        }

        // ── 2. Try LRCLIB for synced (.lrc) — only if no karaoke found ───────
        var synced = await FetchLrclibAsync(httpClient, title, artist, album, duration, ct);
        if (synced is not null)
        {
            await File.WriteAllTextAsync(lrcSidecar, synced, ct);
            _logger.LogDebug("Saved synced sidecar for \"{Title}\" by {Artist}", title, artist);
            return (false, true, false);
        }

        return (false, false, false);
    }

    // ── Kugou ─────────────────────────────────────────────────────────────────

    private async Task<string?> FetchKugouAsync(
        HttpClient client, string title, string artist, int durationMs, CancellationToken ct)
    {
        try
        {
            // 1. Search
            var keyword   = $"{artist} - {title}";
            var searchUrl = "http://lyrics.kugou.com/search?ver=1&man=yes&client=pc" +
                            $"&keyword={Uri.EscapeDataString(keyword)}&duration={durationMs}";

            using var sRes  = await client.GetAsync(searchUrl, ct);
            if (!sRes.IsSuccessStatusCode) return null;

            await using var sStream = await sRes.Content.ReadAsStreamAsync(ct);
            using var sDoc  = await JsonDocument.ParseAsync(sStream, cancellationToken: ct);

            if (!sDoc.RootElement.TryGetProperty("candidates", out var candidates) ||
                candidates.GetArrayLength() == 0)
                return null;

            var first     = candidates[0];
            var id        = first.GetProperty("id").GetRawText().Trim('"');
            var accessKey = first.GetProperty("accesskey").GetString() ?? string.Empty;

            // 2. Download
            var dlUrl = "http://lyrics.kugou.com/download?ver=1&client=pc" +
                        $"&id={id}&accesskey={Uri.EscapeDataString(accessKey)}&fmt=krc&charset=utf8";

            using var dRes  = await client.GetAsync(dlUrl, ct);
            if (!dRes.IsSuccessStatusCode) return null;

            await using var dStream = await dRes.Content.ReadAsStreamAsync(ct);
            using var dDoc  = await JsonDocument.ParseAsync(dStream, cancellationToken: ct);

            if (!dDoc.RootElement.TryGetProperty("content", out var contentEl))
                return null;

            var b64 = contentEl.GetString();
            if (string.IsNullOrWhiteSpace(b64)) return null;

            // 3. Decrypt: skip 4-byte 'krc1' magic, XOR with key, zlib inflate
            var encrypted = Convert.FromBase64String(b64);
            var raw       = encrypted.AsSpan(4);
            var decrypted = new byte[raw.Length];
            for (var i = 0; i < raw.Length; i++)
                decrypted[i] = (byte)(raw[i] ^ KrcKey[i % 16]);

            string krcText;
            using (var ms  = new MemoryStream(decrypted))
            using (var zlib = new ZLibStream(ms, CompressionMode.Decompress))
            using (var sr  = new StreamReader(zlib, Encoding.UTF8))
                krcText = await sr.ReadToEndAsync(ct);

            // 4. Convert KRC → Enhanced LRC
            var enhanced = KrcToEnhancedLrc(krcText);
            return string.IsNullOrWhiteSpace(enhanced) ? null : enhanced;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Kugou fetch failed for \"{Title}\"", title);
            return null;
        }
    }

    /// <summary>
    /// Converts decrypted KRC text to Enhanced LRC format.
    /// KRC line: [lineStartMs,lineDurMs]&lt;wordOffsetMs,wordDurMs,0&gt;text...
    /// Enhanced LRC line: [mm:ss.xx]&lt;mm:ss.xx&gt;word1 &lt;mm:ss.xx&gt;word2
    /// </summary>
    private static string KrcToEnhancedLrc(string krcText)
    {
        var lineRegex = LineRegex();
        var wordRegex = WordRegex();
        var sb = new StringBuilder();

        foreach (var rawLine in krcText.Split('\n'))
        {
            var line = rawLine.Trim();
            var lm = lineRegex.Match(line);
            if (!lm.Success) continue;   // skip [ti:], [ar:], [offset:] etc.

            var lineStartMs = long.Parse(lm.Groups[1].Value);
            var content     = lm.Groups[3].Value;

            var wordParts = new StringBuilder();
            var hasWords  = false;

            foreach (Match wm in wordRegex.Matches(content))
            {
                var wordText = wm.Groups[3].Value;
                if (string.IsNullOrEmpty(wordText)) continue;

                var wOffMs     = long.Parse(wm.Groups[1].Value);
                var wordStartMs = lineStartMs + wOffMs;
                wordParts.Append($"<{MsToLrc(wordStartMs)}>{wordText}");
                hasWords = true;
            }

            if (!hasWords) continue;

            sb.AppendLine($"[{MsToLrc(lineStartMs)}]{wordParts.ToString().TrimStart()}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string MsToLrc(long ms)
    {
        var totalSecs = ms / 1000.0;
        var m  = (int)(totalSecs / 60);
        var s  = totalSecs % 60;
        var cs = (int)Math.Round((s % 1) * 100);
        return $"{m:D2}:{(int)s:D2}.{cs:D2}";
    }

    [GeneratedRegex(@"^\[(\d+),(\d+)\](.*)$")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"<(\d+),(\d+),\d+>([^<]*)")]
    private static partial Regex WordRegex();

    // ── LRCLIB ────────────────────────────────────────────────────────────────

    private async Task<string?> FetchLrclibAsync(
        HttpClient client, string title, string artist, string album, int duration, CancellationToken ct)
    {
        try
        {
            var url = "https://lrclib.net/api/get"
                    + $"?track_name={Uri.EscapeDataString(title)}"
                    + $"&artist_name={Uri.EscapeDataString(artist)}"
                    + (album.Length > 0 ? $"&album_name={Uri.EscapeDataString(album)}" : string.Empty)
                    + (duration > 0    ? $"&duration={duration}"                       : string.Empty);

            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("syncedLyrics", out var el))
            {
                var val = el.GetString();
                return string.IsNullOrWhiteSpace(val) ? null : val;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LRCLIB fetch failed for \"{Title}\"", title);
        }

        return null;
    }
}
