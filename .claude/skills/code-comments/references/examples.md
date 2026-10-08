# 함수 주석 예시 (경우별)

`code-comments` 스킬 2절의 표를 전체 코드로 펼친 것이다.

## 여러 입력

```csharp
// 기능: 대상 플레이어에게 서버 판정 피해를 적용한다.
// 입력: attackerId - 공격자 ID, targetId - 대상 ID, damage - 적용할 피해량.
// 출력: 실제 적용된 피해량.
private int ApplyDamage(long attackerId, long targetId, int damage)
```

## void

```csharp
// 기능: 세션을 현재 Match에서 제거하고 관련 상태를 정리한다.
// 입력: session - 제거할 세션.
// 출력: 반환값 없음. 세션과 플레이어 상태가 정리된다.
private void RemoveSession(PlayerSession session)
```

## bool

```csharp
// 기능: 현재 위치에 건축물을 배치할 수 있는지 검사한다.
// 입력: player - 건설을 시도한 플레이어, slot - 대상 건설 슬롯.
// 출력: 배치 가능하면 true, 불가능하면 false.
private bool CanPlaceBuild(Player player, BuildSlot slot)
```

## Try 패턴

```csharp
// 기능: 패킷 데이터에서 플레이어 입력을 파싱한다.
// 입력: packet - 수신한 패킷 데이터.
// 출력: 성공하면 true와 파싱된 입력, 실패하면 false.
private bool TryParseInput(
    ReadOnlySpan<byte> packet,
    out PlayerInput input)
```

## async

```csharp
// 기능: DB에서 플레이어 누적 전적을 비동기로 조회한다.
// 입력: playerId - 조회할 플레이어 ID, cancellationToken - 작업 취소 토큰.
// 출력: 조회된 PlayerStats. 데이터가 없으면 null.
private async Task<PlayerStats?> LoadPlayerStatsAsync(
    long playerId,
    CancellationToken cancellationToken)
```

## 생성자

```csharp
// 기능: Match 인스턴스를 초기 상태로 생성한다.
// 입력: matchId - Match ID, config - 경기 설정.
// 출력: Waiting 상태로 초기화된 Match 객체.
public Match(long matchId, MatchConfig config)
```

## 이벤트 Handler

```csharp
// 기능: Client 연결 종료를 처리하고 재접속 유예 상태로 전환한다.
// 입력: peer - 연결이 종료된 Client Peer, reason - 연결 종료 이유.
// 출력: 반환값 없음. 대상 세션의 연결 상태가 변경된다.
private void OnPeerDisconnected(NetPeer peer, DisconnectInfo reason)
```

## Server Tick

```csharp
// 기능: 현재 Tick의 플레이어 입력과 월드 상태를 처리한다.
// 입력: deltaTime - 이전 Tick 이후 경과 시간.
// 출력: 반환값 없음. Server Authoritative World State가 갱신된다.
private void Tick(float deltaTime)
```

## Network

```csharp
// 기능: 현재 World Snapshot을 관심 영역의 Client에게 전송한다.
// 입력: match - Snapshot을 생성할 Match.
// 출력: 반환값 없음. 대상 Client들에게 Snapshot Packet이 전송된다.
private void BroadcastSnapshot(Match match)
```

## 내부 주석이 필요한 경우

```csharp
// PlayerLock을 InventoryLock보다 항상 먼저 획득해야 한다.
// 역순으로 획득하면 Trade 처리와 Deadlock이 발생할 수 있다.
lock (_playerLock)
{
    lock (_inventoryLock)
    {
        ...
    }
}
```

## 길이

좋음:

```csharp
// 기능: 플레이어의 현재 탄약을 차감한다.
// 입력: player - 대상 플레이어, amount - 차감량.
// 출력: 차감에 성공하면 true.
```

피해야 함:

```csharp
// 기능:
// 이 함수는 플레이어의 탄약 정보를 확인한 다음
// 현재 탄약이 충분한지 검사하고...
```

## 기존 이유 주석이 있는 함수를 수정할 때

```csharp
// 기능: 끊긴 참가자를 재접속 유예 상태로 둔다.
// 입력: player - 연결이 끊긴 참가자, now - 현재 서버 Tick.
// 출력: 반환값 없음. 유예 만료 Tick이 기록된다.
// (기존 이유 주석은 지우지 않고 세 줄 아래에 그대로 둔다. 예: 설계 결정 번호, Lifetime 제약)
private void BeginGrace(PlayerEntity player, uint now)
```
