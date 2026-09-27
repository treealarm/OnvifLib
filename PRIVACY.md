# Privacy policy — OnvifLib.Gui

Applies to **OnvifLib.Gui**, the desktop ONVIF camera manager published from this repository
(GitHub Releases, the WiX MSI, and the winget package `treealarm.OnvifLib.Gui`), and to the
**OnvifLib** .NET library it is built on.

Last updated: 2026-09-27.

## Summary

- OnvifLib.Gui has **no telemetry, no analytics, no crash reporting, no accounts and no update
  check**. The developers never receive any data from the app.
- The app talks **only to the cameras you discover or add**, plus one optional download from GitHub
  that you start yourself (see [Network communication](#network-communication)).
- Everything it keeps is stored **locally, on your computer, under your user profile**. Camera
  passwords are kept only if you ask for it, and then only in encrypted form.

## What the app handles

| Data | Where it comes from | What happens to it |
|---|---|---|
| Camera addresses (IP, port, ONVIF service URL), display names | You type them in, or WS-Discovery finds them on your local network | Held in memory while the app runs; saved to `settings.json` so the device list survives a restart |
| Camera usernames | You type them in | Held in memory; saved to `settings.json` with the device list |
| Camera passwords | You type them in | Held in memory and sent only to that camera. Written to disk **only if you tick "Remember password"**, and then **encrypted** (see below) |
| Live video | RTSP stream from the camera | Decoded in memory for display. **Never recorded or written to disk** |
| Snapshots (JPEG) | The camera's snapshot URL | Shown in the window. Written to disk only when you click **Save…** on the Snapshot tab and choose a file |
| Archived recordings (ONVIF Profile G) | The camera or NVR | Search results and replay are streamed and displayed only. **Nothing is downloaded or copied** to your computer |
| Events and analytics (motion, rules, metadata) | The camera's event and analytics services | Displayed in memory while the app runs. **Not stored** |
| SOAP log | The app's own requests and the camera's replies | Kept in memory in the Log tab. Written to disk only when you click **Save…** in the Log tab and choose a file |
| App preferences (theme, video size, ffmpeg path, timeout) | Your choices | Saved to `settings.json` |

## Camera passwords

The app needs a camera's username and password to talk to it, and sends them **only to that camera**:
as a WS-Security password digest inside ONVIF SOAP requests, as HTTP Digest or Basic authentication,
and inside the RTSP address handed to the video decoder.

**If you do not tick "Remember password"** (the default), the password exists only in memory and
is gone when the app closes.

**If you tick "Remember password"**, the password is encrypted before it is written to
`settings.json`. It is never written in clear text:

- **Windows:** encrypted with the Windows Data Protection API (DPAPI), current-user scope. Only
  your Windows account on the same computer can decrypt it.
- **Linux:** encrypted with AES-256-GCM. The key is random, is created on first use and is stored in
  your desktop keyring (Secret Service — GNOME Keyring, KWallet and similar) through `secret-tool`.
- **If no such secure store is available** (for example `secret-tool` is not installed), the
  "Remember password" option is disabled and passwords are never saved.

Versions up to and including 1.2.0 saved remembered passwords in clear text. Newer versions
encrypt any such password, or remove it if encryption is not available, and rewrite the file the
first time they start.

Please be aware of the following limits, which come from how cameras work rather than from the app:

- Many cameras accept only plain `http://` and `rtsp://`. Credentials sent with HTTP Basic
  authentication, and video, then cross the network unencrypted. Use HTTPS on the camera where it is
  supported.
- To play video, the app starts `ffmpeg` (or, if you choose, VLC / mpv / ffplay) with the camera's
  RTSP address, including the username and password, as a command-line argument. Other programs
  running under your account, and administrators of your computer, can see the command lines of
  running processes.
- **Copy URI** on the Media tab puts the full address, including the password, on the clipboard.

## Local storage and deletion

| What | Location |
|---|---|
| Settings and device list | Windows: `%APPDATA%\OnvifLib.Gui\settings.json` · Linux: `~/.config/OnvifLib.Gui/settings.json` |
| Downloaded ffmpeg (only if you use **Download ffmpeg**) | Windows: `%LOCALAPPDATA%\OnvifLib.Gui\ffmpeg\` · Linux: `~/.local/share/OnvifLib.Gui/ffmpeg/` |
| Linux keyring entry | Secret Service item labelled "OnvifLib.Gui settings encryption key" |
| Saved snapshots and logs | Only where you chose to save them |

To delete your data:

- **One camera:** select it and click **Remove**. The device, username and any remembered password
  are removed from `settings.json`.
- **One remembered password:** untick **Remember password** for that camera.
- **Everything:** close the app and delete the `OnvifLib.Gui` folder(s) listed above. On Linux,
  also run `secret-tool clear service OnvifLib.Gui key settings-encryption`.

Uninstalling the app (MSI, winget, or deleting the portable folder) removes the program but, like
most Windows and Linux desktop apps, leaves your per-user settings in place. Delete them as above.

## Network communication

The app makes these connections, and no others:

1. **WS-Discovery** — when you click **Discover**, a UDP multicast probe on port 3702 is sent to
   your local network, and cameras reply.
2. **Cameras you add or discover** — ONVIF SOAP over HTTP/HTTPS, RTSP video, and JPEG snapshots,
   sent to the address you entered or the camera advertised. Events are fetched by polling
   (pull-point); the app does not open a listening port.
3. **ffmpeg download (optional)** — only if you click **Download ffmpeg**, the app downloads a
   pinned LGPL build from `github.com` (BtbN/FFmpeg-Builds) and checks its SHA-256 hash. The Windows
   installer already includes ffmpeg, so this is normally not needed there. GitHub receives an
   ordinary download request (your IP address and user agent), subject to
   [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).

**Does data leave the local network?** Not by the app's own doing. Camera traffic goes wherever the
camera is: if you add a camera that is reachable over the internet or a VPN, traffic to it crosses
those networks. The only connection the app makes to anything other than a camera is the optional
ffmpeg download above.

## The OnvifLib library

The library does what the application asks: it sends SOAP and HTTP requests to the camera
addresses and credentials its caller supplies. It stores nothing on disk, and it has no
telemetry and makes no network connections of its own.

## Children

The app is a tool for managing IP cameras and is not directed at children.

## Changes

Changes to this policy are made in this file and can be seen in its
[commit history](https://github.com/treealarm/OnvifLib/commits/main/PRIVACY.md).

## Contact

Questions or concerns: open an issue at <https://github.com/treealarm/OnvifLib/issues>.
