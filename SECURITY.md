# Security policy

Applies to the **OnvifLib** .NET library and the **OnvifLib.Gui** desktop app (GitHub Releases,
the MSI and the winget package `treealarm.OnvifLib.Gui`).

## Supported versions

Fixes go into the latest release only. Please check that the problem still reproduces on the
newest version before reporting.

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Report privately through GitHub:
[**Report a vulnerability**](https://github.com/treealarm/OnvifLib/security/advisories/new)
(repository → **Security** tab → **Report a vulnerability**). Only the maintainers can see the report.

Useful to include:

- the affected component (library or GUI) and version;
- the operating system;
- steps to reproduce, or a proof of concept;
- what an attacker could gain.

Do not include real camera passwords, addresses or footage. Redact them, or use a test device.

We will acknowledge the report, keep you updated in the advisory, and credit you in it when it is
published, unless you prefer to stay anonymous.

## Scope

In scope, for example:

- remembered passwords leaking from `settings.json`, the keyring entry or process memory beyond
  what [PRIVACY.md](PRIVACY.md) describes;
- the app sending data anywhere other than the cameras and the optional ffmpeg download listed in
  [PRIVACY.md](PRIVACY.md#network-communication);
- the ffmpeg download or the installer accepting a file whose hash does not match;
- malformed camera or WS-Discovery replies causing code execution, or a crash that corrupts saved
  settings.

Out of scope: weaknesses in the cameras themselves, and the limits listed under "Camera passwords"
in [PRIVACY.md](PRIVACY.md#camera-passwords) (plain HTTP/RTSP, the RTSP address on the ffmpeg
command line, **Copy URI**).
