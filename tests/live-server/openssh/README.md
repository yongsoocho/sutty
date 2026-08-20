# Disposable OpenSSH lab for `SSH-LIVE-001`

This test-owned Docker lab exposes one Password-only OpenSSH 9.6p1 target and one silent TCP blackhole. It creates a new runtime password and host keys for every container, records only bounded session categories (`exec`, `shell`, `sftp`, `other`), and removes its container and secret after the run. No credential or raw host key belongs in the repository or an evidence bundle.

Run the complete Direct Password gate from a completely clean candidate commit with the exact `Sutty-v0.1.0-alpha.4-win-x64.zip`. The script requires `-Commit` to equal `HEAD` and, when available, requires that commit to be contained in `origin/main`. It performs a locked restore into a fresh physical artifacts directory, then a non-incremental build of the commit's test harness into a separate fresh verified output directory. This keeps both NuGet-generated `obj` inputs and build outputs outside ignored repository directories. It validates every ZIP entry, then requires and atomically replaces every staged root runtime DLL with the exact candidate entry; only the commit-owned root `sutty.LiveServer.SelfTest.dll` may be absent from the candidate. This includes the Sutty Core/agent, SSH.NET, cryptography, dependency-injection, and logging runtime assemblies; every replacement is SHA-256 compared again before execution. Nested resource assemblies remain outside this root-DLL closure. The test harness DLL remains from the clean commit. The in-process gate still independently hashes the ZIP and rejects it unless root `BUILDINFO.txt` names the same commit/x64 architecture and the executing `sutty.Core.dll` is byte-identical to the root ZIP entry.

```powershell
.\tests\live-server\openssh\Invoke-Gate.ps1 `
  -PackagePath C:\approved-candidate\Sutty-v0.1.0-alpha.4-win-x64.zip `
  -Commit (git rev-parse HEAD) `
  -EvidenceOutputRoot C:\approved-evidence-candidates
```

The default bundle has `redaction_reviewed: false`. Inspect the complete generated bundle, validate that it contains no identifying or secret material, and run a new immutable gate with `-RedactionReviewed` only when a human is making that attestation. Validate the specific reviewed bundle with `.github/scripts/Assert-LiveEvidence.ps1 -ManifestPath <bundle\manifest.yml> -ExpectedCommit <commit> -ExpectedPackageSha256 <sha256> -RequiredGateId SSH-LIVE-001 -RequiredResult Pass`.

This gate proves the Direct+Password Core path against the exact package bytes. It does not prove that the packaged UI starts; retain a separate `PKG-001` exact-package startup result.
