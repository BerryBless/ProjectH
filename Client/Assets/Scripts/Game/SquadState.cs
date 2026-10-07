using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Game
{
    // Phase 14 D8: a revive or reboot in progress as the client keeps it. StartTick is the estimated server tick the start
    // arrived at (the server's channel length is its own setting, so the client measures from there to EndTick).
    public struct ChannelView
    {
        public ChannelKind Kind;
        public ushort ActorId;
        public ushort Target;      // the downed teammate (Revive) or the station index (Reboot)
        public uint EndTick;
        public double StartTick;
    }

    // Phase 14 D2, D8: our own team (the newest TeamState) and the channels of its members (ChannelState). Pure (no
    // UnityEngine): EditMode tests run it outside Unity. Fixed storage: the team is the packet struct (at most 4 members),
    // and the channels a fixed array of MaxChannels (one per actor; a team of 4 has at most 4 actors). Cleared with the
    // match state and when a new round's countdown starts (the server sends the new team at the match start).
    public sealed class SquadState
    {
        public const int MaxChannels = SquadConstants.MaxTeamSize;
        // A channel whose stop never came (should not happen: ReliableOrdered) is dropped this long after its end.
        public const double ChannelGraceSeconds = 2.0;

        private readonly ChannelView[] _channels = new ChannelView[MaxChannels];
        private TeamState _team;
        private bool _hasTeam;
        private int _channelCount;

        public bool HasTeam => _hasTeam;
        public byte TeamId => _hasTeam ? _team.TeamId : (byte)0;
        public int Count => _hasTeam ? _team.Count : 0;
        // Counts applied TeamStates (callers recolor views only when it changed).
        public int Version { get; private set; }
        public int ChannelCount => _channelCount;

        // 기능: index번째 구성원을 돌려준다(입장 순서).
        // 입력: index - 0..Count-1.
        // 출력: 그 구성원.
        public TeamMember Member(int index) => _team.Get(index);

        // 기능: index번째 진행 중 채널을 돌려준다.
        // 입력: index - 0..ChannelCount-1.
        // 출력: 그 채널.
        public ChannelView Channel(int index) => _channels[index];

        // 기능: 서버의 TeamState를 적용한다(검증은 TeamState.TryRead가 했다).
        // 입력: state - 받은 팀 상태.
        // 출력: 반환값 없음. 팀이 바뀌고 Version이 오른다.
        public void Apply(in TeamState state)
        {
            _team = state;
            _hasTeam = true;
            Version++;
        }

        // 기능: 이 id가 우리 팀 구성원인지 알려 준다.
        // 입력: entityId - Entity id.
        // 출력: 팀에 있으면 true(0이거나 팀이 없으면 false).
        public bool Contains(ushort entityId) => TryGet(entityId, out _);

        // 기능: id로 팀 구성원을 찾는다.
        // 입력: entityId - Entity id.
        // 출력: 있으면 true와 구성원, 없으면 false.
        public bool TryGet(ushort entityId, out TeamMember member)
        {
            member = default;
            if (!_hasTeam || entityId == 0) return false;
            for (int i = 0; i < _team.Count; i++)
            {
                TeamMember m = _team.Get(i);
                if (m.EntityId != entityId) continue;
                member = m;
                return true;
            }
            return false;
        }

        // 기능: 이 id가 경기 안에 있는(Up 또는 기절) 우리 팀 구성원인지 알려 준다(분대 관전, 표지).
        // 입력: entityId - Entity id.
        // 출력: Up 또는 Downed 구성원이면 true.
        public bool IsInPlay(ushort entityId) =>
            TryGet(entityId, out TeamMember m) && (m.State == TeamMemberState.Up || m.State == TeamMemberState.Downed);

        // 기능: ChannelState를 적용한다. 시작이면 행위자의 채널을 넣거나 바꾸고, 끝이면 지운다.
        // 입력: channel - 받은 상태, nowTick - 받은 순간의 추정 서버 Tick(진행 막대의 시작).
        // 출력: 반환값 없음. 칸이 모두 차 있으면(서버가 만들지 않는 경우) 새 시작은 버린다.
        public void ApplyChannel(in ChannelState channel, double nowTick)
        {
            int index = IndexOfActor(channel.ActorId);
            if (!channel.Active)
            {
                if (index >= 0) RemoveAt(index);
                return;
            }
            if (index < 0)
            {
                if (_channelCount == MaxChannels) return;
                index = _channelCount++;
            }
            _channels[index] = new ChannelView
            {
                Kind = channel.Kind,
                ActorId = channel.ActorId,
                Target = channel.Target,
                EndTick = channel.EndTick,
                StartTick = nowTick < channel.EndTick ? nowTick : channel.EndTick,
            };
        }

        // 기능: 끝 Tick이 한참 지난 채널을 지운다(끝 알림을 잃었을 때의 안전장치).
        // 입력: nowTick - 추정 서버 Tick, simHz - 서버 Tick 속도.
        // 출력: 반환값 없음.
        public void ExpireChannels(double nowTick, int simHz)
        {
            double grace = ChannelGraceSeconds * (simHz > 0 ? simHz : 30);
            for (int i = _channelCount - 1; i >= 0; i--)
            {
                if (nowTick > _channels[i].EndTick + grace) RemoveAt(i);
            }
        }

        // 기능: 이 플레이어가 행위자이거나 소생 대상인 채널을 찾는다.
        // 입력: entityId - Entity id(보통 나).
        // 출력: 있으면 true와 채널(행위자 쪽을 먼저 본다), 없으면 false.
        public bool TryGetChannelOf(ushort entityId, out ChannelView channel)
        {
            channel = default;
            if (entityId == 0) return false;
            for (int i = 0; i < _channelCount; i++)
            {
                if (_channels[i].ActorId != entityId) continue;
                channel = _channels[i];
                return true;
            }
            for (int i = 0; i < _channelCount; i++)
            {
                if (_channels[i].Kind != ChannelKind.Revive || _channels[i].Target != entityId) continue;
                channel = _channels[i];
                return true;
            }
            return false;
        }

        // 기능: 팀과 채널을 모두 비운다(끊김, 새 라운드 카운트다운).
        // 입력: 없음.
        // 출력: 반환값 없음. 팀이 있었으면 Version이 오른다.
        public void Clear()
        {
            if (_hasTeam) Version++;
            _hasTeam = false;
            _team = default;
            for (int i = 0; i < _channelCount; i++) _channels[i] = default;
            _channelCount = 0;
        }

        // 기능: 채널 진행률을 서버 Tick으로만 계산한다(Client 타이머를 쓰지 않는다, D8).
        // 입력: startTick - 시작을 받은 추정 Tick, endTick - 서버가 알려 준 끝 Tick, nowTick - 지금 추정 Tick.
        // 출력: 0..1 진행률. 끝이 시작보다 앞서면 1.
        public static float Progress(double startTick, uint endTick, double nowTick)
        {
            double length = endTick - startTick;
            if (length <= 0) return 1f;
            double t = (nowTick - startTick) / length;
            return t <= 0 ? 0f : t >= 1 ? 1f : (float)t;
        }

        // 기능: 행위자 id로 채널 칸을 찾는다.
        // 입력: actorId - 행위자 Entity id.
        // 출력: 칸 번호, 없으면 -1.
        private int IndexOfActor(ushort actorId)
        {
            for (int i = 0; i < _channelCount; i++)
            {
                if (_channels[i].ActorId == actorId) return i;
            }
            return -1;
        }

        // 기능: 채널 칸 하나를 지운다(마지막 칸을 그 자리로 옮긴다).
        // 입력: index - 지울 칸.
        // 출력: 반환값 없음.
        private void RemoveAt(int index)
        {
            _channelCount--;
            _channels[index] = _channels[_channelCount];
            _channels[_channelCount] = default;
        }
    }
}
