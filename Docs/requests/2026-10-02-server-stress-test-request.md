# Server Stress Test Scenario 요청 (2026-10-02, 요약 보존)

> 사용자가 붙여 넣은 요청서를 § 번호를 유지해 옮겼다. 사용자 지시: "진행". 새 Stress Framework를 만들지 않고 `ProjectH.QA`를 확장한다.

## 답해야 할 질문
몇 명까지 안정적인가 / 어떤 Gameplay가 가장 비싼가 / 언제 Tick Latency가 무너지는가 / CPU·Memory·GC·Network 중 무엇이 먼저 병목인가 / Burst를 견디는가 / Churn을 견디는가 / 장시간 Memory 누적 / 악성 Client가 품질을 떨어뜨리는가 / DB 장애가 Game Loop에 영향 주는가 / Building이 많을 때 Tick 변화.

## §1-2 원칙
- 재사용: ProjectH.QA, Scenario JSON, Suite, HeadlessClient, spawnActors, moveVectorAll, networkFault, QA Server Metrics, Baseline History, Report, Repeat, Seed Sweep, Parameter Set. 중복 구현 금지, 새 Action은 꼭 필요할 때만.
- Stress Mode: QA 상세 Event 최소화, Live Log 최소화, Inspector Polling 최소화, Screenshot·UnityClient·Manual Step 금지. HeadlessClient만.

## §3-9 모드·지표·구간
- 패턴: Baseline, Ramp, Steady, Spike, Churn, Mixed, Fault, Soak.
- 공통 지표(§4, `/qa/metrics`에 있는 값만): Player Count, Active Sessions, Tick P50/P95/P99/Max, CPU, Managed Memory, Working Set, GC Gen0/1/2, Allocation, Packets/s, Bytes Sent/Recv/s, QA Command Time, DB Queue, DB Save Count, Build Piece Count.
- 구간(§5-7): Warmup / Ramp / Steady / Cooldown을 구분, 시간은 Scenario Config(하드코딩 금지). 판정은 Steady에서.
- Player Count(§8-9): 파일 복사 대신 Parameters 10/25/50/75/100. 100 초과 금지.

## §10-49 Scenario 목록
- stress_baseline(§10): 최소 Input Keepalive만, Player 수 자체 비용.
- stress_movement(§11-13): 계속 이동, Seed 기반, Move/Turn/Sprint/Jump, 그룹(시계/반시계/중앙↔외곽/Seed 랜덤), 한곳에 몰리지 않게.
- stress_combat(§14-17): 실제 Aim→Fire→Hitscan→LagComp→Damage. damagePlayer 금지. Pair/소그룹, 한 Target 집중 금지. Arrange에서 Ammo 충분히, Reload 일부. Fire Rate는 Weapon Config 준수(Spam은 별도).
- stress_lag_compensation(§18-19): 그룹별 0/50/100/200 ms(+작은 Jitter), Move/Aim/Fire 계속.
- stress_building(§20-22): 실제 build Action, Resource Arrange, Wall/Floor/Ramp/Roof, PlayerIndex Offset으로 영역 분리.
- stress_turbo_build(§23): 정상 Rate Limit 안 고빈도.
- stress_build_count(§24-26): 1,000/2,500/5,000/10,000/20,000 Piece(현실적 한도까지), spawnBuildPiece(기존 QA 명령 경로만, 새 Backdoor 금지). Memory/Tick/CPU/GC 측정.
- stress_build_destruction(§27-29): 실제 Damage 경로로 Foundation 파괴 → Support 재계산 → 대량 Collapse(100/500/1,000). Tick Spike/P99/Max/Network Burst/GC.
- stress_loot(§30): Pickup/Drop/Pickup/Use 반복.
- stress_zone(§31): 다수 Zone 밖, 일부 안팎 반복.
- stress_elimination_burst(§32): 몇 초 내 50명 이상 탈락.
- stress_join_ramp(§33): 초당 5/10/20명, 최대 100. stress_join_spike(§34): 100명 동시 접속.
- stress_disconnect_spike(§35). stress_reconnect_churn(§36-38): Cycle마다 10~20 % Disconnect → Grace 안 Reconnect, 비율 Parameter, 수십 Cycle. Leak·Duplicate·Tick Spike 확인.
- stress_input_timeout(§39). stress_invalid_packets(§40-42): 90 정상 + 10 악성, Rate Parameter, 무제한 금지. stress_build_spam(§43): Rate Limit/Reject/Log/Queue Starvation.
- §44: QA HTTP 자체 공격은 범위 밖.
- stress_network_fault_mixed(§45-46): 60 % 정상, 15 % 100 ms, 10 % 200 ms, 10 % 5 % Loss, 5 % Latency+Loss+Jitter. Fault Proxy 사용, 서버 Protocol 변경 금지.
- stress_db_persistence(§47), stress_db_down(§48-49): Queue Bound·Memory·Retry Storm·복구 후 Writer 정상·Game Loop 영향 없음.

