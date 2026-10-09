using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Phase 13 D5: the tool in hand as the local player predicts it: the server's rule (HarvestRules.SelectTool) run on
    // every input that acts, so Q, F and 1-3 switch at once (request §26, §112). The snapshot's self block carries the
    // server's tool for the acked input; a different value replaces the prediction there and the newer inputs are run
    // again. ToolTests.TheClientsCopy_SwitchesLikeTheServer keeps this rule the same as the server's. Pure, no UnityEngine,
    // no allocation after construction. Main thread only.
    public sealed class ToolState
    {
        public const int HistorySize = 64;

        private readonly ToolKind[] _after = new ToolKind[HistorySize];
        private readonly ToolKind[] _previousAfter = new ToolKind[HistorySize];
        private readonly InputButtons[] _buttons = new InputButtons[HistorySize];
        private readonly bool[] _acts = new bool[HistorySize];
        private uint _lastSeq;

        public ToolKind Current { get; private set; }
        public ToolKind Previous { get; private set; }

        // 기능: 서버 규칙(HarvestRules.SelectTool)대로 입력 하나가 바꾸는 도구를 고른다: 무기 슬롯 키가 먼저, 다음 F(채집), 다음 Q(건설.
        //   건설 중이면 그 전 도구로 돌아간다).
        // 입력: current - 지금 도구, previous - 건설 모드 전의 도구(건설로 들어갈 때 current로 바뀐다), buttons - 입력 버튼.
        // 출력: 입력 뒤의 도구. 도구 키가 없거나 같은 도구면 current 그대로.
        // The server's rule (HarvestRules.SelectTool): a weapon slot key wins, then F, then Q (from build mode: back to the
        // tool before it). Returns the tool after the input and updates previous.
        public static ToolKind Select(ToolKind current, ref ToolKind previous, InputButtons buttons)
        {
            ToolKind target;
            if ((buttons & (InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3)) != 0) target = ToolKind.Weapon;
            else if ((buttons & InputButtons.ToolHarvest) != 0) target = ToolKind.Harvest;
            else if ((buttons & InputButtons.ToolBuild) != 0) target = current == ToolKind.Build ? previous : ToolKind.Build;
            else return current;
            if (target == current) return current;
            if (target == ToolKind.Build) previous = current;
            return target;
        }

        // 기능: 입력 하나를 서버가 처리할 대로 예측에 적용하고 이력에 적는다. 입력이 행동할 수 있을 때(ActionsAllowedAt)만 도구가 바뀐다.
        // 입력: seq - 입력 순번, buttons - 입력 버튼, acts - 이 입력이 행동할 수 있는지.
        // 출력: 입력 뒤의 도구. Current·Previous와 seq 칸의 이력이 갱신되고 seq가 마지막 순번이 된다.
        // One input (seq) as the server will run it: it switches only when the input acts (ActionsAllowedAt).
        public ToolKind Step(uint seq, InputButtons buttons, bool acts)
        {
            if (acts)
            {
                ToolKind previous = Previous;
                Current = Select(Current, ref previous, buttons);
                Previous = previous;
            }
            int slot = (int)(seq % HistorySize);
            _after[slot] = Current;
            _previousAfter[slot] = Previous;
            _buttons[slot] = buttons;
            _acts[slot] = acts;
            _lastSeq = seq;
            return Current;
        }

        // 기능: 입력 seq 뒤에 예측이 들고 있던 도구를 낸다.
        // 입력: seq - 입력 순번(마지막 HistorySize개 안이어야 한다. 그 밖이면 같은 칸을 쓴 다른 입력의 값).
        // 출력: 그 입력 뒤의 도구.
        // The tool the prediction had after input seq (one of the last HistorySize).
        public ToolKind At(uint seq) => _after[(int)(seq % HistorySize)];

        // 기능: Snapshot이 알린 서버 도구를 Ack 입력 자리에 맞춘다. 예측과 같으면 그대로, 다르면 그 자리를 서버 값으로 바꾸고 그 뒤 입력을
        //   다시 돌린다(그 전 도구 기억은 서버 도구에서 다시 이어진다).
        // 입력: server - 서버의 도구, ackSeq - 서버가 마지막으로 처리한 입력 순번.
        // 출력: 반환값 없음. Current·Previous·이력이 갱신된다. Ack가 0이거나 이력 밖이면 Current가 서버 값과 다를 때만 Reset(server).
        // The snapshot's tool after the acked input. A match keeps the prediction; otherwise the server's value replaces it
        // there and the inputs after it are run again (their previous-tool memory follows from the server's tool).
        public void ApplyServer(ToolKind server, uint ackSeq)
        {
            if (ackSeq == 0 || ackSeq > _lastSeq || _lastSeq - ackSeq >= HistorySize)
            {
                if (Current != server) Reset(server);
                return;
            }
            int ackSlot = (int)(ackSeq % HistorySize);
            if (_after[ackSlot] == server) return;
            ToolKind current = server;
            ToolKind previous = _previousAfter[ackSlot] == server ? ToolKind.Weapon : _previousAfter[ackSlot];
            _after[ackSlot] = server;
            _previousAfter[ackSlot] = previous;
            for (uint seq = ackSeq + 1; seq <= _lastSeq; seq++)
            {
                int slot = (int)(seq % HistorySize);
                if (_acts[slot]) current = Select(current, ref previous, _buttons[slot]);
                _after[slot] = current;
                _previousAfter[slot] = previous;
            }
            Current = current;
            Previous = previous;
        }

        // 기능: 도구 예측을 처음 상태로 되돌린다(부활·참가·재개: 서버 인벤토리가 새로 시작한다). 이력 칸은 지우지 않는다.
        // 입력: tool - 들고 시작할 도구(기본 무기).
        // 출력: 반환값 없음. Current가 tool, Previous가 Weapon이 된다.
        // A respawn, a join or a resume: the weapons are out (the server's inventory starts over).
        public void Reset(ToolKind tool = ToolKind.Weapon)
        {
            Current = tool;
            Previous = ToolKind.Weapon;
        }
    }
}
