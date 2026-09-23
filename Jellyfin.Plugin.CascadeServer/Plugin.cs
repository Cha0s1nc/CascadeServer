using System;
using System.Collections.Generic;
using Jellyfin.Plugin.CascadeServer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CascadeServer;

/// <summary>
/// Cascade Server: the Jellyfin-side companion to the Cascade music player.
/// Today that is lyrics: fetching them from Kugou and LRCLIB, and storing
/// word-level karaoke (.slrc) server-side so every user on the server shares it.
/// Was "Cascade Lyrics" before 2.0.0.0; the GUID is unchanged so it installs as
/// an update.
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
    public override string Name => "Cascade Server";

    /// <inheritdoc/>
    public override Guid Id => Guid.Parse("a8b9c0d1-e2f3-4a5b-6c7d-8e9f0a1b2c3d");

    /// <inheritdoc/>
    public override string Description =>
        "Server companion for the Cascade music player: fetches lyrics (Kugou karaoke, LRCLIB synced) " +
        "and stores word-level timed lyrics server-side so they are shared across all users, with more to come.";

    /// <inheritdoc/>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "cascadeserver",
            DisplayName = "Cascade Server",
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.status.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "queue_music",
        };
    }
}
