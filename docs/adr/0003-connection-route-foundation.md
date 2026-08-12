# ADR 0003: Fail-closed connection-route foundation

- **Status:** Accepted for Alpha
- **Date:** 2026-08-12

## Decision

Every SSH session resolves one immutable connection route before a network client is created. The
same resolved route is used to construct both the SSH terminal client and the SFTP client. The
session also creates one credential-free `AuditContext` containing a correlation id, target,
target identity, route id, and route type.

The Alpha implements real Direct, HTTP CONNECT, SOCKS4, and SOCKS5 routes through SSH.NET. No
automatic fallback is attempted. When enterprise mode or a route policy disables Direct, route
resolution fails before the SSH or SFTP client is created.

Proxy username and password are transient connection values. They are not added to JSON settings,
saved-host SQLite rows, connection history, or `AuditContext`.

## Deliberately incomplete

SSH jump, centrally audited gateway, and external proxy-command routes are domain values but do
not have adapters yet. Selecting one through code fails explicitly. GA also requires managed
gateway profiles, explicit proxy-DNS behavior and tests, policy distribution, route timeline UI,
central audit evidence, support bundles, and proof that a failed gateway never opens a direct
target connection.

