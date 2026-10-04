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

        // 기능: Server HarvestRules.SelectTool과 같은 규칙으로 입력 후 도구를 정한다.
        // 입력: current - 현재 도구, previous - 건설 모드 전 도구(ref, 건설로 바뀌면 current로 갱신), buttons - 입력 버튼.
        // 출력: 입력 후 도구.
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

        // 기능: 입력 하나를 예측 실행하고 이력에 기록한다.
        // 입력: seq - 입력 번호, buttons - 입력 버튼, acts - 이 입력이 행동 가능한지(ActionsAllowedAt).
        // 출력: 입력 후 예측 도구. Current·Previous와 이력이 갱신된다.
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

        // 기능: 입력 seq 직후의 예측 도구를 이력에서 읽는다.
        // 입력: seq - 입력 번호(최근 HistorySize 안).
        // 출력: 그 입력 후 예측 도구. 범위 밖 seq는 같은 슬롯을 쓴 다른 입력의 값이다.
        // The tool the prediction had after input seq (one of the last HistorySize).
        public ToolKind At(uint seq) => _after[(int)(seq % HistorySize)];

        // 기능: Snapshot의 ack 입력 후 Server 도구로 예측을 보정한다.
        // 입력: server - ack 입력 후 Server 도구, ackSeq - Server가 처리한 마지막 입력 번호.
        // 출력: 반환값 없음. 다르면 ack 이후 입력을 다시 실행해 Current·Previous가 갱신되고, ack가 이력 밖이면 Server 도구로 Reset된다.
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

        // 기능: 리스폰·참가·재개 때 도구 상태를 초기화한다.
        // 입력: tool - 들 도구(기본 Weapon).
        // 출력: 반환값 없음. Current가 tool, Previous가 Weapon이 된다.
        // A respawn, a join or a resume: the weapons are out (the server's inventory starts over).
        public void Reset(ToolKind tool = ToolKind.Weapon)
        {
            Current = tool;
            Previous = ToolKind.Weapon;
        }
    }
}
