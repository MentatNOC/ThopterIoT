# Contributing to ThopterIoT

Thanks for helping. ThopterIoT is [Apache-2.0](LICENSE) licensed and uses the
**Developer Certificate of Origin (DCO)** - not a CLA.

## Developer Certificate of Origin

Every commit must be signed off. By signing off you certify the [DCO 1.1](https://developercertificate.org/):
that you wrote the change or have the right to submit it under the open-source license.

Add the sign-off automatically with:

```bash
git commit -s -m "your message"
```

That appends a line to your commit message:

```text
Signed-off-by: Your Name <you@example.com>
```

Use your real name and a reachable email. Unsigned commits will be asked to amend.

## The hard wall (please read before proposing discovery features)

ThopterIoT is the open, commodity front end to MentatNOC. To keep it clean and keep
contributions mergeable, the open tool does **light, unauthenticated, one-shot identify
only**. In short:

- **Yes:** L2 MAC + IEEE OUI, ICMP reachability, ONVIF WS-Discovery scopes, SSDP, mDNS,
  TCP-connect open/closed, and light banners (HTTP `Server`, TLS CN, RTSP `OPTIONS`).
- **No:** any authenticated call, any media/frames, any SNMP, any continuous observation
  (health, uptime, tamper, baselining, polling loops), any compliance logic, and **any
  telemetry / call-home**.

CI enforces this (`tools/wall-check/Check-Wall.ps1` and the `WallCheck` test category).
PRs that add outbound network egress, monitoring semantics, or cloud/connector code to
the open repo will be declined - that work belongs in the separate private connector.

## Building and testing

Requires the .NET 10 SDK.

```bash
dotnet build -c Release      # warnings are errors; keep it AOT/trim clean
dotnet test  -c Release
pwsh tools/wall-check/Check-Wall.ps1
```

Keep the discovery engine NativeAOT-clean: source-generated P/Invoke and JSON, no
reflection-based serialization, and compiled bindings only in the Avalonia app.
