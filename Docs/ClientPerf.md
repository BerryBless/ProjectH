# Client 성능 측정 (Phase 8)

## 목적

요청서 Phase 8의 Client 항목(Frame Time, GC, Draw Call, UI, Physics, Memory)을 측정한다. 이 세션에서는 Unity를 실행해 프로파일링할 수 없으므로(사용자 Editor), 측정 절차와 결과 표를 남기고 값은 사용자가 채운다. 원칙은 요청서 §63이다: 측정하고, 병목을 확인하고, 고치고, 다시 측정한다. **측정 없이 Client 코드를 고치지 않는다.** 아래 "정적 점검"의 항목도 모두 측정 전에는 고치지 않는다.

## 절차

봇이 생겨서 혼자서도 100명 상황을 만들 수 있다(`Docs/Bots.md`).

1. 서버를 띄운다.

```bash
dotnet Server/src/ProjectH.Server/bin/Release/net10.0/ProjectH.Server.dll --Server:Port=7777 --Server:MaxPlayers=100 --Server:DevRespawn=true --Server:ConnectBurstPerIp=200
```

2. 봇 99명을 붙인다(내 Client 1명 + 봇 99명 = 100명).

```bash
dotnet Server/src/ProjectH.Bots/bin/Release/net10.0/ProjectH.Bots.dll --port 7777 --count 99 --duration 600
```

3. Unity Editor에서 Play를 누르고 접속한다.
4. Unity Profiler(CPU Usage, Rendering, Memory, UI, Physics 모듈)로 30초를 기록한다. 이동하고 쏘는 구간과 가만히 있는 구간을 모두 넣는다.
5. Development Build(Autoconnect Profiler)에서 한 번 더 기록한다. Editor 값은 에디터 자체 부담이 섞이므로 Build 값이 기준이다.

비교 기준으로 봇 인원을 1명, 50명, 99명으로 바꿔 같은 절차를 반복하면 인원에 비례하는 비용을 가를 수 있다.

## 기록할 값

| 값 | 어디서 보는가 |
|---|---|
| Frame Time p50·p95·최대 | Profiler CPU Usage의 프레임 시간(30초 구간) |
| GC Alloc/frame | CPU Usage의 GC Alloc 열, Memory 모듈의 GC Allocated In Frame |
| Draw Calls, SetPass Calls | Rendering 모듈, Game 뷰 Stats, Frame Debugger |
| UI Canvas Rebuild | UI Profiler(Canvas.BuildBatch, Canvas.SendWillRenderCanvases) |
| Physics 시간 | Physics 모듈, `Physics.SyncTransforms`, `Physics.Raycast` 마커 |
| Total Used Memory | Memory 모듈(Simple 보기), 필요하면 Memory Profiler 스냅샷 |

## 결과

값 칸은 사용자가 측정해서 채운다.

| 값 | Editor, 봇 99명 | Development Build, 봇 99명 |
|---|---|---|
| Frame Time p50 (ms) | 측정 대기(사용자) | 측정 대기(사용자) |
| Frame Time p95 (ms) | 측정 대기(사용자) | 측정 대기(사용자) |
| Frame Time 최대 (ms) | 측정 대기(사용자) | 측정 대기(사용자) |
| GC Alloc/frame (B) | 측정 대기(사용자) | 측정 대기(사용자) |
| Draw Calls | 측정 대기(사용자) | 측정 대기(사용자) |
| SetPass Calls | 측정 대기(사용자) | 측정 대기(사용자) |
| UI Canvas Rebuild | 측정 대기(사용자) | 측정 대기(사용자) |
| Physics 시간 (ms) | 측정 대기(사용자) | 측정 대기(사용자) |
| Total Used Memory (MB) | 측정 대기(사용자) | 측정 대기(사용자) |

측정 환경(PC, Unity 버전, 해상도, URP 설정)도 함께 적는다.

## 정적 점검

Client 코드(`Client/Assets/Scripts`)를 Phase 8에서 읽기만 하고 훑은 결과다(실행하지 않았다). Protocol v7의 `MaxSnapshotEntities`가 100으로 오르기 전에 점검했다. 실행·측정한 값이 아니므로 모든 항목은 "측정 필요"이고, 이 문서의 측정에서 비용이 확인될 때만 고친다(§63). 심각도는 추정이다: 높음 0, 중간 1, 낮음 7.

LINQ, 프레임마다 문자열 만들기, `renderer.material`, 프레임마다 반복되는 `GetComponent`는 찾지 못했다. HUD 텍스트는 값이 바뀔 때만 다시 만든다.

