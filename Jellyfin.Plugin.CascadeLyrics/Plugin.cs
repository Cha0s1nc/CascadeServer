using System;
using System.Collections.Generic;
using Jellyfin.Plugin.CascadeLyrics.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CascadeLyrics;

/// <summary>
/// Cascade Lyrics plugin — stores enhanced karaoke lyrics (.slrc) server-side
/// so per-word timing is shared across all users on the Jellyfin server.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initialises a new instance of <see cref="Plugin"/>.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the singleton plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc/>
    public override string Name => "Cascade Lyrics";

    /// <inheritdoc/>
    public override Guid Id => Guid.Parse("a8b9c0d1-e2f3-4a5b-6c7d-8e9f0a1b2c3d");

    /// <inheritdoc/>
    public override string Description =>
        "Enhanced karaoke lyrics storage and retrieval for the Cascade music player. " +
        "Stores word-level timed lyrics server-side so they are shared across all users.";

    /// <inheritdoc/>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "cascadelyricsstatus",
            DisplayName = "Cascade Lyrics",
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.status.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "queue_music",
        };
    }
}
