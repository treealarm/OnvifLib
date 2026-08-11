# OnvifLib.Probe

A console test harness for [OnvifLib](../../README.md). It walks the whole public API against a
real camera, one call at a time, and prints what each one returned:

```
── device ──────────────────────────────────────────────────────────────
  [ OK ] GetDeviceInformation                              88 ms
            manufacturer          HIKVISION
            model                 DS-2CD2143G0-I
  [FAIL] GetStorageConfigurations                          55 ms
            not authorized — check user/password (400 Not Authorized)
  [SKIP] Reboot                                        destructive — never run by the probe
```

Its job is to answer two questions fast: **does the library work against this camera**, and
**what does this camera actually support**. Every call is timed, every failure is reduced to one
readable line, and nothing is silently skipped — a call that is not made says so and says why.

## Running it

```bash
dotnet run --project samples/OnvifLib.Probe -- --ip 192.168.1.64 --user admin --password secret
```

Configuration comes from three places, in order of priority:

| | |
|---|---|
| command line | `--ip 192.168.1.64 --port 80 --user admin --password secret` |
| environment | `ONVIF_IP`, `ONVIF_PORT`, `ONVIF_USER`, `ONVIF_PASSWORD`, `ONVIF_TIMEOUT`, `ONVIF_XADDR` |
| source | the constants in [`ProbeOptions.cs`](ProbeOptions.cs) — edit them to stop typing the address |

`--help` prints the full option list.

### Finding a camera first

`--discovery` runs a WS-Discovery multicast probe before connecting, and needs no credentials:

```bash
dotnet run --project samples/OnvifLib.Probe -- --discovery --only discovery
```

It distinguishes three outcomes that are easy to confuse: devices found, *probed successfully but
nobody answered*, and *could not probe at all* (no interface could join the multicast group —
firewall, container networking, or a VM-only adapter). `--scan 192.168.1.1 192.168.1.254` adds a
brute-force sweep with `CameraScanner` on top.

### Exit codes

| | |
|---|---|
| `0` | everything that ran passed |
| `1` | at least one step failed |
| `2` | could not connect — nothing past the connect section was attempted |
| `3` | bad arguments |

So it drops straight into a script or a CI job: `dotnet run … || echo "camera regressed"`.

## What it will and will not do to your camera

**By default the probe only reads.** Every setter is listed and reported as `SKIP` so you can see
it exists and was not exercised.

**`--allow-writes`** enables writes that undo themselves:

- PTZ: a continuous nudge and its mirror image, a relative move there and back, and a preset that
  is created, visited, and removed in a `finally`.
- Imaging: brightness moved by 5 % of its range, read back, and restored in a `finally`.
- Configurations: `SetVideoEncoderConfig`, `SetAudioEncoderConfig`, `SetMetadataConfig`,
  `SetRelayOutputSettings` and `SetDigitalInputIdleState` are re-sent with exactly the values that
  were just read — a no-op write that proves the call is accepted without choosing settings the
  camera might not support.

**`--allow-relay`** is separate from `--allow-writes` on purpose. Pulsing a relay output is
electrically reversible but physically is not: the output is usually wired to a door strike, a
gate, or an alarm.

**Never, at any flag level:**

`Reboot`, `SetTime` / `SyncTime`, `SendAuxiliaryCommand` (one of the vendor commands typically
formats the SD card), `DeleteRecording` / `DeleteTrack` / `DeleteRecordingJob`,
`SetRecordingConfiguration`, `SetRecordingJobMode`, the analytics
`Create` / `Modify` / `Delete` calls, and the metadata/analytics `Attach` / `Detach` pair.
Those belong in the GUI, behind a confirmation dialog.

`AbsoluteMove` is skipped even with `--allow-writes` for a different reason: the library exposes no
way to read the current pan/tilt/zoom, so there is nothing to restore to.

## Output

- **stdout** carries the report. Colour switches itself off when the output is redirected or when
  `NO_COLOR` is set, so `probe > run.txt` produces a clean file.
- **stderr** carries the library's own log. A logger is always passed to `Camera.Create`, because
  several library methods swallow their exception and only log it — `MediaService.GetImage()`
  returns `null`, and so does `Camera.GetServicesAsync()` — so without one those failures have no
  explanation anywhere. `--verbose` lowers the level to Debug, which also turns on the full SOAP
  request/response dump.
- `--json report.json` writes the same run in a machine-readable shape, so two runs can be
  diffed: the same camera before and after a firmware update, or two different models.
- `--save-snapshot <dir>` writes any snapshot it fetched.

## Sections

`discovery`, `connect`, `device`, `media`, `ptz`, `imaging`, `events`, `analytics`, `recording`,
`deviceio` — narrow the run with `--only media,ptz` or widen it with `--skip events`. `connect`
always runs; everything else depends on the session it establishes.

Each section starts by checking whether its service resolved at all and reports
`SKIP — service not available` rather than failing, so a camera that simply lacks Profile G reads
as a camera that lacks Profile G and not as a broken library.

## Things the output will tell you that are worth knowing

- **`advertised, creation failed`** in the connect table means the camera lists the service but the
  library could not build a client for it. In practice that is almost always a rejected credential,
  not a missing feature.
- **Snapshots are always the first profile's.** `MediaService.GetImage()` takes no profile token
  and the library resolves the snapshot URI for `_profiles.FirstOrDefault()`.
- **Stream and replay URIs come back without credentials.** A player needs `user:password` spliced
  in, and the password must be percent-encoded or a `@` in it will corrupt the host.
- **Replay URIs are frequently single-use.** Fetch a fresh one immediately before each playback.
- **Recording timestamps are in the camera's clock.** The `recording` section prints both the raw
  camera time and its equivalent in ours, using the offset the `device` section measured.
- **Relays and digital inputs have no readable state.** ONVIF does not expose one, so neither the
  probe nor a GUI can show a live indicator — only the last command that was sent.

## Requirements

The library targets `net10.0`, so the probe does too — a project cannot reference a library on a
newer target framework. Do not enable trimming or NativeAOT: `System.ServiceModel.*` builds its
channels, serializers and generated proxies by reflection with no trim annotations, and a trimmed
build launches and then fails on the first SOAP call.
