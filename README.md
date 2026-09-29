# 한국 주식 Strategy Research / Paper Trading

공개 저장소: [hyeyoung7264/krx-strategy-research](https://github.com/hyeyoung7264/krx-strategy-research). 현재 단계는 검증된 기반 구현이며 전체 목표는 미완료입니다. 남은 작업과 검증 범위는 [docs/status.md](docs/status.md)에 기록했습니다.

우선 목표는 [docs/goal.md](docs/goal.md)에 기록했습니다. 일봉 신호로 다음 거래일 시가부터 수일 보유하는 C# 연구 시스템입니다. 실제 주문 기능은 없습니다. **현재 실제 시장의 양의 기대값이나 일평균 1%를 검증하지 않았습니다.**

첫 실증 연구의 후보·데이터 품질·검증·모의투자 순서는 [전략 연구 계획 제안](docs/strategy-research-plan.md)에 정리했습니다. 이 문서는 사전 등록이나 정책 승인이 아닙니다.

## 실행

.NET 10 SDK가 필요합니다. 저장소 루트에서 실행합니다.

```powershell
dotnet restore Investment.sln --configfile NuGet.Config
dotnet test Investment.sln --no-restore
dotnet run --project src/Investment.Cli --no-restore -- cohort-demo
```

`cohort-demo`는 고정 seed 합성 데이터로 네 후보와 두 전략군, 5개 walk-forward fold, 전략군별 및 공유 자본 포트폴리오의 마지막 holdout을 하나의 등록 연구 주기에서 실행합니다. 결과 JSON, 사람이 읽는 보고서, 원본 dataset, 소스 snapshot을 `artifacts/`에 새 파일로 저장합니다. `demo`/`research`의 단일 최종 후보 연구는 진단용으로 유지하며 이 결과만으로 paper를 시작하지 않습니다. 합성 데이터는 승격 불가합니다. 실제 실험에서는 최초 holdout 접근 시 `data/private/holdout-seals/`를 예약하며 단일 후보 연구와 cohort 모두 같은 dataset의 재사용을 차단합니다. 실패한 예약을 삭제해서 재시도하지 마세요. 새 데이터 revision을 만들어도 이미 본 날짜가 독립적인 holdout이 되는 것은 아닙니다.

## 구조와 현재 동작

| 프로젝트 | 책임 |
|---|---|
| Investment.Core | 시점 검증, CSV, OpenDART, 모멘텀/평균회귀, 비용 포함 백테스트, 지표, walk-forward, 통계 gate, paper 장부, 승격 gate |
| Investment.Persistence | PostgreSQL / EF Core / Npgsql, 불변 원본과 실험 결과, DB 변경 방지 trigger |
| Investment.Cli | 실험·수입·공시 수집·paper 이벤트 수신·DB 명령 |
| Investment.Tests | 누수, 비용, 손실, 재현성, 시간 분할, paper 증거, API, PostgreSQL 통합 검증 |

연구 루프는 사전 등록된 제한된 후보 집합을 평가합니다. 결정적 baseline 외에 `AiResearchWorker`가 학습 전용 후보 생성·비용 포함 피드백·반복을 수행하고 `ai-cohort`가 생성 후보 전체를 전략군/포트폴리오 검증에 연결합니다. 외부 API의 실제 인증·유료 호출은 아직 검증하지 않았습니다. 자유로운 생성 코드를 실행하는 기능은 없습니다.

`config/research.json`의 비용·위험·통계 기준은 가상 연구 기본값입니다. 실제 시장/기간별 세금·증권사 수수료가 확인된 값이 아닙니다. `PolicyReviewed=false`로 승격을 막으며, Owner와 해당 조건을 검토하기 전 true로 변경하지 않습니다. 전략 비중은 최대 50%이며, paper 시작에는 같은 cohort의 서로 다른 전략군 두 버전과 결합 포트폴리오가 모두 통과해야 합니다. 비중 분산만으로 전략 간 상관이 낮다는 의미는 아닙니다.

학습에서 후보 선택 → 검증에서 veto → 변경 없는 미래 테스트를 반복합니다. 미래 테스트 구간은 겹치지 않습니다. 마지막 holdout은 이전 구간에서 선택한 한 버전으로 평가합니다. 블록 bootstrap의 블록 길이는 최소 5세션 또는 최대 보유기간이며, 등록 후보 수에 Bonferroni 보정을 적용합니다. 모델 가정과 작은 표본에 따른 불확실성이 있으므로 통과를 수익 보장으로 해석하지 않습니다.

기본 연구 경로는 `cohort-research`입니다. 전략군당 최소 두 후보를 사전 등록하고 각 전략군에서 학습 성과로 한 버전을 선택합니다. 전략군별 검증과 동일 버전의 공유 자본 포트폴리오 검증을 같은 분할에서 수행합니다. 매 fold의 결합 시뮬레이션은 현금·종목/전략/섹터 비중·유동성 예산을 공유하며 독립 백테스트 수익률을 단순히 더하지 않습니다. 최종 버전은 holdout을 보기 전에 고정합니다. 하나의 holdout에서 미리 등록한 가족별/결합 평가를 수행하되 이를 독립적인 여러 실험으로 세지 않습니다.

cohort의 통계 보정 수는 `등록 개별 후보 수 + 전략군당 한 후보를 택하는 모든 조합 수`로 holdout 접근 전에 정합니다. 기본 네 후보(모멘텀 2, 평균회귀 2)는 8개로 보정하며 전략군과 포트폴리오 모두 같은 보정을 사용합니다. 보정된 꼬리 확률에 bootstrap 표본이 하나도 해당하지 않을 만큼 해상도가 부족하면 통계 gate를 실패시킵니다. 후보를 많이 생성해 검증 기준을 완화하지 않습니다. 전략군별 holdout 일수익률 상관은 진단 자료이며 자동 선택 규칙이나 독립성 증거로 사용하지 않습니다.

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
dotnet run --project src/Investment.Cli --no-restore -- cohort-research --dataset artifacts/dataset-HASH.json
```

날짜 형식은 `yyyy-MM-dd`, AvailableAt에는 명시적 timezone이 필요합니다. Member/섹터/거래 가능 상태는 해당 거래일 당시 자료여야 합니다. 상장된 기간에는 거래정지·무거래 행까지 매 세션 보존합니다. 상장 전과 폐지 후의 행은 만들지 않고, 종목이 나타나거나 사라지는 경계에는 출처와 공개시각을 가진 `LifecycleEvents`를 요구합니다. 보유 종목이 폐지 등으로 가격 없이 사라지면 회수·권리 처리 증거가 없는 백테스트를 중단합니다. 날짜를 임의로 채워 만든 종목이나 잔여 가치를 실제 데이터로 인증하면 안 됩니다.

CSV 수입은 `PointInTimeCertified=false`로 생성됩니다. 인증은 데이터 품질·역사적 universe·수정주가·시점·공식 거래일 캘린더의 별도 검토가 필요합니다. 인증된 JSON에는 `Sessions`에 실제 거래일을 순서대로 명시해야 합니다. 단순히 flag를 바꾸면 품질이 검증되는 것은 아닙니다. `CorporateAction=true`인 자료는 대응하는 명시적 주식 단위 변경이 필요합니다. 분할·병합의 효력일·원시 가격 전환일·입고 가용성을 구분하며, 미지원 단주나 다른 권리의 정산을 추정하지 않습니다. [분할·병합 회계](docs/share-unit-accounting.md)를 참고하세요.

## OpenDART

[공시검색](https://opendart.fss.or.kr/guide/detail.do?apiGrpCd=DS001&apiId=2019001)과 [기업개황](https://opendart.fss.or.kr/guide/detail.do?apiGrpCd=DS001&apiId=2019002)을 읽기 전용 GET으로 호출합니다. 인증키는 로컬 환경변수 `OPENDART_API_KEY`로 설정합니다. 키를 코드·설정파일·CLI 인자·Git에 넣지 마세요.

```powershell
dotnet run --project src/Investment.Cli --no-restore -- dart-disclosures --start 2026-09-01 --end 2026-09-26
dotnet run --project src/Investment.Cli --no-restore -- dart-company --corp-code 00126380
dotnet run --project src/Investment.Cli --no-restore -- dart-check
```

최종보고서만 선택하지 않고 정정보고서까지 수집합니다. 접수번호와 원본 페이지를 보존합니다. API의 날짜 정밀도로 실제 장중 공개시각을 알 수 없으므로 공시 사용 시각은 `max(실제 관측시각, 접수일 다음 날 00:00 KST)`입니다. 오늘 받은 과거 공시를 과거에 알고 있었던 입력으로 사용하지 않습니다. 현재 `rm`의 후속 정정/철회 정보는 과거 전략 feature로 노출하지 않습니다. 기업개황은 오늘의 snapshot이며 과거 종목 master로 사용하지 않습니다. 호출 오류·조회 결과 없음·불완전 pagination을 구분합니다.

`dart-check`는 공식 기업개황 API를 최대 한 번 호출하고, 호출 전 attempt와 완료 receipt를 로컬 증거에 저장합니다. HTTP 오류·거절된 리디렉션·API 상태 코드·비정상 JSON·네트워크 실패를 구분하며 자동 재시도하지 않습니다. 오류 응답 본문·전체 URL·Location·예외 메시지는 저장하지 않습니다. 성공 시 회사 식별자를 대조하고 현재 관측한 원본과 hash를 보존합니다. 키가 반사된 응답은 보존하지 않으며 인증 성공으로 인정하지 않습니다. 진단은 투자 승격이나 역사적 데이터 인증과 별개입니다.

2026-09-27 로컬 키를 확인하고 실제 공시검색·기업개황을 호출했으나 HTTP 302와 공식 `/error1.html` 오류 페이지 응답으로 실패했습니다. 로그인 포털의 승인 표시만으로 API 인증 성공을 주장하지 않습니다. 정상 `000` JSON과 원본 수집은 아직 확인되지 않았습니다.

OpenDART 확장 경로는 [공시·재무 연구 안내](docs/dart-research.md)에 설명했습니다. 전체 재무제표·자사주 취득·유상증자를 요청 예산과 간격 안에서 수집하는 `dart-collect`, 원본 대조와 접수번호 연결로 cutoff까지 관측한 자료만 제공하는 `dart-context`를 추가했습니다. 이 출력은 연구 입력이며 아직 기존 가격 전략의 매매 신호로 연결하지 않았습니다. 2026-09-28 실제 전체 재무제표·XML 비교도 302 오류여서 정상 자료 수집은 미완료입니다.

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

## AI 가설 생성과 실패 피드백

[OpenAI 공식 Structured Outputs 규격](https://developers.openai.com/api/docs/guides/structured-outputs)에 따라 고정 HTTPS Responses endpoint, `text.format` JSON schema와 strict 검증을 사용합니다. 모델에 도구·코드 실행·주문 권한을 주지 않습니다. `store:false`를 명시하며 이는 모든 API 데이터 보존이 없다는 보장이 아닙니다. [공식 Responses 이전 안내](https://developers.openai.com/api/docs/guides/migrate-to-responses).

`config/ai.example.json`은 기본 비활성화이며 모델을 임의로 선택하지 않습니다. 본인 계정에서 사용할 모델과 요청·출력 토큰·입력 길이 한도를 검토한 로컬 설정을 준비하고 `OPENAI_API_KEY`를 환경변수에 넣어야 실제 호출할 수 있습니다. 요청/토큰 제한은 금액 상한과 같지 않습니다. 키는 기록하지 않으며 예시 키 설정 스크립트도 OPENAI_API_KEY를 지원합니다.

학습 자료를 자를 때 해당 구간의 상장·폐지 사건을 보존하고, AI 호출 전에 데이터와 비용표 범위를 검증합니다. 모델에는 실제 날짜 대신 공동 학습 달력의 세션 순번을 제공합니다. 재상장 구간은 별도 가격·거래량 기준으로 정규화하여 이전 종목의 가격 점프를 수익으로 해석하지 않도록 합니다. 날짜별 비용표도 학습 순번과 세율만 전달하며 실제 날짜·근거·미래 구간은 제외합니다.

```powershell
dotnet run --project src/Investment.Cli --no-restore -- ai-explore --dataset data/private/dataset.json --ai-config data/private/ai.json
dotnet run --project src/Investment.Cli --no-restore -- ai-cohort --dataset data/private/dataset.json --ai-config data/private/ai.json
```

첫 학습 구간의 익명화한 가격/거래량 지수와 해당 구간에서 계산한 비용 포함 성과만 모델에 보냅니다. 종목명·실제 ticker·전체 데이터 hash·holdout 가격은 prompt에서 제외합니다. 이후 반복에는 이전 후보의 학습 성과와 위험 이벤트 종류를 반영합니다. 생성 파라미터는 기존 모멘텀/평균회귀의 제한된 범위로 검증하고 원본 응답 hash·정규화 후보·token usage를 대조합니다. 버전 번호만 바꾼 같은 파라미터를 새 후보로 세지 않습니다. 실패/불완전/거절/요청 제한 응답은 후보로 사용하거나 자동 재시도하지 않습니다.

요청 시도와 응답, 학습 피드백, 탐색 결과를 별도 불변 증거로 보존합니다. 중단된 worker를 자동 재개하거나 비용 발생 여부를 추정해 재호출하지 않습니다. `ai-explore`는 학습 탐색이며 승격 증거가 아닙니다. `ai-cohort`는 탐색 중 생성한 후보 전체를 등록하고 모든 조합을 통계 보정 수에 포함합니다. AI 기록과 생성 시각을 아카이브에 보존합니다. 가설 생성 이전에 종료된 forward 검증 구간은 역사적 탐색으로만 인정하고 paper 승격을 막습니다. 익명화만으로 LLM의 사전 지식에 따른 누수 부재를 증명하지 않습니다. 가설 생성 이후의 검증 구간과 실제 forward paper 자료가 필요합니다.

실제 연구의 holdout registry는 작업공간 전체에서 거래일 중복을 검사합니다. source 문자열·종목 집합·데이터 revision·실행 코드를 바꿔도 이미 예약한 날짜를 다시 검증하지 못합니다. 이전 학습 기록을 유지한 채 진짜 새 holdout 날짜를 추가할 수는 있습니다. 구형 seal에 날짜 출처가 없으면 새 연구를 거부하며 원본 증거 검토가 필요합니다. registry를 삭제해서 재시도하거나 다른 작업공간으로 옮겨 독립 실험이라고 부르지 않습니다. 로컬 파일 검사는 외부 서명이나 다른 작업공간의 연구 이력 인증이 아닙니다.

## Paper 실행과 한계

```powershell
dotnet run --project src/Investment.Cli --no-restore -- paper-start --dataset certified-dataset.json --evidence archive-COHORT.json
dotnet run --project src/Investment.Cli --no-restore -- paper-step --state paper-SESSION-0.json --observation observation.json
dotnet run --project src/Investment.Cli --no-restore -- paper-evaluate --state paper-SESSION-N.json
```

`paper-start --evidence`는 원본 dataset·소스·전략군별 결과·결합 검증 결과가 들어 있는 하나의 schema 2 cohort `archive-ID.json`을 요구합니다. 시작 전에 현재 빌드로 모든 학습·검증·walk-forward·holdout·평가를 재현합니다. 판정 문자열만 PAPER_ELIGIBLE로 바꾼 결과, 지표/통과 표시/데이터 분류가 달라진 결과, 원본이나 소스 증거 없는 단독 연구 JSON은 거부합니다. 재현이 일치해도 원래 연구가 부적격이면 시작할 수 없습니다. 독립 단일 후보 연구 결과 두 개만으로 결합 검증을 건너뛰지 않습니다. 아카이브 확인은 기존 holdout의 계산 일관성 검사이며 새 독립 증거가 아닙니다. 같은 dataset·설정·코드 버전과 서로 다른 전략군 조건도 유지합니다.

관측 이벤트는 open → quote(복수 가능) → close 순서이며 매 이벤트의 실측 시각, bid/ask, 거래 가능 상태, close 시 해당 날짜에 활성인 전체 universe의 완성된 bar가 필요합니다. 종목이 새로 나타나거나 사라지면 close 관측의 `LifecycleEvents`에 공개시각과 증거를 명시해야 합니다. 보유 종목의 상장폐지에는 검증된 정산 모델이 없어 장부를 중단합니다. 오래된/미래/역순 이벤트와 보유 종목 시세 누락은 거부합니다. 원본 관측, 신호 당시 자료의 hash/이유, 가격·시간·수량·비용·손익·stop·시장상태를 보존합니다. 기대수익과 take profit은 추정 근거가 없으므로 null로 남깁니다. `data/private/paper-journal/`의 원자적으로 게시된 상태가 기준이며 결과 파일은 내보낸 사본입니다. 입력 사본의 잔고·규칙 변경, 같은 상태에서 분기 실행, 검증 당시 소스와 다른 실행 코드로 시작/계속하는 것을 거부합니다. 결과 내보내기가 실패하면 `paper-recover --state 이전사본.json`으로 이미 commit된 다음 상태를 재출력하며 거래를 다시 실행하지 않습니다. hash chain/로컬 journal은 변조 방지 서명이나 외부 시세 인증이 아닙니다.

파일로 받은 관측은 `VerifiedFeed=false`이며 실제 forward 증거로 승격하지 않습니다. 실제 시세 공급원 adapter와 검증된 관측 경로를 연결해야 합니다. Paper 성과가 나쁘거나 부족하면 백테스트가 좋아도 승격 불가입니다. 최소 120세션·60종료거래가 검토 기본값이며 실제 주문 승인은 구현되어 있지 않습니다.

`paper-evaluate`는 내보낸 JSON을 최신 확정 장부와 대조한 뒤, 시작 상태부터 각 관측을 재계산해 거래·잔고·감사 기록이 일치하는지 확인합니다. 이후 손실이나 중단을 숨기는 과거 사본, 변경된 현금/체결/인증 표시, 다른 실행 버전, 누락된 장부는 평가를 거부합니다. 재계산은 새 관측이나 거래를 게시하지 않습니다. 통계 보정 수는 시작 시 cohort의 개별 후보·조합 수로 고정하며 이후 설정 파일의 후보 수를 줄여 보정을 완화하지 않습니다. `ResearchCandidateCount` 필드에 이 보정 수를 기록합니다. 이 값이 없는 구형 세션은 현재 평가 경로에서 거부하며 사본에 값을 추가해 승격하지 않습니다.

장부가 없는 `PaperEngine.Evaluate` 직접 호출은 진단용이며 항상 `UNCOMMITTED_PAPER_EVIDENCE`를 포함합니다. `PromotionGate.Review`도 장부와 검증 코드 버전이 없으면 검토 적격으로 승격하지 않습니다. 이 검사는 로컬 원본과 계산의 일관성을 확인하며, 외부 서명/공급원 인증을 대체하지 않습니다. 현재 파일 관측은 재계산해도 실시간 인증 자료가 되지 않습니다. 평가 시 모든 상태를 재계산하므로 긴 세션에서는 비용이 증가하며, 대규모 관측을 위한 저장 구조 최적화는 남아 있습니다.

일봉 백테스트의 손실 한도는 중단 trigger입니다. gap·정지·가격제한폭 때문에 손실 상한을 보장할 수 없습니다. 일봉 stop 체결은 OHLC 근사이며 정확한 체결시각/호가 대기/장중 유동성을 증명하지 못합니다. 시가 크기 산정에는 이미 공개된 최근 거래일의 거래량만 사용합니다(30일 이상 오래된 값은 제외). 당일 high/low/거래량에서 시가 체결 가능 여부를 역추론하지 않습니다. 거래정지·가격제한 등 시점별 체결 가능 상태는 별도 입력인 Tradable로 제공해야 합니다. 양 엔진은 종목별 일일 공유 participation 예산에서 매수/매도와 부분체결을 처리하며 미체결 잔량을 유지합니다. 이는 이전 거래량 기반 근사이며 실제 주문 대기열/호가 잔량/체결량의 증거는 아닙니다. 나누어 청산한 포지션은 최종 청산 이후 하나의 종료 거래로 집계합니다. 비용/slippage 민감도·실제 시세로 보정하기 전 실전 유효성을 인정하지 않습니다. 장부의 미청산 포지션은 추정 순청산가치로 평가해 손실을 숨기지 않습니다.

`Costs.TaxSchedule`로 거래일·결제일·총매도세·근거를 명시한 날짜별 표를 사용할 수 있습니다. 표가 있으면 누락 날짜에 고정 세율을 대신 쓰지 않고 중단하며 매도 체결과 추정 순청산가치에 같은 요율을 적용합니다. 기본 고정 비용은 진단용이고 승인 정책이 아닙니다. [공식 근거와 비용표 형식](docs/cost-policy.md)을 참고하세요.

선택 사항인 `ResearchPlan.CostStress`는 미리 등록한 수수료·슬리피지 가산 시나리오로 선택 전략을 고정 재실행합니다. 진단에는 holdout 이전 자료만 사용하며 기존 합격 판정과 가설 수를 바꾸지 않습니다. `config/research-cost-stress.example.json`과 [비용 악화 진단](docs/cost-sensitivity.md)에 실행·재현 방법이 있습니다.

Regime은 과거 20세션의 연속 구성종목 equal-weight 가격 변화 proxy(bull >3%, bear <-3%, 그 외 sideways)입니다. 기준이 바뀌면 새 진입을 중단하고 거래 가능 시 청산합니다. 산업별·변동성별 regime와 정교한 변화점 모델, 포트폴리오 상관/공통 위험 요인 분석은 후속 작업입니다.

## KRX 공식 일봉 연결

Owner가 가격 공급원으로 KRX를 선택했습니다. [공식 유가증권 일별매매정보](https://openapi.krx.co.kr/contents/OPP/USES/service/OPPUSES002_S2.cmd?BO_ID=JvJFzlAENzZlPBDNGAWC)와 [코스닥 일별매매정보](https://openapi.krx.co.kr/contents/OPP/USES/service/OPPUSES002_S2.cmd?BO_ID=hZjGpkllgCBCWqeTsYFj)의 개발 명세를 확인하여 구현했습니다. 공식 HTTPS 경로에 GET 요청, `AUTH_KEY` 헤더 인증, `basDd=yyyyMMdd` 인자를 사용합니다. [인증키와 각 서비스 활용 승인](https://openapi.krx.co.kr/contents/OPP/INFO/OPPINFO003.jsp) 후 `KRX_API_KEY` 환경변수를 설정해야 합니다.

```powershell
dotnet run --project src/Investment.Cli --no-restore -- krx-fetch --market KOSPI --date 2026-09-23
dotnet run --project src/Investment.Cli --no-restore -- krx-fetch --market KOSDAQ --date 2026-09-23
dotnet run --project src/Investment.Cli --no-restore -- krx-basic-fetch --market KOSPI --date 2026-09-23
dotnet run --project src/Investment.Cli --no-restore -- krx-index-fetch --market KOSPI --date 2026-09-23
dotnet run --project src/Investment.Cli --no-restore -- krx-build --manifest config/my-reviewed-manifest.json
```

`krx-fetch`는 전체 시장 원본과 SHA-256, 조회 시각, 종목명, OHLCV, 거래대금, 시가총액, 상장주식수를 보존합니다. `krx-basic-fetch`는 날짜별 종목기본정보, `krx-index-fetch`는 이름이 구분된 지수 일별시세 원본을 보존합니다. 각 서비스는 별도 승인이 필요합니다. 한 번에 한 거래일만 조회합니다. 빈 응답을 거래소 휴장일로 단정하지 않습니다. placeholder `-`는 누락값이며 0이나 전일 가격으로 바꾸지 않습니다. KRX의 거래량 0·시고저가 0·잔존 종가 행은 원문대로 보존하고 0원 시가 체결을 금지합니다. [실제 응답의 초기 품질 감사](docs/krx-data-audit.md)를 참조하세요.

여러 날짜의 원본을 수집하려면 `KrxCollectionPlan` JSON에 시장과 중복 없이 오름차순인 날짜 목록을 명시합니다. `config/krx-collection.example.json`은 요청 형식 예시이며 실제 거래일을 인증하지 않습니다. 주말을 제외해 자동 생성한 달력을 연구용 거래일로 사용하지 않습니다.

계획의 선택적 `Service`는 `DAILY`(일별매매정보), `BASIC`(종목기본정보), `INDEX`(지수 일별시세)입니다. 생략하면 기존 일봉 계획·해시·원본 경로를 그대로 사용합니다. `config/krx-basic-collection.example.json`은 기본정보 요청 예시입니다. 각 계획은 한 시장과 한 서비스를 가지며, 세 서비스와 양 시장은 각각 독립적인 요청 예산을 사용합니다. 여러 실행의 합계 호출량을 별도로 관리해야 합니다.

공식 휴장일 공개 화면을 읽는 `krx-calendar`는 연도별 원본과 후보 거래일을 보존합니다. 지원되는 Open API 계약과는 구분하며, 이 달력만으로 가격 제공시각이나 결제일을 인증하지 않습니다. `krx-audit`는 선택한 원본 revision과 요청 날짜를 대조해 누락·빈 응답·잘못된 가격, 종목 출현/소멸, 주식 수 변화, 큰 미조정 가격 변화를 보고합니다. 변화는 검토 요청이며 상장폐지·분할을 자동 확정하는 사건이 아닙니다. [공식 역사 자료 경로와 접근 한계](docs/historical-data-sources.md)를 참고하세요.

```powershell
dotnet run --project src/Investment.Cli --no-restore -- krx-calendar --year 2026 --start 2026-09-01 --end 2026-09-28
dotnet run --project src/Investment.Cli --no-restore -- krx-audit --plan config/my-audit.json
```

감사 계획 형식은 `config/krx-audit.example.json`에 있습니다. 같은 날짜의 여러 원본 revision을 자동 선택하지 않으며, 누락/빈 날짜를 건너뛴 연속 비교도 하지 않습니다. 감사 결과는 연구 데이터 시점 인증이나 수익성 판정이 아닙니다.

`krx-reference-audit`는 날짜별 일봉·기본정보의 종목/상장주식 수와 표준코드 변화, 지정 지수를 원본과 대조합니다. `krx-review-queue`는 가격 감사와 이 대조 보고서의 발견 항목을 원문 근거를 보존한 검토 목록으로 묶습니다. 큰 자료도 날짜별로 검사하며 모든 검토 항목은 미확정 상태를 유지합니다. [계획 형식·명령·한계](docs/krx-cross-service-audit.md)를 참고하세요.

공개 KIND 상장폐지현황은 다음처럼 읽을 수 있습니다. 검증한 각 페이지를 즉시 보존하고 전체 페이지·건수·중복 검증이 끝난 뒤 전체 snapshot을 저장합니다. 로그인이나 인증키는 필요 없으며 공식 화면 변경·빈 결과 구조 미확인·예산 초과 시 중단합니다. 회사 링크 식별자를 종목코드·ISIN으로 변환하거나 현재 현황을 과거 매도 신호·폐지 정산가로 사용하지 않습니다.

```powershell
dotnet run --project src/Investment.Cli --no-restore -- kind-delistings --start 2026-09-01 --end 2026-09-28 --max-requests 5 --interval-seconds 1
```

분할·배당 공시 원문은 `kind-notice --source KIND공식외부공시주소`로 바이트와 관측시각을 보존합니다. `corporate-append`로 근거를 연결한 해석·정정·취소 기록을 추가하고 `corporate-at`으로 시장 기준시각과 기록부 기준시각을 지정해 조회합니다. 기본 방식은 실제 관측 이후만 허용합니다. 이 기록부 자체는 보유 수량·배당 현금이나 연구 인증을 변경하지 않습니다. [기업행위 근거와 시점별 조회](docs/corporate-actions.md)에 형식과 회계 적용 전제를 설명했습니다. 백테스트·paper의 분할·병합 회계에는 별도의 검토된 실행 입력이 필요합니다.

```powershell
dotnet run --project src/Investment.Cli --no-restore -- krx-collect --plan config/my-collection.json --max-requests 5 --interval-seconds 1
```

같은 명령을 다시 실행하면 `--output` 아래 `krx-collections/계획해시/`의 기존 원본을 검증하고 없는 날짜부터 이어갑니다. 매 응답을 원자적으로 저장한 뒤 다음 요청을 수행합니다. 호출 한도와 receipt의 `Attempts`는 해당 실행에서 수집 함수 호출을 시도한 수이며, 키 미설정 등으로 실제 HTTP 송신 전에 실패한 시도도 포함합니다. 호출 간 간격은 해당 실행 안에서 적용합니다. 예시 값은 보수적인 로컬 기본값이고 공급원 승인 quota에 관한 주장이 아닙니다. 승인 범위에 맞춰 조절하고 다른 계획/프로세스의 호출량도 함께 관리해야 합니다.

한 계획의 동시 실행은 파일 lease로 거부합니다. 오류는 자동 재시도하지 않으며 앞서 성공한 원본과 키/예외 메시지를 제외한 수집 receipt를 남깁니다. 빈 응답은 원본을 보존하고 `EMPTY_RESPONSE_REQUIRES_REVIEW`로 중단합니다. 재실행해도 그 날짜를 완료 처리하거나 다음 날짜로 건너뛰지 않습니다. 요청 날짜/시장/관측 시점/원본 해시/정규화 결과가 맞지 않는 저장 자료는 덮어쓰거나 재조회하지 않고 거부합니다. 원본 수집의 `COLLECTED_UNREVIEWED`는 품질·시점 인증이나 dataset 생성 완료를 의미하지 않습니다. 일봉 receipt의 `SnapshotFiles`만 검토한 가격 manifest나 `krx-audit`에 사용합니다. 기본정보·지수 원본은 별도 검토 자료이며 가격 자료로 자동 병합하지 않습니다.

기본정보 원문은 기준일 필드가 없으므로 snapshot의 `Date`는 요청한 날짜입니다. 재개 시 상장일이 요청일보다 미래인 행과 다른 시장의 행을 거부하지만, 이 검사가 과거 구성종목의 시점 정확성을 인증하지는 않습니다. 지수는 원문의 기준일·지수 분류까지 대조합니다. 서비스별로 원문·해시·관측시각과 정규화 행을 보존하며, 실제 관측시각을 과거 공시시각으로 바꾸지 않습니다.

`config/krx-manifest.example.json`은 형식 예시이며 역사적 사실을 인증하는 파일이 아닙니다. SnapshotFiles, 실제 거래 세션, 날짜별 universe·산업 섹터·거래 가능 상태·공개시각, 품질 검토 증거를 채워야 합니다. 신규상장·상장폐지 경계에는 `LifecycleEvents`의 `Ticker`, `Date`, `Kind`(`LISTED`/`DELISTED`), `AvailableAt`, `Evidence`를 추가합니다. 상장 중간의 누락 행은 여전히 거부하며, 인증 자료에서는 사건 공개시각이 해당 거래일 시가보다 늦으면 거부합니다. 예시 공개시각 08:00은 승인된 가격 공개 계약이 아니며 공급원 검증이 필요합니다. KRX의 소속부(`SECT_TP_NM`)는 산업 섹터가 아닙니다. 원본이나 파싱 결과가 바뀌었거나 선택 종목이 빠졌으면 dataset 생성을 거부합니다.

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

Owner의 계속 시도 요청에 따라 기본 방향은 단일 cohort의 전략군별·결합 포트폴리오 검증으로 구현했습니다. 실제 데이터·검토된 정책·검증된 시세 adapter와 장기 forward paper 결과가 필요한 작업은 남아 있으며 코드 분기 테스트의 통과는 실전 유효성 증거가 아닙니다.
