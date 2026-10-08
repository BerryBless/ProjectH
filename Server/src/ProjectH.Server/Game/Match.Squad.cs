using System;
using System.Numerics;
using LiteNetLib;
using ProjectH.Server.Game.Build;
using ProjectH.Server.Game.Combat;
using ProjectH.Server.Game.Items;
using ProjectH.Server.Game.Squad;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Server.Game;

// Phase 14: teams, knock-downs (DBNO), revives, reboot cards and stations (Docs/Squad.md, spec D1-D13). Same rules as the
// rest of Match: game loop thread only, no lock, no allocation per tick (fixed arrays sized at construction).
public sealed partial class Match
{
    // A team id is a byte, so these arrays hold every id (0 = no team is never used as an index).
    private const int TeamSlots = 256;

    private readonly SquadCatalog _squad;
    private readonly int _teamSize;
    // D1: the join counter of this match object (PlayerEntity.JoinOrder). Only grows; uint lasts far past any server run.
    private uint _joinCounter;
    // D6: teams already wiped out in the current match (their placement is fixed). Cleared at every match start and reset.
    private readonly bool[] _teamOut = new bool[TeamSlots];
    // D2: what each team was last told (TeamState) and the scratch state built at the end of every tick. Index = TeamId;
    // fixed size, so nothing grows. _maxTeamId bounds the per-tick pass (0 = no team: the lobby).
    private readonly TeamState[] _sentTeams = new TeamState[TeamSlots];
    private readonly TeamState[] _teamScratch = new TeamState[TeamSlots];
    private int _maxTeamId;
    // D10: the server tick each station's cooldown ends at (0 = ready), and what every client was last told.
    private readonly uint[] _stationEnd = new uint[RebootStations.Count];
    private RebootStationsState _sentStations;
    private bool _stationsDirty;
    // SendWorldItems: the indexes of the items one recipient may see (cards of other teams left out). Join and resume only.
    private readonly int[] _visibleItems = new int[WorldItems.Capacity];

    // Phase 14 counters since this match object was made (the Health line and the Meter).
    public long Downs { get; private set; }
    public long Revives { get; private set; }
    public long Reboots { get; private set; }
    public long BleedOuts { get; private set; }
    public long CardsDropped { get; private set; }
    public long CardsExpired { get; private set; }
    public long SquadWipes { get; private set; }
    public long ChannelsCancelled { get; private set; }

    // 기능: Health 줄과 Meter용 분대 수치(GameLoop가 매 Tick 복사한다).
    // 입력: 없음.
    // 출력: SquadCounts 값.
    public Diagnostics.SquadCounts SquadCounts() => new(Downs, Revives, Reboots, BleedOuts, CardsDropped, CardsExpired, SquadWipes, ChannelsCancelled);

    // Test seams.
    internal SquadCatalog Squad => _squad;
    internal int TeamSize => _teamSize;
    internal uint StationEndTick(int station) => _stationEnd[station];

    // 기능: 두 플레이어가 같은 팀인지 본다(D1, 모든 Gameplay 팀 판정의 한 곳). TeamId 0(관전자·대기)은 누구와도 같은 팀이 아니다.
    // 입력: a, b - 플레이어.
    // 출력: 같은 팀이면 true.
    internal static bool SameTeam(PlayerEntity a, PlayerEntity b) => a.TeamId != 0 && a.TeamId == b.TeamId;

    // 기능: 팀에 서 있는(살아 있고 기절 아닌) 구성원이 있는지 본다(D5 기절 조건, D6 분대 전멸 검사).
    // 입력: team - 팀 id, except - 빼고 볼 플레이어(null = 없음).
    // 출력: 있으면 true.
    private bool HasUpMember(byte team, PlayerEntity? except)
    {
        if (team == 0) return false;
        foreach (var p in _players)
        {
            if (p != except && p.TeamId == team && p.IsUp) return true;
        }
        return false;
    }

    // 기능: 치명 피해 경로 하나(D6): 사격·자기장·낙하·QA damagePlayer가 체력 0을 만들면 여기로 온다. 기절한 사람은 탈락하고(처치 =
    //   마무리한 사람, 없으면 기절시킨 사람), 같은 팀에 서 있는 구성원이 있으면 기절하며, 아니면 탈락한다. 분대 전멸은 Kill이 본다.
    // 입력: victim - 체력이 0이 된 플레이어, attacker - 공격자(null = 자기장·낙하·QA), cause - 원인(Phase 17: 공격자가 있어도 그대로 전달된다.
    //   사격은 Zone, 폭발은 Explosion).
    // 출력: 반환값 없음. 기절 또는 탈락(필요하면 분대 전멸 연쇄)이 반영되고 방송된다.
    private void ApplyFatal(PlayerEntity victim, PlayerEntity? attacker, DeathCause cause)
    {
        if (!victim.Alive) return;
        if (victim.IsDowned)
        {
            PlayerEntity? killer = attacker ?? DownedKiller(victim);
            Kill(victim, killer, attacker != null ? cause : victim.DownedCause);
            return;
        }
        if (CanBeDowned(victim)) Down(victim, attacker, cause);
        else Kill(victim, attacker, cause);
    }

    // 기능: 치명 피해를 받으면 기절할지 본다(D5): 팀이 있고 같은 팀에 서 있는 다른 구성원이 있다. Solo는 항상 false.
    // 입력: victim - 피해자(기절 아님).
    // 출력: 기절하면 true.
    private bool CanBeDowned(PlayerEntity victim) => _flow.DamageAllowed && HasUpMember(victim.TeamId, victim);

    // 기능: 기절시킨 사람이 아직 경기에 있으면 돌려준다(나간 사람의 id는 다시 쓰일 수 있어 처치를 주지 않는다).
    // 입력: victim - 기절한(했던) 플레이어.
    // 출력: DownedBy 또는 null.
    private PlayerEntity? DownedKiller(PlayerEntity victim)
    {
        PlayerEntity? by = victim.DownedBy;
        return by != null && by != victim && _players.Contains(by) ? by : null;
    }

