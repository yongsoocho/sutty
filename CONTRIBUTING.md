# Contributing to Sutty / Sutty 기여 가이드

Sutty is a local-first Windows SSH/SFTP operations workspace for individuals and small teams. Start by reading [Product Scope](docs/PRODUCT_SCOPE.md), [Development Playbook](docs/DEVELOPMENT_PLAYBOOK.md), and [Requirements](docs/REQUIREMENTS.md).

Sutty는 개인과 소규모 팀을 위한 Windows local-first SSH/SFTP operations workspace입니다. 먼저 [제품 범위](docs/PRODUCT_SCOPE.md), [개발 Playbook](docs/DEVELOPMENT_PLAYBOOK.md), [요구사항](docs/REQUIREMENTS.md)을 읽어주세요.

## Change flow / 변경 순서

Build one reviewable vertical slice in this order:

1. Define the user problem, boundary, failure state, cancellation, and cleanup behavior.
2. Implement the transport/storage behavior in `sutty.Core`, `sutty.Command`, or `sutty.Setting`.
3. Add focused normal, failure, cancellation, shutdown, and migration tests.
4. Add the smallest UI that exposes the tested behavior without placeholder controls.
5. Run credential-free checks locally and record any required live-server evidence separately.

하나의 리뷰 가능한 vertical slice를 다음 순서로 만듭니다.

1. 사용자 문제, 범위, 실패 상태, 취소, 정리 동작을 정의합니다.
2. `sutty.Core`, `sutty.Command`, `sutty.Setting`에 전송·저장 동작을 구현합니다.
3. 정상·실패·취소·종료·마이그레이션 테스트를 추가합니다.
4. 검증된 동작을 노출하는 최소 UI만 추가하고 빈 기능은 표시하지 않습니다.
5. 로컬 검사를 실행하고 실제 서버 증거가 필요한 항목은 별도로 기록합니다.

## Before opening a pull request / PR 전 확인

```powershell
.\tests\product-scope\Assert-ProductScope.Tests.ps1
.\.github\scripts\Assert-ProductScope.ps1
dotnet restore .\sutty.slnx --locked-mode -p:Platform=x64
dotnet build .\sutty.slnx -c Debug --no-restore -p:Platform=x64
dotnet run --project .\tests\sutty.Core.Security.SelfTest\sutty.Core.Security.SelfTest.csproj -c Debug --no-build
dotnet run --project .\tests\sutty.Command.SelfTest\sutty.Command.SelfTest.csproj -c Debug --no-build
dotnet run --project .\tests\sutty.Terminal.SelfTest\sutty.Terminal.SelfTest.csproj -c Debug --no-build
dotnet run --project .\tests\sutty.Setting.SelfTest\sutty.Setting.SelfTest.csproj -c Debug --no-build
dotnet run --project .\tests\sutty.Sftp.SelfTest\sutty.Sftp.SelfTest.csproj -c Debug --no-build
```

Never commit passwords, tokens, private keys, passphrases, production hostnames, terminal transcripts, or unredacted diagnostics. Tests use generated synthetic values only.

비밀번호, token, 개인키, passphrase, 운영 Host, 터미널 transcript, redaction하지 않은 진단 정보는 커밋하지 않습니다. 테스트에는 실행 중 생성한 합성 값만 사용합니다.

## Definition of Done / 완료 정의

A change is done only when its scope and exclusions are explicit, resource ownership is deterministic, cancellation and timeout behavior are bounded, secrets and existing data are protected, focused tests pass, user-visible text is Korean/English, and documentation reflects the implemented state. Live-dependent behavior is not marked complete without recorded live evidence.

범위와 제외 항목, 리소스 소유권, 제한된 취소·timeout, secret과 기존 데이터 보호, 집중 테스트, 한국어·영어 UI, 구현 상태 문서가 모두 확인돼야 완료입니다. 실제 환경 의존 기능은 실행 증거 없이 완료로 표시하지 않습니다.