## §50-65 실전·장시간
- stress_mixed_match(§50-53): 70 % Moving, 40 % Sprint, 30 % Combat, 20 % Loot, 20 % Building, 10 % Turbo, 일부 Zone/Death/Reconnect/High Ping. 비율은 Parameters. Seed 기반 역할 전환 허용, 결정적 재현.
- stress_final_zone(§54-55): 30~50명 좁은 영역, Combat/Build/Damage/Death.
- Hotspot vs Distributed 비교(§56-58).
- stress_soak(§59-64): 5분/30분/1시간/4시간 Parameter, Mixed Gameplay, Match 반복(Start→Play→Finish→Reset) 버전, Memory/WorkingSet/Sessions/Queue/Build/GC Gen2/Tick 추세, GC 후 Baseline 증가 여부, Start/After Match 1/5/10 Sample.
- 반복 Restart(§65)는 별도 Suite.

## §66-73 Suite
stress-quick(baseline·movement·combat·building·mixed 50), stress-gameplay, stress-network, stress-building, stress-fault, stress-soak, stress-full.

## §74-91 판정·리포트
- 5 ms를 모든 Scenario에 복사 금지(§74). Hard Limit은 Scenario 명시값만, Baseline Regression은 Warning(§75, §78). P50/P95/P99/Max 모두(§76-77). 비교 대상 Tick P95/P99, CPU, Memory, Network Send(§79).
- Player Count 비교 Matrix(§80). FAIL: Crash, Hang, Timeout, 접속 불가, Hard Limit 초과, 예상 밖 Disconnect, Assertion 실패(§81). Warning: Regression, 높은 Max, GC 증가, Memory 증가 추세, Network 급증(§82).
- Stall Watchdog 연결: Stall 시 Tick, 최근 Step, Player Count, Metrics, 최근 Event(§83). Crash: Exit Code, Log Tail, 현재 Step, Actor 수, 마지막 Metrics(§84). QA Process CPU 기록 가능하면(§85).
- 1 Thread·30 Hz·최대 100 Actor 그대로(§86). 100 Actor 개별 HTTP 조회 금지, `/qa/players`·`/qa/metrics`·Events 낮은 주기(§87-88). Event Ring 덮어쓰기 정상(§89).
- R1 Input Latency: 간단히 연결 가능하면 P50/P95/P99, 아니면 "Not Available"(§90-91).

## §92-107 구조
- 파일: QA/Scenarios/Stress/ 아래, 프로젝트 Naming에 맞춤. 중복 최소화(Parameters), description에 목적, Tag(stress/server/movement/combat/building/network/soak/slow/quick/nightly). pre-push에는 넣지 않음(§97).
- 공통 단계(§98): Arrange → Spawn → Wait Connected → Prepare → Warmup → Start Measurement → Workload → Stop Measurement → Assert Healthy → Collect → Cooldown → Cleanup. `mark`로 warmup_start/measurement_start/measurement_end/cooldown(§99).
- 끝에 server.running, 기대 Player 수, Stall 없음(§100). Cleanup 항상, Proxy·DB·Block·Child 원복(§101-102).
- Report 상단 Summary(§103), Batch 비교(§104), Soak 주기 Sample(Config, Bounded)(§105-106). Stress Fuzzer 금지(§107).

## §108-125 우선순위·검증
- 먼저 8개: baseline, movement, combat, building, mixed_match, reconnect_churn, final_zone, soak.
- MVP 상세(§109-116): baseline 10/25/50/75/100; movement(Sprint·방향 전환·일부 Jump, 60 s); combat(Pair, Weapon/Ammo Arrange, Move/Aim/Fire/Reload); building(Resource, 영역, Wall/Floor/Ramp, Turbo 일부); mixed 50·100 우선; reconnect churn 수십 Cycle; final zone; soak 5분 개발 버전.
- Tool 테스트(§117): Actor Group 분배, Player Count Parameter, Seed Determinism, Measurement Window, Cancellation, Cleanup, Metric Sampling, Batch Summary.
- 기존 smoke·pre-push 통과(§118). 서버 수정 시 Server Test 전체(§119). 서버 Production에 Stress 전용 로직 금지(§120). QA API 확장은 부족이 명확할 때만, QA Mode only·localhost·bounded·GameLoop 영향 최소(§121).
- 실제 실행(§124): baseline·movement·combat·building·mixed 50명 필수, 가능하면 100명. 실행 안 한 것을 했다고 보고 금지. 기존 50 Bot p95 ≈0.1 ms와 비교하되 Workload 차이로 Regression 단정 금지(§125).

## §122 절대 규칙
새 Framework 금지, 기존 QA Tool 재사용, Player별 Thread 금지, 100명 초과 금지, Unity로 Stress 금지, Stress 중 Screenshot 금지, 상세 Log 남발 금지, Gameplay Stress는 실제 Protocol, Arrange Cheat와 Act 구분, DB Stress가 Game Loop에서 DB 직접 호출 금지, Building 같은 Grid 금지, Combat 한 Player 집중 금지, Hard Threshold 임의 생성 금지, 측정 안 한 값 추측 금지, 실패를 테스트 삭제로 해결 금지, Cleanup은 항상.

## §123 완료 조건 / §126 최종 보고 형식
추가 Scenario, Suite, Parameters, 실행 방법(실제 명령), 50 Player 결과(Tick P50/P95/P99/Max, CPU, Memory, GC, Network Send/Recv — 실측만), 100 Player 결과(실행했다면), 병목(측정으로 확인된 것만), Regression, 알려진 제한, 다음 Stress 후보.
