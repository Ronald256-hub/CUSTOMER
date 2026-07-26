# Windows release success checkpoint — 27 July 2026

The controlled Nexus POS 4.0 Windows test build completed successfully on a 64-bit Windows computer using .NET SDK 10.0.302.

## Confirmed successful stages

- runtime-specific .NET restore
- server build with warnings treated as errors
- launcher build with warnings treated as errors
- self-contained `win-x64` ReadyToRun server publish
- self-contained single-file launcher publish
- required file validation
- JavaScript syntax validation
- automated release smoke test
- portable ZIP generation
- SHA-256 manifest generation
- portable package extraction and launch
- strong temporary administrator credential generation

## Smoke-test coverage confirmed

- health endpoint
- mandatory password change
- business profile
- category creation
- product creation
- teller creation
- shift opening
- sale completion
- receipt/invoice document generation
- stock deduction
- audited sale void
- stock restoration
- backup integrity
- reporting

## Defects corrected during the checkpoint

1. Escaped an invalid `document-meta` HTML attribute string in `AuditDocumentWriter.cs`.
2. Restored runtime-specific ReadyToRun compiler packages before `--no-restore` publishing.
3. Added safer .NET SDK detection so missing SDKs produce an actionable error.
4. Added explicit `[FromBody]` binding for affected DELETE endpoint request objects.
5. Increased smoke-test startup allowance and preserved server diagnostics on failure.
6. Normalized the Authenticode certificate thumbprint before signing.

## Release boundary

The successful package was an unsigned development build. It proves the build and core smoke-test pipeline but is not approved for customer distribution until the installer and binaries are Authenticode-signed and the commercial release gate in `README.md` is completed.
