#requires -Version 7.0
<#
.SYNOPSIS
    Enforce the ThopterIoT open-core hard wall against the public repository.

.DESCRIPTION
    The open tool must contain zero monitoring IP and must never phone home. This
    script implements the automatable open-core wall-checks. It is run
    in CI and can be run locally:  pwsh tools/wall-check/Check-Wall.ps1

    Checks:
      (1) No reference to a private connector / MentatNOC implementation assembly.
      (2) No forbidden strings in *.cs (ingest secrets or monitoring vocabulary).
      (3) Egress-guard placeholder — the runtime "no socket to a public address during
          a scan" test lives in Thopter.Tests (category WallCheck); documented here.
      (4) Public-API snapshot placeholder — wire PublicApiAnalyzers on Abstractions.

    Exit code is non-zero if any hard check fails.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..'))
)

$ErrorActionPreference = 'Stop'
$failures = @()

$srcRoot = Join-Path $RepoRoot 'src'
$csFiles = Get-ChildItem -Path $srcRoot -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }

Write-Host "== Wall-check over $($csFiles.Count) C# source files ==" -ForegroundColor Cyan

# --- Check 1: no private-connector / MentatNOC implementation references ---
# The public repo may NAME MentatNOC in prose/comments, but must never reference a
# private connector *type or assembly*. These namespaces live only in the private repo.
$forbiddenRefs = @(
    'Thopter\.Cloud\.MentatNoc',
    'Thopter\.Cloud\.Attestation',
    'Thopter\.Connector'
)
foreach ($file in $csFiles) {
    $text = Get-Content -Raw -LiteralPath $file.FullName
    foreach ($pat in $forbiddenRefs) {
        if ($text -match $pat) {
            $failures += "Check 1: '$($file.Name)' references private connector namespace matching /$pat/."
        }
    }
}

# --- Check 2: forbidden strings in *.cs (secrets + monitoring vocabulary) ---
# Case-insensitive. These belong only in the private connector / server, never in OSS.
$forbiddenStrings = @(
    'x-api-key',
    'BEGIN PRIVATE KEY',
    'attestation',
    'fingerprint',
    'normaliz',          # normalize / normalization (D17 monitoring IP)
    '\btamper\b'
)
foreach ($file in $csFiles) {
    $text = Get-Content -Raw -LiteralPath $file.FullName
    foreach ($pat in $forbiddenStrings) {
        if ($text -imatch $pat) {
            $failures += "Check 2: '$($file.Name)' contains forbidden token matching /$pat/ (monitoring IP or secret must not appear in the open tool)."
        }
    }
}

# --- Check 3: egress guard (runtime) ---
# Enforced by the WallCheck-category test in Thopter.Tests that runs a scan and asserts
# no TCP/UDP connection is made to a non-RFC1918 / non-link-local / non-multicast address.
# TODO: expand coverage once the protocol layer (step 2) lands.
Write-Host "Check 3 (egress guard): enforced at runtime via 'dotnet test --filter Category=WallCheck'." -ForegroundColor DarkGray

# --- Check 4: public API snapshot ---
# TODO: add Microsoft.CodeAnalysis.PublicApiAnalyzers + PublicAPI.Shipped.txt to
# Thopter.Cloud.Abstractions so contract drift fails the build.
Write-Host "Check 4 (public-API snapshot): TODO — wire PublicApiAnalyzers on Thopter.Cloud.Abstractions." -ForegroundColor DarkGray

if ($failures.Count -gt 0) {
    Write-Host "`nWALL-CHECK FAILED:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "`nWall-check passed (checks 1-2 enforced; 3-4 tracked)." -ForegroundColor Green
exit 0
