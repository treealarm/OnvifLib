# OnvifLib.Gui

A desktop ONVIF device manager and test bench for [OnvifLib](../../README.md), built with
[Avalonia](https://avaloniaui.net/) so the same code runs on Windows and Linux. The layout follows
ONVIF Device Manager: a list of cameras on the left, live video and the service tabs on the right.

Where [OnvifLib.Probe](../OnvifLib.Probe/README.md) answers "does this camera work" in one
non-interactive run, this answers "what does this camera do when I poke it" — including the
destructive operations the probe refuses to touch.

The **library** is still control-plane only (SOAP, RTSP URIs, JPEG snapshots). This sample is what
decodes video, and only here.

## Running it

Prebuilt **Linux x64** zip (includes an LGPL `ffmpeg` next to the app):
[GitHub Releases](https://github.com/treealarm/OnvifLib/releases).

```bash
dotnet run --project samples/OnvifLib.Gui
```

**Discover** (left pane) runs WS-Discovery and fills the device list. **Add** takes an address and
port typed above the list. Selecting a camera connects it; **Connect** does the same for the
already-selected row. Several cameras can stay connected; the tabs and the live player always
follow the **selected** row.

The list, the last address, and video preferences are written back to `settings.json` under your
config directory after a successful login and at exit. A password is only stored if you tick
**Remember** on that device — and then in clear text, which is why it is off by default and labelled.

A tab the camera cannot support still opens and says so, rather than disappearing — that answer is
usually the point of the exercise.

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
HEVC main stream is expensive to decode into raw frames.

ffmpeg is located in this order:

1. **`PATH`** — if `ffmpeg` is already installed, nothing is downloaded.
2. **App data cache** — `~/.local/share/OnvifLib.Gui/ffmpeg/` on Linux, `%LocalAppData%\OnvifLib.Gui\ffmpeg\` on Windows.
3. **A path you type** in the player bar (kept in `settings.json`).
4. **Download** — the **Download ffmpeg** button, and the first **Play** if nothing else was found.
   That fetches a pinned **LGPL** BtbN build for `win-x64` or `linux-x64`, checks SHA256, and
   extracts only the `ffmpeg` binary. Other RIDs are told to install ffmpeg themselves
   (`sudo apt install ffmpeg` on Linux).

The repository and the OnvifLib NuGet package **do not ship ffmpeg**. The download is a separate
LGPL program; this sample stays MIT.

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
must not restart ffmpeg. Switching the selected camera must.

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
