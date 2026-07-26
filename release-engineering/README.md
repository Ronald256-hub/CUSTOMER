# Nexus POS Windows release engineering

This directory preserves the exact corrections that produced a successful self-contained Nexus POS Windows build and passing release smoke test on 27 July 2026.

## Deployment rule

Nexus POS is published self-contained for `win-x64`. Customer computers do **not** need the .NET SDK or a separately installed .NET runtime. Developer tooling should not be installed on shop tills merely to run Nexus.

The release computer requires:

- 64-bit Windows 10 or Windows 11
- .NET 10 SDK
- Inno Setup 6 for installer generation
- Windows SDK SignTool for Authenticode signing
- Node.js LTS for JavaScript parser checks

## Prepare a release computer

Run Administrator PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\release-engineering\INSTALL_BUILD_PREREQUISITES.ps1 `
  -IncludeInnoSetup `
  -IncludeNodeJs
```

For signed commercial releases:

```powershell
.\release-engineering\INSTALL_BUILD_PREREQUISITES.ps1 `
  -IncludeInnoSetup `
  -IncludeNodeJs `
  -IncludeSigningTools
```

The script detects installed components and installs only missing ones through WinGet.

## Build Nexus

Unsigned internal test build:

```powershell
.\release-engineering\BOOTSTRAP_WINDOWS_RELEASE.ps1 `
  -SourceRoot "C:\path\to\Nexus-source" `
  -SkipInstaller
```

Installer build:

```powershell
.\release-engineering\BOOTSTRAP_WINDOWS_RELEASE.ps1 `
  -SourceRoot "C:\path\to\Nexus-source"
```

Signed commercial build:

```powershell
.\release-engineering\BOOTSTRAP_WINDOWS_RELEASE.ps1 `
  -SourceRoot "C:\path\to\Nexus-source" `
  -CertificateThumbprint "YOUR_REAL_40_CHARACTER_CERTIFICATE_THUMBPRINT"
```

The bootstrap checks/installs prerequisites, applies verified fixes, invokes the existing release build, requires its smoke test to pass, and verifies generated artifacts and SHA-256 manifests.

## Customer installation

Place these files together on a trusted installation USB or download folder:

- `INSTALL_NEXUS_POS.ps1`
- `INSTALL_NEXUS_POS.cmd`
- the signed `Nexus_POS_Setup_<version>.exe`

Preferred commercial installation:

```powershell
.\INSTALL_NEXUS_POS.ps1 `
  -InstallerPath ".\Nexus_POS_Setup_4.5.0.exe" `
  -ExpectedSha256 "64_CHARACTER_RELEASE_SHA256" `
  -RequiredPublisherThumbprint "40_CHARACTER_PUBLISHER_THUMBPRINT"
```

The customer bootstrap verifies supported Windows, free disk space, SHA-256, Authenticode signature, exact publisher certificate, installer result, installation location, launcher presence, and launcher signature. Installation reports are stored under:

```text
C:\ProgramData\Nexus POS\Install Logs
```

`-AllowUnsignedTestBuild` is only for controlled development testing and must not be used for customer distribution.

## Verified corrections preserved

`APPLY_KNOWN_WINDOWS_BUILD_FIXES.ps1` safely preserves:

- receipt/invoice HTML quote correction
- explicit `[FromBody]` binding on affected DELETE endpoints
- safe .NET SDK detection
- runtime-specific ReadyToRun restore
- single-file launcher restore
- normalized certificate thumbprints
- longer startup allowance and clearer smoke-test diagnostics

The patch is idempotent: already-correct source is left unchanged.

## Commercial release gate

A successful unsigned test build is not a customer release. Commercial distribution still requires a trusted signing certificate, timestamped signatures, installation/upgrade/uninstall testing, physical printer/scanner/cash-drawer tests, backup/restore tests, and a controlled customer pilot.
