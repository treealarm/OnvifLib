# OnvifLib.Gui.Setup

WiX v5 MSI for the [desktop GUI](../OnvifLib.Gui/README.md). The zip on
[Releases](https://github.com/treealarm/OnvifLib/releases) still exists for a
portable copy; this is the path that lands in Program Files so Windows does not
treat the binaries as "downloaded from the Internet."

The payload is the self-contained `win-x64` publish folder plus the LGPL
`ffmpeg/ffmpeg.exe` sidecar. The WiX build (and a Windows `dotnet publish -r win-x64`)
download that binary if it is not already there, so an installed app can play
video without a separate ffmpeg install. This project does not compile the GUI.

## Build

Windows only. Publish first, then point WiX at that directory (the default is
`artifacts/OnvifLib.Gui-win-x64` at the repo root):

```powershell
dotnet publish samples/OnvifLib.Gui/OnvifLib.Gui.csproj `
  -c Release -r win-x64 --self-contained `
  -o artifacts/OnvifLib.Gui-win-x64 `
  -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false

dotnet build samples/OnvifLib.Gui.Setup/OnvifLib.Gui.Setup.wixproj `
  -c Release -p:Version=1.0.0
```

The MSI is `samples/OnvifLib.Gui.Setup/bin/Release/OnvifLib.Gui.msi`.
ffmpeg is required: the build fails rather than ship a player that cannot play.
The BtbN zip is cached under `artifacts/ffmpeg-cache/` so a dropped download can
resume instead of starting over.

`GuiPublishDir` overrides the harvest folder if the publish output lives
somewhere else. `Version` must be `major.minor.build` — MSI has no prerelease
label, and the `gui-*` tag is what CI passes.

The project is in the solution for browsing but is not part of the default
solution build, so `dotnet build OnvifLib.sln` on Linux stays a library+samples
build.
