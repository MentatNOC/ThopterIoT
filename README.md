<!-- markdownlint-disable MD013 MD033 -->
# ThopterIoT

**A free, open-source Windows tool that discovers the IoT devices and IP cameras on a local network - IP, MAC + vendor (OUI), and the standard discovery protocols they speak - with no driver, no admin rights, and no telemetry.**

ThopterIoT is a local scanner. It runs one-shot, reports what it finds, and stops. It never phones home. Everything it does is unauthenticated, standard, and one-shot - the kind of thing any device on the network already answers.

> ThopterIoT is the free, open front door to [MentatNOC](https://mentatnoc.com) camera-fleet monitoring. The scanner is fully open source and self-contained; an optional paid connector (separate download) can send a scan's findings to the MentatNOC cloud for reports and monitoring. The open tool contains **no** cloud/monitoring code - see [The wall](#the-wall).

## What it finds

- **Every host with a MAC**, driver-free - a ping sweep seeds the OS ARP cache, then the IPv4 neighbor table is read via the in-box IP Helper API (`GetIpNetTable2`). No Npcap, no raw sockets, no elevation.
- **The vendor for each MAC**, offline, via the embedded IEEE OUI registry (MA-L/MA-M/MA-S, longest-prefix match). Locally-administered / randomized MACs are flagged as such.
- *(coming in the protocol layer)* ONVIF WS-Discovery, SSDP, mDNS/DNS-SD, and a light TCP port/banner scan, fused into an offline device type/model guess.

## Status

Early build. **Working today:** the driver-free IP + MAC + vendor discovery engine (`Thopter.Discovery`) and a headless scan mode. The Avalonia GUI and the protocol-discovery layer are in progress.
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

## The wall

ThopterIoT does **light identify only**. It is a hard, structural rule that the open tool contains no monitoring intellectual property and leaks nothing about how MentatNOC monitors:

**Allowed** (unauthenticated, standard, one-shot): L2 MAC + IEEE OUI; ICMP reachability; ONVIF WS-Discovery scopes; SSDP + the device's own advertised description; mDNS PTR/SRV/TXT/A; TCP connect open/closed; HTTP `Server` / `WWW-Authenticate`; TLS cert CN; RTSP `OPTIONS`.

**Never, in the open tool:** any authenticated call, any media (RTSP `DESCRIBE`/`PLAY`, snapshots, frames), any SNMP, any continuous observation (health, uptime, tamper, baselining, polling loops), any compliance logic, and **any telemetry / call-home / attestation**. The paid connector is a separate, out-of-process signed executable in a separate private repository; the dependency arrow only ever points private → public. CI enforces this (see `.github/workflows/ci.yml`).

## Build

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
dotnet test  -c Release
```

## License

[Apache-2.0](LICENSE), with a Developer Certificate of Origin (DCO) sign-off on contributions. See [`NOTICE`](NOTICE) and [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). "ThopterIoT", "Thopter", and "MentatNOC" are trademarks of MentatNOC; the license does not grant permission to use them.
