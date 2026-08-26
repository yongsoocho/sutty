## 사용자 문제 / User problem

<!-- 이 변경이 해결하는 실제 사용자 문제를 설명하세요. / Describe the user problem this change solves. -->

## 이번 PR의 범위 / Scope

<!-- 구현·수정하는 범위를 구체적으로 적으세요. / State the exact implementation scope. -->

## 의도적으로 제외한 범위 / Deliberately excluded

<!-- 제외 항목과 이유를 적으세요. 해당 없음만 쓰지 마세요. / Name exclusions and why; do not write only N/A. -->

## 상태·취소·종료 동작 / State, cancellation, and shutdown

<!-- 해당 경로를 설명하거나 적용되지 않는 이유를 적으세요. / Describe these paths or explain why they do not apply. -->

## Secret과 기존 데이터 영향 / Secrets and existing data

<!-- Secret·개인정보·migration·기존 데이터 영향을 적으세요. / Describe secret, privacy, migration, and existing-data impact. -->

## 정상·실패·취소 테스트 / Normal, failure, and cancellation tests

<!-- 실행한 테스트와 미실행 경계를 적으세요. / List executed tests and any untested boundary. -->

## 실제 환경 검증 / Live validation

<!-- 환경, 결과, 증거를 적거나 해당 없음/미실행 이유를 명시하세요. -->

## 문서와 요구사항 ID / Documentation and requirement IDs

<!-- docs/REQUIREMENTS.md에 존재하는 ID를 하나 이상 적으세요. / Name at least one ID from docs/REQUIREMENTS.md. -->

## 완료 확인 / Definition of Done

<!-- 적용되는 항목을 체크하고, 적용되지 않는 항목은 위 섹션에서 이유를 설명하세요. / Check applicable items and explain non-applicable items above. -->

- [ ] Core 또는 저장 계약을 UI보다 먼저 구현했습니다.
- [ ] 정상·실패·취소·종료·migration 중 해당하는 경로를 테스트했습니다.
- [ ] Timeout, cancellation, dispose, event 해제를 확인했습니다.
- [ ] Secret이 코드·fixture·log·설정·SQLite에 포함되지 않습니다.
- [ ] SFTP 변경은 staging·검증·승격과 기존 대상 보존을 확인했습니다.
- [ ] Multi 변경은 기본 대상 0개와 명시적 대상 확인을 유지합니다.
- [ ] 사용자 표시 문구를 한국어와 영어로 제공했습니다.
- [ ] 제품 범위 검사와 관련 빌드·self-test가 통과합니다.
- [ ] 실환경 의존 항목은 증거를 기록했거나 미검증 상태로 남겼습니다.
