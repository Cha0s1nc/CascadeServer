# Cascade Server

A Jellyfin plugin that works with the [Cascade](https://github.com/Cha0s1nc/Cascade-Project) music player. It fetches lyrics and keeps them on your server, so every Cascade user on it shares them:

- word-level karaoke lyrics from Kugou,
- synced and plain lyrics from LRCLIB,
- with your own SpicyLyrics key, SpicyLyrics' word-synced lyrics (with background vocals and held notes),
- a lyrics coverage page, and a scheduled task that fetches lyrics for the whole library.

Works with Jellyfin 10.11 and Jellyfin 12.

## Install

1. In Jellyfin, open **Dashboard → Plugins → Repositories** (called **Manage Repositories** in some versions) and add:

   ```
   https://raw.githubusercontent.com/Cha0s1nc/CascadeServer/main/manifest.json
   ```

2. Open **Catalog**, install **Cascade Server**, and restart Jellyfin.

Jellyfin picks the right build for your server version by itself, and offers updates the same way as for any other plugin.

## Settings

Open **Dashboard → Plugins → Cascade Server**.

- **Don't write fetched lyrics next to media files.** Keep fetched lyrics in Jellyfin's data folder instead of writing `.slrc`/`.lrc`/`.txt` files beside your audio.
- **SpicyLyrics secret key.** Optional. Get a key starting with `sl_sk_` from [developers.spicylyrics.org](https://developers.spicylyrics.org). Songs are matched to their Spotify track through ListenBrainz, which covers most popular music. SpicyLyrics results are kept for at most 25 days, as its terms require, and never written as files.
- **Who can link songs to Spotify for everyone.** When the automatic match finds nothing, or picks the wrong release, a Cascade user can paste the song's Spotify link. Links from administrators and the users ticked here apply to the whole server. Everyone else's links stay on their own computer and only affect what they see.

## What it depends on

- **ListenBrainz Labs** matches songs to Spotify tracks. It is an experimental service; if it changes or goes away, automatic matching stops working and Cascade falls back to its other lyric sources. Hand-made links keep working.
- **SpicyLyrics**, **Kugou** and **LRCLIB** are third-party services with their own terms and availability.

## Building

```
dotnet build Jellyfin.Plugin.CascadeServer -c Release -p:JellyfinTarget=10.11   # .NET 9
dotnet build Jellyfin.Plugin.CascadeServer -c Release -p:JellyfinTarget=12      # .NET 10
```

Releases are built and published by GitHub Actions from a version tag; see `.github/workflows/build.yml`.
