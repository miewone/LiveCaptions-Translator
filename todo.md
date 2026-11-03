GPT API만 이용해야 하는 건 아니야. OAuth로 ChatGPT 계정에 로그인해서 번역하는 방식도 가능해. 다만 현재 LiveCaptions-Translator에는 그 기능이 구현돼 있지 않아.

2026년 11월 기준, OpenAI는 공식적으로 Sign in with ChatGPT 기능을 제공하고 있어. 오픈소스 프로그램에서 사용자의 ChatGPT 계정으로 OAuth 인증한 뒤, 해당 계정의 이용 자격과 한도 내에서 AI 모델을 호출할 수 있는 방식이야.&#x20;

[image](https://www.google.com/s2/favicons?domain=https://developers.openai.com\&sz=32)

OpenAI Developers+1



## 1. 현재 LiveCaptions-Translator의 인증 방식

실제 GitHub 소스코드를 확인했어.

| 번역 엔진            | 인증 방식     | API 키 없이 사용 |
| ---------------- | --------- | ----------- |
| OpenAI           | API Key   | 기본 구현은 불가   |
| OpenRouter       | API Key   | 기본 구현은 불가   |
| Google Translate | 별도 로그인 없음 | 가능          |
| Ollama           | 로컬 모델 실행  | 가능          |
| LM Studio        | 로컬 모델 실행  | 가능          |
| DeepL            | API Key   | 기본 구현은 불가   |

OpenAI 엔진은 `TranslateAPI.cs`에서 다음과 같이 요청하고 있어.

```
client.DefaultRequestHeaders.Add(
    "Authorization",
    $"Bearer {config.ApiKey}"
);
```

즉, 현재 프로그램에는 ChatGPT OAuth 로그인 버튼이나 토큰 발급·갱신 기능이 없어.

소스: [TranslateAPI.cs](https://github.com/SakiRinn/LiveCaptions-Translator/blob/master/src/apis/TranslateAPI.cs?utm_source=chatgpt.com)

## 2. OAuth 방식으로 개조하는 방법

LiveCaptions-Translator

영어 자막 추출

Sign in with ChatGPT

브라우저 로그인 → OAuth 토큰 발급

OpenAI Responses API

ChatGPT 이용 자격 및 한도로 번역 요청

한국어 게임 오버레이

필요한 개발 작업은 다음과 같아.

1. `Sign in with ChatGPT` 로그인 버튼 추가
2. OAuth 2.0 PKCE 인증 및 토큰 갱신 구현
3. 기존 `OpenAI` 번역 엔진과 별도로 `ChatGPT OAuth` 엔진 추가
4. 기존 Chat Completions 요청을 OAuth 사용이 지원되는 Responses API 요청으로 변경
5. 스트리밍 번역 결과를 기존 오버레이에 연결

공식 문서에 따르면 오픈소스 앱은 동적 클라이언트 등록 방식을 이용할 수 있으며, 별도 API 키나 클라이언트 시크릿 없이 인증할 수 있어. 단, 사용자의 동의와 이용 자격이 필요해.&#x20;

[image](https://www.google.com/s2/favicons?domain=https://developers.openai.com\&sz=32)

OpenAI Developers



### API 요청 예시

OAuth 인증이 성공한 뒤에는 다음과 같은 방식으로 번역할 수 있어.

```

{
  "model": "사용_가능한_모델_ID",
  "instructions": "Translate English game voice chat into natural Korean. Output only the translation.",
  "input": [
    {
      "role": "user",
      "content": "Enemy helicopter coming from the east!"
    }
  ],
  "store": false,
  "stream": true
}

```

요청 주소는 `https://api.openai.com/v1/responses`이고, `Authorization: Bearer <OAuth 액세스 토큰>` 헤더를 사용해.

현재 OAuth 경로에서는 `store: false`, `stream: true`가 필수이며 기존 프로그램이 사용하는 `temperature` 필드는 허용되지 않아. 응답도 `choices` 형태가 아니라 스트리밍 이벤트로 처리해야 하므로 API 키만 OAuth 토큰으로 바꾸는 것으로는 동작하지 않아.&#x20;

[image](https://www.google.com/s2/favicons?domain=https://developers.openai.com\&sz=32)

OpenAI Developers+1



## 3. 비용은 어떻게 될까?

| 방식               | 비용 구조                         |
| ---------------- | ----------------------------- |
| OpenAI API Key   | API 사용량 기준으로 별도 청구            |
| ChatGPT OAuth    | 지원되는 계정의 ChatGPT 이용 한도·크레딧 적용 |
| Google Translate | 프로젝트의 기본 무료 번역 방식             |
| Ollama           | 별도 API 비용 없이 PC에서 로컬 실행       |

OAuth 방식은 무제한이 아니고, 계정이나 앱별 사용 제한이 적용될 수 있어. 지속적인 음성 번역에서는 요청 빈도 때문에 한도 관리가 중요해.

관련 공식 문서: [Sign in with ChatGPT – 오픈소스 앱 연동](https://developers.openai.com/siwc/token-sharing-open-source?utm_source=chatgpt.com)

내가 추천하는 방향은 LiveCaptions-Translator를 포크해서 `ChatGPT OAuth` 번역 엔진을 추가하는 것이야. 기존의 영어 자막 추출과 게임 오버레이는 그대로 사용하고, 번역 호출 부분만 바꾸면 돼.

이 방식이면 별도 API 키 없이 ChatGPT 계정으로 로그인하는 경험을 만들 수 있어. 다만 현재 공식 OAuth 기능은 프리뷰 단계이므로 실제 계정에서 사용 가능한 모델과 한도는 인증 후 확인해야 해.
