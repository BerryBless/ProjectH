using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;

namespace ProjectH.Client.Game
{
    // Phase 14 D14: the strings of the squad HUD (one row per team member, the card line, the bleed-out line), built only
    // when a shown value changes, so an unchanged HUD allocates nothing per frame. Pure (no UnityEngine): SquadHud puts a
    // string on screen when its Set* call returns true. Names are PlayerSpawned's strings, compared by reference.
    public sealed class SquadHudText
    {
        public const int MaxRows = SquadConstants.MaxTeamSize;

        private struct RowValues
        {
            public bool Built;
            public ushort EntityId;
            public string Name;
            public TeamMemberState State;
            public TeamMemberFlags Flags;
            public bool Self;
        }

        private readonly RowValues[] _rows = new RowValues[MaxRows];
        private readonly string[] _rowText = new string[MaxRows];
        private int _cards = -1;
        private int _bleedSeconds = int.MinValue;

        // 기능: 빈 문자열로 시작하는 문구 모음을 만든다.
        // 입력: 없음.
        // 출력: 모든 줄이 비어 있고 첫 Set이 반드시 만들게 된 객체.
        public SquadHudText()
        {
            for (int i = 0; i < MaxRows; i++) _rowText[i] = string.Empty;
        }

        // How many strings were built so far (tests check that unchanged values build nothing).
        public int Rebuilds { get; private set; }
        public string Cards { get; private set; } = string.Empty;
        public string Bleed { get; private set; } = string.Empty;

        // 기능: index번째 줄의 현재 문자열을 돌려준다.
        // 입력: index - 0..MaxRows-1.
        // 출력: 줄 문자열(아직 안 만들었으면 빈 문자열).
        public string Row(int index) => _rowText[index];

        // 기능: 한 구성원 줄을 값이 바뀌었을 때만 다시 만든다(체력은 막대라 문자열에 넣지 않는다).
        // 입력: index - 줄 번호, entityId - 구성원 id, name - 이름(null 가능), state - 상태, flags - 카드 위치, self - 나인지.
        // 출력: 문자열을 새로 만들었으면 true.
        public bool SetRow(int index, ushort entityId, string name, TeamMemberState state, TeamMemberFlags flags, bool self)
        {
            ref RowValues v = ref _rows[index];
            if (v.Built && v.EntityId == entityId && ReferenceEquals(v.Name, name) && v.State == state && v.Flags == flags && v.Self == self)
                return false;
            v.Built = true;
            v.EntityId = entityId;
            v.Name = name;
            v.State = state;
            v.Flags = flags;
            v.Self = self;
            _rowText[index] = UiText.SquadRow(name, entityId, UiText.MemberStatus(state, flags), self);
            Rebuilds++;
            return true;
        }

        // 기능: 소지 카드 줄을 값이 바뀌었을 때만 다시 만든다.
        // 입력: cards - 소지 카드 수.
        // 출력: 문자열을 새로 만들었으면 true.
        public bool SetCards(int cards)
        {
            if (cards == _cards) return false;
            _cards = cards;
            Cards = UiText.CardsLine(cards);
            Rebuilds++;
            return true;
        }

        // 기능: 기절 줄을 값이 바뀌었을 때만 다시 만든다.
        // 입력: seconds - 출혈 탈락까지 남은 초, 음수면 기절이 아니다(빈 문자열).
        // 출력: 문자열을 새로 만들었으면 true.
        public bool SetBleed(int seconds)
        {
            if (seconds < 0) seconds = -1;
            if (seconds == _bleedSeconds) return false;
            _bleedSeconds = seconds;
            Bleed = seconds < 0 ? string.Empty : UiText.Bleeding(seconds);
            Rebuilds++;
            return true;
        }
    }
}
