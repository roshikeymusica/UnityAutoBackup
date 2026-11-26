# Unity Auto Backup

씬을 저장할 때(끌 수 있음) 또는 수동 버튼을 누를 때 현재 Unity 프로젝트 폴더를 zip으로 백업하는 에디터 패키지.

## 설치
- **방법 1:** `com.roshikey.unityautobackup` 폴더를 프로젝트의 `Packages/` 안에 그대로 넣기
- **방법 2:** Package Manager → `+` → *Add package from disk...* → `package.json` 선택

Unity 2020.3 이상 (VRChat용 2022.3 OK).

## 사용법
- `Tools > Unity Auto Backup > 백업 창 열기` : 설정 / 진행률 / 백업 목록
- `Tools > Unity Auto Backup > 지금 백업` : 바로 수동 백업
- 진행 상황은 에디터 우측 하단 Background Tasks에도 표시되고 거기서 취소 가능

## 동작
- 압축은 백그라운드 스레드에서 돌아가서 에디터가 멈추지 않음
- 백업 중에는 스크립트 리컴파일(도메인 리로드)을 잠시 막음 → 끝나면 자동 해제
- 씬 저장 자동 백업은 **최소 간격(기본 10분)** 이 지나야 다시 실행 (Ctrl+S 연타 대비)
- 다른 프로그램이 잠근 파일은 건너뛰고 콘솔에 개수 표시
- `.zip.tmp`로 쓰다가 완료되면 이름 변경 → 중간에 꺼져도 깨진 zip이 목록에 안 남음
- 최대 보관 개수를 넘으면 오래된 것부터 삭제
- 기본 저장 위치: 프로젝트 옆 `<프로젝트이름>_Backups/`
- 기본 제외: `Library, Temp, Logs, obj, Build, Builds, .git, .vs, .idea, .vscode`
  (Library는 Unity가 다시 만들어주니 빼는 게 용량상 이득)
- 설정은 `UserSettings/UnityAutoBackupSettings.json` (사용자별)

## 복원
zip을 빈 폴더에 풀고 Unity Hub에서 그 폴더를 열면 됨. Library는 첫 실행 때 재생성됨.
