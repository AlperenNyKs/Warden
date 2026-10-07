<div align="center">

<img src="Assets/icon.png" width="112" height="112" alt="Warden logo" />

# Warden

**The quiet gaming companion for Windows.**
Per-game SteelSeries Sonar profiles, hardware monitoring, game history and more — from the system tray.

[![Latest release](https://img.shields.io/github/v/release/AlperenNyKs/Warden?style=flat-square&color=FF8A3D&label=release)](https://github.com/AlperenNyKs/Warden/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/AlperenNyKs/Warden/total?style=flat-square&color=FF8A3D)](https://github.com/AlperenNyKs/Warden/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/AlperenNyKs/Warden/build.yml?branch=main&style=flat-square)](https://github.com/AlperenNyKs/Warden/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/github/license/AlperenNyKs/Warden?style=flat-square)](LICENSE)
![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0B0B0F?style=flat-square)

[**Download**](https://github.com/AlperenNyKs/Warden/releases/latest) · [Features](#features) · [Install](#installation) · [Build from source](#building-from-source) · [Türkçe kılavuz](KULLANIM_KILAVUZU.md)

</div>

---

Warden sits in the system tray and reacts to the game you are playing. When a game comes into focus it switches
your SteelSeries Sonar audio profile, keeps an eye on temperatures, and when the game closes it tells you how long
you played and how hot things got. Everything is modular: turn off what you don't use and it stops running.

## Features

### 🎧 Sonar profile switching
- Switches the SteelSeries Sonar **Game** channel to the preset set for the game in focus, and back to your default
  on the desktop.
- **Automatic profiles:** games that have a dedicated preset in SteelSeries GG (e.g. *Valorant Pro Preset*,
  *War Thunder*) get it assigned automatically. Your own presets win, existing rules are never overwritten, and a
  rule you delete is not re-added.

### 🎮 Game library
- Finds installed games from **Steam, Epic, GOG, EA, Ubisoft Connect, Xbox and Riot** — on startup, in the background.
- A notification lists new games, newly assigned profiles and games that were uninstalled; uninstalled games and their
  rules are cleaned up. Games you added by hand are kept.

### 📈 Game history & session summary
- When a game closes: *“War Thunder · 1 h 42 min — CPU max 88 °C · GPU max 79 °C”*.
- **History** page: total play time, sessions, last played and average / max CPU-GPU temperature per game, plus the
  latest sessions. The first minute of a game (loading, menus) is left out of the temperature stats.

### 💾 Disk space
- Installed games by size with last played date — straight from Steam where available.
- Games not played for 60 days (or never opened) are highlighted, so you know what to uninstall.
- Warns when a game drive drops below 10 % free space.

### 🌡️ Hardware monitoring
- Live CPU / GPU temperature, clock, power and load (NVIDIA, AMD, Intel), a 60-second chart and ⭐ favorites.
- **Temperature alarm** when CPU or GPU stays above your limit, and **CSV recording** — by hand or automatically
  while a game runs.

### ⚡ GPU profile with MSI Afterburner
- Applies an MSI Afterburner profile automatically when the GPU clock exceeds a limit you set
  (for example a power-limited profile).

### 🔇 Audio device enforcer
- Keeps unused or phantom virtual audio devices disabled, even after Windows or GG re-enables them.

### 🧩 Modular by design
- Turn each module off in **Settings → Modules**. A module that is off does no background work at all: with Sonar off
  Warden never talks to GG, with hardware monitoring off the sensor driver is never loaded.
- Plus: a HUD-style **dashboard**, a **System status** page that tells you what is missing, **automatic updates**
  verified with SHA256, and full **English / Turkish** UI.

## Requirements

| Requirement | Details |
|---|---|
| **Windows** | Windows 10 or 11, 64-bit. Runs as administrator (needed for sensors and audio devices). |
| **SteelSeries GG** *(optional)* | For Sonar profile switching. |
| **MSI Afterburner** *(optional)* | For the GPU profile feature. |
| **PawnIO** *(optional)* | Driver for CPU temperature / clock / power sensors. The installer can install it for you. |

The **System status** page shows which of these are present and links to what is missing.

## Installation

1. Download **`Warden-Setup-x.y.z.exe`** from the [latest release](https://github.com/AlperenNyKs/Warden/releases/latest).
2. Run it. Warden installs to `C:\Program Files\Warden` (self-contained, no .NET runtime needed) and can optionally
   install the **PawnIO** driver.
3. Warden starts in the system tray. Turn on **Start with Windows** in Settings if you want it at logon.

**Updates:** Warden checks GitHub Releases at startup and every 6 hours and offers to install a new version; the
setup is verified against the release's SHA256 digest before it runs. Your settings and history in
`%AppData%\Warden` are kept. Uninstalling also removes the "Warden" startup task.

> **"Start with Windows" requires a protected install folder.** Warden is launched at logon with administrator
> rights (Task Scheduler, `/rl highest`). If its folder is writable by standard users (e.g. `D:\Warden\bin\...`),
> any program could swap `Warden.exe` or a DLL and get admin rights, so Warden refuses to register the task there.
> The installer puts it in Program Files, which is safe.

## How it works

| Integration | Used for |
|---|---|
| **SteelSeries GG local API** | Reads `coreProps.json` to find the local Sonar server on `127.0.0.1` and switch presets. |
| **LibreHardwareMonitor** | CPU / GPU sensors (with the PawnIO driver for CPU sensors). |
| **MSI Afterburner command line** | Applies `-Profile1` … `-Profile5`. |
| **Windows Core Audio API** | Enumerates and disables audio endpoints. |
| **Store manifests & registry** | Steam `appmanifest` files, Epic manifests and launcher registry keys to find games. |

Built with **C# / .NET 10 (WPF)**, **LibreHardwareMonitorLib** and **NAudio**.

## Building from source

```bash
git clone https://github.com/AlperenNyKs/Warden.git
cd Warden
dotnet build -c Release
dotnet test Tests/Warden.Tests
```

To build the installer locally (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
dotnet publish Warden.csproj -c Release -r win-x64 --self-contained true -o publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\Warden.iss
# -> installer-output\Warden-Setup-x.y.z.exe
```

**Releasing:** bump `<Version>` in `Warden.csproj` and merge to `main`. If there is no `v<Version>` release yet, CI
builds the setup and publishes it as a GitHub Release (pushing a `v*` tag works too).

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

> Signing is being set up. Releases published before this is completed are **unsigned**; once it is in place,
> every `Warden.exe` and `Warden-Setup-x.y.z.exe` on the [Releases](https://github.com/AlperenNyKs/Warden/releases)
> page is built by GitHub Actions from this repository and signed in that same pipeline.

**Team roles**

* Committers and reviewers: [Alperen Burhan](https://github.com/AlperenNyKs)
* Approvers: [Alperen Burhan](https://github.com/AlperenNyKs)

Only release builds produced by the CI workflow in this repository are signed. Third-party components bundled with
Warden are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); they are open source and are not modified.

## Privacy policy

Warden does not collect, store remotely or sell any personal data, and it contains no telemetry or analytics.
It will not transfer any information to other networked systems unless specifically requested by the user or the
person installing or operating it, with these exceptions:

* **Update check:** at startup and every 6 hours Warden asks the GitHub API (`api.github.com`) for the latest
  release of this repository, and downloads the new setup from GitHub when you choose to install it. You can turn
  this off in Settings → Updates. GitHub's [privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement) applies to these requests.
* **SteelSeries GG / Sonar:** Warden talks to the local GG service on `127.0.0.1` only (your own computer).

All settings, game history and recordings stay on your computer in `%AppData%\Warden`.

## License

[MIT](LICENSE) © Alperen Burhan. Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

*Warden is not affiliated with, endorsed, or sponsored by SteelSeries or MSI.*
