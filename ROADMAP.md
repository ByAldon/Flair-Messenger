# Flair Messenger Roadmap

This roadmap describes the intended direction of Flair Messenger. It is not a release promise, and priorities may change as the application is tested and developed. Features listed here have no guaranteed delivery date unless a specific release milestone states otherwise.

## Current status

Flair Messenger 0.4.x is an active development preview. Its current focus is reliable text communication on the official Second Life main grid, a clear Windows desktop interface and privacy-conscious local storage.

The client currently supports login without an MFA/2FA challenge, private and group instant messages, friends and groups, offline messages, encrypted local chat history, notifications, unread badges and a logout handshake when the application closes normally.

## Guiding principles

- Protect user privacy and collect no unnecessary data.
- Be honest about security boundaries and unfinished functionality.
- Prefer reliable messaging and logout behavior over cosmetic additions.
- Keep visible application and runtime text in English.
- Keep local data understandable, removable and excluded from shared releases.
- Avoid introducing an MFA/2FA bypass or asking users to weaken account security.

## Planned areas

### Authentication and account security

- Add support for legitimate Second Life MFA/2FA login challenges.
- Research and test the challenge flow supported by Second Life and LibreMetaverse before designing the interface.
- Keep credentials out of logs and improve guidance for users who choose not to remember a password.
- Provide clearer error messages for rejected credentials, unavailable services and unsupported authentication challenges.

MFA/2FA support is a future objective, but it is not a current priority and has no target release while Flair Messenger remains in active development. Until it is implemented, accounts that require a second factor cannot sign in through this client. Users should not disable MFA/2FA merely to use Flair Messenger.

### Messaging reliability

- Expand real-grid testing for private messages, group messages and offline-message retrieval.
- Improve reconnect behavior after temporary network interruptions.
- Make delivery failures and group-session failures clearer in the conversation view.
- Continue preventing protocol-only events, such as typing notifications, from appearing as normal chat messages.

### Friends and groups

- Improve diagnostics and recovery when the friends list is slow or unavailable.
- Refine presence refresh behavior without creating excessive network traffic.
- Improve group-chat session recovery and member information when supported by the underlying protocol.

### Privacy and security

- Maintain a documented threat model for credentials, tokens, local history and release packaging.
- Review dependencies and security advisories before each release.
- Add automated checks that release archives contain no `data` folder, credentials, messages or build artifacts.
- Explore safe export and deletion tools for user-owned local data.

### User experience and accessibility

- Improve keyboard navigation, focus visibility and screen-reader labels.
- Continue testing high-DPI layouts and different Windows scaling settings.
- Add more notification controls without weakening unread-message tracking.
- Improve empty, loading and error states throughout the client.

### Packaging and development

- Continue using the BAT-based launch flow during the development preview because it is easier for both developers and users to inspect, move and run the client files.
- Flair Messenger has not officially launched yet. A standalone `.exe` package is planned for a future release, but there is no confirmed timeline.
- Add broader automated tests for storage, messaging events and window lifecycle behavior.
- Add continuous build and release validation for GitHub.
- Investigate an optional installer and digitally signed releases when the project is mature enough.
- Consider an update mechanism only if it can be implemented transparently and without tracking users.

## Outside the current scope

Flair Messenger is intended to remain a focused messaging client. A full 3D world viewer, voice client, inventory browser and avatar renderer are not current goals.

## Contributing to the roadmap

Suggestions and implementation proposals are welcome through the repository's GitHub issues or discussions. Do not include passwords, session credentials, private messages or an unredacted `data` folder in a request.

Roadmap entries describe intentions rather than commitments. Security, privacy and message reliability may take priority over the order shown above.

