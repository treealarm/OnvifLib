# OnvifLib

[![NuGet](https://img.shields.io/nuget/v/OnvifLib.svg)](https://www.nuget.org/packages/OnvifLib)
[![Downloads](https://img.shields.io/nuget/dt/OnvifLib.svg)](https://www.nuget.org/packages/OnvifLib)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**OnvifLib** is a modern and lightweight .NET library for interacting with ONVIF-compliant IP cameras. It provides a simple interface to discover devices, control PTZ, retrieve media streams, and handle events over the ONVIF protocol.

📂 **Source code:** [github.com/treealarm/OnvifLib](https://github.com/treealarm/OnvifLib) — issues and pull requests welcome.

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
- ✅ Targets .NET 8

---

## 📦 Installation

```bash
dotnet add package OnvifLib
