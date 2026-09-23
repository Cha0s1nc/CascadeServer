using System.IO;

namespace Jellyfin.Plugin.CascadeServer.LyricStore;

/// <summary>
/// Paths of the lyrics files kept next to an audio file:
///   {audioFile}.slrc - karaoke, word-level Enhanced LRC
///   {audioFile}.lrc  - synced, line-level LRC
///   {audioFile}.txt  - plain, untimed text
/// </summary>
public readonly record struct Sidecars(string Slrc, string Lrc, string Txt)
{
    /// <summary>Gets the sidecar paths for an audio file.</summary>
    public static Sidecars For(string audioPath) => new(
        Path.ChangeExtension(audioPath, ".slrc"),
        Path.ChangeExtension(audioPath, ".lrc"),
        Path.ChangeExtension(audioPath, ".txt"));
}
