# DB 성능 점검 기준 (MySQL)

`db-query` 점검이 켜졌을 때만 읽는다. Connection 반환과 Transaction 범위는 성능 점검이 아니라 항상 적용 규칙이다(`game-core-rules` 16–17절).

## 38. DB 성능 점검 자동 활성화 조건

다음 작업일 때만 DB 성능 검토를 활성화한다.

- Query 추가
- Query 수정
- Index 추가
- Table 구조 변경
- Transaction
- 반복 Query
- 대량 조회
- 대량 저장
- 실시간 처리 중 DB 접근

실시간 처리 경로(Packet 처리, Game Tick)에서 DB 응답을 기다리면 DB 지연이 곧바로 게임 Latency가 된다. 이 경우 비동기 저장이나 처리 경로 분리를 검토한다.

## 39. Query 성능 점검

성능에 영향을 줄 수 있는 Query라면 다음을 확인한다.

- WHERE
- Index
- 예상 Row 수
- Full Scan
- ORDER BY
- JOIN
- Query Frequency
- 반환 데이터 크기

필요하면 EXPLAIN을 사용한다.

## 40. N+1 점검

반복 Query 또는 Collection 순회 중 Query 호출이 있을 때만 N+1 가능성을 확인한다.

## 41. Index 점검

Query 성능 문제가 있을 때만 Index 추가를 검토한다.

다음을 함께 고려한다.

```text
SELECT 성능
INSERT 비용
UPDATE 비용
DELETE 비용
Storage
```

Index를 무조건 추가하지 않는다.
