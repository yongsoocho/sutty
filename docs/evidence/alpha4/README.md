# Alpha 4 evidence index / Alpha 4 증거 색인

This directory tracks the approved Alpha 4 evidence scopes. It currently contains **zero accepted evidence bundles** and no `Pass` manifest. The README files are scope trackers, not evidence.

이 디렉터리는 승인된 Alpha 4 증거 범위를 추적합니다. 현재 인수 완료 증거 bundle은 **0개**이며 `Pass` manifest도 없습니다. README 파일은 범위 추적 문서이지 증거가 아닙니다.

| Scope directory | Alpha 4 slices | Bundle content |
| --- | --- | --- |
| [`package`](package/README.md) | `PKG-001` | Exact x64 Candidate ZIP and complete unpacked-tree identity plus reviewed UI startup, shutdown, and silent `Alt+1`–`Alt+7` navigation. |
| [`ssh-auth`](ssh-auth/README.md) | `SSH-LIVE-001` through `SSH-LIVE-004` | Direct password, key-format, Agent, and keyboard-interactive runs; each material combination is separate. |
| [`ssh-routes`](ssh-routes/README.md) | `ROUTE-LIVE-001`, `ROUTE-LIVE-002`, `TUN-LIVE-001` | Route and forwarding runs, separated by route/authentication/forwarding mode. |
| [`ssh-transport`](ssh-transport/README.md) | `SSH-FAULT-001` | Unexpected transport failure, cleanup, and safe resume runs. |
| [`connection-info`](connection-info/README.md) | `SSH-INFO-001` | Negotiated information, reconnect freshness, clearing, and no-exec runs. |

After a real run, promote only a new reviewed bundle at `docs/evidence/alpha4/<scope>/<bundle>/`, containing `manifest.yml`, `summary.json`, required `review.json`, any explicitly declared attachments, and no other file. Follow [EVIDENCE_SCHEMA.md](../EVIDENCE_SCHEMA.md) and [Alpha 4 execution plan](../../ALPHA4_EXECUTION_PLAN.md). `PKG-001` and `SSH-LIVE-001` use separate bundles bound to the same exact candidate commit and x64 ZIP SHA-256.

실제 실행 뒤 새로 검토·승격한 bundle만 `docs/evidence/alpha4/<scope>/<bundle>/`에 두며 `manifest.yml`, `summary.json`, 필수 `review.json`, 명시적으로 선언한 attachment 외에는 포함하지 않습니다. [증거 스키마](../EVIDENCE_SCHEMA.md)와 [Alpha 4 실행 계획](../../ALPHA4_EXECUTION_PLAN.md)을 따릅니다. `PKG-001`과 `SSH-LIVE-001`은 같은 exact candidate commit과 x64 ZIP SHA-256에 결속된 별도 bundle을 사용합니다.
