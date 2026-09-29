# 날짜별 거래비용과 결제일 검토

예시 설정의 고정 매도세 0.20%는 과거 전체에 적용할 수 있는 법정 요율표가 아니다. 2026-09-29 확인한 [증권거래세법 시행령 개정 이력](https://law.go.kr/LSW/lsRvsDocListP.do?chrClsCd=010202&lsId=005028&lsRvsGubun=all)과 [농어촌특별세법 제5조 제1항 제5호](https://www.law.go.kr/LSW/lsLinkCommonInfo.do?chrClsCd=010202&lsJoLnkSeq=1032879999)에 따른 일반적인 KOSPI·KOSDAQ 보통주 매도 총세율은 다음과 같다. 계좌별 면제와 다른 상품은 별도 검토해야 한다.

| 적용 결제 기간 | KOSPI 증권거래세 | KOSPI 농어촌특별세 | KOSDAQ 증권거래세 | 양 시장 총세율 |
|---|---:|---:|---:|---:|
| 2023년 | 0.05% | 0.15% | 0.20% | 0.20% |
| 2024년 | 0.03% | 0.15% | 0.18% | 0.18% |
| 2025년 | 0% | 0.15% | 0.15% | 0.15% |
| 2026-01-01 이후 | 0.05% | 0.15% | 0.20% | 0.20% |

법령의 적용 시점과 거래일은 같다고 가정하면 안 된다. [국세청 집행기준](https://taxlaw.nts.go.kr/downloadPDFFile.do?fleId=300000000000839615&fleSn=703801)의 인쇄면 38쪽은 거래소 거래의 양도시기를 결제 시점으로 설명한다. 예를 들어 [한화투자증권의 2025년 변경 공지](https://www.hanwhawm.com/main/bbs/indexView.cmd?cc_gubun=1&mode=center&nn_id=66823&pageVal=1&vc_bid=notice)는 2024-12-27 체결분부터 새 세율을 적용한다. 달력 연도만 보고 2024년 세율을 쓰면 이 경계가 틀린다.

`Costs.TaxSchedule`은 각 `TradeDate`의 `SettlementDate`, 총세율 `Rate`, 근거 `Evidence`와 표 전체의 `Source`를 저장한다. 포트폴리오 전체에 공통으로 적용할 수 있는 검토된 총세율 표를 입력하는 구조이며 시장·상품별 세율을 자동 선택하지 않는다. 주식·ETF·ETN·KONEX·장외 종목을 섞어 같은 표를 적용하면 안 된다. 행은 거래일 순으로 중복 없이 있어야 하고, 결제일은 거래일 이후이며, 비용과 근거가 유효해야 한다. 프로그램은 근거 문자열 자체의 사실성을 인증하지 않는다.

```json
{
  "TaxSchedule": {
    "Source": "reviewed ordinary-share policy and settlement-calendar evidence reference",
    "Sessions": [
      {
        "TradeDate": "2024-12-27",
        "SettlementDate": "2025-01-02",
        "Rate": 0.0015,
        "Evidence": "https://www.hanwhawm.com/main/bbs/indexView.cmd?cc_gubun=1&mode=center&nn_id=66823&pageVal=1&vc_bid=notice"
      }
    ]
  }
}
```

위 조각은 `Costs`에 넣는 형식 예시이고 연구 기간 전체를 덮는 표가 아니다. 표를 설정하면 백테스트의 모든 평가 거래일과 paper의 각 관측일에 정확한 행이 있어야 한다. 없는 날짜에는 고정 세율로 돌아가지 않고 중단한다. 미래 paper 기간도 필요한 결제 달력·세율 근거를 검토하여 포함해야 한다. 매도 체결과 매일의 추정 순청산가치에 같은 당일 매도세를 사용하며, 비용 전 비교 실행에는 세금표를 적용하지 않는다.

연구 실행·holdout 봉인 전에는 전체 연구 날짜의 표 범위를 검사한다. 잘못된 표 때문에 검증 도중 실패하면서 미래 테스트 기간의 예약만 남기는 일을 방지한다. 시가 손실 판정과 신규 진입 자본 역시 당일 비용 반영 순청산가치를 사용한다. 노출·집중 한도에서 이미 보유한 주식의 금액은 비용 차감 전 가격 기준으로 유지한다.

표는 연구 아카이브·설정·holdout 식별에 포함되고 JSON 저장·복원 후에도 내용 기준으로 비교된다. AI 가설 입력에는 학습 세션의 익명 순번과 세율만 제공하며 실제 날짜, 근거, 미래 구간의 표는 노출하지 않는다.

증권사별 실제 수수료, 계좌 혜택, 세금 원 단위 처리, 체결별/일별 합산 기준, 슬리피지는 아직 확정되지 않았다. 기본 수수료·슬리피지와 `PolicyReviewed=false`를 그대로 유지한다. 법률 자료 확보나 비용 계산 테스트 통과는 투자 정책 승인·실전 비용 보정을 뜻하지 않는다.
