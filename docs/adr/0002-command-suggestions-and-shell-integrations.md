# ADR 0002: Command suggestions and shell integrations

- **Status:** Accepted for Alpha
- **Date:** 2026-08-12

## Decision

Sutty provides client-side REPL suggestions through a text-only provider contract in
`sutty.Core.Plugins`. The built-in provider checks the current session's recent commands first,
then saved Commands ordered by the local command store. A visible suggestion is accepted with
Right Arrow at the end of the input or, when enabled in Settings, with Tab.

The provider contract receives only command text. It receives no SSH session, credentials,
terminal stream, file-system capability, or command execution callback. Providers are registered
by Sutty code; this Alpha does not scan folders or load arbitrary assemblies.

## Remote shell boundary

Remote shell plug-ins remain owned by the remote account. Sutty does not clone repositories,
edit remote startup files, or enable remote plug-ins automatically. In Terminal mode, Right Arrow
and Tab continue to be sent to the PTY, so a user-installed Zsh suggestion plug-in can handle them
normally. Client-side suggestions apply to REPL mode, where Sutty owns the input buffer.

The requested open-source reference is
[`zsh-autosuggestions`](https://github.com/zsh-users/zsh-autosuggestions). Its installation and
remote shell policy are outside Sutty's local application settings.

## Follow-up gates

- Define a signed, versioned provider manifest before third-party binary loading is considered.
- Add per-provider enable/disable, provenance, timeout, and diagnostics.
- Never allow a suggestion provider to execute a command as part of suggestion generation.
- Add confidential-command filtering before cross-session or persistent suggestion sources.

