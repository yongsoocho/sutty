# SshNet.Agent source provenance

This project contains the MIT-licensed SshNet.Agent source needed by Sutty's
Windows OpenSSH Agent integration. It is compiled in this repository against
SSH.NET 2026 instead of suppressing an incompatible NuGet dependency range.

- Upstream: https://github.com/darinkes/SshNet.Agent
- Pinned commit: `30b376d5f0420687b8982de2e783f9f0c43bca23`
- Local changes: the packaging project was replaced with a net10.0 Sutty project
  that references SSH.NET 2026.0.0 directly, and the byte-array reversal helper
  was renamed to avoid a .NET 10 extension-method ambiguity.

Before updating this source, build the adapter against the pinned SSH.NET
version and run `sutty.Core.Security.SelfTest` with and without the Windows
OpenSSH Authentication Agent service available.
