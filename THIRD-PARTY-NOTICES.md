# Third-Party Notices

Warden itself is licensed under the MIT License (see `LICENSE`).
The Warden installer and published builds include the following third-party components,
unmodified and as separate libraries (DLLs). Their full license texts are in the `licenses` folder.

| Component | License | Copyright | Source |
|---|---|---|---|
| LibreHardwareMonitorLib | MPL-2.0 | LibreHardwareMonitor contributors | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| DiskInfoToolkit | MPL-2.0 | Florian K. (Blacktempel) | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD | MPL-2.0 | Florian K. (Blacktempel) | https://github.com/Blacktempel/RAMSPDToolkit |
| BlackSharp.Core | MPL-2.0 | Florian K. (Blacktempel) | https://github.com/Blacktempel/BlackSharp |
| HidSharp | Apache-2.0 | James Bellinger | https://www.zer7.com/software/hidsharp |
| NAudio | MIT | Mark Heath and contributors | https://github.com/naudio/NAudio |
| .NET runtime and libraries (self-contained build) | MIT | .NET Foundation and contributors | https://github.com/dotnet/runtime |
| Mono.Posix.NETStandard | MIT | Mono Project / .NET Foundation | https://github.com/mono/mono |

## License texts

- `licenses/MPL-2.0.txt` — Mozilla Public License 2.0. The source code of the MPL-licensed components is
  available at the URLs above.
- `licenses/Apache-2.0.txt` — Apache License 2.0.
- `licenses/NAudio-MIT.txt` — MIT License (NAudio).

## Not bundled

- **PawnIO** (LGPL-2.1 / GPL-2.0) is not shipped with Warden. The installer can optionally install it from
  the official package (`winget install namazso.PawnIO`).
- **MSI Afterburner** and **SteelSeries GG** are not shipped or modified; Warden only talks to them if they are
  installed. Warden is not affiliated with, endorsed or sponsored by MSI or SteelSeries.
