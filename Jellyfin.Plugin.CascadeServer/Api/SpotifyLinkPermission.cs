using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.CascadeServer.Api;

/// <summary>
/// Who may link a song to a Spotify track for the whole server: administrators, and the
/// users an administrator picked in the plugin settings. Everyone else links for
/// themselves in Cascade, which keeps those links on their own computer.
/// </summary>
public static class SpotifyLinkPermission
{
    /// <summary>Whether the user making this request may set server-wide links.</summary>
    public static async Task<bool> ServerWideAsync(IAuthorizationContext auth, HttpContext context)
    {
        var info = await auth.GetAuthorizationInfo(context);
        if (info.User is null) return false;
        if (info.User.HasPermission(PermissionKind.IsAdministrator)) return true;
        var allowed = Plugin.Config.ServerWideSpotifyLinkUsers ?? [];
        return allowed.Any(id => Guid.TryParse(id, out var g) && g == info.UserId);
    }
}
