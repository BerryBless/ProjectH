using System;

namespace ProjectH.Client.Game
{
    // Maps local time to server ticks from snapshot arrival times. Jitter is smoothed (quick to
    // follow earlier-arriving samples, slow to follow late ones) and the render tick never moves
    // backwards, so remote players never visibly jump back in time.
    // Review fix D3 (SEC-26): a tick far ahead of the newest one is refused (TickRejects), so one corrupt or forged tick
    // cannot pin LatestTick and the render tick until the next join. Pure (no UnityEngine): the EditMode tests drive it.
    public sealed class ServerClock
    {
        // How far past the newest tick a snapshot may be, in seconds of server time, on top of the local time since that tick.
        public const double MaxAheadSeconds = 10.0;

        private readonly double _simHz;
        private double _offsetSeconds;   // server time - local time
        private double _lastRenderTick;
        private double _latestAt;        // local time LatestTick was taken

        // 기능: 서버 시계를 만든다.
        // 입력: simHz - 서버 시뮬레이션 Tick 속도(초당 Tick).
        // 출력: 아직 준비되지 않은(IsReady=false) ServerClock.
        public ServerClock(int simHz)
        {
            _simHz = simHz;
        }

        public bool IsReady { get; private set; }
        public uint LatestTick { get; private set; }
        // Review fix D3: snapshot ticks refused as too far ahead (F1 line). Starts at 0 with each clock (each join).
        public int TickRejects { get; private set; }

        // 기능: Snapshot(또는 입장 응답) 하나의 Tick으로 시계를 맞춘다. 리뷰 수정 D3: 준비된 뒤 LatestTick보다
        //   SimHz × (10초 + 마지막 표본 뒤 지난 로컬 시간)을 넘게 앞선 Tick은 버리고 TickRejects를 센다(지난 시간을 더해 멈췄던 Client도 따라온다).
        // 입력: serverTick - 서버 Tick, localTime - 받은 로컬 시각(초).
        // 출력: 받아들였으면 true, 버렸으면 false(아무것도 바뀌지 않는다. 호출자는 그 Snapshot을 버린다).
        public bool OnSnapshot(uint serverTick, double localTime)
        {
            if (IsReady)
            {
                double elapsed = Math.Max(0.0, localTime - _latestAt);
                double limit = LatestTick + _simHz * (MaxAheadSeconds + elapsed);
                if (serverTick > limit)
                {
                    TickRejects++;
                    return false;
                }
            }
            if (serverTick > LatestTick || !IsReady)
            {
                LatestTick = serverTick;
                _latestAt = localTime;
            }
            double sample = serverTick / _simHz - localTime;
            if (!IsReady)
            {
                _offsetSeconds = sample;
                IsReady = true;
                return true;
            }
            double rate = sample > _offsetSeconds ? 0.5 : 0.05;
            _offsetSeconds += (sample - _offsetSeconds) * rate;
            return true;
        }

        // 기능: 지금 로컬 시각에 원격 플레이어를 그릴 서버 Tick을 구한다. 뒤로는 가지 않는다(단조 증가).
        // 입력: localTime - 현재 로컬 시각(초), delaySeconds - 보간 지연(초).
        // 출력: 렌더 Tick(소수). 이전 값보다 작으면 이전 값을 돌려준다.
        public double RenderTick(double localTime, double delaySeconds)
        {
            double tick = (localTime + _offsetSeconds - delaySeconds) * _simHz;
            if (tick < _lastRenderTick) tick = _lastRenderTick;
            _lastRenderTick = tick;
            return tick;
        }

        // 기능: 렌더 Tick을 입력의 ViewTick(uint)으로 바꾼다(리뷰 수정 D2, STB-1: float는 2^24 Tick부터 정수를 잃는다).
        // 입력: renderTick - 이번 프레임 원격 플레이어를 그린 서버 Tick.
        // 출력: 소수점을 버린 Tick. NaN·음수·무한대·uint 범위 밖이면 uint.MaxValue("지금", 서버가 최신 Tick으로 자른다).
        public static uint ToViewTick(double renderTick)
        {
            if (!(renderTick >= 0.0) || renderTick >= uint.MaxValue) return uint.MaxValue;
            return (uint)Math.Floor(renderTick);
        }
    }
}
