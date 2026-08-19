# Third-Party Notices

ThopterIoT is distributed under the Apache License 2.0 (see `LICENSE`). It includes
or depends on the third-party material listed below. This file satisfies the
attribution requirements of those materials.

---

## 1. IEEE OUI Registry data (embedded dataset)

**What:** `src/Thopter.Discovery/Oui/oui.tsv` is generated from the IEEE Registration
Authority's public **MA-L, MA-M and MA-S** assignment listings, used for offline
MAC-address → vendor (OUI) lookup.

**Source:** https://standards-oui.ieee.org/
- MA-L: https://standards-oui.ieee.org/oui/oui.csv
- MA-M: https://standards-oui.ieee.org/oui28/mam.csv
- MA-S: https://standards-oui.ieee.org/oui36/oui36.csv

**Regeneration:** `tools/update-oui/Update-Oui.ps1` downloads the current CSVs and
rebuilds `oui.tsv`. Only the derived two-column table (assignment → organization
name) is embedded; addresses and other columns are dropped.

**Terms:** The IEEE public listings are published by the IEEE Registration Authority
for public reference. This project embeds only the factual assignment→organization
mapping. IEEE is not affiliated with, and does not endorse, ThopterIoT. We deliberately
use IEEE's own published CSVs (not the Wireshark `manuf` file) to keep the dataset
provenance and licensing clean.

---

## 2. NuGet package dependencies

The GUI application (`Thopter.App`) depends on the following packages, each under its
own license. Their full license texts are distributed inside their respective NuGet
packages and are reproduced in the published build's licenses folder.

| Package | License | Project |
|---|---|---|
| Avalonia (and Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter) | MIT | https://github.com/AvaloniaUI/Avalonia |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |

`Thopter.Discovery` and `Thopter.Cloud.Abstractions` have **no third-party runtime
package dependencies** — they use only the .NET base class library.

---

## 3. Reference material — learned-from, NOT included

The following projects were studied for architecture and technique only. **No code
from any of them is copied, forked, linked, or bundled into ThopterIoT.** They are
listed for transparency; none of their licenses apply to this repository.

- **iot-inspector-client** (Apache-2.0) — discovery architecture reference.
- **iot-risk-detect** (no license / all rights reserved) — concept reference only.
- **LibreNMS** (GPLv3) — SNMP/auto-discovery data-model concepts only; no code used.
- **Nmap** (NPSL) — host-discovery and service-fingerprint *techniques* only; no Nmap
  code, binary, or service-probe database is copied or bundled.
