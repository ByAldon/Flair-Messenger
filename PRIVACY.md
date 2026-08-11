# Privacy

Flair Messenger is designed as a local, privacy-focused Second Life messaging client. This document explains what information the application processes, where it goes and how users can remove it.

## Summary

- No Flair Messenger account is created.
- No developer-operated server is used.
- No analytics, advertising, telemetry or user tracking is included.
- No automatic crash reports are uploaded.
- No automatic update checks are performed.
- Login and messaging traffic goes directly through LibreMetaverse to the official Second Life services.
- Remembered settings and local message history are encrypted for the current Windows user.
- Local information can be removed by deleting the `data` folder while the application is closed.

## Information processed

### Account credentials

Flair Messenger processes the Second Life login name and password entered on the login screen. These values are passed to LibreMetaverse so it can authenticate with the official Second Life main-grid login endpoint.

The password is never intentionally written to logs. When **Remember details** is disabled, the application saves no reusable password. When it is enabled, the complete settings record is encrypted with Windows Data Protection API (DPAPI) using `DataProtectionScope.CurrentUser`.

### Messages and conversation metadata

Private and group instant messages are exchanged with Second Life through LibreMetaverse. Flair Messenger stores conversation identifiers, conversation names, sender names, message text and timestamps locally so conversations remain visible after a restart.

The complete local history file is encrypted with Windows DPAPI for the current Windows user.

### Friends and groups

Friend names, online status and group information are requested from Second Life after login. They are held in memory for the active session and are not written to a separate local database by Flair Messenger.

### Notifications

The Notifications page contains status and message notifications generated during the current session. These notifications are held in memory and are discarded when the application closes.

### Diagnostic information

The launcher redirects application output to `data/launcher.log`. This file is local and is not uploaded automatically. Review it before sharing because third-party library diagnostics could contain account or session-related context.

## Local files

| File | Contents | Storage protection |
| --- | --- | --- |
| `data/settings.dat` | Remember preference, login name, password and login location | Entire file encrypted with DPAPI for the current Windows user |
| `data/messages.dat` | Conversation IDs, names, senders, messages and timestamps | Entire file encrypted with DPAPI for the current Windows user |
| `data/launcher.log` | Local startup and diagnostic output | Plain text |

Temporary `.tmp` files may briefly exist while an encrypted file is written atomically. Their contents are encrypted before they are written.

## Legacy-data migration

Versions before 0.4.0 used `settings.json` and `messages.json`. Version 0.4.0 reads these files locally and writes encrypted replacements named `settings.dat` and `messages.dat`.

A legacy JSON file is deleted only after the encrypted replacement has been written successfully. If migration fails, the original file is retained to avoid data loss.

## Encryption scope and limitations

DPAPI protection is tied to the Windows user account that created the file. This helps prevent another Windows user or a copied application folder from reading the saved information.

DPAPI does not protect information after the current Windows user has signed in and the application has decrypted it for use. Malware or another process running with the same user privileges may still be able to access application memory or interact with local files. Keep Windows and security software up to date.

Encrypted `.dat` files generally cannot be moved to another Windows account or computer and remain readable there. Delete them and sign in again instead.

## Network communication

The Flair Messenger source code defines one login endpoint:

```text
https://login.agni.lindenlab.com/cgi-bin/login.cgi
```

After login, LibreMetaverse communicates with Second Life simulator and messaging services required for the active session. Flair Messenger does not add analytics, advertising, telemetry, tracking pixels or unrelated network destinations.

Second Life is a third-party service. Its handling of account and session information is governed by Linden Lab's own terms and privacy documentation.

- [Linden Lab Privacy Policy](https://lindenlab.com/privacy)
- [Second Life Terms and Conditions](https://lindenlab.com/legal/second-life-terms-and-conditions)
- [Policy on Third-Party Viewers](https://secondlife.com/corporate/third-party-viewers)

## Retention and deletion

Local settings, history and logs remain until the user deletes them. Flair Messenger does not impose a retention period.

To remove all local Flair Messenger information:

1. Exit Flair Messenger normally so the avatar is logged out.
2. Open the Flair Messenger application folder.
3. Delete the complete `data` folder.

The application creates a new empty `data` folder when required.

## Sharing and publishing

Never upload or distribute the `data` folder. In particular, do not attach its files to GitHub issues or include them in a release ZIP.

Before sharing a screenshot, log or bug report, remove account names, avatar names, UUIDs, group names, message contents and other identifying information.

## Changes to this document

Privacy-related behavior changes should be documented here and in the README version history. Contributors should treat any new analytics, external service, persistent identifier or network destination as a privacy-impacting change requiring explicit review.
