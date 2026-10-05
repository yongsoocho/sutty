# Small-team next steps / 소규모 팀의 다음 단계

This is a proposed plan for 1-20 Windows users, not a statement that planned features are implemented or supported. Existing requirements and release gates remain authoritative.

1-20명 Windows 사용자용 제안 기획입니다. 계획을 구현·지원 완료로 해석하지 않으며 기존 요구사항과 출시 게이트를 유지합니다.

## Product contract / 제품의 약속

One local-first workspace for connection, terminal, files, deliberate multi-host operations, and recovery. Replace the team's daily workflows first, not every legacy protocol or remote-desktop feature. No account or central server should be required for individual use.

접속·터미널·파일·명시적인 다중 서버 작업·복구를 하나의 local-first 작업 공간으로 제공합니다. 모든 레거시 프로토콜을 복제하기보다 팀의 일상 작업을 먼저 대체합니다. 개인 사용에 계정이나 중앙 서버를 요구하지 않습니다.

Interview one solo operator, one 2-5 person team and one 6-20 person team. Inventory actual protocols, authentication, jump paths, common commands, file sizes, deployment restrictions, and reasons they still open another tool. Decide explicitly whether FTP/FTPS, Serial, RDP/VNC or X11 are mandatory; they are outside the current product scope. Any expansion needs a separate scope decision, security model, and acceptance matrix.

개인 운영자·2-5명 팀·6-20명 팀 각 한 곳에서 실제 프로토콜·인증·경유 경로·명령·파일 규모·설치 제약·다른 도구를 여는 이유를 확인합니다. FTP/FTPS·Serial·RDP/VNC·X11은 현재 비목표이며, 필수라면 별도 범위 결정과 보안·인수 계획이 필요합니다.

## 1. Finish the daily-driver release / 일상 사용 릴리스 완성

- Seal a candidate from protected main, accept that exact x64 ZIP through PKG-001 and SSH-LIVE-001, obtain human-redaction-reviewed evidence, then tag and promote. Source self-tests are not release acceptance.
- Exercise native 720/1100/1440px logical widths, 100/150/200% scaling, Korean/English, keyboard focus, IME, shell/TUI resize, repeated tab closure and resource cleanup.
- Verify file collision, disk-full, disconnect, pause/resume, cancellation, app restart, retained edits and content conflict using test-owned data. Start with the existing representative 1 GiB / 1,000-file / ten-session one-hour workload.
- Finish signed installation, upgrade, failed-update rollback and local-data retention before positioning the app as the sole production access tool.

정확한 후보 ZIP의 앱·SSH 인수와 사람의 evidence 검토를 먼저 완료합니다. DPI·키보드·IME·종료·전송 실패/복구와 서명 설치·업데이트/롤백을 검증합니다. 기존 도구는 검증 기간의 복구 수단으로 유지합니다.

## 2. Remove switching costs / 도구 전환 비용 제거

- Validate current importers against representative real-machine formats, with explicit unsupported-field previews and no silent loss (IMP-001/002/003).
- Complete both-pane transfer and external-editor acceptance before adding more file automation (SFTP-001/002/005).
- Add optional host-specific local/remote folder pairs, then opt-in synchronized browsing (SFTP-006). Navigation must never initiate transfer or deletion.
- Add read-only directory comparison with a reviewed transfer plan (SFTP-007). Size/time equality must not be labeled content equality; hash comparison is explicit and bounded. No automatic destructive synchronization.

가져오기 손실을 드러내고 기존 전송·편집을 인수한 뒤, 호스트별 폴더 쌍 → 선택형 동기 탐색 → 읽기 전용 비교와 확인형 전송 계획 순서로 진행합니다. 탐색·비교가 자동 전송·삭제로 이어지지 않게 합니다.

## 3. Make team onboarding repeatable / 팀 온보딩 반복 가능하게 만들기

- Build on existing definition sharing (IMP-004), not a second serializer. Restore a deliberate sharing entry distinct from history, review the export, preview imports and bind authentication locally on a second PC.
- Keep host/group/tag/route/tunnel and reviewed command definitions separate from personal credentials, private-key paths, known-host trust, histories and workspace state. Command text and endpoint identifiers still need explicit privacy review.
- Version a Team Pack in a team-owned file or Git repository. Add schema/version checks, stable IDs, item-level diff, conflict decisions, provenance and rollback without requiring background sync or automatically executing imported commands.
- A Pack can standardize definitions, not grant or revoke server access. Server accounts/keys remain the authority; local PROD confirmations are accident prevention, not access control.
- Typed/secret command parameters and durable per-target outcome summaries come after onboarding; never infer remote termination from cancellation or retry an uncertain command automatically (CMD-003/005/006/007).

기존 JSON 공유를 기반으로 별도 공유 진입·export 검토·import 미리보기·PC별 인증 연결을 완성합니다. 팀 소유 파일/Git Pack은 버전·차이·충돌·출처·복구를 제공하되 자동 실행하지 않습니다. Pack 배포는 서버 권한 관리나 퇴사자 접근 회수를 대신하지 않습니다.

## Pilot exit criteria / 파일럿 종료 기준

Proposed targets to agree before a two-week pilot: at least 90% of the agreed daily workflows completed in Sutty; zero observed wrong-target execution or data loss in the declared acceptance suite; every injected transfer failure leaves a clear recoverable outcome; a new member imports a reviewed Pack and makes a first connection within ten minutes. These are pilot targets, not guarantees. Record task completion, failures and tool-switch reasons with consent, without collecting credentials or terminal transcripts.

2주 파일럿 전에 합의할 제안 목표: 선정한 일상 작업 90% 이상을 앱에서 완료, 선언한 인수 범위에서 오대상 실행·데이터 손실 0건 관찰, 주입한 전송 장애마다 복구 가능한 결과, 신규 구성원 10분 내 Pack 가져오기·첫 접속. 이는 보장이 아닙니다. 동의받은 작업 성공·실패·도구 전환 이유만 기록하며 자격증명·터미널 transcript는 수집하지 않습니다.

The next development slice is exact-candidate acceptance and release, followed by two-PC credential-free Team Pack onboarding. Cloud vaults, SSO, live collaboration and additional protocols need demonstrated demand before commitment.

다음 개발 단위는 정확한 후보 인수·릴리스, 이후 두 PC의 자격증명 없는 Team Pack 온보딩입니다. Cloud vault·SSO·실시간 협업·추가 프로토콜은 실제 수요 확인 이후 결정합니다.
