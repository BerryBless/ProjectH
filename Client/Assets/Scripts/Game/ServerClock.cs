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

        public ServerClock(int simHz)
        {
            _simHz = simHz;
        }

        public bool IsReady { get; private set; }
        public uint LatestTick { get; private set; }

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

        public double RenderTick(double localTime, double delaySeconds)
        {
            double tick = (localTime + _offsetSeconds - delaySeconds) * _simHz;
            if (tick < _lastRenderTick) tick = _lastRenderTick;
            _lastRenderTick = tick;
            return tick;
        }
    }
}
