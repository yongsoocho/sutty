# Disposable OpenSSH lab for `SSH-LIVE-001`

This test-owned Docker lab exposes one Password-only OpenSSH 9.6p1 target and one silent TCP blackhole. It creates a new runtime password and host keys for every container, records only bounded session categories (`exec`, `shell`, `sftp`, `other`), and removes its container and secret after the run. No credential or raw host key belongs in the repository or an evidence bundle.

Run the complete Direct Password gate from a completely clean candidate commit with the exact `Sutty-v0.1.0-alpha.4-win-x64.zip`. The script requires `-Commit` to equal `HEAD` and, when available, requires that commit to be contained in `origin/main`. The harness independently hashes the ZIP and rejects it unless root `BUILDINFO.txt` names the same commit/x64 architecture and the executing `sutty.Core.dll` is byte-identical to the root ZIP entry.

```powershell
.\tests\live-server\openssh\Invoke-Gate.ps1 `
  -PackagePath C:\approved-candidate\Sutty-v0.1.0-alpha.4-win-x64.zip `
  -Commit (git rev-parse HEAD) `
  -EvidenceOutputRoot C:\approved-evidence-candidates
```

The default bundle has `redaction_reviewed: false`. Inspect the complete generated bundle, validate that it contains no identifying or secret material, and run a new immutable gate with `-RedactionReviewed` only when a human is making that attestation. Validate a reviewed result with `.github/scripts/Assert-LiveEvidence.ps1 -EvidenceRoot <root> -ExpectedCommit <commit> -ExpectedPackageSha256 <sha256> -RequiredResult Pass`.

This gate proves the Direct+Password Core path against the exact package bytes. It does not prove that the packaged UI starts; retain a separate `PKG-001` exact-package startup result.
