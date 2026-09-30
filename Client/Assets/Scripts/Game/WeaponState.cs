using System;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Presentation copy of the server's weapon rules (Server WeaponRules), stepped once per predicted
    // input, so the local tracer and the ammo counter react at once (D12). The server still decides
    // every shot. One Step = one simulation tick, the unit of the catalog's tick values, so intervals and
    // reloads line up with the server as long as no input is lost. Keep the rules identical to WeaponRules.
    public sealed class WeaponState
    {
        public const int SlotCount = 2;           // Slot1 / Slot2 = catalog entries 0 / 1
        private const int HistorySize = 64;       // same as LocalPlayerPredictor: covers every unacked input

        // Everything needed to compare with the server and to replay the step (same idea as the movement
        // predictor's history): the input and the result it produced.
        private struct Record
        {
            public uint Seq;
            public long Step;
            public InputButtons Buttons;
            public int Slot;
            public int Ammo;
            public bool Reloading;
            public long NextFireStep;   // of Slot, after this step
        }

        private readonly WeaponInfo[] _weapons;
        private readonly int[] _ammo = new int[SlotCount];
        private readonly long[] _nextFireStep = new long[SlotCount];
        private readonly Record[] _history = new Record[HistorySize];
        private long _step;
        private long _reloadEndStep;
        private bool _fireHeld;

        public WeaponState(WeaponInfo[] catalog)
        {
            if (catalog == null || catalog.Length == 0) throw new ArgumentException("Empty weapon catalog.", nameof(catalog));
            int count = Math.Min(catalog.Length, SlotCount);
            _weapons = new WeaponInfo[count];
            Array.Copy(catalog, _weapons, count);
            Refill();
        }

        public int Slot { get; private set; }
        public bool Reloading { get; private set; }
        public WeaponInfo Current => _weapons[Slot];
        public int Ammo => _ammo[Slot];
        public int ReloadRemainingSteps => Reloading ? (int)Math.Max(0, _reloadEndStep - _step) : 0;

        // Join and respawn: full magazines, first slot (server WeaponRules.Equip).
        public void Refill()
        {
            Slot = 0;
            for (int i = 0; i < SlotCount; i++)
            {
                _ammo[i] = i < _weapons.Length ? _weapons[i].MagazineSize : 0;
                _nextFireStep[i] = 0;
            }
            Reloading = false;
            _fireHeld = false;
        }

        // One predicted input, oldest first. Returns true when the server is expected to fire it.
        public bool Step(uint seq, InputButtons buttons)
        {
            return Run(seq, buttons, _step++);
        }

        private bool Run(uint seq, InputButtons buttons, long now)
        {
            if (Reloading && now >= _reloadEndStep)
            {
                Reloading = false;
                _ammo[Slot] = Current.MagazineSize;
            }

            bool fired = Apply(buttons, now);
            _history[seq % HistorySize] = new Record
            {
                Seq = seq, Step = now, Buttons = buttons, Slot = Slot, Ammo = _ammo[Slot],
                Reloading = Reloading, NextFireStep = _nextFireStep[Slot],
            };
            return fired;
        }

        // The server's values after it processed input ackSeq (snapshot self block). If they equal what this
        // copy had after the same input, the newer local steps stand. Otherwise the server wins at that
        // point: the state restarts from its values and the inputs after ackSeq are replayed, rewriting their
        // history, so the next snapshot compares against consistent records and ReloadRemainingTicks is
        // measured from the ack point (D12).
        public void ApplyServer(in SnapshotSelf server, uint ackSeq)
        {
            if (ackSeq == 0) return;                               // no input processed yet: nothing to compare
            if (server.WeaponSlot >= _weapons.Length) return;      // untrusted value outside the loadout

            bool serverReloading = server.ReloadRemainingTicks > 0;
            Record local = _history[ackSeq % HistorySize];
            bool known = local.Seq == ackSeq;
            if (known && local.Slot == server.WeaponSlot && local.Ammo == server.Ammo && local.Reloading == serverReloading)
                return;

            Slot = server.WeaponSlot;
            _ammo[Slot] = Math.Min(server.Ammo, (int)Current.MagazineSize);
            Reloading = serverReloading;

            if (!known)
            {
                // The ack is older than the history: no replay possible, restart from the server's values.
                _reloadEndStep = _step + server.ReloadRemainingTicks;
                return;
            }

            long stepAfterAck = local.Step + 1;
            _reloadEndStep = stepAfterAck + server.ReloadRemainingTicks;
            if (local.Slot == Slot) _nextFireStep[Slot] = local.NextFireStep;
            _fireHeld = (local.Buttons & InputButtons.Fire) != 0;
            _history[ackSeq % HistorySize] = new Record
            {
                Seq = ackSeq, Step = local.Step, Buttons = local.Buttons, Slot = Slot, Ammo = _ammo[Slot],
                Reloading = Reloading, NextFireStep = _nextFireStep[Slot],
            };

            long end = _step;
            _step = stepAfterAck;
            for (uint seq = ackSeq + 1; _step < end; seq++)
            {
                Record r = _history[seq % HistorySize];
                if (r.Seq != seq) break;                           // cannot happen while the ack record is valid
                Run(seq, r.Buttons, _step++);
            }
            _step = end;
        }

        // Same order as the server: switch -> reload -> fire.
        private bool Apply(InputButtons buttons, long now)
        {
            bool slot1 = (buttons & InputButtons.Slot1) != 0;
            bool slot2 = (buttons & InputButtons.Slot2) != 0;
            if (slot1 != slot2)
            {
                int target = slot1 ? 0 : 1;
                if (target < _weapons.Length && target != Slot)
                {
                    Slot = target;
                    Reloading = false;
                }
            }

            WeaponInfo weapon = Current;
            if ((buttons & InputButtons.Reload) != 0 && !Reloading && _ammo[Slot] < weapon.MagazineSize)
                StartReload(weapon, now);

            bool fireHeld = (buttons & InputButtons.Fire) != 0;
            bool trigger = fireHeld && (weapon.Automatic || !_fireHeld);
            _fireHeld = fireHeld;
            if (!trigger || Reloading || now < _nextFireStep[Slot]) return false;

            if (_ammo[Slot] == 0)
            {
                StartReload(weapon, now);
                return false;
            }

            _ammo[Slot]--;
            _nextFireStep[Slot] = now + weapon.FireIntervalTicks;
            if (_ammo[Slot] == 0) StartReload(weapon, now);
            return true;
        }

        private void StartReload(WeaponInfo weapon, long now)
        {
            Reloading = true;
            _reloadEndStep = now + weapon.ReloadTicks;
        }
    }
}
