using System;
using System.IO;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.CascadeServer.LyricStore;

/// <summary>
/// The plugin's folder under Jellyfin's data path: {DataPath}/cascade-lyrics.
///
/// The name predates the rename to Cascade Server and is kept on purpose. It is
/// built from <see cref="IApplicationPaths.DataPath"/>, not from the assembly
/// name, so renaming the assembly never moved it; existing installs already
/// have lyrics and the status report here. Do not use the plugin's own
/// DataFolderPath instead: that is the install folder and is replaced on
/// every plugin update.
/// </summary>
public static class DataDir
{
    /// <summary>Gets the root folder, creating it if needed.</summary>
    public static string Root(IApplicationPaths appPaths)
    {
        var dir = Path.Combine(appPaths.DataPath, "cascade-lyrics");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Path of a data-dir lyrics file for an item. Read as a fallback by the GET, and
    /// written by the lyrics-editor POST when the media folder is read-only.
    /// </summary>
    public static string LyricsPath(IApplicationPaths appPaths, Guid itemId, string ext)
        => Path.Combine(appPaths.DataPath, "cascade-lyrics", $"{itemId:N}{ext}");
}