| # | 추정 | 위치 | 내용 | 측정 방법 |
|---|---|---|---|---|
| 1 | 중간 | `GameClient.cs:354`(`FindAimPoint`, `LateUpdate` :164), `RemotePlayers.cs:76`, `PlayerViewFactory.cs:39` | 매 프레임 `Physics.SyncTransforms()`를 부르는데, 원격 뷰 99개가 매 Update 움직이는 `BoxCollider`다(Rigidbody 없음, 움직이는 정적 Collider). 비용이 인원에 비례하고 아무것도 쏘지 않는 프레임에도 든다. | 원격 1·50·99명에서 `Physics.SyncTransforms`, `Physics.Raycast` 마커를 잰다. A/B 측정 실험이다(권장 수정이 아니다): 같은 조건에서 (A) 현재 상태, (B) 뷰에 Kinematic Rigidbody를 단 상태, (C) `SyncTransforms` 호출을 임시로 뺀 상태의 마커 시간을 비교한다. 차이가 크지 않으면 바꾸지 않는다. |
| 2 | 낮음 | `GameClient.cs:164`, 사용처 :181·:187 | `FindAimPoint`(SyncTransforms + Raycast)가 매 프레임 돌지만 결과는 `_pendingSteps > 0`일 때만 쓴다. 렌더 속도가 시뮬레이션 속도보다 높으면 대부분의 프레임이 1번 항목의 비용을 헛되이 낸다. | 목표 프레임 속도에서 Step이 0인 프레임의 비율과 그 프레임의 비용. |
| 3 | 낮음 | `RemotePlayers.cs:69-77`, `RemotePlayerInterpolator.cs:71-94` | `Render`가 99개 뷰 전부에 `SetPositionAndRotation`을 매 프레임 쓴다(안 변한 자세 포함, 매번 `Quaternion.Euler`). Transform 쓰기가 인원에 비례하고 1번 항목을 유발한다. | 99명에서 `Transform.SetPositionAndRotation`과 `GameClient` Update 시간. |
| 4 | 낮음 | `PlayerViewFactory.cs:23`, `:45-47` | 캡슐 렌더러 99개가 각각 Draw 하나다. Mesh와 `sharedMaterial`은 공유하지만 GPU Instancing은 없다. SRP Batcher·URP 설정에 달려 있다. | Frame Debugger와 Rendering 통계(Batches, SetPass)를 99명에서. |
| 5 | 낮음 | `RemotePlayers.cs:27-43`, `PlayerViewFactory.cs:21-47` | 접속·경기 시작 때 원격마다 `CreatePrimitive` + `DestroyImmediate` + `AddComponent<BoxCollider>` + 이름 문자열을 만든다. 한 번의 끊김이고 프레임마다 드는 비용은 아니다. | Development Build에서 생성이 몰리는 프레임. |
| 6 | 낮음 | `ZoneView.cs:116-124`, `DrawCircle` :143-156 | 원이 줄어드는 동안 매 프레임 점 128개를 다시 그린다(`Terrain.Height` 128번 + `LineRenderer.SetPositions`). 인원과 무관하고 할당은 없다. | 축소 중 `ZoneView.Tick`과 `LineRenderer` Mesh 갱신. |
| 7 | 낮음 | `CombatHud.cs:150-163`, 활성 :127-128 | 피격 방향 표시가 활성인 동안 매 프레임 움직이고 회전한다. 인원이 많으면 이 Canvas가 거의 매 프레임 더러워져 같은 Canvas의 다른 텍스트까지 다시 배치할 수 있다. | 교전 중 `Canvas.BuildBatch`, `Canvas.SendWillRenderCanvases`. |
| 8 | 낮음 | `WorldItemViews.cs:84-88`, `GameClient.cs:242`, `PickupRule.cs:19-31`, `WorldItemList.cs:17` | 월드 아이템(최대 256개)을 매 프레임 순회한다. 활성 뷰마다 회전하고, 줍기 후보를 전부 훑는다(접근마다 구조체 복사). 인원이 아니라 아이템 수에 비례한다. | 아이템 256개에서 `WorldItemViews.Tick`, `UpdateInventoryHud`. |

문제가 없다고 본 곳: `NetClient`의 Snapshot 경로(버퍼 재사용), HUD 텍스트 캐시(`CombatHud`, `InventoryHud`, `MatchHud`, `MatchHudText`, `InventoryHudText`, `PoiLabel`), `LocalFireEffects`(고정 풀), `MapWorld`(한 번만 생성), `ShoulderCamera`(단일 결과 `SphereCast`), `SpectatorCamera`와 `RemotePlayers.CollectAlive`(버퍼 재사용). 리뷰 중 `SpectatorCamera._alive`와 `NetClient._snapshotEntities`가 50명 크기라는 지적이 있었으나, `MaxSnapshotEntities`가 100이 되면서(Protocol v7) 해결됐다.

## Client 쪽 Protocol v7 영향

Unity Client 코드는 바뀌지 않았다. 여러 패킷 Snapshot은 패킷마다 독립 적용되고(`GameClient.OnSnapshot`), 양자화된 서버 상태로 재조정해도 예측이 보정되지 않는다는 점은 Client EditMode 테스트가 고정한다(`Docs/Networking.md`).
