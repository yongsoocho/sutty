# Sutty release governance / Sutty 릴리스 거버넌스

This document defines the repository controls that make an Alpha release traceable from source to published bytes. It is a release contract, not evidence that a release already exists.

이 문서는 Alpha 릴리스를 소스부터 공개 바이트까지 추적할 수 있게 만드는 저장소 통제 계약입니다. 이 문서 자체는 릴리스가 이미 존재한다는 증거가 아닙니다.

## Protected repository state / 보호된 저장소 상태

The canonical GitHub ruleset request bodies are tracked in:

- [`.github/rulesets/main.json`](../.github/rulesets/main.json)
- [`.github/rulesets/release-tags.json`](../.github/rulesets/release-tags.json)

`main` has no bypass actor. When the contract is active, every change must arrive through a pull request, the branch must be current with its base, and these six exact checks must pass. Each context is pinned to the GitHub Actions integration (`15368`), not just to a reusable status name:

1. `Governance guards`
2. `Pull request body contract`
3. `x64 Debug`
4. `x64 Release`
5. `ARM64 compile`
6. `Validate unsigned x64 release artifact`

Deletion and non-fast-forward updates are rejected. Review conversations must be resolved, stale reviews are dismissed on a new push, and only squash or rebase merge is allowed. The approval count is intentionally zero because this is currently a one-maintainer repository; the pull-request boundary and required checks are enforced, but they are not misrepresented as an independent human approval.

`release-tags` targets `refs/tags/v*`, permits the first creation of a version tag, and rejects every later update or deletion. It also has no bypass actor.

`Pull request body contract` is the dedicated sixth required check. Its read-only `pull_request_target` job checks out and executes only the exact trusted base commit with persisted credentials disabled; it never executes pull-request-head code. The context was observed from the default branch before both tracked and live `main` rulesets were updated. The duplicate head-controlled enforcement was then removed. Body edits retrigger this small trusted check without rerunning the full Windows CI.

`main`에는 bypass actor가 없습니다. 계약이 활성화되면 모든 변경은 pull request를 거쳐야 하고 base의 최신 상태여야 하며 GitHub Actions integration(`15368`)에 고정된 위 여섯 검사를 정확히 통과해야 합니다. 삭제와 non-fast-forward update, 미해결 review 대화, stale review를 허용하지 않으며 squash 또는 rebase merge만 사용합니다. 현재 단일 maintainer 저장소이므로 승인 수는 의도적으로 0입니다. PR 경계와 자동 검사는 강제하지만 독립적인 사람 승인으로 과장하지 않습니다.

`release-tags`는 `refs/tags/v*`의 최초 생성을 허용하지만 이후 모든 update와 삭제를 거부하며 bypass actor가 없습니다.

`Pull request body contract`는 전용 여섯 번째 required check입니다. read-only `pull_request_target` job은 credential persistence를 끄고 정확한 신뢰 base commit만 checkout·실행하며 PR head 코드를 실행하지 않습니다. default branch에서 context가 실제 생성된 것을 확인한 뒤 tracked·live `main` ruleset을 함께 갱신했고, 그 다음 head-controlled 중복 enforcement를 제거했습니다. PR 본문 수정은 전체 Windows CI가 아니라 이 작은 trusted check만 다시 실행합니다.

## Contract verification / 계약 검증

The offline fixture checks the tracked request bodies. Candidate creation also queries the rule scope, enforcement, rule inventory, merge policy, and GitHub Actions integration pins that are visible to its read-only `GITHUB_TOKEN`:

```powershell
.\tests\repository-governance\Assert-RepositoryGovernance.Tests.ps1
.\.github\scripts\Assert-RepositoryGovernance.ps1 -QueryGitHub -Repository yongsoocho/sutty
```

GitHub omits `bypass_actors` from a ruleset detail response unless the caller has write access to that ruleset. Candidate creation therefore uses the explicit `-AllowOmittedBypassActors` mode: it rejects a nonempty inventory when GitHub returns one, but it does not claim that the read-only token completed a bypass audit. Release promotion is stricter. The protected `alpha-release` environment must provide `SUTTY_RULESET_AUDIT_TOKEN` from a short-lived GitHub App installation or fine-grained token with ruleset Administration write access. Promotion queries both rulesets twice without the omission allowance and fails if the token, property, empty inventory, or any other exact rule is missing. Publication must not continue merely because the JSON files exist in Git.

Offline fixture는 추적 중인 request body를 검사합니다. Candidate 생성은 read-only `GITHUB_TOKEN`에 보이는 scope, enforcement, rule inventory, merge policy, GitHub Actions integration pin을 조회합니다. GitHub API는 ruleset write 권한이 없는 호출자에게 `bypass_actors`를 생략하므로 이 단계는 명시적인 제한 모드이며 완전한 bypass 감사를 주장하지 않습니다. Release promotion은 보호된 `alpha-release` 환경의 `SUTTY_RULESET_AUDIT_TOKEN`을 사용해 두 번 strict 조회하고, 토큰·필드·빈 inventory·exact rule 중 하나라도 없으면 차단합니다. 짧은 수명의 GitHub App installation token 또는 ruleset Administration write 범위의 fine-grained token을 사용해야 합니다. Git에 JSON 파일만 있다는 이유로 출시를 계속할 수 없습니다.

## Evidence review is separate / 증거 검토는 별도 단계