    // 기능: 플레이어를 기절시킨다(D4, D5): Downed 모드, 체력 = downedHealth, 실드 0, 출혈 시작, 진행 중인 재장전·회복·소생 취소,
    //   PlayerDowned 방송. 자유낙하·글라이드 중이었으면 이어지는 착지의 낙하 피해를 한 번 면한다(DownedInAir). Phase 19: 차량에 탔으면 먼저 내린다.
    // 입력: victim - 서 있던 플레이어, attacker - 기절시킨 사람(null = 없음), cause - 원인(Phase 17: 공격자가 있어도 그대로 보낸다. 사격은 Zone, 폭발은 Explosion).
    // 출력: 반환값 없음.
    private void Down(PlayerEntity victim, PlayerEntity? attacker, DeathCause cause)
    {
        ForceExit(victim);   // Phase 19 D6: out of the vehicle first (the downed body crawls from the exit spot)
        victim.DownedInAir = victim.State.Mode == MovementMode.Freefall || victim.State.Mode == MovementMode.Glide;
        victim.State.Mode = MovementMode.Downed;
        victim.State.ModeTicks = 0;
        victim.State.HorizontalVelocity = Vector2.Zero;
        victim.Sprinting = false;
        victim.Health = _squad.DownedHealth;
        victim.Shield = 0;
        victim.DownedBy = attacker;
        victim.DownedCause = cause;
        victim.BleedCarry = 0;
        victim.Reloading = false;
        victim.ReloadEndTick = 0;
        victim.FireHeld = false;
        ConsumableRules.Cancel(victim.Inventory);
        CancelChannel(victim);
        Downs++;

        var writer = new PacketWriter(_sendBuffer);
        // Phase 17 D8: the cause as given (a shot passes Zone, an explosion Explosion with its owner as the attacker).
        PlayerDowned.Write(ref writer, new PlayerDowned { VictimId = victim.EntityId, AttackerId = attacker?.EntityId ?? 0, Cause = cause });
        Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 기절한 플레이어의 출혈(D5, §37): downedHealth를 bleedOutSeconds에 걸쳐 정수 체력으로 줄인다(나머지는 BleedCarry에 쌓는다).
    //   소생 중이거나 피해가 허용되지 않으면(결과 화면) 멈춘다. 0이 되면 탈락(처치 = 기절시킨 사람).
    // 입력: player - 살아 있는 기절 플레이어.
    // 출력: 출혈로 탈락했으면 true.
    private bool Bleed(PlayerEntity player)
    {
        // Review fix: no bleed when damage is not allowed (the result screen): a downed member of the winning team keeps
        // its placement 1 and is not eliminated after the result went out.
        if (!_flow.DamageAllowed) return false;
        if (player.RevivedBy != null) return false;
        player.BleedCarry += (uint)_squad.DownedHealth;
        while (player.BleedCarry >= _squad.BleedOutTicks && player.Health > 0)
        {
            player.BleedCarry -= _squad.BleedOutTicks;
            player.Health--;
        }
        if (player.Health > 0) return false;
        BleedOuts++;
        Kill(player, DownedKiller(player), player.DownedCause);
        return true;
    }

    // 기능: 한 명의 탈락을 반영한다(D9의 옛 Kill 본문 + Phase 14): 사망, 재장전·회복·소생 취소, 경기 중이면 사람 수와 배치·처치 수,
    //   PlayerDied 방송(announce), 소지품 드롭, 팀이 살아 있으면 들고 있던 카드와 자기 카드 드롭(아니면 카드는 사라진다). Phase 19: 차량에 탔으면 먼저 내린다.
    // 입력: victim - 탈락자, killer - 처치자(null = 없음), cause - PlayerDied의 원인(Phase 17: 처치자가 있어도 그대로. 사격 Zone, 폭발 Explosion),
    //   placement - 보낼(잠정) 배치,
    //   announce - PlayerDied를 보낼지(경기 이탈은 보내지 않는다), teamSurvives - 팀이 아직 살아 있는지(카드를 떨어뜨린다).
    // 출력: 반환값 없음.
    private void EliminateOne(PlayerEntity victim, PlayerEntity? killer, DeathCause cause, byte placement, bool announce, bool teamSurvives)
    {
        ForceExit(victim);   // Phase 19 D6: out first, so DropEverything drops where it stands
        victim.Alive = false;
        victim.RespawnAtTick = ServerTick + _respawnTicks;
        // The reload dies with the player; otherwise the corpse's snapshots would report it (D10).
        victim.Reloading = false;
        victim.ReloadEndTick = 0;
        // Likewise the heal channel: DropEverything does not call Inventory.Clear, and a late Complete
        // must not heal the corpse or the respawned player.
        ConsumableRules.Cancel(victim.Inventory);
        EndChannelsOf(victim);
        victim.DownedBy = null;
        victim.BleedCarry = 0;

        if (_flow.InMatch && victim.Participant)
        {
            _flow.EliminatePlayer();
            victim.Placement = placement;
            victim.EliminatedTick = ServerTick;
            if (killer != null && killer != victim) killer.Kills++;
        }

        if (announce)
        {
            var writer = new PacketWriter(_sendBuffer);
            PlayerDied.Write(ref writer, new PlayerDied
            {
                // Phase 17 D8: the cause as given (shots pass Zone with their killer, explosions Explosion).
                VictimId = victim.EntityId, KillerId = killer?.EntityId ?? 0, Placement = placement, Cause = cause,
            });
            Broadcast(writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        }

        // After PlayerDied, so every client hears of the death before the items appear.
        DropEverything(victim);
        if (teamSurvives)
        {
            DropHeldCards(victim);
            DropCard(victim);
        }
        else
        {
            ClearHeldCards(victim);
        }
    }

    // 기능: 팀을 전멸 처리한다(D6): 배치 = 지금 남은 팀 수, first(이번 탈락자)를 먼저, 이어서 남은 기절 구성원을 같은 Tick에 탈락시키고
    //   (처치 = 각자 기절시킨 사람), 팀 카드를 지우고, 구성원(나간 사람 포함) 모두에게 팀 배치를 준다.
    // 입력: team - 팀 id, first - 이번 탈락자(null = 이미 빠진 뒤의 검사), killer·cause - first의 처치자·원인, announce - first의 PlayerDied 여부.
    // 출력: 반환값 없음.
    private void WipeTeam(byte team, PlayerEntity? first, PlayerEntity? killer, DeathCause cause, bool announce)
    {
        _teamOut[team] = true;
        SquadWipes++;
        byte placement = _flow.EliminateTeam();
        if (first != null) EliminateOne(first, killer, cause, placement, announce, teamSurvives: false);
        foreach (var p in _players)
        {
            if (p.TeamId != team || !p.Alive) continue;
            EliminateOne(p, DownedKiller(p), p.DownedCause, placement, announce: true, teamSurvives: false);
        }
        RemoveTeamCards(team);
        SetTeamPlacement(team, placement);
    }

    // 기능: Up 구성원 수가 줄어든 뒤(경기 이탈, 유예 만료) 팀에 서 있는 사람이 없으면 전멸시킨다(D6).
    // 입력: team - 팀 id.
    // 출력: 반환값 없음.
    private void CheckTeam(byte team)
    {
        if (!_flow.InMatch || team == 0 || _teamOut[team] || HasUpMember(team, null)) return;
        WipeTeam(team, null, null, DeathCause.Zone, announce: false);
    }

    // 기능: 팀 구성원 모두(경기에 남은 참가자와 나간 참가자의 기록)의 배치를 정한다(D6: 배치는 팀의 것).
    // 입력: team - 팀 id, placement - 배치.
    // 출력: 반환값 없음.
    private void SetTeamPlacement(byte team, byte placement)
    {
        foreach (var p in _players)
        {
            if (p.Participant && p.TeamId == team) p.Placement = placement;
        }
        for (int i = 0; i < _leftParticipants.Count; i++)
        {
            if (_leftParticipants[i].TeamId == team) _leftParticipants[i] = _leftParticipants[i] with { Record = _leftParticipants[i].Record with { Placement = placement } };
        }
    }

    // 기능: 경기 시작 Tick에 참가자를 입장 순서로 TeamSize씩 묶는다(D1). 팀은 항상 2개 이상이다: 참가자 수 ≤ TeamSize이면 앞에서부터
    //   절반(올림)과 나머지로 나눈다. Solo는 모두 다른 팀이다.
    // 입력: 없음(_players 순서 = 입장 순서).
    // 출력: 반환값 없음. 참가자의 TeamId와 MatchFlow의 팀 수가 정해진다.
    private void AssignTeams()
    {
        int n = _players.Count;
        int firstTeam = n <= _teamSize ? (n + 1) / 2 : 0;   // the small-match split (D1)
        int teams = 0;
        for (int i = 0; i < n; i++)
        {
            int team = firstTeam > 0 ? (i < firstTeam ? 1 : 2) : i / _teamSize + 1;
            _players[i].TeamId = (byte)team;
            teams = Math.Max(teams, team);
        }
        _maxTeamId = teams;
        _flow.SetTeams(teams);
    }

    // 기능: 개발 모드(DevRespawn) 입장자의 팀을 정한다(경기 흐름이 없어 입장 때 정한다). 지금 있는 플레이어 기준으로 TeamSize칸이 남은
    //   가장 작은 팀 번호, 없으면 아무도 쓰지 않는 가장 작은 번호다. 입장 카운터로 계산하지 않으므로 재접속이 아무리 많아도 번호가
    //   돌아 기존 플레이어와 겹치지 않는다(Solo는 언제나 서로 다른 팀). MaxPlayers ≤ 100 < 255라 번호는 항상 있다.
    // 입력: player - 입장자(아직 _players에 넣기 전).
    // 출력: 반환값 없음. player.TeamId가 정해진다.
    private void AssignDevTeam(PlayerEntity player)
    {
        Span<byte> members = stackalloc byte[TeamSlots];
        foreach (var p in _players)
        {
            if (p.TeamId != 0 && members[p.TeamId] < byte.MaxValue) members[p.TeamId]++;
        }
        int team = 0;
        for (int t = 1; t < TeamSlots && team == 0; t++)
        {
            if (members[t] > 0 && members[t] < _teamSize) team = t;
        }
        for (int t = 1; t < TeamSlots && team == 0; t++)
        {
            if (members[t] == 0) team = t;
        }
        player.TeamId = (byte)team;
        _maxTeamId = Math.Max(_maxTeamId, team);
    }

    // 기능: 경기 시작·라운드 리셋에 모든 분대 상태를 지운다(팀 전멸 표시, 진행, 기절 정보, 스테이션 대기). 알림 없이 지운다(곧 Respawn이 간다).
    // 입력: keepTeams - false면 TeamId도 0으로(라운드 리셋).
    // 출력: 반환값 없음.
    private void ResetSquadState(bool keepTeams)
    {
        Array.Clear(_teamOut);
        Array.Clear(_sentTeams);
        Array.Clear(_stationEnd);
        _stationsDirty = true;
        foreach (var p in _players)
        {
            if (!keepTeams) p.TeamId = 0;
            ClearSquadFields(p);
        }
        if (!keepTeams) _maxTeamId = _flow.DevRespawn ? _maxTeamId : 0;
    }

    // 기능: 한 플레이어의 기절·진행 필드를 지운다(알림 없음).
    // 입력: p - 플레이어.
    // 출력: 반환값 없음.
    private static void ClearSquadFields(PlayerEntity p)
    {
        p.DownedBy = null;
        p.BleedCarry = 0;
        p.DownedInAir = false;
        p.ChannelActive = false;
        p.ReviveTarget = null;
        p.RevivedBy = null;
        p.ChannelEndTick = 0;
    }

    // 기능: 한 Tick의 소생·재투입 진행(D7, D8, D10): 진행 중이면 계속 조건을 보고 끝나면 완료하며, 아니면 E를 누르고 있을 때 시작한다.
    //   누르고 있음은 매 입력의 InteractHeld다(놓친 입력의 반복도 이어지지만 유예 중인 사람은 바로 취소한다, D13). 다른 행동(사격, 회복,
    //   무기·도구 키, 드롭, 재장전)을 한 실제 입력은 취소한다. 경기 중(또는 개발 모드)에만 돈다. Phase 19 D15: 차량에 탄 사람은 시작·계속할 수 없다.
    // 입력: player - 살아 있는 플레이어, input - 이 Tick 입력, sent - 실제로 받은 입력인지, previous - 직전 실제 입력의 버튼, now - 마지막 Tick.
    // 출력: 반환값 없음.
    private void UpdateChannel(PlayerEntity player, in InputCommand input, bool sent, InputButtons previous, uint now)
    {
        // Review fix: only while the match runs (or in the dev sandbox). FinishMatch cancels what was in progress; on the
        // result screen nothing starts, so no reboot can change a fixed placement.
        if (!_flow.InMatch && !_flow.DevRespawn)
        {
            CancelChannel(player);
            return;
        }
        bool held = (input.Buttons & InputButtons.InteractHeld) != 0 && !player.IsGraced;
        bool interrupted = sent && ((input.Buttons & ChannelInterruptHeld) != 0 || (PressedOnly(input.Buttons, previous) & ChannelInterruptEdges) != 0);
        bool able = player.IsUp && CanAct(player);   // Phase 19 D15: never from a seat
        if (player.ChannelActive)
        {
            if (!held || interrupted || !able || !ChannelStillValid(player))
            {
                CancelChannel(player);
                return;
            }
            if (now >= player.ChannelEndTick) CompleteChannel(player, now);
            return;
        }
        if (!held || interrupted || !able) return;
        TryStartChannel(player, now);
    }

    private const InputButtons ChannelInterruptHeld = InputButtons.Fire | InputButtons.UseMedkit | InputButtons.UseShieldCell;
    // Phase 17 D9: pressing 6 (a grenade) is another action too.
    private const InputButtons ChannelInterruptEdges = InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3 | InputButtons.Drop |
                                                       InputButtons.ToolHarvest | InputButtons.ToolBuild | InputButtons.Reload |
                                                       InputButtons.ThrowGrenade;

    // 기능: 진행 중인 채널이 계속될 수 있는지 본다. 소생: 대상이 같은 팀의 기절 상태이고 거리 ≤ reviveRange + 0.5. 재투입: 카드가 있고
    //   스테이션 거리 ≤ rebootRange + 0.5.
    // 입력: player - 진행 중인 행위자.
    // 출력: 계속되면 true.
    private bool ChannelStillValid(PlayerEntity player)
    {
        if (player.Channel == ChannelKind.Revive)
        {
            PlayerEntity? target = player.ReviveTarget;
            return target != null && target.IsDowned && SameTeam(player, target) &&
                   Vector3.Distance(player.State.Position, target.State.Position) <= _squad.ReviveRange + SquadCatalog.ChannelCancelSlack;
        }
        return player.Inventory.CardCount > 0 && InStationRange(player.State.Position, player.ChannelStation, _squad.RebootRange + SquadCatalog.ChannelCancelSlack);
    }

    // 기능: 소생 대상을 찾는다(D8): 같은 팀, 기절, 아직 아무도 소생하지 않음, 발 사이 거리 ≤ reviveRange, 두 눈 사이에 맵 상자·문·채집물·
    //   지형·조각이 없다, 일어설 공간(웅크린 상자)이 있다. 가장 가까운 대상.
    // 입력: player - 서 있는 행위자.
    // 출력: 대상 또는 null.
    private PlayerEntity? FindReviveTarget(PlayerEntity player)
    {
        if (player.TeamId == 0) return null;
        PlayerEntity? best = null;
        float bestDistance = float.MaxValue;
        foreach (var p in _players)
        {
            if (p == player || !p.IsDowned || !SameTeam(player, p) || p.RevivedBy != null) continue;
            float distance = Vector3.Distance(player.State.Position, p.State.Position);
            if (distance > _squad.ReviveRange || distance >= bestDistance || !InSight(player, p) || !CanRise(p)) continue;
            best = p;
            bestDistance = distance;
        }
        return best;
    }

    // 기능: 행위자의 눈에서 대상의 눈까지 막는 것이 없는지 본다(맵 상자·닫힌 문·채집물·지형·조각).
    // 입력: from - 행위자, to - 대상.
    // 출력: 보이면 true.
    private bool InSight(PlayerEntity from, PlayerEntity to)
    {
        Vector3 eye = from.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(from.State.Mode), 0f);
        Vector3 target = to.State.Position + new Vector3(0f, CombatRules.EyeHeightOf(to.State.Mode), 0f);
        Vector3 delta = target - eye;
        float distance = delta.Length();
        if (distance < 1e-3f) return true;
        Vector3 direction = delta / distance;
        if (HitScan.TraceWorld(eye, direction, distance, Blockers, GameMap.Terrain) < distance - 1e-3f) return false;
        return !PieceTrace.Trace(eye, direction, distance, _build, out _, out _, out _);
    }

    // 기능: 발 위치가 스테이션 사용 범위 안인지 본다(지면 거리 ≤ range, 높이 차 ≤ StationHeightRange).
    // 입력: feet - 발 위치, station - 스테이션 번호, range - 지면 거리 한계.
    // 출력: 안이면 true.
    private static bool InStationRange(Vector3 feet, int station, float range)
    {
        Vector3 d = RebootStations.All[station] - feet;
        return d.X * d.X + d.Z * d.Z <= range * range && MathF.Abs(d.Y) <= SquadCatalog.StationHeightRange;
    }

    // 기능: 재투입할 스테이션을 찾는다(D10, §44): 카드를 들고 있고, 범위 안이며, 대기 끝, 다른 사람이 쓰고 있지 않음. 가장 가까운 것.
    // 입력: player - 서 있는 행위자, now - 마지막 Tick.
    // 출력: 스테이션 번호 또는 -1.
    private int FindStation(PlayerEntity player, uint now)
    {
        if (player.Inventory.CardCount == 0 || player.TeamId == 0) return -1;
        int best = -1;
        float bestSq = float.MaxValue;
        ReadOnlySpan<Vector3> all = RebootStations.All;
        for (int i = 0; i < all.Length; i++)
        {
            if (now < _stationEnd[i] || !InStationRange(player.State.Position, i, _squad.RebootRange) || StationInUse(i)) continue;
            Vector3 d = all[i] - player.State.Position;
            float sq = d.X * d.X + d.Z * d.Z;
            if (sq >= bestSq) continue;
            best = i;
            bestSq = sq;
        }
        return best;
    }

    // 기능: 다른 플레이어가 이 스테이션에서 재투입 중인지 본다(스테이션 하나에 사용자 한 명).
    // 입력: station - 스테이션 번호.
    // 출력: 쓰는 사람이 있으면 true.
    private bool StationInUse(int station)
    {
        foreach (var p in _players)
        {
            if (p.ChannelActive && p.Channel == ChannelKind.Reboot && p.ChannelStation == station) return true;
        }
        return false;
    }

    // 기능: E 우선순위(D7): 이 플레이어 범위 안에 소생 대상이나 쓸 수 있는 스테이션이 있는지 본다(있으면 E 누름은 줍기·문을 하지 않는다).
    // 입력: player - 행위자, now - 마지막 Tick.
    // 출력: 있으면 true.
    private bool HasChannelTarget(PlayerEntity player, uint now) =>
        player.TeamId != 0 && (player.ChannelActive || FindReviveTarget(player) != null || FindStation(player, now) >= 0);

    // 기능: 소생 또는 재투입을 시작한다(소생 대상이 먼저). 진행 중인 회복은 끊는다. 팀에 ChannelState(Active)를 보낸다.
    // 입력: player - E를 누르고 있는 서 있는 행위자, now - 마지막 Tick.
    // 출력: 반환값 없음.
    private void TryStartChannel(PlayerEntity player, uint now)
    {
        PlayerEntity? target = FindReviveTarget(player);
        if (target != null)
        {
            player.Channel = ChannelKind.Revive;
            player.ReviveTarget = target;
            target.RevivedBy = player;
            player.ChannelEndTick = now + _squad.ReviveTicks;
        }
        else
        {
            int station = FindStation(player, now);
            if (station < 0) return;
            player.Channel = ChannelKind.Reboot;
            player.ChannelStation = station;
            player.ChannelEndTick = now + _squad.RebootTicks;
        }
        player.ChannelActive = true;
        ConsumableRules.Cancel(player.Inventory);
        SendChannel(player, active: true);
    }

    // 기능: 진행을 끝낸다(D8 완료). 소생: 대상이 서 있을 공간이 있으면 Ground, 없으면 Crouch로 일어나고 체력 reviveHealth, 실드 0.
    //   재투입: 들고 있던 카드의 주인 모두를 스테이션 옆에 되살리고 카드를 쓰며 스테이션 대기를 시작한다.
    // 입력: player - 행위자, now - 마지막 Tick.
    // 출력: 반환값 없음. 팀에 ChannelState(끝)가 간다.
    private void CompleteChannel(PlayerEntity player, uint now)
    {
        if (player.Channel == ChannelKind.Revive)
        {
            PlayerEntity target = player.ReviveTarget!;
            // The target may have crawled under something during the revive: it never rises into a piece or a box.
            if (!CanRise(target))
            {
                CancelChannel(player);
                return;
            }
            EndChannel(player);
            StandUp(target);
            Revives++;
            return;
        }
        int station = player.ChannelStation;
        EndChannel(player);
        Inventory inventory = player.Inventory;
        int k = 0;
        for (int i = 0; i < inventory.CardCount; i++)
        {
            PlayerEntity? owner = FindByJoinOrder(inventory.CardOwners[i]);
            if (owner == null || owner.Alive || !owner.Participant || !SameTeam(owner, player)) continue;
            RebootPlayer(owner, station, k++);
        }
        ClearHeldCards(player);
        _stationEnd[station] = now + _squad.StationCooldownTicks;
    }

    // 기능: 기절한 플레이어가 지금 자리에서 적어도 웅크린 자세로 일어설 수 있는지 본다(웅크린 상자 1.2 m가 맵 상자·문·채집물·조각·
    //   경사면에 박히지 않음). 기절 상자(0.9 m)로만 들어갈 수 있는 낮은 틈에서는 소생하지 않는다(일어서며 조각을 뚫지 않게).
    // 입력: target - 기절한 플레이어.
    // 출력: 일어설 수 있으면 true.
    private bool CanRise(PlayerEntity target)
    {
        Vector3 feet = target.State.Position;
        return !MovementSimulation.Penetrates(feet, MovementTuning.CrouchHeight, GatherAround(feet));
    }

    // 기능: 소생된 대상을 일으킨다: 서 있는 상자가 무엇에도 박히지 않으면 Ground, 아니면 Crouch(이후 평소 자세 규칙으로 선다).
    // 입력: target - 기절한 대상.
    // 출력: 반환값 없음.
    private void StandUp(PlayerEntity target)
    {
        Vector3 feet = target.State.Position;
        bool stand = !MovementSimulation.Penetrates(feet, MoveSettings.Height, GatherAround(feet));
        target.State.Mode = stand ? MovementMode.Ground : MovementMode.Crouch;
        target.State.ModeTicks = 0;
        target.State.HorizontalVelocity = Vector2.Zero;
        target.Health = _squad.ReviveHealth;
        target.Shield = 0;
        target.DownedBy = null;
        target.BleedCarry = 0;
        target.RevivedBy = null;
    }

    // 기능: 카드 주인을 스테이션 옆 땅에 되살린다(D10, §45): Ground 모드, 체력 100, rebootLoadout 장비(원래 인벤토리는 없다),
    //   경기의 남은 사람 수를 되돌린다. 처치 수는 유지된다.
    // 입력: owner - 탈락한 같은 팀 참가자, station - 스테이션 번호, k - 이번 재투입의 몇 번째(자리를 나눈다).
    // 출력: 반환값 없음. PlayerRespawned가 방송된다.
    private void RebootPlayer(PlayerEntity owner, int station, int k)
    {
        Vector3 center = RebootStations.All[station];
        // Inside RebootStations.ClearRadius (2 m) of the station, so clear of every box, door and harvestable.
        float angle = k * MathF.PI * 0.5f;
        float x = center.X + MathF.Cos(angle) * RebootSpotRadius;
        float z = center.Z + MathF.Sin(angle) * RebootSpotRadius;
        Respawn(owner, new Vector3(x, GameMap.Terrain.Height(x, z), z));
        _squad.RebootLoadout.ApplyTo(owner.Inventory, _weapons);
        owner.Shield = _squad.RebootLoadout.Shield;
        _flow.RestorePlayer();
        owner.Placement = 0;
        owner.EliminatedTick = 0;
        Reboots++;
    }

    private const float RebootSpotRadius = 1.2f;

    // 기능: 경기 종료 때 진행 중인 소생·재투입을 모두 끊는다(결과 화면에서 배치가 바뀌지 않게).
    // 입력: 없음.
    // 출력: 반환값 없음. 팀들에 ChannelState(끝)가 간다.
    private void CancelAllChannels()
    {
        foreach (var p in _players) CancelChannel(p);
    }

    // 기능: 진행 중인 채널을 취소한다(행위자 기준). 소생 대상의 출혈이 다시 흐른다. 팀에 ChannelState(끝)를 보낸다.
    // 입력: player - 행위자.
    // 출력: 반환값 없음(진행 중이 아니면 아무것도 하지 않는다).
    private void CancelChannel(PlayerEntity player)
    {
        if (!player.ChannelActive) return;
        ChannelsCancelled++;
        EndChannel(player);
    }

    // 기능: 채널 상태를 지우고 팀에 끝을 알린다(완료와 취소 공통).
    // 입력: player - 행위자.
    // 출력: 반환값 없음.
    private void EndChannel(PlayerEntity player)
    {
        if (player.Channel == ChannelKind.Revive && player.ReviveTarget != null && player.ReviveTarget.RevivedBy == player)
            player.ReviveTarget.RevivedBy = null;
        player.ChannelActive = false;
        SendChannel(player, active: false);
        player.ReviveTarget = null;
    }

    // 기능: 기절·탈락하는 플레이어와 관련된 채널을 모두 끝낸다(자기 진행과, 자기를 소생하던 사람의 진행).
    // 입력: victim - 기절·탈락하는 플레이어.
    // 출력: 반환값 없음.
    private void EndChannelsOf(PlayerEntity victim)
    {
        CancelChannel(victim);
        if (victim.RevivedBy != null) CancelChannel(victim.RevivedBy);
        victim.RevivedBy = null;
    }

    // 기능: 피해를 받은 플레이어의 진행을 정책대로 끊는다(D8 reviveCancelOnDamage, 재투입도 같은 규칙).
    // 입력: target - 피해자.
    // 출력: 반환값 없음.
    private void OnDamaged(PlayerEntity target)
    {
        if (_squad.ReviveCancelOnDamage) CancelChannel(target);
    }

    // 기능: 행위자의 팀(대상 포함) 모두에게 ChannelState를 보낸다.
    // 입력: actor - 행위자, active - 시작이면 true, 끝이면 false.
    // 출력: 반환값 없음.
    private void SendChannel(PlayerEntity actor, bool active)
    {
        if (actor.TeamId == 0) return;
        var writer = new PacketWriter(_sendBuffer);
        ChannelState.Write(ref writer, ChannelOf(actor, active));
        foreach (var p in _players)
        {
            if (SameTeam(p, actor)) _send(p.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        }
    }

    // 기능: 행위자의 채널을 ChannelState 값으로 만든다.
    // 입력: actor - 행위자, active - 진행 중이면 true.
    // 출력: ChannelState 값.
    private static ChannelState ChannelOf(PlayerEntity actor, bool active) => new()
    {
        Kind = actor.Channel,
        ActorId = actor.EntityId,
        Target = actor.Channel == ChannelKind.Revive ? actor.ReviveTarget?.EntityId ?? 0 : (ushort)actor.ChannelStation,
        EndTick = actor.ChannelEndTick,
        Active = active,
    };

    // 기능: 이 JoinOrder의 플레이어를 찾는다(카드 주인 확인, D9).
    // 입력: joinOrder - 입장 순번.
    // 출력: 플레이어 또는 null(나갔다).
    private PlayerEntity? FindByJoinOrder(uint joinOrder)
    {
        foreach (var p in _players)
        {
            if (p.JoinOrder == joinOrder) return p;
        }
        return null;
    }

    // 기능: 탈락한 플레이어의 Reboot 카드를 탈락 위치에 떨어뜨린다(D9, 팀이 살아 있을 때만). 같은 팀에게만 알린다.
    // 입력: owner - 카드 주인(방금 탈락).
    // 출력: 반환값 없음.
    private void DropCard(PlayerEntity owner)
    {
        if (!_flow.InMatch || !owner.Participant || owner.TeamId == 0) return;
        if (SpawnCard(owner, DropAt(owner, Vector3.Zero)) != 0) CardsDropped++;
    }

    // 기능: 들고 있던 카드를 몸 주위에 떨어뜨린다(D9: 소지자가 탈락하면 카드도 떨어진다).
    // 입력: holder - 탈락하거나 떠나는 소지자.
    // 출력: 반환값 없음. 카드 칸이 빈다.
    private void DropHeldCards(PlayerEntity holder)
    {
        Inventory inventory = holder.Inventory;
        for (int i = 0; i < inventory.CardCount; i++)
        {
            PlayerEntity? owner = FindByJoinOrder(inventory.CardOwners[i]);
            if (owner == null || owner.Alive) continue;
            Vector3 offset = ItemRules.Offset(holder.State.Yaw + 360f * i / inventory.CardCount + 45f, ItemRules.DeathDropRadius * 0.5f);
            SpawnCard(owner, DropAt(holder, offset));
        }
        ClearHeldCards(holder);
    }

    // 기능: 소지자의 카드 칸을 비운다(카드는 사라진다: 사용, 팀 전멸, 떠남).
    // 입력: holder - 소지자.
    // 출력: 반환값 없음. 카드가 있었으면 Changed가 켜진다.
    private static void ClearHeldCards(PlayerEntity holder)
    {
        Inventory inventory = holder.Inventory;
        if (inventory.CardCount == 0) return;
        Array.Clear(inventory.CardOwners);
        inventory.CardCount = 0;
        inventory.Changed = true;
    }

    // 기능: 카드 월드 아이템을 만든다(Amount = 주인 Entity id, 주인 JoinOrder·팀·수명은 서버만 안다). 주인 팀에게만 ItemSpawned를 보낸다.
    // 입력: owner - 카드 주인, position - 놓을 위치.
    // 출력: 새 아이템 id, 넣지 못했으면 0(가득 찬 저장소에 지울 드롭이 없음, 실제로는 일어나지 않는다).
    private ushort SpawnCard(PlayerEntity owner, Vector3 position)
    {
        if (!_worldItems.TryAdd(ItemKind.RebootCard, 0, 0, owner.EntityId, position, -1, out ushort itemId, out ushort evictedId,
                owner.TeamId, owner.JoinOrder, ServerTick + _squad.CardLifetimeTicks))
            return 0;
        if (evictedId != 0) BroadcastItemRemoved(evictedId, 0);
        var writer = new PacketWriter(_sendBuffer);
        ItemSpawnedPacket.Write(ref writer, _worldItems[_worldItems.Count - 1].Data);
        BroadcastTeam(writer.WrittenSpan, owner.TeamId);
        return itemId;
    }

    // 기능: 수명이 끝난 카드를 지운다(D9). 카드가 없으면 바로 끝난다.
    // 입력: now - 마지막 Tick.
    // 출력: 반환값 없음.
    private void ExpireCards(uint now)
    {
        if (_worldItems.CardCount == 0) return;
        for (int i = _worldItems.Count - 1; i >= 0; i--)
        {
            ref readonly WorldItem item = ref _worldItems[i];
            if (item.Data.Kind != ItemKind.RebootCard || now < item.ExpireTick) continue;
            RemoveItemAt(i);
            CardsExpired++;
        }
    }

    // 기능: 떠나는 플레이어의 카드를 모두 지운다(D9: 월드의 카드와 팀원이 들고 있는 카드). 다시 쓰인 Entity id가 가로채지 않는다.
    // 입력: owner - 떠나는 플레이어(이미 _players에서 빠졌을 수 있다).
    // 출력: 반환값 없음.
    private void RemoveCardsOf(PlayerEntity owner)
    {
        if (_worldItems.CardCount > 0)
        {
            for (int i = _worldItems.Count - 1; i >= 0; i--)
            {
                if (_worldItems[i].Data.Kind == ItemKind.RebootCard && _worldItems[i].CardOwner == owner.JoinOrder) RemoveItemAt(i);
            }
        }
        foreach (var p in _players)
        {
            int index = p.Inventory.IndexOfCard(owner.JoinOrder);
            if (index >= 0) p.Inventory.RemoveCardAt(index);
        }
    }

    // 기능: 전멸한 팀의 카드를 모두 지운다(D9: 떨어뜨리지 않고 있던 카드도 사라진다).
    // 입력: team - 팀 id.
    // 출력: 반환값 없음.
    private void RemoveTeamCards(byte team)
    {
        if (_worldItems.CardCount > 0)
        {
            for (int i = _worldItems.Count - 1; i >= 0; i--)
            {
                if (_worldItems[i].Data.Kind == ItemKind.RebootCard && _worldItems[i].CardTeam == team) RemoveItemAt(i);
            }
        }
        foreach (var p in _players)
        {
            if (p.TeamId == team) ClearHeldCards(p);
        }
    }

    // 기능: 카드를 줍는다(D9): 같은 팀 카드만(FindNearest가 걸렀다), 소지 한도(maxCardsHeld)까지.
    // 입력: player - 줍는 사람, index - 카드 아이템 index.
    // 출력: 결과 코드(Ok 또는 Full).
    private PickupResultCode PickupCard(PlayerEntity player, int index)
    {
        if (player.Inventory.CardCount >= _squad.MaxCardsHeld) return PickupResultCode.Full;
        uint owner = _worldItems[index].CardOwner;
        if (!player.Inventory.AddCard(owner)) return PickupResultCode.Full;
        RemoveItemAt(index);
        return PickupResultCode.Ok;
    }

    // 기능: 팀 구성원 모두에게 보낸다(TeamId 0은 아무에게도, 카드 아이템이 아니면 BroadcastTeam(.., 0) = 모두).
    // 입력: data - 패킷, team - 팀 id(0 = 모두).
    // 출력: 반환값 없음.
    private void BroadcastTeam(ReadOnlySpan<byte> data, byte team)
    {
        foreach (var p in _players)
        {
            if (team == 0 || p.TeamId == team) _send(p.PeerId, data, DeliveryMethod.ReliableOrdered);
        }
    }

    // 기능: 한 구성원의 팀 상태 칸을 만든다(D2): 상태, 10 단위로 올린 체력, 탈락했으면 카드 위치.
    // 입력: p - 구성원.
    // 출력: TeamMember 값.
    private TeamMember MemberOf(PlayerEntity p)
    {
        var member = new TeamMember { EntityId = p.EntityId };
        if (p.Alive)
        {
            member.State = p.IsDowned ? TeamMemberState.Downed : TeamMemberState.Up;
            member.Health = RoundHealth(p.Health);
            return member;
        }
        member.State = TeamMemberState.Eliminated;
        foreach (var holder in _players)
        {
            if (!SameTeam(holder, p) || holder.Inventory.IndexOfCard(p.JoinOrder) < 0) continue;
            member.Flags |= TeamMemberFlags.CardHeld;
            if (holder.ChannelActive && holder.Channel == ChannelKind.Reboot) member.State = TeamMemberState.Rebooting;
        }
        if (_worldItems.CardCount > 0)
        {
            for (int i = 0; i < _worldItems.Count; i++)
            {
                if (_worldItems[i].Data.Kind == ItemKind.RebootCard && _worldItems[i].CardOwner == p.JoinOrder) member.Flags |= TeamMemberFlags.CardDropped;
            }
        }
        return member;
    }

    // 기능: 체력을 10 단위로 올린다(D2, 0 < h는 10..100).
    // 입력: health - 체력.
    // 출력: 올린 값(0 이하면 0).
    internal static byte RoundHealth(int health) => health <= 0 ? (byte)0 : (byte)Math.Min(CombatRules.MaxHealth, (health + 9) / 10 * 10);

    // 기능: 모든 팀의 지금 상태를 _teamScratch에 만든다(구성원 순서 = 입장 순서 = _players 순서).
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void BuildTeamStates()
    {
        for (int t = 1; t <= _maxTeamId; t++)
        {
            _teamScratch[t] = default;
            _teamScratch[t].TeamId = (byte)t;
        }
        foreach (var p in _players)
        {
            if (p.TeamId == 0 || p.TeamId > _maxTeamId) continue;
            ref TeamState s = ref _teamScratch[p.TeamId];
            if (s.Count >= SquadConstants.MaxTeamSize) continue;
            s.Set(s.Count, MemberOf(p));
            s.Count++;
        }
    }

    // 기능: Tick 끝에 상태·올린 체력·카드가 바뀐 팀에 TeamState를 보낸다(D2). 각 구성원에게 자기 팀만.
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void SendTeamChanges()
    {
        if (_maxTeamId == 0) return;
        BuildTeamStates();
        for (int t = 1; t <= _maxTeamId; t++)
        {
            ref TeamState s = ref _teamScratch[t];
            if (s.Count == 0 || SameTeamState(s, _sentTeams[t])) continue;
            _sentTeams[t] = s;
            var writer = new PacketWriter(_sendBuffer);
            TeamState.Write(ref writer, s);
            foreach (var p in _players)
            {
                if (p.TeamId == t) _send(p.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    // 기능: 한 플레이어에게 자기 팀 상태를 지금 보낸다(경기 중 입장·Resume). 팀이 없으면 보내지 않는다.
    // 입력: player - 받는 사람.
    // 출력: 반환값 없음.
    private void SendTeamTo(PlayerEntity player)
    {
        if (player.TeamId == 0 || player.TeamId > _maxTeamId) return;
        BuildTeamStates();
        var writer = new PacketWriter(_sendBuffer);
        TeamState.Write(ref writer, _teamScratch[player.TeamId]);
        _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 두 팀 상태가 보낼 내용으로 같은지 본다.
    // 입력: a, b - 팀 상태.
    // 출력: 같으면 true.
    private static bool SameTeamState(in TeamState a, in TeamState b)
    {
        if (a.TeamId != b.TeamId || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            TeamMember x = a.Get(i), y = b.Get(i);
            if (x.EntityId != y.EntityId || x.State != y.State || x.Health != y.Health || x.Flags != y.Flags) return false;
        }
        return true;
    }

    // 기능: 지금 스테이션 상태(대기 마스크와 끝 Tick)를 만든다.
    // 입력: 없음.
    // 출력: RebootStationsState 값.
    private RebootStationsState StationsState()
    {
        var s = new RebootStationsState();
        for (int i = 0; i < RebootStations.Count; i++)
        {
            if (_stationEnd[i] <= ServerTick) continue;
            s.CooldownMask |= (byte)(1 << i);
            s.SetEndTick(i, _stationEnd[i]);
        }
        return s;
    }

    // 기능: Tick 끝에 스테이션 상태가 바뀌었거나(대기 시작·끝) 경기 시작·리셋이면 모두에게 RebootStations를 보낸다(D10).
    // 입력: 없음.
    // 출력: 반환값 없음.
    private void SendStationChanges()
    {
        RebootStationsState s = StationsState();
        bool same = s.CooldownMask == _sentStations.CooldownMask;
        for (int i = 0; i < RebootStations.Count && same; i++) same = s.GetEndTick(i) == _sentStations.GetEndTick(i);
        if (same && !_stationsDirty) return;
        _stationsDirty = false;
        _sentStations = s;
        foreach (var p in _players) SendStations(p.PeerId, s);
    }

    // 기능: 한 연결에 RebootStations 패킷을 보낸다.
    // 입력: peerId - 연결 id, s - 스테이션 상태.
    // 출력: 반환값 없음.
    private void SendStations(int peerId, in RebootStationsState s)
    {
        var writer = new PacketWriter(_sendBuffer);
        RebootStationsState.Write(ref writer, s);
        _send(peerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
    }

    // 기능: 입장·Resume한 사람에게 분대 상태를 보낸다(D2, D10, D13): 팀 상태, 스테이션, 진행 중인 팀 채널.
    // 입력: player - 받는 사람.
    // 출력: 반환값 없음.
    private void SendSquadStateTo(PlayerEntity player)
    {
        SendStations(player.PeerId, StationsState());
        SendTeamTo(player);
        if (player.TeamId == 0) return;
        foreach (var p in _players)
        {
            if (!p.ChannelActive || !SameTeam(p, player)) continue;
            var writer = new PacketWriter(_sendBuffer);
            ChannelState.Write(ref writer, ChannelOf(p, active: true));
            _send(player.PeerId, writer.WrittenSpan, DeliveryMethod.ReliableOrdered);
        }
    }

    // 기능: QA 관찰용으로 플레이어의 진행 중인 채널을 돌려준다.
    // 입력: p - 플레이어, channel - 결과.
    // 출력: 진행 중이면 true와 채널.
    // QA (Phase 14): what the QA observation shows of a player's channel.
    internal static bool TryGetChannel(PlayerEntity p, out ChannelState channel)
    {
        channel = p.ChannelActive ? ChannelOf(p, true) : default;
        return p.ChannelActive;
    }

    // 기능: QA downPlayer: 시나리오 준비용으로 플레이어를 바로 기절시킨다(같은 팀에 서 있는 구성원이 있어야 한다, Down과 같은 경로).
    // 입력: victim - 서 있는 플레이어.
    // 출력: 기절시켰으면 true, 살아 있지 않거나 이미 기절했거나 서 있는 팀원이 없으면 false.
    internal bool DownPlayer(PlayerEntity victim)
    {
        if (!victim.IsUp || !CanBeDowned(victim)) return false;
        Down(victim, null, DeathCause.Zone);
        return true;
    }

    // 기능: QA giveRebootCard: 같은 팀의 탈락한 참가자의 카드를 플레이어 카드 칸에 넣는다(월드에 있던 그 카드는 지운다).
    // 입력: holder - 받을 서 있는 플레이어, owner - 카드 주인.
    // 출력: 넣었으면 true, 조건이 맞지 않거나 칸이 가득 찼으면 false.
    internal bool GiveCard(PlayerEntity holder, PlayerEntity owner)
    {
        if (!holder.Alive || owner.Alive || !owner.Participant || !SameTeam(holder, owner) || holder.Inventory.CardCount >= _squad.MaxCardsHeld) return false;
        RemoveCardsOf(owner);
        return holder.Inventory.AddCard(owner.JoinOrder);
    }

    // 기능: QA setStationCooldown: 스테이션 대기를 바꾼다(0 = 바로 사용 가능).
    // 입력: station - 번호, seconds - 지금부터 대기 초.
    // 출력: 반환값 없음.
    internal void SetStationCooldown(int station, int seconds)
    {
        _stationEnd[station] = seconds <= 0 ? 0 : ServerTick + (uint)(seconds * _simHz);
    }
}
