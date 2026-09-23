using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CascadeServer.LyricFetch;

/// <summary>
/// Fetches word-level karaoke lyrics from Kugou (KRC format) and converts them to Enhanced
/// LRC. Shared by the scheduled download task and the on-demand status recheck endpoint.
/// </summary>
public partial class KugouLyricsClient
{
    private static readonly byte[] KrcKey =
        [0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69];

    private readonly ILogger _logger;

    /// <summary>Initialises a new instance of <see cref="KugouLyricsClient"/>.</summary>
    public KugouLyricsClient(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Maximum number of search candidates to try before giving up. Kugou often lists
    /// a mix of karaoke (KRC) and plain-LRC-only entries under the same search hit - the top
    /// result isn't guaranteed to have word-level content, so we fall through the list.</summary>
    private const int MaxCandidatesToTry = 5;

    /// <summary>Searches Kugou for a track and returns Enhanced LRC karaoke lyrics, or <c>null</c> if unavailable.</summary>
    public async Task<string?> FetchKaraokeAsync(
        HttpClient client, string title, string artist, int durationMs, CancellationToken ct)
    {
        try
        {
            // 1. Search
            var keyword   = $"{artist} - {title}";
            var searchUrl = "http://lyrics.kugou.com/search?ver=1&man=yes&client=pc" +
                            $"&keyword={Uri.EscapeDataString(keyword)}&duration={durationMs}";

            using var sRes  = await client.GetAsync(searchUrl, ct);
            if (!sRes.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Kugou search HTTP {Status} for \"{Title}\" by {Artist}", sRes.StatusCode, title, artist);
                return null;
            }

            await using var sStream = await sRes.Content.ReadAsStreamAsync(ct);
            using var sDoc  = await JsonDocument.ParseAsync(sStream, cancellationToken: ct);

            if (!sDoc.RootElement.TryGetProperty("candidates", out var candidates) ||
                candidates.GetArrayLength() == 0)
            {
                _logger.LogInformation(
                    "Kugou returned no candidates for \"{Title}\" by {Artist} (keyword \"{Keyword}\")",
                    title, artist, keyword);
                return null;
            }

            // 2. Try candidates in order until one yields usable word-level KRC content.
            var candidateCount = Math.Min(candidates.GetArrayLength(), MaxCandidatesToTry);
            for (var i = 0; i < candidateCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = candidates[i];
                var id        = candidate.GetProperty("id").GetRawText().Trim('"');
                var accessKey = candidate.GetProperty("accesskey").GetString() ?? string.Empty;

                var dlUrl = "http://lyrics.kugou.com/download?ver=1&client=pc" +
                            $"&id={id}&accesskey={Uri.EscapeDataString(accessKey)}&fmt=krc&charset=utf8";

                using var dRes = await client.GetAsync(dlUrl, ct);
                if (!dRes.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "Kugou download HTTP {Status} for candidate {Index} of \"{Title}\"", dRes.StatusCode, i, title);
                    continue;
                }

                await using var dStream = await dRes.Content.ReadAsStreamAsync(ct);
                using var dDoc  = await JsonDocument.ParseAsync(dStream, cancellationToken: ct);

                if (!dDoc.RootElement.TryGetProperty("content", out var contentEl))
                {
                    _logger.LogInformation(
                        "Kugou download response had no content field for candidate {Index} of \"{Title}\"", i, title);
                    continue;
                }

                var b64 = contentEl.GetString();
                if (string.IsNullOrWhiteSpace(b64))
                {
                    _logger.LogInformation(
                        "Kugou download content was empty for candidate {Index} of \"{Title}\"", i, title);
                    continue;
                }

                // 3. Decrypt: skip 4-byte 'krc1' magic, XOR with key, zlib inflate
                var encrypted = Convert.FromBase64String(b64);
                var raw       = encrypted.AsSpan(4);
                var decrypted = new byte[raw.Length];
                for (var j = 0; j < raw.Length; j++)
                    decrypted[j] = (byte)(raw[j] ^ KrcKey[j % 16]);

                string krcText;
                using (var ms  = new MemoryStream(decrypted))
                using (var zlib = new ZLibStream(ms, CompressionMode.Decompress))
                using (var sr  = new StreamReader(zlib, Encoding.UTF8))
                    krcText = await sr.ReadToEndAsync(ct);

                // 4. Convert KRC → Enhanced LRC
                var enhanced = KrcToEnhancedLrc(krcText);
                if (string.IsNullOrWhiteSpace(enhanced))
                {
                    _logger.LogInformation(
                        "Kugou candidate {Index} of \"{Title}\" had no word-level lines after conversion, trying next", i, title);
                    continue;
                }

                return enhanced;
            }

            _logger.LogInformation(
                "Exhausted {Count} Kugou candidate(s) for \"{Title}\" by {Artist} with no usable karaoke lyrics",
                candidateCount, title, artist);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Kugou fetch failed for \"{Title}\"", title);
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
}
