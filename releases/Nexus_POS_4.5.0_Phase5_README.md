# Nexus POS 4.5.0 — Phase 5 source release candidate

This release advances Nexus POS with inventory traceability, procurement control, stocktake, import and modular hardware support while preserving Phases 1–4.

## Included release files

- Base64-split `Nexus_POS_4.5.0_Phase5_Complete_Source.zip` parts, reconstructed and checksum-verified by CI
- `Nexus_POS_4.5.0_Phase5_SHA256.txt`
- Phase 5 implementation, validation and checkpoint reports
- East Africa business recommendations
- Hardware compatibility and inventory import guides

## Validation represented by the package

- Static/source checks: 347/347 passed
- Phase 1–5 focused assertions: 239/239 passed
- JavaScript parser checks: 11/11 passed
- SQLite migration chain: versions 1–10
- Application tables: 75
- API route declarations: 148 with no duplicate method/path pair

The pull-request workflow independently reconstructs the archive, verifies its SHA-256 checksum, reruns the validation suites and builds all three .NET 10 projects with warnings treated as errors on Windows.

## Release boundary

This remains a source release candidate. A production installation still requires Authenticode signing, Inno Setup tests, physical printer/scanner/drawer/display/scale validation, concurrency and power-loss testing, legal review and a controlled customer pilot.

Nexus is not represented as URA/EFRIS approved. No unofficial live Mobile Money, bank, card or EFRIS integration is included.
