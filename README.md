<!-- markdownlint-disable MD013 MD033 -->
# ThopterIoT

**A free, open-source Windows tool that discovers the IoT devices and IP cameras on a local network - IP, MAC + vendor (OUI), and the standard discovery protocols they speak - with no driver, no admin rights, and no telemetry.**

ThopterIoT is a local scanner. It runs one-shot, reports what it finds, and stops. It never phones home. Everything it does is unauthenticated, standard, and one-shot - the kind of thing any device on the network already answers.

> ThopterIoT is the free, open front door to [MentatNOC](https://mentatnoc.com) camera-fleet monitoring. The scanner is fully open source and self-contained; an optional paid connector (separate download) can send a scan's findings to the MentatNOC cloud for reports and monitoring. The open tool contains **no** cloud/monitoring code - see [what it does on your network](#what-it-does-on-your-network).

## What it finds

- **Every host with a MAC**, driver-free - a ping sweep seeds the OS ARP cache, then the IPv4 neighbor table is read via the in-box IP Helper API (`GetIpNetTable2`). No Npcap, no raw sockets, no elevation.
- **The vendor for each MAC**, offline, via the embedded IEEE OUI registry (MA-L/MA-M/MA-S, longest-prefix match). Locally-administered / randomized MACs are flagged as such.
- **The discovery protocols each device speaks** - ONVIF WS-Discovery, SSDP, mDNS/DNS-SD, and a light unauthenticated TCP port/banner scan - fused offline into a device type and model guess.
- **A hostname for hosts that expose one** - from mDNS/SSDP where advertised, and otherwise from a direct NetBIOS node status query (UDP 137) that recovers Windows machine names on the LAN. No public DNS is ever queried.

## Status

Early build. **Working today:** the driver-free IP + MAC + vendor discovery engine, the protocol-discovery layer (ONVIF / SSDP / mDNS / TCP port scan + NetBIOS name resolution), and the Avalonia desktop GUI - a live device grid (IP, MAC, vendor, type/model, discovered-via, hostname, open ports) with a right-click row menu (copy IP, copy MAC, open in browser), a detail flyout, and CSV/JSON export. A headless `scan` mode is also available.
## Download

**[Download for Windows](https://github.com/MentatNOC/ThopterIoT/releases/latest/download/ThopterIoT-win-x64.exe)** - a single self-contained exe, no install and no admin. Or browse [all releases](https://github.com/MentatNOC/ThopterIoT/releases/latest).

The build is not code-signed yet, so Windows SmartScreen may warn on first run - choose **More info -> Run anyway**. Signing is planned.

## Try the headless scan

```bash
dotnet run --project src/Thopter.App -- scan
```

```text
IP               MAC                Vendor
------------------------------------------------------------------------------
10.10.10.1       E0:23:FF:9E:BE:7F  Fortinet, Inc.
10.10.10.2       48:1B:A4:D8:72:46  Cisco Systems, Inc
10.10.10.60      1C:FC:17:10:05:98  Cisco Systems, Inc
```

Add `--json` for machine-readable output.

## Repository layout

| Project | What it is |
|---|---|
| `src/Thopter.Discovery` | The discovery engine. UI-agnostic class library, NativeAOT-clean, no internet egress. |
| `src/Thopter.Cloud.Abstractions` | The public open-core seam: `IFindingsSink` + the `thopter.findings/v1` DTOs. Published to NuGet. Zero MentatNOC references. |
| `src/Thopter.App` | The Avalonia desktop GUI (also runs the headless `scan` mode). |
| `test/Thopter.Tests` | xUnit tests. |
| `tools/update-oui` | Regenerates the embedded IEEE OUI table. |

## What it does on your network

ThopterIoT does light identify only. Every request it makes is unauthenticated and standard - the kind of thing a device already answers for anyone on the LAN:

- L2 MAC + IEEE OUI vendor
- ICMP reachability
- ONVIF WS-Discovery scopes
- SSDP, plus the description a device advertises about itself
- mDNS PTR/SRV/TXT/A, and NetBIOS node status (UDP 137) for a machine name
- TCP connect (open/closed) with light banners: HTTP `Server` / `WWW-Authenticate`, TLS certificate CN, RTSP `OPTIONS`

It never logs in, never pulls video (no RTSP `DESCRIBE` / `PLAY`, snapshots, or frames), never speaks SNMP, and never watches anything over time - it scans once and stops. There is no telemetry and no call-home.

Reports, history, and fleet monitoring live in the optional MentatNOC connector, a separate download. This repository holds none of that code, and CI keeps it that way (`.github/workflows/ci.yml`).

## Build

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
dotnet test  -c Release
```

## License

[Apache-2.0](LICENSE), with a Developer Certificate of Origin (DCO) sign-off on contributions. See [`NOTICE`](NOTICE) and [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). "ThopterIoT", "Thopter", and "MentatNOC" are trademarks of MentatNOC; the license does not grant permission to use them.
