# Warden 🛡️

<div align="center">
  <img src="Assets/icon.png" width="128" height="128" alt="Warden Logo" />
</div>

**Warden** is an all-in-one gaming companion and system telemetry suite built with WPF and .NET 10. It quietly runs in the Windows system tray and provides automated profile switching, GPU frequency control, audio device management, and real-time hardware telemetry.

---

## ✨ Features

* **🎧 SteelSeries Sonar Auto EQ:** Automatically detects the foreground game and switches your SteelSeries Sonar "Game" channel to the configured EQ preset in real-time.
* **📊 Hardware Telemetry & Monitor:** Live monitoring of CPU/GPU temperatures, clock speeds (MHz), wattage (W), and usage (%). Includes a real-time 60-second chart and quick-glance ⭐ Favorites system.
* **⚡ MSI Afterburner GPU Control:** Monitors GPU clock speeds in the background and automatically applies target MSI Afterburner profiles when user-defined MHz limits are exceeded.
* **🔇 Audio Device Enforcer:** Keeps unwanted or phantom virtual audio devices permanently disabled / hidden in the background.
* **🚀 Zero-Flicker Tray Architecture:** Runs silently in the system tray, wakes up with single-click via global IPC, and launches at Windows startup without UAC prompts.
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

1. Build or download the latest release.
2. Run **`Warden.exe`**.
3. Warden will minimize to your system tray. Click the tray icon to open the dashboard.

*To build from source:*
```bash
git clone https://github.com/AlperenNyKs/Warden.git
cd Warden
dotnet build -c Release
```

---

## 💻 Technologies Used

* **C# / .NET 10.0 Windows Desktop (WPF)**
* **LibreHardwareMonitorLib** (Hardware Sensors & Telemetry)
* **NvAPIWrapper & MSI Afterburner SDK**
* **NAudio & CoreAudioApi** (Audio Device Management)

---

## 📝 License

This project is licensed under the MIT License - see the LICENSE file for details.

*Disclaimer: This project is not affiliated with, endorsed, or sponsored by SteelSeries or MSI.*
