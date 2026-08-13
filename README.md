# OnvifLib

[![NuGet](https://img.shields.io/nuget/v/OnvifLib.svg)](https://www.nuget.org/packages/OnvifLib)
[![Downloads](https://img.shields.io/nuget/dt/OnvifLib.svg)](https://www.nuget.org/packages/OnvifLib)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**OnvifLib** is a modern and lightweight .NET library for interacting with ONVIF-compliant IP cameras. It provides a simple interface to discover devices, control PTZ, retrieve media streams, and handle events over the ONVIF protocol.

📂 **Source code:** [github.com/treealarm/OnvifLib](https://github.com/treealarm/OnvifLib) — issues and pull requests welcome.

🖥 **Desktop GUI (Linux / Windows x64):** [Releases](https://github.com/treealarm/OnvifLib/releases) — Avalonia test bench with in-window video (`ffmpeg` included).

---

## 🚀 Features

- 🔍 Device discovery and information
- 🎥 Media profile and RTSP URI retrieval
- 🕹️ PTZ (Pan-Tilt-Zoom) camera control
- 📡 Event handling (PullPoint or Subscription)
- 🧠 Analytics (ver20): analytics modules and rules, metadata configuration (Profile M)
- 🎞️ Profile G: the camera's own recordings — search, replay and recording jobs
- 🔌 Device I/O: relay outputs and digital inputs
- 🔐 WS-Security (UsernameToken) support
- ✅ Targets .NET 10

---

## 📦 Installation

```bash
dotnet add package OnvifLib
```

---

## ⚡ Quick start

```csharp
using OnvifLib;

// Create returns immediately and discovers the camera's services in the background.
var camera = Camera.Create("192.168.1.64", 80, "admin", "secret");
await camera.InitTask;

// A null service list means unreachable or unauthorized — Create itself never throws.
if (await camera.GetServicesAsync() is null)
    throw new InvalidOperationException("could not reach the camera");

var info = await camera.GetDeviceInformationAsync();
Console.WriteLine($"{info.Manufacturer} {info.Model}, firmware {info.FirmwareVersion}");

// Every Get*Service() returns null when the camera does not offer that service.
if (await camera.GetMediaService() is { } media)
{
    foreach (var profile in media.GetProfiles())
    {
        var uri = await media.GetStreamUri(profile.Token);
        Console.WriteLine($"{profile.Name} {profile.Width}x{profile.Height} {profile.Encoding} → {uri}");
    }

    // Snapshot bytes, or null (the failure is logged rather than thrown).
    if (await media.GetImage() is { } image)
        await File.WriteAllBytesAsync("snapshot.jpg", image.Data);
}

if (await camera.GetPtzService() is { } ptz)
{
    var profile = (await camera.GetProfiles())!.First();
    await ptz.ContinuousMoveAsync(profile.Token, panTiltX: 0.5f, panTiltY: 0f);
    await Task.Delay(500);
    await ptz.StopAsync(profile.Token);
}
```

Stream URIs come back exactly as the camera reported them, which is usually **without
credentials** — a player needs `user:password` spliced in, percent-encoded.

To find cameras rather than address one directly:

```csharp
var result = await WsDiscovery.ProbeAsync(TimeSpan.FromSeconds(4), CancellationToken.None);

// ScanOk distinguishes "scanned, found nothing" from "could not scan at all".
if (!result.ScanOk)
    Console.WriteLine("no interface could join the multicast group — check the firewall");

foreach (var device in result.Devices)
    Console.WriteLine($"{device.Name} at {device.Ip}:{device.Port}");
```

---

## 🧪 Samples

Two runnable applications live in [`samples/`](samples), both referencing the library directly:

| | |
|---|---|
| [**OnvifLib.Probe**](samples/OnvifLib.Probe) | A console harness that walks the whole public API against a camera and prints OK/FAIL/SKIP per call, with a summary and an exit code. Read-only by default; `--allow-writes` adds writes that undo themselves. Good as a smoke test and as a way to find out what a camera actually supports. |
| [**OnvifLib.Gui**](samples/OnvifLib.Gui) | A cross-platform Avalonia device manager (Windows and Linux) in the style of ONVIF Device Manager: a camera list with snapshots, WS-Discovery, in-window live video via ffmpeg, and a tab per service (device, media, PTZ, imaging, events, analytics, Profile G, device I/O) plus a SOAP log. Several cameras can stay connected; the tabs follow the selected one. |

```bash
dotnet run --project samples/OnvifLib.Probe -- --discovery
dotnet run --project samples/OnvifLib.Gui
```

In VS Code, press **F5** and pick a configuration — the GUI, its self-test, or the probe with
prompts for the address and credentials. In Visual Studio, set either sample as the startup
project and pick a launch profile.

> **Note:** do not enable trimming or NativeAOT in a consuming application.
> `System.ServiceModel.*` builds its channels, serializers and generated proxies by reflection
> with no trim annotations, so a trimmed build launches and then fails on the first SOAP call.