Repository pull-request policy does not approve live evidence. A formal run always writes `redaction_reviewed: false`. After the run, a human must inspect every declared file and use `Review-LiveEvidence.ps1` to create a new write-once reviewed bundle. `review.json` binds the original file names, sizes, SHA-256 values, a declared public GitHub actor identifier, review time, and the exact privacy/integrity scope. The script validates the identifier's form but does not authenticate the GitHub account; the acceptance pull request provides the auditable publication boundary. The source bundle remains unchanged.

저장소 pull-request 정책은 실환경 증거를 승인하지 않습니다. 정식 실행은 항상 `redaction_reviewed: false`를 기록합니다. 실행 뒤 사람이 선언된 모든 파일을 확인하고 `Review-LiveEvidence.ps1`로 새 write-once reviewed bundle을 만들어야 합니다. `review.json`은 원본 파일명·크기·SHA-256, 선언한 공개 GitHub actor 식별자, 검토 시각, privacy/integrity 범위를 묶습니다. Script는 식별자 형식만 검사하고 GitHub 계정을 인증하지 않으며 acceptance pull request가 감사 가능한 공개 경계가 됩니다. 원본 bundle은 변경하지 않습니다.

The append-only history guard compares the candidate commit with the acceptance commit. A new bundle may be added, but any file below a bundle that already existed at the candidate commit cannot be changed, deleted, renamed, or extended.

Append-only history guard는 candidate commit과 acceptance commit을 비교합니다. 새 bundle은 추가할 수 있지만 candidate commit에 이미 존재한 bundle 아래 파일은 수정·삭제·rename·추가할 수 없습니다.

## Candidate-to-release state machine / Candidate에서 릴리스까지

1. Merge the release-preparation source commit `C` through the protected `main` workflow.
2. Run `alpha-candidate.yml` once for `C`. It builds and seals both ZIP files, `SHA256SUMS.txt`, and `CANDIDATE-MANIFEST.json` in one immutable Actions artifact.
3. Execute the exact x64 Candidate UI and record `PKG-001` startup, shutdown, and silent `Alt+1`–`Alt+7` navigation in an unreviewed source bundle. The recorder validates the locked ZIP against the complete unpacked physical tree by path, size, and SHA-256, but never performs or invents the observations.
4. Execute the exact x64 candidate bytes in the formal SSH gate. A successful automated run still creates a separate unreviewed `SSH-LIVE-001` source bundle.
5. Human-review both source bundles into new reviewed directories, then merge them as acceptance commit `A`, where `A` descends from `C`.
6. Create the version tag once at `C`. The tag ruleset then prevents movement or deletion.
7. Dispatch `alpha-release.yml` from protected `main` with the exact candidate run identity, `A`, and both reviewed manifest paths under the `alphaN` directory matching the tag's `-alpha.N` suffix.
8. Promotion rechecks the candidate artifact ID and digest, commit ancestry, append-only evidence history, reviewed `PKG-001` and `SSH-LIVE-001` Pass semantics bound to the same Candidate x64 ZIP, active rulesets, immutable-release setting, and tag target.
9. Publish exactly five assets without rebuilding:
   - x64 ZIP
   - ARM64 ZIP
   - `SHA256SUMS.txt`
   - `CANDIDATE-MANIFEST.json`
   - `RELEASE-ATTESTATION.json`
10. Download every public asset, compare its bytes, validate the release attestation again, verify GitHub release attestations, and require an immutable non-draft prerelease with the exact five-file inventory.

`RELEASE-ATTESTATION.json` binds repository/tag, candidate run and artifact identity, candidate commit and package inventory, acceptance commit, both reviewed manifests and `review.json` hashes, both declared reviewer/times and gates, promotion run, and the hash/size of the four sealed candidate files. The workflow separately downloads and byte-verifies the fifth attestation asset and requires the exact five-asset public inventory. A failed or incomplete promotion does not authorize editing an existing public release; source changes require a new candidate and an already-published immutable release requires a new version.

`RELEASE-ATTESTATION.json`은 repository/tag, candidate run·artifact identity, candidate commit·package inventory, acceptance commit, 두 reviewed manifest·`review.json` hash, 두 reviewer/time·gate, promotion run, 봉인된 candidate 파일 4개의 hash·size를 연결합니다. Workflow는 다섯 번째 attestation asset 자체를 별도로 내려받아 byte 검증하고 공개 inventory가 정확히 5개인지 확인합니다. 실패하거나 불완전한 승격은 기존 공개 Release 수정을 허용하지 않습니다. 소스 변경에는 새 candidate가 필요하고 이미 공개된 immutable release를 고쳐야 한다면 새 버전을 사용합니다.

## Claim boundary / 주장 경계

- A green CI run means the source and governance fixtures passed; it is not live validation.
- A sealed candidate is not a public release.
- An unreviewed bundle is never accepted evidence.
- One reviewed Direct/Password x64 tuple does not validate ARM64, another authentication method, an indirect route, forwarding, fault, scale, or soak behavior.
- **Released** applies only after the public asset SHA-256 and commit match the reviewed tuple and release attestation.

- CI 성공은 소스와 거버넌스 fixture가 통과했다는 뜻이며 실환경 검증이 아닙니다.
- Sealed candidate는 공개 릴리스가 아닙니다.
- 검토 전 bundle은 accepted evidence가 아닙니다.
- Direct/Password x64 한 조합은 ARM64, 다른 인증, 간접 route, forwarding, fault, scale, soak를 대신 검증하지 않습니다.
- **Released**는 공개 asset SHA-256과 commit이 reviewed tuple 및 release attestation과 일치한 뒤에만 사용합니다.
