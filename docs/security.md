# Security

- Login is HTTPS POST only. Credentials never enter a query string.
- `MFL_USER_ID` is attached only as an HTTP cookie and is redacted from every log/tool/journal surface.
- The Windows app stores the cookie in PasswordVault under `MflManager.MflSession`. SQLite contains only league data and sanitized operations.
- Protected responses may contain owner email/accounting data; agent tools return only the fields required by each typed operation.
- The broker uses `NamedPipeServerStream` with `CurrentUserOnly`; it is available only while the desktop process or explicit local broker is running.
- Every tool requires an explicit league id after selection. Historical seasons reject mutation.
- Confirmation is on by default for add/drop, waivers, picks, trade responses/proposals, and commissioner actions. The agent cannot alter policy.
- Reads use bounded retry; writes never retry automatically because their outcome could be ambiguous.
- Logs use correlation IDs and exception types, not request bodies, passwords, cookies, or owner PII.
- Optional 1Password loading invokes the local `op` CLI for only the `username` and `password` fields of the fixed `Myfantasyleague` item; values are held in memory and passed directly to the normal login flow.
- Contract/integration fixtures are synthetic. Live mutations require an explicit test league and `MFL_ALLOW_TEST_MUTATIONS=1`.

Release audit: run tests, search for `PASSWORD`, `MFL_USER_ID`, `Cookie`, and URL construction, inspect the package, and verify no `.db`, `.pfx`, `.cer`, logs, or real MFL payloads are included.
