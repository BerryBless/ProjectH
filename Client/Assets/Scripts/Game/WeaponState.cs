using System;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.Client.Game
{
    // Presentation copy of the server's weapon rules (Server WeaponRules), stepped once per predicted input,
    // so the local tracer and the ammo counter react at once (Phase 3 D12). The server still decides every
    // shot. One Step = one simulation tick, the unit of the catalog's tick values.
    // Phase 4: three inventory slots, any of them empty, and reloads that draw on the ammo reserve. What sits
    // in each slot and the reserves come from the server's InventoryState (ApplyInventory); the current slot
    // and its magazine come from the snapshot self block (ApplyServer). Keep the rules identical to WeaponRules.
    public sealed class WeaponState
    {
        public const int SlotCount = ItemConstants.WeaponSlotCount;
        private const int HistorySize = 64;       // same as LocalPlayerPredictor: covers every unacked input

        private struct Held
        {
            public int Catalog;         // index into _catalog, -1 = empty slot
            public byte Rarity;
            public int Ammo;
            public long NextFireStep;
        }

        // Everything needed to compare with the server and to replay the step (same idea as the movement
        // predictor's history): the input and the result it produced.
        private struct Record
        {
            public uint Seq;
            public long Step;
            public InputButtons Buttons;
            public bool Gated;          // Phase 12 D12: no action allowed in this step's mode
            public int Slot;
            public int Ammo;
            public bool Reloading;
            public long NextFireStep;   // of Slot, after this step
            public int Light;           // reserves after this step, restored before a replay
            public int Medium;
            public int Heavy;
        }

        private readonly WeaponInfo[] _catalog;
        private readonly Held[] _slots = new Held[SlotCount];
        private readonly int[] _reserve = new int[ItemConstants.AmmoTypeCount];   // index = AmmoType - 1
        private readonly Record[] _history = new Record[HistorySize];
        private long _step;
        private long _reloadEndStep;
        private bool _fireHeld;

        // catalog: every weapon of the server's WeaponCatalog (weapon items and slots refer to them by id).
        public WeaponState(WeaponInfo[] catalog)
        {
            if (catalog == null || catalog.Length == 0) throw new ArgumentException("Empty weapon catalog.", nameof(catalog));
            _catalog = (WeaponInfo[])catalog.Clone();
            Clear();
        }

        public int Slot { get; private set; }
        public bool Reloading { get; private set; }
        public bool HasWeapon => _slots[Slot].Catalog >= 0;
        // Only meaningful while HasWeapon.
        public WeaponInfo Current => _catalog[Math.Max(0, _slots[Slot].Catalog)];
        public int Ammo => HasWeapon ? _slots[Slot].Ammo : 0;
        public int Reserve => HasWeapon ? GetReserve(Current.AmmoType) : 0;
        public int ReloadRemainingSteps => Reloading ? (int)Math.Max(0, _reloadEndStep - _step) : 0;

        public int GetReserve(AmmoType type) => type == AmmoType.None ? 0 : _reserve[(int)type - 1];

        // For the HUD: false for an empty slot.
        public bool TryGetSlot(int slot, out WeaponInfo weapon, out int rarity, out int ammo)
        {
            Held held = _slots[slot];
            weapon = held.Catalog >= 0 ? _catalog[held.Catalog] : default;
            rarity = held.Rarity;
            ammo = held.Ammo;
            return held.Catalog >= 0;
        }

        // Join, death and respawn: empty-handed (D1) until the server's InventoryState says otherwise.
        public void Clear()
        {
            Slot = 0;
            for (int i = 0; i < SlotCount; i++) _slots[i] = new Held { Catalog = -1 };
            SetReserves(0, 0, 0);
            Reloading = false;
            _fireHeld = false;
        }

        // The server's inventory (Reliable, only when it changed). Slot contents and reserves are taken as they
        // are. A slot's magazine is taken when the weapon in it changed or the slot is not the current one;
        // the current slot's magazine belongs to the snapshot, whose ack-based check would otherwise never
        // correct a value set here (the two packets arrive in any order). The current slot index also comes
        // from the snapshot.
        public void ApplyInventory(in InventoryState server)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                InventorySlotState s = server.GetSlot(i);
                int catalog = s.IsEmpty ? -1 : IndexOf(s.WeaponId);
                bool same = catalog == _slots[i].Catalog && (catalog < 0 || s.Rarity == _slots[i].Rarity);
                if (!same)
                {
                    _slots[i] = new Held { Catalog = catalog, Rarity = s.Rarity, Ammo = catalog < 0 ? 0 : s.MagAmmo };
                    if (i == Slot) Reloading = false;
                }
                else if (i != Slot && catalog >= 0)
                {
                    _slots[i].Ammo = s.MagAmmo;
                }
            }
            SetReserves(server.LightAmmo, server.MediumAmmo, server.HeavyAmmo);
        }

        // New authoritative reserves (InventoryState, or 0 on Clear). The history records the reserves after each
        // step for the replay in ApplyServer; left alone, a later mismatch would restore the values from before
        // this change and undo it (e.g. an ammo pickup). So every record moves by the same per-type difference
        // between the new value and the current prediction: the replay then starts from the new authority, and
        // reload rounds predicted after the ack stay counted once (the difference already includes them).
        // Records of other seqs are shifted too; they are never read unless their seq matches. 64 x 3 adds, no
        // allocation, only when the inventory changes.
        private void SetReserves(int light, int medium, int heavy)
        {
            int dLight = light - _reserve[0];
            int dMedium = medium - _reserve[1];
            int dHeavy = heavy - _reserve[2];
            if (dLight == 0 && dMedium == 0 && dHeavy == 0) return;
            for (int i = 0; i < HistorySize; i++)
            {
                _history[i].Light += dLight;
                _history[i].Medium += dMedium;
                _history[i].Heavy += dHeavy;
            }
            _reserve[0] = light;
            _reserve[1] = medium;
            _reserve[2] = heavy;
        }

        // One predicted input, oldest first. Returns true when the server is expected to fire it.
        // actionsAllowed false (Phase 12 D12: riding, falling, gliding, vaulting): the input acts on nothing, but the fire
        // button's held state still follows it, like the server's FireHeld, so landing with Fire held does not fire a
        // semi-automatic weapon without a new press.
        public bool Step(uint seq, InputButtons buttons, bool actionsAllowed = true)
        {
            return Run(seq, buttons, !actionsAllowed, _step++);
        }

        private bool Run(uint seq, InputButtons buttons, bool gated, long now)
        {
            if (Reloading && now >= _reloadEndStep)
            {
                Reloading = false;
                FinishReload();
            }

            bool fired = false;
            if (gated) _fireHeld = (buttons & InputButtons.Fire) != 0;
            else fired = Apply(buttons, now);
            _history[seq % HistorySize] = new Record
            {
                Seq = seq, Step = now, Buttons = buttons, Gated = gated, Slot = Slot, Ammo = Ammo, Reloading = Reloading,
                NextFireStep = _slots[Slot].NextFireStep, Light = _reserve[0], Medium = _reserve[1], Heavy = _reserve[2],
            };
            return fired;
        }

        // The server's values after it processed input ackSeq (snapshot self block). If they equal what this
        // copy had after the same input, the newer local steps stand. Otherwise the server wins at that
        // point: the state restarts from its values and the inputs after ackSeq are replayed, rewriting their
        // history, so the next snapshot compares against consistent records and ReloadRemainingTicks is
        // measured from the ack point (Phase 3 D12).
        public void ApplyServer(in SnapshotSelf server, uint ackSeq)
        {
            if (ackSeq == 0) return;                                  // no input processed yet: nothing to compare
            if (server.WeaponSlot >= SlotCount) return;              // untrusted value outside the inventory

            bool serverReloading = server.ReloadRemainingTicks > 0;
            Record local = _history[ackSeq % HistorySize];
            bool known = local.Seq == ackSeq;
            if (known && local.Slot == server.WeaponSlot && local.Ammo == server.Ammo && local.Reloading == serverReloading)
                return;

            Slot = server.WeaponSlot;
            if (HasWeapon) _slots[Slot].Ammo = Math.Min(server.Ammo, (int)Current.MagazineSize);
            Reloading = serverReloading && HasWeapon;

            if (!known)
            {
                // The ack is older than the history: no replay possible, restart from the server's values.
                _reloadEndStep = _step + server.ReloadRemainingTicks;
                return;
            }

            long stepAfterAck = local.Step + 1;
            _reloadEndStep = stepAfterAck + server.ReloadRemainingTicks;
            if (local.Slot == Slot) _slots[Slot].NextFireStep = local.NextFireStep;
            _reserve[0] = local.Light;
            _reserve[1] = local.Medium;
            _reserve[2] = local.Heavy;
            _fireHeld = (local.Buttons & InputButtons.Fire) != 0;
            _history[ackSeq % HistorySize] = new Record
            {
                Seq = ackSeq, Step = local.Step, Buttons = local.Buttons, Gated = local.Gated, Slot = Slot, Ammo = Ammo, Reloading = Reloading,
                NextFireStep = _slots[Slot].NextFireStep, Light = local.Light, Medium = local.Medium, Heavy = local.Heavy,
            };

            long end = _step;
            _step = stepAfterAck;
            for (uint seq = ackSeq + 1; _step < end; seq++)
            {
                Record r = _history[seq % HistorySize];
                if (r.Seq != seq) break;                               // cannot happen while the ack record is valid
                Run(seq, r.Buttons, r.Gated, _step++);
            }
            _step = end;
        }

        // Same order as the server: switch -> reload -> fire.
        private bool Apply(InputButtons buttons, long now)
        {
            int target = -1;
            switch (buttons & (InputButtons.Slot1 | InputButtons.Slot2 | InputButtons.Slot3))
            {
                case InputButtons.Slot1: target = 0; break;
                case InputButtons.Slot2: target = 1; break;
                case InputButtons.Slot3: target = 2; break;
            }
            if (target >= 0 && target != Slot)
            {
                Slot = target;
                Reloading = false;
            }

            bool fireHeld = (buttons & InputButtons.Fire) != 0;
            if (!HasWeapon)
            {
                _fireHeld = fireHeld;
                return false;
            }

            WeaponInfo weapon = Current;
            ref Held held = ref _slots[Slot];
            if ((buttons & InputButtons.Reload) != 0 && !Reloading && held.Ammo < weapon.MagazineSize)
                TryStartReload(weapon, now);

            bool trigger = fireHeld && (weapon.Automatic || !_fireHeld);
            _fireHeld = fireHeld;
            if (!trigger || Reloading || now < held.NextFireStep) return false;

            if (held.Ammo == 0)
            {
                TryStartReload(weapon, now);
                return false;
            }

            held.Ammo--;
            held.NextFireStep = now + weapon.FireIntervalTicks;
            if (held.Ammo == 0) TryStartReload(weapon, now);
            return true;
        }

        private void TryStartReload(WeaponInfo weapon, long now)
        {
            if (GetReserve(weapon.AmmoType) == 0) return;
            Reloading = true;
            _reloadEndStep = now + weapon.ReloadTicks;
        }

        private void FinishReload()
        {
            if (!HasWeapon) return;
            WeaponInfo weapon = Current;
            int index = (int)weapon.AmmoType - 1;
            if (index < 0) return;
            int take = Math.Min(weapon.MagazineSize - _slots[Slot].Ammo, _reserve[index]);
            if (take <= 0) return;
            _slots[Slot].Ammo += take;
            _reserve[index] -= take;
        }

        private int IndexOf(byte weaponId)
        {
            for (int i = 0; i < _catalog.Length; i++)
            {
                if (_catalog[i].WeaponId == weaponId) return i;
            }
            return -1;
        }
    }
}
