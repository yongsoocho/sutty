# Disposable OpenSSH lab for `SSH-LIVE-001`

This test-owned Docker lab exposes one Password-only OpenSSH 9.6p1 target and one silent TCP blackhole. Its Ubuntu base image is digest-pinned, its test packages come from the `20260818T000000Z` Ubuntu snapshot, and the bootstrap installs exact `ca-certificates` and `openssl` versions before snapshot access. It creates a new runtime password and host keys for every container, records only bounded session categories (`exec`, `shell`, `sftp`, `other`), and removes its container and secret after the run. No credential or raw host key belongs in the repository or an evidence bundle.

Run the complete Direct Password gate from a completely clean candidate commit with the exact `Sutty-v0.1.0-alpha.4-win-x64.zip`. The script requires `-Commit` to equal `HEAD` and, when available, requires that commit to be contained in `origin/main`. It performs a locked restore into a fresh physical artifacts directory, then a non-incremental build of the commit's test harness into a separate fresh verified output directory. This keeps both NuGet-generated `obj` inputs and build outputs outside ignored repository directories. It validates every ZIP entry, then requires and atomically replaces every staged root runtime DLL with the exact candidate entry; only the commit-owned root `sutty.LiveServer.SelfTest.dll` may be absent from the candidate. This includes the Sutty Core/agent, SSH.NET, cryptography, dependency-injection, and logging runtime assemblies; every replacement is SHA-256 compared again before execution. Nested resource assemblies remain outside this root-DLL closure. The test harness DLL remains from the clean commit. The in-process gate still independently hashes the ZIP and rejects it unless root `BUILDINFO.txt` names the same commit/x64 architecture and the executing `sutty.Core.dll` is byte-identical to the root ZIP entry.

```powershell
.\tests\live-server\openssh\Invoke-Gate.ps1 `
  -PackagePath C:\approved-candidate\Sutty-v0.1.0-alpha.4-win-x64.zip `
  -Commit (git rev-parse HEAD) `
  -EvidenceOutputRoot C:\approved-evidence-candidates
```

The gate writer always creates a candidate bundle with `redaction_reviewed: false`; no command-line or environment input can pre-approve it. After a human has inspected the complete candidate for identifying or secret material, promote it into a fresh reviewed bundle without editing or rerunning the source gate:

```powershell
.\.github\scripts\Review-LiveEvidence.ps1 `
  -SourceManifestPath C:\approved-evidence-candidates\<candidate>\manifest.yml `
  -DestinationRoot C:\approved-reviewed-evidence `
  -ReviewerId github-your-public-actor `
  -ReviewedAtUtc 2026-08-21T12:34:56.000Z `
  -PrivacyReview Confirmed `
  -ExpectedCommit <commit> `
  -ExpectedPackageSha256 <sha256> `
  -RequiredGateId SSH-LIVE-001 `
  -RequiredResult Pass
```

The promotion records the exact source `manifest.yml` and every declared source file in ordinal name order, hashes their canonical `<sha256> <size_bytes> <name>\n` inventory, and writes `review.json` plus new `redaction_reviewed: true` manifest/summary files in a deterministic fresh directory. It refuses an already reviewed source or an existing destination. Validate that specific new bundle with `.github/scripts/Assert-LiveEvidence.ps1 -ManifestPath <reviewed-bundle\manifest.yml> -ExpectedCommit <commit> -ExpectedPackageSha256 <sha256> -RequiredGateId SSH-LIVE-001 -RequiredResult Pass`.

This gate proves the Direct+Password Core path against the exact package bytes. It does not prove that the packaged UI starts; retain a separate `PKG-001` exact-package startup result.
