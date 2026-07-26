# Nexus POS Security Model

## Trust boundaries

Nexus POS is a local-first application. The server owns the SQLite database and document archive. Browsers are untrusted clients. Teller accounts are restricted to their assigned operational workflows; administrator endpoints require an authenticated, active administrator session.

## Implemented controls

- ASP.NET Core password hashing; no plaintext passwords in the database.
- Strong randomly generated first-run and reset passwords.
- Mandatory password change for temporary credentials.
- Failed-login counters, timed account lockout and login throttling.
- Opaque random session tokens stored as hashes, HttpOnly cookies, expiry and revocation.
- Administrator-only account, inventory, settings, backup, reporting and void operations.
- Same-origin checks for state-changing API requests and an explicit trusted-origin policy.
- Restrictive browser security headers and generic production error responses.
- SQLite foreign keys, transactions, constraints, optimistic versions, immutable financial history and audited corrections.
- Verified database backups with SHA-256 and SQLite integrity checks.
- HTTPS, SHA-256, Authenticode and exact publisher-certificate verification for updates and customer installers.

## Operational requirements

- Keep Windows and Nexus POS updated through signed releases.
- Use unique accounts; do not share the administrator account with tellers.
- Protect and then delete FIRST_LOGIN_CREDENTIALS.txt after the mandatory password change.
- Restrict physical access and use full-disk encryption where appropriate.
- Do not expose the POS port directly to the public internet.
- Back up daily to encrypted offline or versioned storage and test restoration.
- Review audit records and cash variances routinely.
- Never send customer passwords or live databases through public support channels.

## Release boundary

A customer release must pass Windows compilation, automated smoke tests, installer and upgrade testing, Authenticode signing, malware scanning, physical device testing, backup restoration and customer acceptance before deployment.
