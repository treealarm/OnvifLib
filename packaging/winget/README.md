# winget: OnvifLib.Gui

Manifests for [winget-pkgs](https://github.com/microsoft/winget-pkgs) using the **WiX MSI**
from GitHub Releases (`InstallerType: wix`). Built by [`samples/OnvifLib.Gui.Setup`](../../samples/OnvifLib.Gui.Setup).

Current package: **treealarm.OnvifLib.Gui** `1.2.0` → tag `gui-1.2.0` → `OnvifLib.Gui-win-x64.msi`.

## Validate on Windows

```powershell
winget validate --manifest packaging\winget
winget install --manifest packaging\winget
```

## Submit

1. Fork [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs).
2. Copy the three YAML files into `manifests/t/treealarm/OnvifLib.Gui/1.2.0/`.
3. Open a PR.

Or with [wingetcreate](https://github.com/microsoft/winget-create):

```powershell
wingetcreate new https://github.com/treealarm/OnvifLib/releases/download/gui-1.2.0/OnvifLib.Gui-win-x64.msi
# later:
wingetcreate update treealarm.OnvifLib.Gui -u <msi-url> -v 1.3.0 --submit
```

## Bump for a new `gui-X.Y.Z` tag

1. Set `PackageVersion` to `X.Y.Z` in all three files.
2. Point `InstallerUrl` at `OnvifLib.Gui-win-x64.msi` for that tag.
3. Set `InstallerSha256` (uppercase) from the GitHub API `assets[].digest` or `Get-FileHash`.
4. Update `ReleaseDate` / `ReleaseNotesUrl`.

Portable zip installs belong in Scoop (`packaging/scoop`). There is no second Windows
installer (no Inno/NSIS) — only the WiX MSI plus the portable zip.
