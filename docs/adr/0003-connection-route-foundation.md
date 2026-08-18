# ADR 0003: Fail-closed connection-route foundation

- **Status:** Accepted for Alpha
- **Date:** 2026-08-12

## Decision

Every SSH session resolves one immutable connection route before a network client is created. The
same resolved route is used to construct both the SSH terminal client and the SFTP client. The
session also creates one credential-free `ConnectionCorrelationContext` containing a correlation id, target,
target identity, route id, and route type.

The Alpha implements real Direct, HTTP CONNECT, SOCKS4, SOCKS5, SSH jump, and external
ProxyCommand routes through SSH.NET and a loopback process bridge. No
automatic fallback is attempted. When strict route policy disables Direct, route
resolution fails before the SSH or SFTP client is created.

Unknown or retired saved-route metadata is loaded as an explicit non-connectable
`Unsupported` state with `SAVED_ROUTE_UNSUPPORTED`; malformed metadata becomes `Corrupt`
with `SAVED_ROUTE_CORRUPT`. The UI explains the cause and opens the Host editor. The user
must explicitly choose and save a supported route before another connection can start.

Proxy username and password are transient connection values. They are not added to JSON settings,
saved-host SQLite rows, connection history, or `ConnectionCorrelationContext`.

## Deliberately incomplete

SSH jump and ProxyCommand have Alpha adapters but still require live compatibility, lifecycle,
process-containment, and failure-path evidence. GA also requires explicit proxy-DNS behavior and
tests, actionable local route diagnostics, support bundles, and proof that a failed indirect route
never opens a direct target connection.

