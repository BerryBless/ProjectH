namespace ProjectH.Client.Game
{
    // Turns "trigger held" into a whole number of shots per frame at a fixed rate (D11: 10/s).
    // A long frame fires at most maxShotsPerFrame and drops the rest of the backlog, so a hitch never
    // bursts a pile of effects. Releasing the trigger does not bank shots: tapping cannot beat the rate.
    public sealed class FireRateAccumulator
    {
        private readonly float _interval;
        private readonly int _maxShotsPerFrame;
        private float _cooldown;   // seconds until the next shot; 0 means ready

        public FireRateAccumulator(float shotsPerSecond, int maxShotsPerFrame)
        {
            _interval = 1f / shotsPerSecond;
            _maxShotsPerFrame = maxShotsPerFrame;
        }

        public int Consume(float deltaTime, bool triggerHeld)
        {
            _cooldown -= deltaTime;
            int shots = 0;
            if (triggerHeld)
            {
                while (_cooldown <= 0f && shots < _maxShotsPerFrame)
                {
                    shots++;
                    _cooldown += _interval;
                }
            }
            if (_cooldown < 0f) _cooldown = 0f;
            return shots;
        }
    }
}
