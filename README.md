# Asterism

Asterism은 로컬 폴더의 지식 자료를 정리·열람하고 관계를 자신만의 성좌로 탐색하는 Windows 앱입니다. Markdown과 TXT는 직접 편집·렌더링·링크·성좌 지도까지 지원하며, PDF는 탐색기에서 함께 관리하고 앱 안에서 읽거나 원본을 훼손하지 않는 주석을 추가할 수 있습니다.

제품 정의와 용어, 현재·예정 자료 형식의 경계는 [Asterism 정의서](docs/ASTERISM-DEFINITION.md)를 기준으로 합니다.

앱 안의 `Asterism 안내` 폴더에는 시작 방법, Markdown, 링크, 성좌 지도, 단축키를 설명하는 읽기 전용 문서가 포함됩니다. 이 문서는 사용자 저장소에 파일을 만들지 않으며 앱 업데이트와 함께 자동으로 갱신됩니다.

Markdown 호환 미리보기는 언어가 지정된 코드 블록의 문법을 오프라인으로 강조하며 TXT에도 사용할 수 있습니다. TXT는 `TXT＋` 또는 폴더 메뉴로 만들 수 있고, PDF는 `PDF＋` 또는 폴더 메뉴로 저장소에 가져온 뒤 같은 탐색기에서 열 수 있습니다. PDF 뷰어에서는 텍스트 메모, 사각형·지시선 설명, 하이라이터를 사용할 수 있으며 주석은 별도 사이드카에 저장됩니다. 왼쪽 탐색기에서는 같은 부모 아래의 형제 폴더가 아코디언처럼 하나씩 열리고, 본문 제목은 서로 독립적으로 접고 펼칠 수 있습니다.

## 로컬 AI 연결

내장된 다국어 임베딩 모델이 Markdown·TXT 자료의 제목과 본문을 이 PC에서 직접 분석해 의미가 비슷한 자료를 추천합니다. 인터넷 연결, 계정, API 키가 필요하지 않으며 원문은 외부로 전송되지 않습니다.

- 추천의 `＋ 링크`를 누르면 현재 Markdown 또는 TXT에 `[[자료 제목]]` 링크가 추가됩니다.
- 로컬 그래프의 흐린 실선은 AI가 제안한 관계이고, 밝은 실선은 텍스트 자료에 실제로 저장된 링크입니다.
- 텍스트 자료의 링크와 본문은 원본 파일에 저장되며, 의미 벡터는 `%LOCALAPPDATA%\Asterism\semantic-index.db`에 재생성 가능한 캐시로만 저장됩니다. PDF도 제품 모델에서 성좌 자료이며, 원문을 바꾸지 않는 관계 메타데이터와 추출 텍스트 기반 분석을 추가하는 단계입니다.
- 변경되지 않은 본문 조각은 다시 계산하지 않습니다.

## 개발

```powershell
dotnet restore Checks/Asterism.Checks.csproj
dotnet build Asterism.csproj -c Release
dotnet run --project Checks/Asterism.Checks.csproj -c Release
```

## 릴리스

릴리스 워크플로를 수동 실행하고 `MAJOR.MINOR.PATCH` 버전을 지정하면 GitHub Actions가 Windows x64 자체 포함 빌드를 만들고, Inno Setup 설치 파일과 SHA-256 체크섬을 GitHub Release에 등록합니다.

앱의 **업데이트** 버튼은 안정화 릴리스 목록을 읽어 원하는 버전을 선택하게 합니다. 설치 파일은 GitHub가 제공하는 SHA-256 값과 대조한 뒤에만 실행됩니다.
