# ChatGPT OAuth 자막 번역

`todo.md`에 따라 [LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator)의
`a6fee12757b15edbeef7c60f8895e0be694801e1` 커밋을 가져와 확장했습니다.
원본 라이선스와 저작권 표기는 `LICENSE`에 유지합니다.

## Windows에서 실행

Windows 11의 Live Captions와 .NET 8 SDK가 필요합니다. Live Captions를 먼저 열고 영어 음성 인식을 설정하세요.

```powershell
dotnet restore LiveCaptionsTranslator.csproj
dotnet run --project LiveCaptionsTranslator.csproj
```

1. **API Setting → ChatGPT OAuth → Continue with ChatGPT**를 누릅니다.
2. 기본 브라우저에서 로그인하고 ChatGPT 플랜 사용 권한에 동의합니다.
3. 앱으로 돌아와 로그인 상태를 확인하고 **Load models**에서 모델을 선택합니다.
4. 메인 설정의 번역 엔진을 **ChatGptOAuth**, 대상 언어를 **ko-KR**로 설정합니다.
5. 오버레이를 켜면 번역 중간 결과가 표시되고, 완료된 번역만 기존 이력에 저장됩니다.

API 키, 고정 client ID, client secret을 입력할 필요가 없습니다. 최초 로그인에서 클라이언트를 동적으로 등록합니다.
실제 사용 가능 여부와 모델 목록은 로그인한 계정·워크스페이스의 권한 및 OpenAI 제공 범위에 따라 달라집니다.
기존 OpenAI API-key 엔진은 별도 항목으로 유지됩니다.

배포용 실행 파일:

```powershell
dotnet publish LiveCaptionsTranslator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64
```

## 게임 채팅형 화면

메인창과 오버레이는 같은 대화 목록을 공유합니다. 완료된 대화는 시간순으로 쌓이고,
번역 중인 문장은 맨 아래에서 갱신됩니다. 원문과 번역은 같은 요청의 텍스트끼리 표시합니다.
최근 완료 문장 50개를 화면에 유지하며, 전체 저장 이력은 기존 **기록** 화면에서 확인할 수 있습니다.

- 메인창 **원문 켜짐 / 꺼짐**: 번역 아래 작은 원문 표시를 전환합니다.
- **Ctrl + 마우스 휠**: 메인창의 글자 크기를 조절합니다.
- **일시정지 / 번역 재개**: 진행 중인 번역을 취소하고 재개합니다. 완료된 대화는 유지합니다.
- **오버레이**: 게임 위에 반투명 채팅창을 띄웁니다. 제목 부분을 드래그해 옮기고 가장자리로 크기를 조절합니다.
- 오버레이의 **A− / A+**, **원문**, **···**: 글자 크기, 표시 방식, 배경과 색상을 조절합니다.
- 오버레이 **잠금**: 게임으로 클릭을 전달합니다. 메인창의 **잠금 해제**로 다시 이동·조작할 수 있습니다.
- 위로 스크롤하면 새 메시지가 도착해도 읽던 위치를 유지합니다. **최신 대화 ↓**로 자동 스크롤을 다시 켭니다.
- 문장 클릭으로 복사합니다. 화자 식별 정보가 없으므로 임의의 닉네임을 만들지 않고 `VOICE`로 표시합니다.

창은 새 문장이 와도 자동으로 커지지 않습니다. 원문 표시를 바꾸어도 오버레이 위치와 높이는 유지됩니다.
Fast 모드 옵션은 추가하지 않았습니다.

## 계정과 오류 처리

- **Add account**: 다른 계정/워크스페이스 등록. 이메일이 같아도 발급된 client ID별로 분리합니다.
- 계정 선택: 이전 계정의 진행 중 요청을 취소하고 모델 선택을 초기화합니다. **Load models**로 새 목록을 확인하세요.
- **Continue with ChatGPT**: 선택된 등록을 재사용해 로그인합니다. **Cancel** 또는 창 닫기로 취소할 수 있습니다.
- **Enable plan usage**: 로그인은 됐지만 플랜 사용 권한이 없는 경우 명시적으로 다시 동의를 요청합니다.
- **Sign out**: 원격 refresh token 폐기를 시도하고 로컬 토큰을 삭제합니다. 원격 폐기를 확인하지 못하면 화면에 안내합니다.
- **ChatGPT Usage**: 계정과 앱 사용 한도를 확인합니다. 한도/권한 오류로 중지된 호출은 원인을 해결한 뒤 **Resume requests**로 재개합니다.
- 일시적 서비스 오류는 잠시 호출을 보류합니다. 무효화된 refresh token은 삭제하고 재로그인을 요구합니다.
- 브라우저 로그인은 최대 5분, 번역은 최대 45초입니다. 브라우저 콜백 수신 메시지만으로 로그인이 완료된 것은 아니며 앱에서 검증 결과를 확인해야 합니다.

## 구현 구조와 자체 검토

