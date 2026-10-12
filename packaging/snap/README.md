# Snap: onviflib-gui

[Snap Store](https://snapcraft.io/onviflib-gui) package of OnvifLib.Gui: `base: core24`, strict
confinement, `grade: stable`. The snap repackages the **same linux-x64 publish output** that ships
as `OnvifLib.Gui-linux-x64.zip` on GitHub Releases (self-contained .NET 10 app plus the LGPL
`ffmpeg`); nothing is compiled by snapcraft.

```
sudo snap install onviflib-gui
sudo snap connect onviflib-gui:password-manager-service   # for "Remember password"
sudo snap connect onviflib-gui:removable-media            # to save snapshots on USB drives
```

Settings inside the snap: `~/snap/onviflib-gui/current/.config/OnvifLib.Gui/settings.json`.
ffmpeg is the bundled `$SNAP/app/ffmpeg/ffmpeg`; nothing is downloaded.
External players (VLC, mpv, ffplay on the host) cannot be launched from the strict snap; in-window
playback via the bundled ffmpeg is unaffected.

## Files

| File | Role |
|---|---|
| `snapcraft.yaml` | The recipe. `plugin: dump` of `payload/app`; `extensions: [gnome]` for the desktop runtime. |
| `desktop/onviflib-gui.desktop` | Menu entry; snapcraft rewrites `Exec=` and `Icon=` for the snap. |
| `stage.sh` | Fills `payload/` (gitignored) from a release zip or a publish directory, copies the repository `icon.png`, writes the version. |

## Release workflow

`.github/workflows/release-gui.yml`, job **snap** (after **build**): downloads the linux-x64 zip
artifact, runs `stage.sh` with the version from the `gui-X.Y.Z` tag, builds with
[snapcore/action-build](https://github.com/snapcore/action-build) and releases to the **stable**
channel with [snapcore/action-publish](https://github.com/snapcore/action-publish). The store
credentials come from the repository secret `SNAPCRAFT_STORE_CREDENTIALS`. A run that is not
on a `gui-X.Y.Z` tag (for example `workflow_dispatch` on a branch) builds a `0.0.0+git…` snap as
a workflow artifact and skips the publish step.

## Build locally

Requires snapcraft and LXD:

```bash
sudo snap install snapcraft --classic
sudo snap install lxd
sudo lxd init --auto
sudo usermod -aG lxd "$USER"   # then log out and back in (or: newgrp lxd)
```

Then, from the repository root:

```bash
# Publish exactly as the release workflow does (or download OnvifLib.Gui-linux-x64.zip from Releases).
dotnet publish samples/OnvifLib.Gui/OnvifLib.Gui.csproj -c Release -r linux-x64 --self-contained true \
  -o artifacts/local/OnvifLib.Gui-linux-x64 -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
# Put an LGPL BtbN ffmpeg at artifacts/local/OnvifLib.Gui-linux-x64/ffmpeg/ffmpeg (see the workflow for the pinned URL).

packaging/snap/stage.sh artifacts/local/OnvifLib.Gui-linux-x64 1.3.0+local
(cd packaging/snap && snapcraft)                 # -> packaging/snap/onviflib-gui_1.3.0+local_amd64.snap
sudo snap install --dangerous packaging/snap/onviflib-gui_*_amd64.snap
sudo snap connect onviflib-gui:password-manager-service
onviflib-gui
```

`snapcraft expand-extensions` (in `packaging/snap`) prints the recipe with the gnome extension
applied. `snap run --shell onviflib-gui` opens a shell inside the confinement for debugging;
`journalctl -f | grep -i apparmor` shows denials.

## Store setup (once)

```bash
snapcraft login
snapcraft register onviflib-gui
# Credentials for CI, limited to this snap, the stable channel and the four ACLs the publish action needs:
snapcraft export-login --snaps onviflib-gui --channels stable \
  --acls package_access,package_push,package_update,package_release snapcraft-store-credentials.txt
gh secret set SNAPCRAFT_STORE_CREDENTIALS --repo treealarm/OnvifLib < snapcraft-store-credentials.txt
rm snapcraft-store-credentials.txt
```

`password-manager-service` is not auto-connected by default. Ask for it on
<https://forum.snapcraft.io/c/store-requests> ("Auto-connect request: onviflib-gui
password-manager-service"), explaining that the app stores only its own AES key there, under the
label "OnvifLib.Gui settings encryption key", and that without it the Remember-password option is
disabled rather than falling back to clear text. Until that is approved, the README and the store
description tell users to run `snap connect` themselves.
