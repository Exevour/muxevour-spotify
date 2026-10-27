# Spotify for Muxevour (plugin)

Plays Spotify through [Muxevour](https://github.com/Exevour/muxevour)'s own sound engine, with your liked songs, saved
albums and playlists read live into Muxevour's Home and Your Library. **Unofficial**: not made, supported or approved by
Spotify; it may stop working whenever Spotify changes something. Buying or using Muxevour pays for the player, not for Spotify.

## Install

1. Muxevour 0.12.0 or newer.
2. Put `Muxevour.Streaming.dll` in Muxevour's Plugins folder (Muxevour → Settings → Plugins → Open), and start Muxevour again.
3. Settings → Library → Mode: **Spotify**.

A plugin is built for one version of Muxevour: after a Muxevour update, get the plugin's matching release
(Settings → Plugins says when it can't be loaded).

The plugin plays Spotify through Muxevour's own engine - your equalizer, volume, exclusive mode and ASIO apply - with your Spotify playlists, liked songs and albums in Muxevour's Home and Your Library.

## What you need

- **Spotify Premium** (Standard, Platinum, Student...). Spotify Free doesn't work.
- **librespot**, a free, unofficial, open-source Spotify Connect speaker. It isn't part of Muxevour and isn't made by Spotify; Muxevour fetches it for you after you agree to its terms (personal use, your own account, at your own risk).
- A free **Spotify Client ID** of your own, for Muxevour's buttons and your library (about two minutes, below).

## Setting it up

1. Settings → Library → Mode: **Spotify**. Muxevour restarts in Spotify's mode.
2. Settings → Library → Spotify → **Get librespot** → read and agree. Muxevour downloads it and starts it.
3. Your browser opens Spotify's sign-in page: sign in and approve. (Spotify's own page: Muxevour never sees your password.) This happens once; it's remembered on this PC.
4. Make your Client ID - easiest with Settings → Library → **Set up (guided)**, which opens Spotify's page, copies what to fill in and picks the Client ID up by itself. By hand: go to **developer.spotify.com/dashboard**, sign in, **Create app**. Any name and description. **Redirect URI:** http://127.0.0.1:5589/callback exactly. Tick **Web API**. Save.
5. Open the app's **Settings**, copy the **Client ID** (not the secret), and paste it in Muxevour: Settings → Library → Muxevour's Spotify controls → Client ID.
6. Click **Connect** and approve on Spotify's page. Muxevour then brings in your liked songs, saved albums and playlists, and its own buttons work on Spotify.

> Each person needs their own Client ID: Spotify lets one app serve only 25 people while it's in development mode.


## Playing

Pick anything in Muxevour, or choose **Muxevour** as the speaker in the Spotify app on your phone or PC. Right-click the output button in Muxevour for your Spotify devices, to move the music to another one.
- Spotify's **+** (where the stars are with files) adds a song to your Liked Songs on Spotify; it turns into Spotify's green tick.
- **Play on Spotify** (the now-playing card, or right-click a song) opens it in the Spotify app.
- As Spotify asks of apps using its content, its covers are never blurred or moved behind things: behind a Spotify song the moving background is made from its cover's colours instead.
- The top-left mark pulses while you're in Spotify's mode.

## Good to know

- Spotify sends 320 kbps ("Very High") to speakers like Muxevour's, whatever the plan: lossless stays in Spotify's own apps.
- Spotify's transitions (Crossfade, Automix, Mix) and AI DJ play only in Spotify's own apps, never on a speaker like Muxevour's.
- Spotify's own made-for-you mixes (Daily Mix and the like) can't be played by unofficial speakers; your playlists, albums and liked songs can.
- Local and Spotify keep their own plays and queues; Stats adds both together.

## Is my Spotify account safe?

Honestly: very probably, but nobody can promise it. There's no real number to give - Spotify publishes nothing about it - so here is what is known.
- **Your Client ID** (the buttons and your library) is Spotify's own official way for apps to work with your account. That part is allowed.
- **librespot** (the sound) is unofficial, and Spotify's terms don't allow unofficial players. Players built on it (Raspotify, spotifyd and others) have been used for years with no known wave of bans for normal listening on Premium.
- The accounts that do get into trouble are the ones using tools that **download** songs. Muxevour only plays: it never saves Spotify's music, at the normal 320 kbps, on your own Premium account.
- The likeliest bad day is Spotify changing something so librespot stops working for a while - not losing your account.

> That's why Muxevour asks you to agree before it fetches librespot: personal use, your own account, at your own risk. If that doesn't sit right with you, keep to Local mode - everything else in Muxevour works the same.


## Known issues

- Spotify's own made-for-you mixes (Daily Mix, Discover Weekly and the like) don't play through librespot; your playlists, albums and liked songs do.
- Each user needs their own Spotify Client ID (an app in Spotify's development mode serves at most 25 people).
- Apps can't read the Spotify plan any more, so Free (which doesn't play) can't be told from Premium beforehand.
- Liking songs very quickly one after another can leave a heart out of step until the next reading.
- After a song from a search plays, Coming up next can show a song from before it.

## Build

`build.bat` (Windows, .NET Framework 4.8's csc), against a built `Muxevour.exe` next door (`..\..\XVR Player\Muxevour.exe`).

## Credits and trademarks

- **Spotify** and the Spotify icon are trademarks of Spotify AB, used here as Spotify's design guidelines ask, unaltered.
- **librespot** by the librespot-org contributors (MIT); not part of this plugin, downloaded only after you agree to its terms.
- Spotify Web API, with your own Client ID.

## Licence

MIT (see LICENSE.txt) - except Spotify's logos and marks in Resources\, which are Spotify AB's trademarks.
