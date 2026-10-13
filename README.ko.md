# samlgate

**브라우저 SAML 로그인으로 AWS 자격증명을 발급받는 CLI — saml2aws Browser provider의 `saml2aws login` 대체재.**

[English](README.md)

> samlgate의 명령 출력과 오류 메시지는 영어로만 나옵니다.

samlgate는 IdP 로그인(Microsoft Entra ID, Okta, Google Workspace 등)을 실제 브라우저에서 진행시키고,
AWS로 향하는 SAML assertion을 중간에서 받아 STS로 임시 자격증명을 발급받은 뒤 `~/.aws/credentials`에
saml2aws와 같은 형식으로 기록합니다. Windows 배포본은 런타임·Playwright·브라우저 드라이버 다운로드 없는 약 7MB 단일 네이티브 바이너리입니다.

이런 조직을 위해 만들었습니다.

- **상위 조직이나 파트너에게 AWS 계정 한두 개를 배정받아 쓰는** 곳이라 **IAM Identity Center를 쓸 수 없고**,
- 사람은 **Microsoft Entra ID**의 *AWS Single-Account Access* 엔터프라이즈 앱(SAML)으로 관리해서,
  로그인이 터미널 비밀번호 입력이 아니라 브라우저·MFA·조건부 액세스로 이뤄지는 곳.

## 동작 방식

SAML에는 `localhost` 콜백이 없습니다. 로그인이 끝나면 IdP가 브라우저로 하여금 `SAMLResponse`를 고정된 AWS 주소
`https://signin.aws.amazon.com/saml`에 `POST`하게 합니다. samlgate는

1. Edge/Chrome을 **전용 프로필**과 DevTools Protocol을 켠 채로 띄우고,
2. 바로 그 ACS 요청(aws, aws-cn, aws-us-gov 파티션)만 가로채서 AWS로 보내지 않고,
3. "창을 닫아도 됩니다" 로컬 페이지로 응답한 뒤 브라우저를 닫고,
4. STS `AssumeRoleWithSAML`을 호출해 프로필을 기록합니다.

전용 프로필에 IdP 쿠키가 남으므로 **로그인은 처음 한 번만** 하면 됩니다. 이후 자격증명이 만료되면 브라우저 창이
몇 초 떴다 닫히는 정도로 갱신됩니다. (첫 Entra 로그인 때 **"로그인 상태를 유지하시겠습니까?"에 "예"**를 누르세요 —
samlgate는 매번 브라우저를 닫기 때문에 세션 쿠키만 있으면 사라집니다.)

## 설치

