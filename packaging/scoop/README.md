# Scoop: OnvifLib.Gui

Portable install from the GitHub Release **zip** (MSI / winget live under
[`../winget`](../winget)).

## Install from this repo (no public bucket yet)

```powershell
scoop install packaging\scoop\onviflib-gui.json
# or:
scoop install .\packaging\scoop\onviflib-gui.json
```

## Own bucket (optional)

```powershell
# after pushing this JSON to a GitHub repo named e.g. scoop-treealarm
scoop bucket add treealarm https://github.com/treealarm/scoop-treealarm
scoop install treealarm/onviflib-gui
```

Bump `version` / `url` / `hash` when a new `gui-*` tag ships (or rely on `checkver` +
`scoop update` once the bucket is published).
