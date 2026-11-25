# Windows 서버 팀의 전환 기준

Windows 11 24H2 이상에서 1~20명이 SSH로 서버를 관리하는 경우를 대상으로 합니다. 2026-11-24 기준 공개 Alpha ZIP과 현재 소스의 기능 범위는 다릅니다. 검증할 설치본의 버전과 해시를 먼저 기록합니다.

| 현재 도구 | Sutty에서 수행하는 업무 | 전환 결정에 필요한 확인 |
| --- | --- | --- |
| FileZilla | SFTP 파일 트리, 다중 업로드·다운로드, 건너뛰기·충돌 처리, 중단 후 재개, 폴더 비교 | 원격 편집·덮어쓰기 후 Unix 권한과 전송 파일 수정 시각 보존 [#40](https://github.com/yongsoocho/sutty/issues/40). FTP/FTPS 전용 접속처는 별도 도구가 필요합니다. |
| PuTTY | 저장 호스트, SSH PTY와 포트 포워딩, 개인 키·Agent, 기존 Windows 세션 가져오기 | 키와 OTP를 차례로 요구하는 운영 서버 [#39](https://github.com/yongsoocho/sutty/issues/39). 물리 시리얼 콘솔은 지원 범위 밖입니다. |
| SecureCRT | 탭·분할 터미널, 저장 명령, 명시적인 다중 서버 실행, 터널 | 표준 SSH 운영은 파일럿 대상입니다. 세션 전체 녹취나 기존 자동화 스크립트가 필수인 팀은 해당 작업을 별도로 확인합니다. |
| MobaXterm | SSH 터미널·같은 목적지의 SFTP, 1홉 Bastion | X11 그래픽 앱과 RDP/VNC 데스크톱에 의존하는 작업은 별도 클라이언트가 필요합니다. |
| Tabby (구 Terminus) | Windows 로컬 셸, SSH 세션, 파일 전송·터널 | 사용 중인 시리얼 연결이나 전용 플러그인이 있는지 확인합니다. |
| Termius | SSH·SFTP, 1홉 경유, 서버 정의 수동 공유 | 팀에서 다중 홉이나 계정 기반 기기 간 동기화를 사용하는지 확인합니다. |

[설치 점검](RELEASE_ACCEPTANCE.md) · [지원 환경](SUPPORTED_ENVIRONMENTS.md) · [일상 작업](DAILY_WORKFLOW.md)

공식 비교 자료: [FileZilla](https://filezilla-project.org/client_features.php), [PuTTY](https://the.earth.li/~sgtatham/putty/latest/htmldoc/), [SecureCRT](https://www.vandyke.com/products/securecrt/key_features.html), [MobaXterm](https://mobaxterm.mobatek.net/documentation.html), [Tabby](https://github.com/Eugeny/tabby), [Termius](https://termius.com/).
