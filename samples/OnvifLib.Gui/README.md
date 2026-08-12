# OnvifLib.Gui

A desktop test bench for [OnvifLib](../../README.md), built with [Avalonia](https://avaloniaui.net/)
so the same code runs on Windows and Linux. Ten tabs, one per service, with a button for every
public call the library exposes.

Where [OnvifLib.Probe](../OnvifLib.Probe/README.md) answers "does this camera work" in one
non-interactive run, this answers "what does this camera do when I poke it" — including the
destructive operations the probe refuses to touch.

## Running it

```bash
dotnet run --project samples/OnvifLib.Gui
```

Fill in the address, port, user and password at the top, press **Connect**, and the tabs light up.
The connection bar is written back to `settings.json` under your config directory after a
successful connect and at exit, so the next run starts filled in. The password is only stored if
you tick **Remember** — and then in clear text, which is why it is off by default and labelled.
A tab the camera cannot support still opens and says so, rather than disappearing — that answer is
usually the point of the exercise.

```bash
# Loads every view and exits non-zero if any fails to construct. Needs a display.
dotnet run --project samples/OnvifLib.Gui -- --selftest
```

## The tabs

| | |
|---|---|
| **Discovery** | WS-Discovery multicast, plus a brute-force IP sweep. Works with no session — the place to start when a connection is failing. Double-click a result to fill in the connection bar. |
| **Device** | Identity, the service table, the clock offset, camera storage, and the destructive operations (reboot, set clock, vendor auxiliary commands), each behind a confirmation. |
| **Media** | Profiles, stream URIs, snapshots (single and polled), video and audio encoder configuration, and the Profile M metadata plumbing. |
| **PTZ** | A press-and-hold direction pad, relative and absolute movement, and presets. |
| **Imaging** | Brightness, contrast, saturation and sharpness, with the ranges the camera reports. |
| **Events** | The pull-point subscription, the raw notifications it produces, and a panel for `Camera.ParseEvent`. |
| **Analytics** | Modules and rules, their parameters, and the raw XML three of the calls return by design. |
| **Profile G** | The camera's own recordings: configuration, jobs, search, and replay. |
| **Device I/O** | Relay outputs and digital inputs. |
| **Log** | Everything the library logged, optionally including the full SOAP exchange. **The first place to look when anything above fails.** |

## Video

The library is control-plane only: it hands out URIs and JPEG snapshots and decodes nothing. So
this app shows video two ways, neither of which needs a native media dependency:

- **Snapshots**, polled on a timer into an `Image`. Requests never overlap, so a slow camera simply
  answers less often instead of piling up. Note that `MediaService.GetImage()` takes no profile
  token — the library always fetches the *first* profile's snapshot. The URL box next to it calls
  the public `MediaService.DownloadImageAsync` so any other snapshot URI can still be fetched.
- **An external player.** The app finds `vlc`, `ffplay` or `mpv` on `PATH` (plus VLC's standard
  install locations on Windows), splices the credentials into the RTSP URI, and launches it.
  There is a box for a player path if yours is somewhere unusual, and a drop-down to pick between
  the ones that were found.

  The default differs by platform for an empirical reason: **the VLC packaged for current Debian
  and Ubuntu no longer ships the live555 demuxer**, so it cannot open a plain RTSP stream at all —
  it falls back to the satip and realrtsp modules and fails to connect, no matter which options it
  is given. `ffplay` carries its own RTSP support in libavformat and works, so it leads on Linux;
  the official Windows VLC build still has live555 and leads there.

  For the same reason the transport preference is passed to VLC as the MRL option `:rtsp-tcp`
  rather than the global `--rtsp-tcp` flag. That flag belongs to live555, and a VLC built without
  it rejects the unknown option and refuses to start — an MRL option no module claims is simply
  ignored. A player that starts and then dies is reported in the status bar, so this kind of
  failure is visible in the app rather than only to whoever launched it from a terminal.

**The password is passed to the player on its command line**, so it is visible in `ps` or Task
Manager. VLC's `--rtsp-pwd` is no better (same `argv`) and ffplay has no alternative, so the app
discloses it rather than pretending otherwise. Everything the app *displays* or *logs* has the
password blanked; only the clipboard and the player get the real URI.

## Things worth knowing while using it

- **"Advertised, but the library could not create a client"** on a tab means the camera lists the
  service and the library still could not talk to it. In practice that is almost always a rejected
  credential — check the Log tab.
- **Capture SOAP** in the connection bar decides whether a logger is handed to `Camera.Create`,
  which is what switches on the request/response dump. It cannot be changed on a live connection,
  so it takes effect on the next connect. The Log tab's level filter then decides whether those
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

## Requirements

The library targets `net10.0`, so this does too — a project cannot reference a library on a newer
target framework. Avalonia is pinned to 11.3.13 across all its packages: mismatched versions
produce obscure XAML-compiler errors, and `Avalonia.Controls.DataGrid` stops at 11.3.13 in the
11.3 line, so a higher core version drags the DataGrid to 12.x and fails the restore.

```bash
# Self-contained builds, no runtime needed on the target
dotnet publish samples/OnvifLib.Gui -c Release -r linux-x64 --self-contained true -o out/linux-x64
dotnet publish samples/OnvifLib.Gui -c Release -r win-x64   --self-contained true -o out/win-x64
```

**Do not enable trimming or NativeAOT.** `System.ServiceModel.*` builds its channels, serializers
and generated proxies by reflection with no trim annotations. A trimmed build launches and then
fails on the first SOAP call, typically with `MissingMethodException` or a `TypeInitializationException`
out of `System.ServiceModel.Primitives` — which looks nothing like the trimming problem it is.
