namespace ProjectH.Client.Game
{
    // Maps local time to server ticks from snapshot arrival times. Jitter is smoothed (quick to
    // follow earlier-arriving samples, slow to follow late ones) and the render tick never moves
    // backwards, so remote players never visibly jump back in time.
    public sealed class ServerClock
    {
        private readonly double _simHz;
        private double _offsetSeconds;   // server time - local time
        private double _lastRenderTick;

        // 기능: 서버 시뮬레이션 주기로 시계를 만든다.
        // 입력: simHz - 서버 초당 Tick 수.
        // 출력: Snapshot을 받기 전이라 IsReady가 false인 ServerClock.
        public ServerClock(int simHz)
        {
            _simHz = simHz;
        }

        public bool IsReady { get; private set; }
        public uint LatestTick { get; private set; }

        // 기능: Snapshot 도착 시각으로 서버 시간과 로컬 시간의 차이를 갱신한다.
        // 입력: serverTick - Snapshot의 서버 Tick, localTime - 도착한 로컬 시간(초).
        // 출력: 반환값 없음. LatestTick과 시간 차이가 갱신되고 IsReady가 true가 된다(첫 샘플은 그대로, 이후 일찍 온 샘플은 0.5, 늦은 샘플은 0.05 비율로 따라감).
        public void OnSnapshot(uint serverTick, double localTime)
        {
            if (serverTick > LatestTick) LatestTick = serverTick;
            double sample = serverTick / _simHz - localTime;
            if (!IsReady)
            {
                _offsetSeconds = sample;
                IsReady = true;
                return;
            }
            double rate = sample > _offsetSeconds ? 0.5 : 0.05;
            _offsetSeconds += (sample - _offsetSeconds) * rate;
        }

        // 기능: 로컬 시간에서 화면에 그릴 서버 Tick을 계산한다.
        // 입력: localTime - 현재 로컬 시간(초), delaySeconds - 보간을 위해 뒤로 미룰 시간(초).
        // 출력: 소수 서버 Tick. 이전 반환값보다 작아지지 않는다.
        public double RenderTick(double localTime, double delaySeconds)
        {
            double tick = (localTime + _offsetSeconds - delaySeconds) * _simHz;
            if (tick < _lastRenderTick) tick = _lastRenderTick;
            _lastRenderTick = tick;
            return tick;
        }
    }
}