- `src/auth/`: PKCE S256, state/nonce, 일회성 loopback 콜백, RS256/JWKS 검증, 계정 저장 및 refresh/revoke 처리.
- `src/apis/ChatGptResponsesClient.cs`: 계정별 모델 조회 및 공식 `/v1/responses` 스트리밍 호출.
- `src/apis/ResponsesStreamReader.cs`: SSE 텍스트 누적, 완료/실패/거절/중단 구분.
- `src/models/TranslationTaskQueue.cs`: 이전 요청의 결과 덮어쓰기 방지, 중간 결과와 최종 이력 분리.
- `src/models/ChatTranscript.cs`: 원문/번역 쌍, 스트리밍 행, 수정된 문장 교체, 최대 50행 유지.
- `src/controls/ChatFeedView.xaml`: 메인창과 오버레이가 재사용하는 채팅 UI.
- `src/windows/SettingWindow.ChatGptOAuth.cs`: 비동기 로그인과 계정 관리 UI.

성능: HTTP 연결을 재사용하고 번역 HTTP 요청은 동시에 최대 2개로 제한합니다. 대기 중 최신 자막보다 오래된 요청은 전송하지 않습니다.
JWKS는 캐시하며, 토큰 갱신은 프로세스 간 파일 잠금으로 직렬화합니다. 서비스 오류 시 요청을 보류해 반복 호출을 줄입니다.

보안: 토큰은 `%LOCALAPPDATA%\LiveCaptionsTranslator\ChatGptOAuth\accounts.dpapi`에 Windows DPAPI `CurrentUser`로 암호화해 원자적으로 저장합니다.
일반 `setting.json`에는 모델 설정만 들어갑니다. 콜백은 임의 포트의 `127.0.0.1`에만 열고, state·nonce·서명·issuer·audience·유효기간을 검증합니다.
원격 응답 본문이나 인증 URL을 로그에 기록하지 않습니다. DPAPI 저장 파일은 같은 Windows 사용자 권한으로만 복호화할 수 있습니다.

확장성/호환성: 인증 저장소와 HTTP 전송을 주입할 수 있도록 분리했습니다. OAuth 엔진은 기존 엔진과 독립된 설정 타입을 사용하고,
기존 설정을 불러올 때 누락된 엔진 항목을 추가하는 원본 마이그레이션을 따릅니다. 새 외부 라이브러리를 추가하지 않았습니다.
RS256 검증은 .NET 암호화 API로 구현하며, OpenAI가 다른 서명 알고리즘을 도입하면 검증기 확장이 필요합니다.

`store:false`, `stream:true`를 전송하고 `temperature`는 전송하지 않습니다.
사용자 입력은 user 메시지, 번역 지침은 `instructions`로 전달합니다. 모델은 하드코딩하지 않습니다.
API 키 과금 경로로 자동 전환하지 않습니다.

## 검증

외부 패키지 없이 실행되는 회귀 테스트:

```powershell
dotnet run --project tests/ChatGptOAuth.Tests --configuration Release
```

테스트는 PKCE, 콜백 재사용/위조 거부, ID 토큰 서명 및 클레임 검증, 동시 토큰 갱신과 회전 저장,
권한 부족, 로그아웃, 모델 조회 및 요청 형식, SSE 오류/취소, 사용량 초과, 계정 전환, 자막 순서와 최종 이력 저장을 검증합니다.
CI에도 같은 테스트 명령을 연결했습니다.

현재 개발 환경은 Linux입니다. Windows 타기팅 앱 빌드(오류 0개)와 회귀 테스트 27개를 통과했습니다.
실제 Windows WPF 렌더러에서 샘플 대화를 넣은 공유 채팅 화면과 오버레이를 확인했고,
오버레이의 클릭 전달 잠금/해제, 원문 표시 전환, 과거 대화 읽기 위치 유지 및 최신 대화 이동을 검증했습니다.
실제 계정의 브라우저 로그인·DPAPI 저장/재시작·게임 중 음성 번역은 별도 인수 확인이 필요합니다.
원본 코드의 nullable 경고가 남아 있습니다.

Windows 인수 확인:

- 새 계정 로그인 → 모델 조회 → 한국어 자막 번역.
- 앱 재시작 후 계정/모델 유지, access token 만료 후 자동 갱신.
- 로그인 취소 및 동의 거절 후 재시도.
- 계정 전환 시 이전 스트림 종료, 새 계정 모델 다시 선택.
- 로그아웃 후 번역 요청 중지, 같은 등록으로 재로그인.
- 네트워크 중단/한도 초과 시 중간 번역이 완료 이력으로 기록되지 않는지 확인.

## 공식 프로토콜 근거

2026-11-03 확인:

- [Registration and sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)
- [Accounts and sessions](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions)
- [Models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
- [Errors and recovery](https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery)
- [OpenID discovery](https://auth.openai.com/.well-known/openid-configuration)
