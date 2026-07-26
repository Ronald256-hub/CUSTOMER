# Nexus POS Windows release and customer installation

This directory preserves the Windows corrections that produced a successful self-contained Nexus POS build and passing release smoke test on 27 July 2026.

## Important deployment rule

Nexus POS is published as a self-contained `win-x64` application. The customer computer therefore does **not** need the .NET SDK or a separately installed .NET runtime. Developer SDKs must not be installed on shop tills merely to run Nexus.

The build/release computer requires:

- 64-bit Windows 10/11
- .NET 10 SDK
- Inno Setup 6 when building the installer
- Windows SDK SignTool when applying Authenticode signatures
- Node.js LTS for JavaScript parser validation

## One-command build workstation preparation

Run Administrator PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\release-engineering\INSTALL_BUILD_PREREQUISITES.ps1 `
  -IncludeInnoSetup `
  -IncludeNodeJs
```

For a commercial signed release, also install SignTool:

```powershell
.\release-engineering\INSTALL_BUILD_PREREQUISITES.ps1 `
  -IncludeInnoSetup `
  -IncludeNodeJs `
  -IncludeSigningTools
```

The script detects installed components and uses WinGet only for missing components.

## One-command controlled Windows release

```powershell
.\release-engineering\BOOTSTRAP_WINDOWS_RELEASE.ps1 `
  -SourceRoot "C:\path\to\Nexus-source"
```

For an unsigned internal test build without Inno Setup:

```powershell
.\release-engineering\BOOTSTRAP_WINDOWS_RELEASE.ps1 `
  -SourceRoot "C:\path\to\Nexus-source" `
  -SkipInstaller
```

For a commercial signed build:

```powershell
.\release-engineering\BOOTSTRAP_WINDOWS_RELEASE.ps1 `
  -SourceRoot "C:\path\to\Nexus-source" `
  -CertificateThumbprint "YOUR_REAL_40_CHARACTER_CERTIFICATE_THUMBPRINT"
```

The bootstrap performs prerequisite checks, applies the verified source/build corrections, invokes the release build, requires the smoke test to pass, and verifies the release artifacts and SHA-256 manifest.

## Customer installation

Place these two files together on the customer installation USB or trusted download folder:

- `INSTALL_NEXUS_POS.ps1`
- the signed `Nexus_POS_Setup_<version>.exe`

Run Administrator PowerShell:

```powershell
.\INSTALL_NEXUS_POS.ps1 `
  -InstallerPath ".\Nexus_POS_Setup_4.5.0.exe" `
  -ExpectedSha256 "64_CHARACTER_RELEASE_SHA256" `
  -RequiredPublisherThumbprint "40_CHARACTER_PUBLISHER_THUMBPRINT"
```

The customer installer bootstrap verifies:

- supported 64-bit Windows
- available disk space
- installer SHA-256
- trusted Authenticode signature
- exact publisher certificate when configured
- installer exit code
- installed location
- presence of the Nexus launcher
- launcher publisher signature

It creates an installation report under:

```text
C:\ProgramData\Nexus POS\Install Logs
```

An unsigned build can be installed only when `-AllowUnsignedTestBuild` is deliberately supplied. That option is for controlled development testing and must not be used for customer distribution.

## Verified corrections preserved

`APPLY_KNOWN_WINDOWS_BUILD_FIXES.ps1` safely applies these corrections when an older source tree still needs them:

- escaped receipt/invoice HTML attribute quoting
- explicit `[FromBody]` binding for affected DELETE endpoints
- safe .NET SDK version detection
- runtime-specific ReadyToRun restore before `--no-restore` publish
- single-file launcher restore properties
- normalized certificate thumbprint use
- longer and more useful release smoke-test diagnostics

The patcher is idempotent: already-correct source is left unchanged.

## Release boundary

A successful unsigned test build proves compilation, publishing and functional smoke-test behavior. Commercial deployment additionally requires:

- a trusted code-signing certificate
- timestamped Authenticode signatures
- physical printer/scanner/cash-drawer testing
- clean installation and upgrade testing on representative Windows computers
- backup and restore testing
- customer acceptance and pilot sign-off
