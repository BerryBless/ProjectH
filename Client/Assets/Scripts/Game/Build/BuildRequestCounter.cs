namespace ProjectH.Client.Game
{
    // Phase 13.5 D4: the one per-connection numbering and send cap that placements (BuildController) and edits
    // (BuildEditController) share. The server keeps one duplicate check (LastBuildSequence), one queue and one
    // "more than MaxRequestsPerSecond is an invalid packet" count for both kinds, so two separate counters would break its
    // duplicate check and two separate caps could together go over its limit. Pure, no UnityEngine, no allocation after
    // construction. Main thread only.
    public sealed class BuildRequestCounter
    {
        // The server's building.maxRequestsPerSecond default (BuildingCatalog.MaxRequestsPerSecond): never send more in a second.
        public const int MaxRequestsPerSecond = 20;

        private readonly float[] _sendTimes = new float[MaxRequestsPerSecond];
        private int _sendTotal;
        private ushort _sequence;

        // The newest sequence handed out (0 = none since the last Reset).
        public ushort Last => _sequence;

        // 기능: 지금 요청 하나를 더 보내도 최근 1초 안의 전송 수가 상한을 넘지 않는지 본다.
        // 입력: now - 현재 시각(초).
        // 출력: 보낼 수 있으면 true.
        public bool Allows(float now) =>
            _sendTotal < MaxRequestsPerSecond || now - _sendTimes[_sendTotal % MaxRequestsPerSecond] >= 1f;

        // 기능: 보낼 요청의 순번을 하나 받고 전송 시각을 기록한다(u16, 넘치면 0을 건너뛰지 않고 감싼다: 서버와 같은 규칙).
        // 입력: now - 현재 시각(초).
        // 출력: 새 순번.
        public ushort Next(float now)
        {
            _sendTimes[_sendTotal % MaxRequestsPerSecond] = now;
            _sendTotal++;
            return ++_sequence;
        }

        // 기능: 새 연결에서 순번을 1부터 다시 시작하고 전송 기록을 비운다(서버 순번도 참가·재개마다 새로 시작한다).
        // 입력: 없음.
        // 출력: 반환값 없음. 순번과 전송 기록이 처음 상태가 된다.
        public void Reset()
        {
            _sequence = 0;
            _sendTotal = 0;
        }
    }
}
