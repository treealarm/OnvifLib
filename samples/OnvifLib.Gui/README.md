# OnvifLib.Gui

**A modern ONVIF Device Manager (ODM) alternative for Windows and Linux.**

Live video (including **H.264 and HEVC/H.265** via bundled ffmpeg), PTZ, imaging, events,
analytics, Profile G archive replay, and Device I/O — in one Avalonia desktop app built on
[OnvifLib](../../README.md).

Classic [ONVIF Device Manager](https://sourceforge.net/projects/onvifdm/) is Windows-only and
shows age on codecs and Profile G/M. This app is cross-platform, ships a current LGPL ffmpeg in
the release zip, and still works as a full ONVIF test bench (including operations ODM-style tools
often skip).

Where [OnvifLib.Probe](../OnvifLib.Probe/README.md) answers “does this camera work” in one
non-interactive run, this answers “what does this camera do when I poke it”.

The **library** is still control-plane only (SOAP, RTSP URIs, JPEG snapshots). This sample is what
decodes video, and only here.

## Download

**→ [GitHub Releases](https://github.com/treealarm/OnvifLib/releases)** — each package includes LGPL `ffmpeg` next to the app.

| | |
|---|---|
| Windows | Prefer **`OnvifLib.Gui-win-x64.msi`** (WiX → Program Files, Start Menu; avoids Mark-of-the-Web on extracted files). Portable: unzip `OnvifLib.Gui-win-x64.zip`. Build notes: [OnvifLib.Gui.Setup](../OnvifLib.Gui.Setup/README.md). winget manifests: [`packaging/winget`](../../packaging/winget). |
| Linux | Unzip `OnvifLib.Gui-linux-x64.zip` → run `./OnvifLib.Gui` (needs a desktop session). |

No separate .NET or ffmpeg install for in-window playback. ffmpeg is also resolved from `PATH`,
an app-data cache, or a path you type if you prefer your own build.

```bash
# From source
dotnet run --project samples/OnvifLib.Gui
```

## Why not just ODM?

| | ONVIF Device Manager | OnvifLib.Gui |
|---|---|---|
| Platforms | Windows | **Windows and Linux** |
| Live codecs | Limited (HEVC often missing) | **H.264 / HEVC via ffmpeg** |
| Install | Classic installer ecosystem | **WiX MSI** + portable zip ([winget](../../packaging/winget), Scoop) |
| Profile G / M | Weak or absent on many builds | Search, archive replay, metadata/analytics configs |
| Multi-camera | One context at a time in practice | Several connected; tabs follow the **selected** row |
| Stack | Aging .NET / DirectShow-era player | .NET 10 + Avalonia + current ffmpeg |

Defaults still prefer the **substream** for live view (smooth UI); pick any profile — including a
HEVC main stream — when you need it.

## Using it

**Discover** fills the list via WS-Discovery. **Add** takes an address and port. Selecting a camera
connects it; **Connect** does the same for the already-selected row. Device list, last address, and
video prefs go to `settings.json`. A password is stored only if you tick **Remember** (clear text —
off by default).

A tab the camera cannot support still opens and says so, rather than disappearing.

```bash
# Loads every view and exits non-zero if any fails to construct. Needs a display.
dotnet run --project samples/OnvifLib.Gui -- --selftest
```

## The panes

| | |
|---|---|
| **Device list** | Cameras you have discovered or added. JPEG thumbnails refresh for connected devices. Click a row to connect. |
| **Live** | In-window RTSP playback of the selected camera (ffmpeg → BGRA). The same player is embedded on Media and PTZ. |
| **Device** | Identity, capabilities, clock, services; storage and destructive ops on a second sub-tab. |
| **Media** | Sub-tabs: Streams, Snapshot, Encoders, Profile M — each fits without page scrolling. |
| **PTZ** | A press-and-hold direction pad, relative and absolute movement, presets, and the live picture so you are not moving the head blind. |
| **Imaging** | Brightness, contrast, saturation and sharpness, with the ranges the camera reports. |
| **Events** | The pull-point subscription, the raw notifications it produces, and a panel for `Camera.ParseEvent`. |
| **Analytics** | Modules, rules, and parameters on separate sub-tabs. |
| **Profile G** | Sub-tabs: On camera (recordings/jobs) and Search & archive (search + archive player). |
| **Device I/O** | Relay outputs and digital inputs. |
| **Discovery** | WS-Discovery again, plus a brute-force IP sweep for networks where multicast does not reach. Double-click a result to add it to the list. |
| **Log** | Everything the library logged, optionally including the full SOAP exchange. **The first place to look when anything above fails.** |

## Video

Live video is decoded **inside the window**. The sample starts `ffmpeg`, reads raw BGRA frames from
its stdout, and paints them on a reused `WriteableBitmap`. One stream at a time — the selected
camera — defaulting to the **substream** (smallest profile) at 640×360 / 12 fps, because a 1440p
HEVC main stream is expensive to decode into raw frames. ffmpeg handles **H.264 and HEVC** (and
whatever else that build decodes); the GUI does not reimplement codecs.

ffmpeg is located in this order:

1. **Next to the app** — release zips ship `ffmpeg/ffmpeg` (or `ffmpeg.exe`).
2. **`PATH`** — if `ffmpeg` is already installed, nothing is downloaded.
3. **App data cache** — `~/.local/share/OnvifLib.Gui/ffmpeg/` on Linux, `%LocalAppData%\OnvifLib.Gui\ffmpeg\` on Windows.
4. **A path you type** in the player bar (kept in `settings.json`).
5. **Download** — the **Download ffmpeg** button, and the first **Play** if nothing else was found.
   That fetches a pinned **LGPL** BtbN build for `win-x64` or `linux-x64`, checks SHA256, and
   extracts only the `ffmpeg` binary. Other RIDs are told to install ffmpeg themselves
   (`sudo apt install ffmpeg` on Linux).

The repository and the OnvifLib NuGet package **do not ship ffmpeg** into source control. Release
zips and the optional download are a separate LGPL program; this sample stays MIT.

JPEG **snapshots** remain available on the Media tab (polled, never overlapping).
`MediaService.GetImage()` still has no profile token — it always uses the first profile. The URL
box next to it calls `MediaService.DownloadImageAsync` so any other snapshot URI can still be
fetched. Thumbnails in the device list use the same JPEG path, not a second live decoder.

An **external player** (VLC / ffplay / mpv) is still offered on the Media tab as a fallback. The
default differs by platform for an empirical reason: **the VLC packaged for current Debian and
Ubuntu no longer ships the live555 demuxer**, so it cannot open a plain RTSP stream. `ffplay`
carries its own RTSP support and leads on Linux; the official Windows VLC build still has live555
and leads there.

**The password is passed to ffmpeg and to any external player on the command line**, so it is
visible in `ps` or Task Manager. Everything the app *displays* or *logs* has the password blanked;
only the child process and the clipboard get the real URI.

Closing the window kills the ffmpeg process. Switching the selected camera stops the current
stream before the next one starts.

## Things worth knowing while using it

- **"Advertised, but the library could not create a client"** on a tab means the camera lists the
  service and the library still could not talk to it. In practice that is almost always a rejected
  credential — check the Log tab.
- **Capture SOAP** in the top bar decides whether a logger is handed to `Camera.Create`,
  which is what switches on the request/response dump. It cannot be changed on a live connection,
  so it takes effect on the next login. The Log tab's level filter then decides whether those
  entries are kept, and it applies where they are produced — leaving it above Debug genuinely
  stops paying for the dumps.
- **Imaging sends only what you tick.** The library treats an omitted value as "leave unchanged"
  and does its own read-modify-write, so re-sending everything is a way to overwrite a setting you
  never meant to touch.
- **Analytics structured parameters are read-only.** Polygons, line segments and schedules have no
  fixed schema across vendors; they are round-tripped verbatim on Modify, because rebuilding them
  from parsed state is how vendor rules get silently corrupted.
- **Profile G times are in the camera's clock.** Measure the offset on the Device tab first; search
  windows are converted into the camera's clock before they are sent, and results are shown as the
  camera reported them.
- **Replay URIs are fetched fresh every time.** They are frequently single-use, so there is
  deliberately no way in this app to replay a cached one.
- **Relays and digital inputs have no readable state.** ONVIF exposes none, so the app shows the
  last command it sent rather than a live indicator that would be a lie.
- **Switching a relay asks first.** It is electrically reversible but physically may not be — the
  output is usually wired to a door strike, a gate or a siren.

## How it is put together

MVVM with `CommunityToolkit.Mvvm` source generators, and compiled bindings throughout, so a
renamed view-model property is a compile error rather than a silently blank field.

Two things in here exist because of how the library behaves and are worth preserving:

- **Every library call goes through `OperationRunner`**, which runs it via `Task.Run`. OnvifLib
  never calls `ConfigureAwait(false)`, so awaiting one directly from the dispatcher would route
  every internal continuation — retry backoffs, the recording-search poll loop, the scanner's
  parallel workers — back through the UI thread. A single direct `await` reintroduces that, and
  the symptom (stutter during a scan) is easy to misattribute.
- **Services are resolved once at connect and disposed only at disconnect.** `OnvifServiceCache`
  owns them with a 10-minute TTL and would keep handing out a disposed instance; re-fetching the
  event service after the TTL lapses would create a second pull-point subscription on the camera.

`EventService1.OnEventReceived` and the scanner's callbacks arrive on threadpool threads and are
marshalled with `Dispatcher.UIThread.Post`.

One more thing worth not undoing: **the PTZ direction pad attaches its pointer handlers in
code-behind with `handledEventsToo: true`**, not with `PointerPressed="…"` in XAML. `Button`
handles both pointer events itself — it captures the pointer on press and raises `Click` on
release — and marks them handled, so a XAML attribute subscription is never called. The failure
mode is silent: the buttons look and feel normal, and no request is ever sent.

The live player is one `VideoPlayerViewModel` shared by Live, Media and PTZ. Switching those tabs
must not restart ffmpeg. Switching the selected camera must. Profile G uses a separate archive
player so live and replay do not fight.

## Requirements

The library targets `net10.0`, so this does too — a project cannot reference a library on a newer
target framework. Avalonia is pinned to 11.3.13 across all its packages: mismatched versions
produce obscure XAML-compiler errors, and `Avalonia.Controls.DataGrid` stops at 11.3.13 in the
11.3 line, so a higher core version drags the DataGrid to 12.x and fails the restore.

ffmpeg is optional until you press Play: `--selftest` constructs `VideoView` without it.

```bash
# Self-contained builds, no runtime needed on the target
dotnet publish samples/OnvifLib.Gui -c Release -r linux-x64 --self-contained true -o out/linux-x64
dotnet publish samples/OnvifLib.Gui -c Release -r win-x64   --self-contained true -o out/win-x64
```

**Do not enable trimming or NativeAOT.** `System.ServiceModel.*` builds its channels, serializers
and generated proxies by reflection with no trim annotations. A trimmed build launches and then
fails on the first SOAP call, typically with `MissingMethodException` or a `TypeInitializationException`
out of `System.ServiceModel.Primitives` — which looks nothing like the trimming problem it is.
