# Security Policy

## Supported version

The latest Flair Messenger release is the supported version. Security fixes are not guaranteed to be backported to older releases.

| Version | Supported |
| --- | --- |
| 0.4.x | Yes |
| 0.3.x and older | No |

## Reporting a vulnerability

Do not disclose a security vulnerability in a public GitHub issue.

If the repository has GitHub private vulnerability reporting enabled, use **Security > Report a vulnerability**. Otherwise, contact the repository maintainer privately and provide:

- A clear description of the issue.
- The affected Flair Messenger version.
- Reproduction steps or a minimal proof of concept.
- The potential privacy or security impact.
- Any suggested mitigation.

Never include real Second Life passwords, session credentials, private messages or unredacted `data` files in a report.

## Security design

- The application connects to the official Second Life main-grid login endpoint.
- The project contains no analytics, advertising, telemetry or automatic update checks.
- Remembered settings and local message history are encrypted with Windows DPAPI for the current Windows user.
- Encrypted files are written through an atomic temporary-file replacement.
- The application performs the Second Life logout handshake before closing and uses a blocking shutdown fallback.
- The distributed release excludes the complete `data` folder.

## Authentication limitation

Flair Messenger does not currently support MFA/2FA login challenges. If Second Life requires a second factor during login, the account cannot complete sign-in through this client. Flair Messenger does not bypass or weaken that protection.

Do not disable MFA/2FA solely to use Flair Messenger. Use a supported Second Life client until this capability is implemented. MFA/2FA support is a future objective, but it is not currently scheduled while Flair Messenger remains in active development. See [ROADMAP.md](ROADMAP.md).

## User responsibilities

- Download releases only from a trusted repository or build from source.
- Keep Windows and the .NET runtime updated.
- Do not share the `data` folder.
- Review `data/launcher.log` before sharing it.
- Exit Flair Messenger normally instead of terminating it through Task Manager.
- Protect the Windows account that owns the DPAPI encryption keys.

## Dependency security

Flair Messenger uses LibreMetaverse and its transitive dependencies. Maintainers should review dependency updates, licenses and published security advisories before releasing a new version.

A dependency update should be built and tested before it is merged. Do not replace packaged DLLs with files from an untrusted source.
