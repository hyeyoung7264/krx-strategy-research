# 한국 주식 Strategy Research / Paper Trading

공개 저장소: [hyeyoung7264/krx-strategy-research](https://github.com/hyeyoung7264/krx-strategy-research). 현재 단계는 검증된 기반 구현이며 전체 목표는 미완료입니다. 남은 작업과 검증 범위는 [docs/status.md](docs/status.md)에 기록했습니다.

우선 목표는 [docs/goal.md](docs/goal.md)에 기록했습니다. 일봉 신호로 다음 거래일 시가부터 수일 보유하는 C# 연구 시스템입니다. 실제 주문 기능은 없습니다. **현재 실제 시장의 양의 기대값이나 일평균 1%를 검증하지 않았습니다.**

## 실행

.NET 10 SDK가 필요합니다. 저장소 루트에서 실행합니다.

```powershell
dotnet restore Investment.sln --configfile NuGet.Config
dotnet test Investment.sln --no-restore
dotnet run --project src/Investment.Cli --no-restore -- demo
```

`demo`는 고정 seed 합성 데이터로 네 후보, 5개 walk-forward fold, 마지막 holdout을 실행합니다. 결과 JSON, 사람이 읽는 보고서, 원본 dataset, 소스 snapshot을 `artifacts/`에 새 파일로 저장합니다. 합성 데이터는 승격 불가합니다. 실제 실험에서는 최초 holdout 접근 시 `data/private/holdout-seals/`를 예약하며 같은 dataset의 재사용을 차단합니다. 실패한 예약을 삭제해서 재시도하지 마세요. 새 데이터 revision을 만들어도 이미 본 날짜가 독립적인 holdout이 되는 것은 아닙니다.

## 구조와 현재 동작

| 프로젝트 | 책임 |
|---|---|
| Investment.Core | 시점 검증, CSV, OpenDART, 모멘텀/평균회귀, 비용 포함 백테스트, 지표, walk-forward, 통계 gate, paper 장부, 승격 gate |
| Investment.Persistence | PostgreSQL / EF Core / Npgsql, 불변 원본과 실험 결과, DB 변경 방지 trigger |
| Investment.Cli | 실험·수입·공시 수집·paper 이벤트 수신·DB 명령 |
| Investment.Tests | 누수, 비용, 손실, 재현성, 시간 분할, paper 증거, API, PostgreSQL 통합 검증 |

연구 루프는 사전 등록된 제한된 후보 집합을 평가합니다. `IHypothesisGenerator`가 확장 지점이며, 현재 구현은 결정적 baseline generator입니다. **외부 LLM을 사용하는 자율 가설 생성·실패 학습은 아직 연결하지 않았습니다.** 자유로운 생성 코드를 실행하는 기능은 없습니다.

`config/research.json`의 비용·위험·통계 기준은 가상 연구 기본값입니다. 실제 시장/기간별 세금·증권사 수수료가 확인된 값이 아닙니다. `PolicyReviewed=false`로 승격을 막으며, Owner와 해당 조건을 검토하기 전 true로 변경하지 않습니다. 전략 비중은 최대 50%이며, paper 시작에는 서로 다른 전략군에서 통과한 두 버전의 독립적인 검증 증거가 필요합니다. 비중 분산만으로 전략 간 상관이 낮다는 의미는 아닙니다.

학습에서 후보 선택 → 검증에서 veto → 변경 없는 미래 테스트를 반복합니다. 미래 테스트 구간은 겹치지 않습니다. 마지막 holdout은 이전 구간에서 선택한 한 버전으로 평가합니다. 블록 bootstrap의 블록 길이는 최소 5세션 또는 최대 보유기간이며, 등록 후보 수에 Bonferroni 보정을 적용합니다. 모델 가정과 작은 표본에 따른 불확실성이 있으므로 통과를 수익 보장으로 해석하지 않습니다.

## 가격 데이터

OpenDART는 공시·재무정보 공급원이며 OHLCV와 실시간 호가는 별도입니다. [공식 공시정보 API 안내](https://opendart.fss.or.kr/guide/main.do?apiGrpCd=DS001).

CSV header는 정확히 다음과 같아야 합니다. 숫자는 소수점 `.` 형식이며 인용부호·쉼표 포함 필드는 지원하지 않습니다.

```text
Ticker,Sector,Date,AvailableAt,Open,High,Low,Close,Volume,TradingValue,Tradable,Member,CorporateAction
```

```powershell
dotnet run --project src/Investment.Cli --no-restore -- import-csv --csv data/private/prices.csv --source provider-export
dotnet run --project src/Investment.Cli --no-restore -- backtest --dataset artifacts/dataset-HASH.json
dotnet run --project src/Investment.Cli --no-restore -- research --dataset artifacts/dataset-HASH.json
```

날짜 형식은 `yyyy-MM-dd`, AvailableAt에는 명시적 timezone이 필요합니다. Member/섹터/거래 가능 상태는 해당 거래일 당시 자료여야 합니다. 모든 종목에 같은 세션 grid가 필요하며 거래정지·퇴출 행을 누락하지 않습니다. 상장 전/폐지 후 placeholder를 사용하는 경우 실제 잔여 가격·회수·거래 상태 처리를 검토해야 합니다. 날짜를 임의로 채워 만든 종목이나 잔여 가치를 실제 데이터로 인증하면 안 됩니다.

CSV 수입은 `PointInTimeCertified=false`로 생성됩니다. 인증은 데이터 품질·역사적 universe·수정주가·시점·공식 거래일 캘린더의 별도 검토가 필요합니다. 인증된 JSON에는 `Sessions`에 실제 거래일을 순서대로 명시해야 합니다. 단순히 flag를 바꾸면 품질이 검증되는 것은 아닙니다. `CorporateAction=true`인 자료는 명시적인 주식수/가격 조정 구현 전까지 거부합니다.

## OpenDART

[공시검색](https://opendart.fss.or.kr/guide/detail.do?apiGrpCd=DS001&apiId=2019001)과 [기업개황](https://opendart.fss.or.kr/guide/detail.do?apiGrpCd=DS001&apiId=2019002)을 읽기 전용 GET으로 호출합니다. 인증키는 로컬 환경변수 `OPENDART_API_KEY`로 설정합니다. 키를 코드·설정파일·CLI 인자·Git에 넣지 마세요.

```powershell
dotnet run --project src/Investment.Cli --no-restore -- dart-disclosures --start 2026-09-01 --end 2026-09-26
dotnet run --project src/Investment.Cli --no-restore -- dart-company --corp-code 00126380
```

최종보고서만 선택하지 않고 정정보고서까지 수집합니다. 접수번호와 원본 페이지를 보존합니다. API의 날짜 정밀도로 실제 장중 공개시각을 알 수 없으므로 공시 사용 시각은 `max(실제 관측시각, 접수일 다음 날 00:00 KST)`입니다. 오늘 받은 과거 공시를 과거에 알고 있었던 입력으로 사용하지 않습니다. 현재 `rm`의 후속 정정/철회 정보는 과거 전략 feature로 노출하지 않습니다. 기업개황은 오늘의 snapshot이며 과거 종목 master로 사용하지 않습니다. 호출 오류·조회 결과 없음·불완전 pagination을 구분합니다. 실제 API 호출은 인증키 확보 전 미검증입니다.

## PostgreSQL

기존 운영 DB와 분리한 전용 빈 DB를 준비하고 `RESEARCH_DB` 환경변수로 연결문자열을 설정합니다.

```powershell
dotnet run --project src/Investment.Cli --no-restore -- db-schema
dotnet run --project src/Investment.Cli --no-restore -- db-init
dotnet run --project src/Investment.Cli --no-restore -- db-check
dotnet run --project src/Investment.Cli --no-restore -- research --dataset artifacts/dataset-HASH.json --persist
```

`db-schema`는 접속 없이 검토 가능한 생성 SQL을 출력합니다. `db-init`은 초기 bootstrap용 `EnsureCreated`이며 기존 DB schema upgrade/migration 기능은 아직 없습니다. DB 소유자는 trigger를 제거할 수 있으므로 절대적 불변 보장을 의미하지 않습니다. 앱에서는 append-only와 revision 분리를 강제합니다. 가격 primary key는 `(DataHash,Ticker,Date)`입니다. Company/Disclosure는 현재 파일 snapshot으로 저장합니다.

`RESEARCH_TEST_DB`를 별도 폐기 가능한 PostgreSQL DB로 설정한 경우 통합 테스트를 실행합니다. 미설정이면 해당 테스트는 명시적으로 skip합니다. 로컬 개발 중 PostgreSQL 18의 독립 임시 인스턴스로 실제 저장·조회/UPDATE·DELETE 거부를 검증했습니다.

## Paper 실행과 한계

```powershell
dotnet run --project src/Investment.Cli --no-restore -- paper-start --dataset certified-dataset.json --evidence 'archive-A.json;archive-B.json'
dotnet run --project src/Investment.Cli --no-restore -- paper-step --state paper-SESSION-0.json --observation observation.json
dotnet run --project src/Investment.Cli --no-restore -- paper-evaluate --state paper-SESSION-N.json
```

`paper-start --evidence`는 원본 dataset·소스·전체 연구 결과가 들어 있는 `archive-ID.json`을 요구합니다. 시작 전에 현재 빌드로 모든 학습·검증·walk-forward·holdout·평가를 재현합니다. 판정 문자열만 PAPER_ELIGIBLE로 바꾼 결과, 지표/통과 표시/데이터 분류가 달라진 결과, 원본이나 소스 증거 없는 단독 연구 JSON은 거부합니다. 재현이 일치해도 원래 연구가 부적격이면 시작할 수 없습니다. 아카이브 확인은 기존 holdout의 계산 일관성 검사이며 새 독립 증거가 아닙니다. 같은 dataset·설정·코드 버전과 서로 다른 전략군 조건도 유지합니다.

관측 이벤트는 open → quote(복수 가능) → close 순서이며 매 이벤트의 실측 시각, bid/ask, 거래 가능 상태, close 시 전체 universe의 완성된 bar가 필요합니다. 오래된/미래/역순 이벤트와 보유 종목 시세 누락은 거부합니다. 원본 관측, 신호 당시 자료의 hash/이유, 가격·시간·수량·비용·손익·stop·시장상태를 보존합니다. 기대수익과 take profit은 추정 근거가 없으므로 null로 남깁니다. `data/private/paper-journal/`의 원자적으로 게시된 상태가 기준이며 결과 파일은 내보낸 사본입니다. 입력 사본의 잔고·규칙 변경, 같은 상태에서 분기 실행, 검증 당시 소스와 다른 실행 코드로 시작/계속하는 것을 거부합니다. 결과 내보내기가 실패하면 `paper-recover --state 이전사본.json`으로 이미 commit된 다음 상태를 재출력하며 거래를 다시 실행하지 않습니다. hash chain/로컬 journal은 변조 방지 서명이나 외부 시세 인증이 아닙니다.

파일로 받은 관측은 `VerifiedFeed=false`이며 실제 forward 증거로 승격하지 않습니다. 실제 시세 공급원 adapter와 검증된 관측 경로를 연결해야 합니다. Paper 성과가 나쁘거나 부족하면 백테스트가 좋아도 승격 불가입니다. 최소 120세션·60종료거래가 검토 기본값이며 실제 주문 승인은 구현되어 있지 않습니다.

`paper-evaluate`는 내보낸 JSON을 최신 확정 장부와 대조한 뒤, 시작 상태부터 각 관측을 재계산해 거래·잔고·감사 기록이 일치하는지 확인합니다. 이후 손실이나 중단을 숨기는 과거 사본, 변경된 현금/체결/인증 표시, 다른 실행 버전, 누락된 장부는 평가를 거부합니다. 재계산은 새 관측이나 거래를 게시하지 않습니다. 후보 수는 시작 시 연구 증거의 등록 후보 합집합으로 고정하며 이후 설정 파일의 후보 수를 줄여 통계 보정을 완화하지 않습니다. 이 값이 없는 구형 세션은 현재 평가 경로에서 거부하며 사본에 값을 추가해 승격하지 않습니다.

장부가 없는 `PaperEngine.Evaluate` 직접 호출은 진단용이며 항상 `UNCOMMITTED_PAPER_EVIDENCE`를 포함합니다. `PromotionGate.Review`도 장부와 검증 코드 버전이 없으면 검토 적격으로 승격하지 않습니다. 이 검사는 로컬 원본과 계산의 일관성을 확인하며, 외부 서명/공급원 인증을 대체하지 않습니다. 현재 파일 관측은 재계산해도 실시간 인증 자료가 되지 않습니다. 평가 시 모든 상태를 재계산하므로 긴 세션에서는 비용이 증가하며, 대규모 관측을 위한 저장 구조 최적화는 남아 있습니다.

일봉 백테스트의 손실 한도는 중단 trigger입니다. gap·정지·가격제한폭 때문에 손실 상한을 보장할 수 없습니다. 일봉 stop 체결은 OHLC 근사이며 정확한 체결시각/호가 대기/장중 유동성을 증명하지 못합니다. 시가 크기 산정에는 이미 공개된 최근 거래일의 거래량만 사용합니다(30일 이상 오래된 값은 제외). 당일 high/low/거래량에서 시가 체결 가능 여부를 역추론하지 않습니다. 거래정지·가격제한 등 시점별 체결 가능 상태는 별도 입력인 Tradable로 제공해야 합니다. 양 엔진은 종목별 일일 공유 participation 예산에서 매수/매도와 부분체결을 처리하며 미체결 잔량을 유지합니다. 이는 이전 거래량 기반 근사이며 실제 주문 대기열/호가 잔량/체결량의 증거는 아닙니다. 나누어 청산한 포지션은 최종 청산 이후 하나의 종료 거래로 집계합니다. 비용/slippage 민감도·실제 시세로 보정하기 전 실전 유효성을 인정하지 않습니다. 장부의 미청산 포지션은 추정 순청산가치로 평가해 손실을 숨기지 않습니다.

Regime은 과거 20세션의 연속 구성종목 equal-weight 가격 변화 proxy(bull >3%, bear <-3%, 그 외 sideways)입니다. 기준이 바뀌면 새 진입을 중단하고 거래 가능 시 청산합니다. 산업별·변동성별 regime와 정교한 변화점 모델, 포트폴리오 상관/공통 위험 요인 분석은 후속 작업입니다.

## KRX 공식 일봉 연결

Owner가 가격 공급원으로 KRX를 선택했습니다. [공식 유가증권 일별매매정보](https://openapi.krx.co.kr/contents/OPP/USES/service/OPPUSES002_S2.cmd?BO_ID=JvJFzlAENzZlPBDNGAWC)와 [코스닥 일별매매정보](https://openapi.krx.co.kr/contents/OPP/USES/service/OPPUSES002_S2.cmd?BO_ID=hZjGpkllgCBCWqeTsYFj)의 개발 명세를 확인하여 구현했습니다. 공식 HTTPS 경로에 GET 요청, `AUTH_KEY` 헤더 인증, `basDd=yyyyMMdd` 인자를 사용합니다. [인증키와 각 서비스 활용 승인](https://openapi.krx.co.kr/contents/OPP/INFO/OPPINFO003.jsp) 후 `KRX_API_KEY` 환경변수를 설정해야 합니다.

```powershell
dotnet run --project src/Investment.Cli --no-restore -- krx-fetch --market KOSPI --date 2026-09-25
dotnet run --project src/Investment.Cli --no-restore -- krx-fetch --market KOSDAQ --date 2026-09-25
dotnet run --project src/Investment.Cli --no-restore -- krx-build --manifest config/my-reviewed-manifest.json
```

`krx-fetch`는 전체 시장 원본과 SHA-256, 조회 시각, 종목명, OHLCV, 거래대금, 시가총액, 상장주식수를 보존합니다. 한 번에 한 거래일만 조회합니다. 빈 응답을 거래소 휴장일로 단정하지 않습니다. placeholder `-`는 누락값이며 0이나 전일 가격으로 바꾸지 않습니다.

여러 날짜의 원본을 수집하려면 `KrxCollectionPlan` JSON에 시장과 중복 없이 오름차순인 날짜 목록을 명시합니다. `config/krx-collection.example.json`은 요청 형식 예시이며 실제 거래일을 인증하지 않습니다. 주말을 제외해 자동 생성한 달력을 연구용 거래일로 사용하지 않습니다.

```powershell
dotnet run --project src/Investment.Cli --no-restore -- krx-collect --plan config/my-collection.json --max-requests 5 --interval-seconds 1
```

같은 명령을 다시 실행하면 `--output` 아래 `krx-collections/계획해시/`의 기존 원본을 검증하고 없는 날짜부터 이어갑니다. 매 응답을 원자적으로 저장한 뒤 다음 요청을 수행합니다. 호출 한도와 receipt의 `Attempts`는 해당 실행에서 수집 함수 호출을 시도한 수이며, 키 미설정 등으로 실제 HTTP 송신 전에 실패한 시도도 포함합니다. 호출 간 간격은 해당 실행 안에서 적용합니다. 예시 값은 보수적인 로컬 기본값이고 공급원 승인 quota에 관한 주장이 아닙니다. 승인 범위에 맞춰 조절하고 다른 계획/프로세스의 호출량도 함께 관리해야 합니다.

한 계획의 동시 실행은 파일 lease로 거부합니다. 오류는 자동 재시도하지 않으며 앞서 성공한 원본과 키/예외 메시지를 제외한 수집 receipt를 남깁니다. 빈 응답은 원본을 보존하고 `EMPTY_RESPONSE_REQUIRES_REVIEW`로 중단합니다. 재실행해도 그 날짜를 완료 처리하거나 다음 날짜로 건너뛰지 않습니다. 요청 날짜/시장/관측 시점/원본 해시/정규화 결과가 맞지 않는 저장 자료는 덮어쓰거나 재조회하지 않고 거부합니다. 원본 수집의 `COLLECTED_UNREVIEWED`는 품질·시점 인증이나 dataset 생성 완료를 의미하지 않습니다. receipt의 `SnapshotFiles`를 검토한 manifest에 사용해야 합니다.

`config/krx-manifest.example.json`은 형식 예시이며 역사적 사실을 인증하는 파일이 아닙니다. SnapshotFiles, 실제 거래 세션, 날짜별 universe·산업 섹터·거래 가능 상태·공개시각, 품질 검토 증거를 채워야 합니다. 예시 공개시각 08:00은 승인된 가격 공개 계약이 아니며 공급원 검증이 필요합니다. KRX의 소속부(`SECT_TP_NM`)는 산업 섹터가 아닙니다. 원본이나 파싱 결과가 바뀌었거나 선택 종목이 빠졌으면 dataset 생성을 거부합니다.

미검토 자료의 공개시각은 실제 관측시각보다 앞당기지 않습니다. 역사적 공개 계약/원본을 검토해 인증한 뒤에만 과거 시점 입력으로 사용할 수 있습니다. 신호는 체결 시가 직전까지 공개된 이전 세션의 bar만 봅니다. 종가 직후 공개되는 공급원과 다음날 아침 공개되는 공급원을 구분합니다. 전일 bar가 다음날 시가 이후 공개되면 해당 시가 거래에 사용할 수 없습니다.

키를 터미널 명령 이력에 직접 쓰지 않고 설정하려면 본인 PowerShell에서 다음을 실행합니다. 입력은 숨겨지며 선택한 Windows 사용자 환경변수만 설정합니다. 환경변수 저장 자체는 Windows 자격증명 금고가 아닙니다. 프로젝트는 프로세스 변수, 이어서 사용자 변수를 읽습니다.

```powershell
./scripts/set-api-key.ps1 -Name KRX_API_KEY
./scripts/set-api-key.ps1 -Name OPENDART_API_KEY
```

## 실험 재현과 증거 저장

신규 `demo`/`research`/`backtest`는 입력 dataset, 소스 snapshot, 실행 파일 SHA-256, runtime, 결과를 하나의 `archive-ID.json`에 저장합니다. JSON 직렬화와 flush 후 동일 디렉터리에서 새 파일로 rename하여 완성된 증거만 게시하며, 기존 파일은 덮어쓰지 않습니다. 원본·보고서 사본 저장이 중간에 실패해도 먼저 저장된 아카이브로 재현할 수 있습니다.

```powershell
dotnet run --project src/Investment.Cli --no-restore -- demo
dotnet run --project src/Investment.Cli --no-build --no-restore -- reproduce --archive artifacts/archive-ID.json
```

빌드 시 `Directory.Build.targets`가 소스 파일 집합과 각 파일의 해시를 실행 파일에 포함합니다. 실행할 때 현재 소스와 비교하므로 코드를 수정한 뒤 `--no-build`로 오래된 실행 파일을 실행하면 연구를 거부합니다. 소스 캡처에는 당시 테스트·설정도 포함됩니다. 재현은 같은 소스 내용과 runtime을 요구하고, 다른 버전이면 원래 아카이브 소스를 격리된 작업공간에 복원하여 다시 빌드해야 합니다. 현재 CLI는 자동 소스 복원을 하지 않습니다. 빌드 출처가 없는 구형 아카이브는 이 검증에서 통과했다고 표시하지 않습니다.

재현은 모든 학습 후보, walk-forward fold의 검증·테스트, 마지막 검증·holdout, 체결, 장부, 비용 전/후 지표와 최종 평가를 비교합니다. 실험 식별자·생성 시각만 비교에서 제외합니다. 결과가 다르면 exit code 2이며 출처/데이터가 다르면 실행을 거부합니다. 재현 결과는 새로운 독립 holdout이나 실제 paper 증거로 집계하지 않습니다.

현재 연구 CLI는 cohort당 최종 후보 하나를 선택하지만 paper 시작은 같은 dataset에서 검증한 두 전략을 요구합니다. 두 전략의 생성·검증을 하나의 cohort에서 수행할지, 독립 cohort의 전략을 별도 결합 검증할지 Owner에게 확인 중입니다. 선택되기 전에는 paper 승격 조건을 약화하지 않습니다.
