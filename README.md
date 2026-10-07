# Warden 🛡️

<div align="center">
  <img src="Assets/icon.png" width="128" height="128" alt="Warden Logo" />
</div>

**Warden** is an all-in-one gaming companion and system telemetry suite built with WPF and .NET 10. It quietly runs in the Windows system tray and provides automated profile switching, GPU frequency control, audio device management, and real-time hardware telemetry.

---

## ✨ Features

* **🎧 SteelSeries Sonar Auto EQ:** Automatically detects the foreground game and switches your SteelSeries Sonar "Game" channel to the configured EQ preset in real-time.
* **📊 Hardware Telemetry & Monitor:** Live monitoring of CPU/GPU temperatures, clock speeds (MHz), wattage (W), and usage (%). Includes a real-time 60-second chart, quick-glance ⭐ Favorites, **temperature alarms** and **CSV session recording** (manual or automatic while a game is running).
* **⚡ MSI Afterburner GPU Control:** Monitors the GPU clock (NVIDIA, AMD or Intel) in the background and automatically applies a target MSI Afterburner profile when a user-defined MHz limit is exceeded.
* **🔇 Audio Device Enforcer:** Keeps unwanted or phantom virtual audio devices permanently disabled / hidden in the background.
* **🚀 Zero-Flicker Tray Architecture:** Runs silently in the system tray, wakes up with single-click via global IPC, and launches at Windows startup without UAC prompts.
* **🎮 Dashboard:** A HUD-style home screen with the active game and session time, CPU/GPU ring gauges, a 60-second temperature graph, memory/hot spot/fan/power tiles and status chips.
* **🩺 System Status:** Shows on first launch which requirements (SteelSeries GG/Sonar, MSI Afterburner, PawnIO, GPU sensors, install location) are present and what to install for missing features.
* **🔄 Auto Updates:** Checks GitHub Releases and installs new versions after SHA256 verification.
* **🌍 Bilingual Support:** Full Turkish (TR) and English (EN) language support.

---

## 🚀 How It Works

Warden integrates directly with:
1. **SteelSeries GG Local API:** Reads `coreProps.json` to communicate with the local Sonar HTTP server.
2. **LibreHardwareMonitor:** Reads hardware telemetry with low-level kernel driver support.
3. **MSI Afterburner Shared Memory:** Communicates with MSI Afterburner hardware monitor and profile switcher.
4. **Windows Audio Core API:** Manages playback endpoints and enforcement.

---

## 📦 Installation & Running

1. Download **`Warden-Setup-x.y.z.exe`** from the [Releases](https://github.com/AlperenNyKs/Warden/releases) page.
2. Run it. Warden installs to `C:\Program Files\Warden` (self-contained, no .NET runtime needed) and can optionally
   install the **PawnIO** driver (required for CPU temperature/clock/power sensors).
3. Warden starts in the system tray. Turn on **Start with Windows** in Settings if you want it at logon.

Upgrading: Warden checks GitHub Releases at startup and every 6 hours (Settings → Updates) and offers to install a new version; the setup is verified against the release's SHA256 digest before it runs. You can also just run a newer setup manually; your settings in `%AppData%\Warden` are kept.
Uninstalling also removes the "Warden" startup task.

*To build from source:*
```bash
git clone https://github.com/AlperenNyKs/Warden.git
cd Warden
dotnet build -c Release
```

*To build the installer locally* (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)):
```powershell
dotnet publish Warden.csproj -c Release -r win-x64 --self-contained true -o publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\Warden.iss
# -> installer-output\Warden-Setup-1.0.0.exe
```

*Releasing:* bump `<Version>` in `Warden.csproj` and merge to `main`; if there is no `v<Version>` release yet, CI builds the setup and publishes it as a GitHub Release (pushing a `v*` tag works too).

> **"Start with Windows" requires a protected install folder.** Warden is launched at logon with administrator
> rights (Task Scheduler, `/rl highest`). If its folder is writable by standard users (e.g. `D:\Warden\bin\...`),
> any program could swap `Warden.exe` or a DLL and get admin rights, so Warden refuses to register the task there.
> The installer puts it in Program Files, which is safe.

---

## 💻 Technologies Used

* **C# / .NET 10.0 Windows Desktop (WPF)**
* **LibreHardwareMonitorLib** (Hardware Sensors & Telemetry)
* **MSI Afterburner** (command-line profile switching)
* **NAudio & CoreAudioApi** (Audio Device Management)

---

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

---

## Privacy policy

Warden does not collect, store remotely or sell any personal data, and it contains no telemetry or analytics.
It will not transfer any information to other networked systems unless specifically requested by the user or the
person installing or operating it, with these exceptions:

* **Update check:** at startup and every 6 hours Warden asks the GitHub API (`api.github.com`) for the latest
  release of this repository, and downloads the new setup from GitHub when you choose to install it. You can turn
  this off in Settings → Updates. GitHub's [privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement) applies to these requests.
* **SteelSeries GG / Sonar:** Warden talks to the local GG service on `127.0.0.1` only (your own computer).

All settings, game history and recordings stay on your computer in `%AppData%\Warden`.

---

## 📝 License

This project is licensed under the MIT License - see the LICENSE file for details.

*Disclaimer: This project is not affiliated with, endorsed, or sponsored by SteelSeries or MSI.*