> **현재는 Windows 전용입니다.** Windows x64·ARM64용 바이너리를 제공하며, Windows 11 x64에서 검증했습니다.
> 배포 바이너리는 Windows용입니다. 소스 빌드에서는 macOS 14 이상용 네이티브 WebView를 실험적으로 사용할 수 있습니다. Linux 지원도 아직 실험 단계입니다([로드맵](#로드맵) 참고).

[Releases](https://github.com/platpharm/samlgate/releases)에서 `samlgate-<버전>-win-x64.zip`(또는 `win-arm64`)을 받아
`samlgate.exe`를 `PATH`에 있는 폴더에 풉니다.

소스 빌드: `dotnet publish src/Samlgate -c Release -r win-x64` (.NET 10 SDK, NativeAOT용 Visual Studio
"C++를 사용한 데스크톱 개발" 워크로드 필요)

**요구 사항:** Windows 기본 탑재 Microsoft Edge — 또는 Google Chrome, Brave, Chromium, Whale.

### macOS 네이티브 WebView (실험 기능)

.NET 10 SDK와 Xcode 명령줄 도구를 설치한 뒤 Mac의 아키텍처에 맞게 빌드합니다.

```bash
dotnet publish src/Samlgate -c Release -r osx-arm64 -o out
# Intel Mac에서는 osx-x64를 사용합니다. 출력된 실행 파일 두 개를 함께 보관하세요.
./out/samlgate login --browser webview
```

`--browser webview`는 AppKit 창 안의 WKWebView에서 로그인을 진행합니다. macOS 14 이상과 사용자가 직접 제어할 수 있는 그래픽 데스크톱이 필요합니다. Chrome 설치나 DevTools 포트는 필요하지 않습니다. `samlgate configure --browser webview --url "<IdP 로그인 URL>"`로 선택을 저장할 수 있습니다. 기본 브라우저는 기존 Chromium 방식이며, `--browser-path`는 Chromium에만 적용됩니다.

함께 빌드되는 `samlgate-webview`는 AWS ACS POST를 취소한 뒤 해당 폼에서 SAML 응답을 읽습니다. 응답은 자식 프로세스의 전용 파이프로 CLI에 전달되어 STS로 전송되고, 파일이나 `login` 출력에는 기록하지 않습니다. AWS 파티션·리전별 ACS와 `target_url`을 지원하며 폼을 읽지 못하면 실패로 종료합니다. 헤드리스 로그인이나 자동 브라우저 전환은 추가하지 않습니다.

네이티브 세션은 `SAMLGATE_DATA_DIR/browser/webview/webkit-profile-id`의 UUID로 구분합니다. 실제 웹사이트 데이터는 WebKit이 해당 디렉터리 밖에서 관리하며 Safari·Chromium의 세션과 분리됩니다. 재사용하려면 식별자 파일을 보존하세요. 파일을 삭제하면 새 저장소를 사용하지만 기존 저장소는 지워지지 않습니다. 이 저장소를 지우려면 창을 닫고 `samlgate-webview --remove-profile <식별자-파일의-UUID>`를 실행한 뒤 식별자 파일을 삭제하세요. 다른 저장소는 건드리지 않습니다.

세션 유지와 패스키 사용 가능 여부는 OS·IdP·정책에 달려 있습니다. 로컬 네이티브 로그인 성공은 클라우드 브라우저의 사용자 제어나 패스키 접근 가능성을 입증하지 않습니다.

## 빠른 시작

```bash
# 1. IdP 로그인 시작 URL 저장 (아래 "로그인 URL 찾기" 참고)
samlgate configure --url "https://launcher.myapps.microsoft.com/api/signin/<app-id>?tenantId=<tenant-id>"

# 2. 로그인 — ~/.aws/credentials에 [saml] 프로필 기록
samlgate login

# 3. 사용
aws sts get-caller-identity --profile saml
samlgate status
```

이미 saml2aws를 쓰고 있다면 1단계는 건너뛰세요 — `~/.samlgate`에 없는 계정은 `~/.saml2aws`에서 읽습니다.

### 로그인 URL 찾기 (Microsoft Entra ID)

Entra 관리 센터 → **엔터프라이즈 애플리케이션** → AWS 앱 → **속성** → **사용자 액세스 URL**.
사용자는 [내 앱](https://myapps.microsoft.com)에서 앱 타일을 우클릭해 링크를 복사해도 됩니다.

## 명령

| 명령 | |
|---|---|
| `samlgate login` | 저장된 자격증명이 5분 안에 만료되면(또는 `--force`) 로그인하고 세션 정보를 출력 |
| `samlgate status` | 세션 정보 출력. 유효하면 종료 코드 `0`, 아니면 `1` — 스크립트용 |
| `samlgate configure --url …` | `~/.samlgate`에 계정 생성/수정 |
| `samlgate credential-process` | [`credential_process`](https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-sourcing-external.html) JSON으로 자격증명 출력 (필요하면 로그인) |
| `samlgate exec -- <명령> …` | `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`/`AWS_SESSION_TOKEN`을 넣어 명령 실행 |

공통 옵션: `-a/--idp-account <이름>`(설정 섹션, 기본 `default`), `-p/--profile <이름>`(credentials 프로필, 기본 `saml`),
`--role <arn>`, `--region`, `--session-duration <초>`, `--browser edge|chrome|brave|chromium|whale|webview`, `--browser-path <실행 파일>`,
`--timeout <초>`. 전체 목록은 `samlgate <명령> --help`.

### Chromium 언어와 세션 갱신

Chrome·Edge·Brave·Chromium·Whale은 같은 CDP 캡처 구현을 사용합니다. Whale은 `--browser whale`로 선택하며, 일반 설치 위치 밖의 실행 파일은 `--browser-path`로 지정할 수 있습니다. 브라우저·관리 정책이 CDP를 허용해야 합니다.

```bash
# IdP의 언어별 화면 리소스가 로드되지 않을 때 명시적으로 언어를 선택합니다.
samlgate login --browser chrome --browser-language en-US

# AWS 자격증명이 유효해도 IdP를 다시 방문하며, 기존 세션을 먼저 시도합니다.
samlgate login --browser chrome --browser-mode auto --browser-language en-US --force

# 이후 로그인에 사용할 설정을 저장합니다.
samlgate configure --url "<IdP 로그인 URL>" --browser chrome --browser-language en-US --browser-mode auto
```

기본값은 언어를 강제하지 않는 대화형 창입니다. 특정 IdP에 대한 자동 언어 우회는 추가하지 않습니다. `--browser-language`는 단일 언어 태그를 받으며, 기본 언어로 돌아가려면 계정 설정의 `browser_language`를 제거하세요.

`auto` 모드는 기존 프로필이 있을 때 `--headless=new`로 시도합니다. `--silent-timeout`(기본 20초) 안에 SAML이 오지 않으면 헤드리스 브라우저를 닫은 뒤 같은 프로필로 대화형 창을 엽니다. 새 프로필은 곧바로 대화형 창을 엽니다. 두 시도는 전체 `--timeout`을 공유하며, 사용자 취소·브라우저 실행 오류·읽을 수 없는 ACS 응답은 자동 재시도하지 않습니다. 유효한 AWS 자격증명이 있으면 기존처럼 브라우저를 생략하므로 갱신 검증에는 `--force`를 사용하세요.

무인 갱신은 비밀번호 입력·패스키 승인·IdP 정책 우회를 수행하지 않습니다. 재인증에는 사용자가 제어할 수 있는 그래픽 브라우저가 필요하며 SSH·클라우드 사용자 핸드오프를 제공하지 않습니다. 네이티브 `webview`는 Chromium 언어·auto 옵션을 무시하지 않고 오류로 안내합니다. 두 구현의 세션은 분리됩니다.

### 롤

SAML 응답에는 IdP 앱에서 사용자에게 할당된 AWS IAM 롤이 모두 들어 있습니다. 하나면 그대로 쓰고, 여러 개면
터미널에서 고르게 하며 계정별로 선택을 기억합니다. `role_arn` / `--role`로 고정할 수도 있습니다.

### credential_process 사용

samlgate가 기록하는 프로필과 **다른** 프로필에 연결하세요 (`~/.aws/credentials`에 정적 키가 있는 프로필은 그 키가 우선합니다).

```ini
# ~/.aws/config
[profile work]
credential_process = samlgate credential-process -a default
region = ap-northeast-2
```

터미널이 연결되지 않은 상황에서는 롤을 물어볼 수 없습니다 — `role_arn`을 지정하거나 `samlgate login`을 한 번 실행해 선택을 기억시키세요.

## 설정

`~/.samlgate`(`SAMLGATE_CONFIG`로 변경 가능)는 계정마다 섹션이 하나인 INI 파일입니다. 키 이름이 saml2aws와 같아서
`~/.saml2aws`를 그대로 복사해도 되고, 모르는 키는 무시합니다.

| 키 | 기본값 | |
|---|---|---|
| `url` | — | IdP 로그인 시작 URL. saml2aws의 `provider = AzureAD` + `app_id`는 자동 변환 |
| `aws_profile` | `saml` | 기록할 credentials 프로필 |
| `role_arn` | — | 묻지 않고 사용할 롤 |
| `region` | 파티션 기본값 (`us-east-1`) | STS 리전 |
| `aws_session_duration` | assertion의 `SessionDuration`, 없으면 3600 | 롤 최대치보다 길면 3600으로 재시도 |
| `credentials_file` | `~/.aws/credentials` | `AWS_SHARED_CREDENTIALS_FILE`도 반영 |
| `browser_type` | Chromium 자동 탐색 | `edge`/`msedge`, `chrome`, `brave`, `chromium`, `whale`, `webview`(macOS 14 이상, 실험 기능) |
| `browser_executable_path` | 자동 탐색 | |
| `browser_language` | 브라우저 기본값 | 단일 언어 태그(예: `en-US`), Chromium 전용 |
| `browser_mode` | `interactive` | `auto`는 기존 Chromium 프로필을 헤드리스로 짧게 시도한 뒤 필요하면 창을 엽니다 |
| `silent_timeout` | `20` | 전체 로그인 시간 제한 안에서 무인 갱신을 기다리는 초 |
| `target_url` | AWS ACS URL | 추가로 가로챌 URL (커스텀 ACS) |

credentials 프로필에는 saml2aws와 똑같이 `aws_access_key_id`, `aws_secret_access_key`, `aws_session_token`,
`aws_security_token`, `x_principal_arn`, `x_security_token_expires`가 기록됩니다.

`SAMLGATE_DATA_DIR`로 브라우저 프로필·기억한 롤의 위치를 바꿀 수 있습니다 (기본 `%LOCALAPPDATA%\samlgate`).

## 보안

- SAML assertion은 AWS STS로만 전송됩니다. 가로챈 ACS 요청은 로컬에서 응답합니다.
- Chromium 프로필은 평소 프로필과 분리되어 데이터 폴더에 있습니다. 지우면 로그아웃됩니다. 네이티브 WebKit 저장소의 삭제 방법은 macOS 절의 설명을 따릅니다.
- credentials 파일은 원자적으로 기록됩니다.
- Chromium DevTools는 samlgate 실행 중에만 `127.0.0.1`의 임의 포트로 열립니다. 네이티브 WebView는 DevTools 포트를 열지 않습니다.
- 텔레메트리 없음.

## 로드맵

- 실험적인 macOS WKWebView 구현의 배포·안정화 및 Chromium이 없을 때 선택적인 폴백
- Linux 지원
- WebDriver BiDi로 Firefox 지원
- 선택적인 Chromium 자동 갱신의 IdP·플랫폼별 안정화

## 개발

```bash
dotnet test samlgate.slnx          # 단위 테스트 + (Edge/Chrome이 있으면) 헤드리스 브라우저 가로채기 테스트
dotnet run --project src/Samlgate -- help
```

`src/Samlgate.Core`에 테스트 가능한 로직(설정, SAML 파싱, STS, credentials 파일, DevTools 가로채기)이 모두 있고
`src/Samlgate`는 얇은 CLI입니다(System.CommandLine). STS는 AWSSDK.SecurityToken으로 호출합니다.

`ISamlCapture`가 브라우저 캡처와 `LoginFlow`를 분리합니다. macOS 빌드는 시스템 Swift 컴파일러로 `native/macos/SamlgateWebView.swift`를 빌드하고 실행 파일을 CLI·테스트·publish 출력으로 복사합니다. 다른 플랫폼에서는 빌드하지 않습니다. 네이티브 테스트는 비밀 없는 로컬 합성 페이지만 사용하며 macOS 14 이상 데스크톱이 필요합니다. 다른 플랫폼에서는 건너뛰며, 그래픽 세션이 없는 Mac에서는 `dotnet test --filter 'Category!=NativeBrowser'`로 제외할 수 있습니다.

## 라이선스

[MIT](LICENSE)
