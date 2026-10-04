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

        // 기능: 무기 카탈로그를 복사해 빈손 상태의 무기 예측기를 만든다.
        // 입력: catalog - 서버 WeaponCatalog의 모든 무기(null이거나 비면 ArgumentException).
        // 출력: 모든 슬롯이 비고 예비 탄약이 0인 WeaponState.
        // catalog: every weapon of the server's WeaponCatalog (weapon items and slots refer to them by id).
        public WeaponState(WeaponInfo[] catalog)
        {
            if (catalog == null || catalog.Length == 0) throw new ArgumentException("Empty weapon catalog.", nameof(catalog));
            _catalog = (WeaponInfo[])catalog.Clone();
            Clear();
        }

        public int Slot { get; private set; }
        public bool Reloading { get; private set; }
        // 기능: 현재 슬롯에 무기가 있는지 확인한다.
        // 입력: 없음.
        // 출력: 무기가 있으면 true, 빈 슬롯이면 false.
        public bool HasWeapon => _slots[Slot].Catalog >= 0;
        // 기능: 현재 슬롯의 무기 정보를 돌려준다.
        // 입력: 없음.
        // 출력: 현재 무기 정보. 빈 슬롯이면 카탈로그 0번(의미 없음).
        // Only meaningful while HasWeapon.
        public WeaponInfo Current => _catalog[Math.Max(0, _slots[Slot].Catalog)];
        // 기능: 현재 무기의 탄창 탄약 수를 돌려준다.
        // 입력: 없음.
        // 출력: 탄창 탄약 수, 무기가 없으면 0.
        public int Ammo => HasWeapon ? _slots[Slot].Ammo : 0;
        // 기능: 현재 무기 탄약 종류의 예비 탄약 수를 돌려준다.
        // 입력: 없음.
        // 출력: 예비 탄약 수, 무기가 없으면 0.
        public int Reserve => HasWeapon ? GetReserve(Current.AmmoType) : 0;
        // 기능: 재장전이 끝날 때까지 남은 Step 수를 돌려준다.
        // 입력: 없음.
        // 출력: 재장전 중이면 남은 Step(시뮬레이션 Tick) 수, 아니면 0.
        public int ReloadRemainingSteps => Reloading ? (int)Math.Max(0, _reloadEndStep - _step) : 0;

        // 기능: 탄약 종류별 예비 탄약 수를 돌려준다.
        // 입력: type - 탄약 종류.
        // 출력: 예비 탄약 수, None이면 0.
        public int GetReserve(AmmoType type) => type == AmmoType.None ? 0 : _reserve[(int)type - 1];

        // 기능: HUD 표시용으로 슬롯 내용을 읽는다.
        // 입력: slot - 슬롯 인덱스(0..SlotCount-1).
        // 출력: 무기가 있으면 true와 무기·희귀도·탄창 탄약, 빈 슬롯이면 false.
        // For the HUD: false for an empty slot.
        public bool TryGetSlot(int slot, out WeaponInfo weapon, out int rarity, out int ammo)
        {
            Held held = _slots[slot];
            weapon = held.Catalog >= 0 ? _catalog[held.Catalog] : default;
            rarity = held.Rarity;
            ammo = held.Ammo;
            return held.Catalog >= 0;
        }

        // 기능: 빈손 상태로 되돌린다.
        // 입력: 없음.
        // 출력: 반환값 없음. 슬롯 0 선택, 모든 슬롯 비움, 예비 탄약 0, 재장전과 발사 버튼 유지 상태가 해제된다.
        // Join, death and respawn: empty-handed (D1) until the server's InventoryState says otherwise.
        public void Clear()
        {
            Slot = 0;
            for (int i = 0; i < SlotCount; i++) _slots[i] = new Held { Catalog = -1 };
            SetReserves(0, 0, 0);
            Reloading = false;
            _fireHeld = false;
        }

        // 기능: 서버 InventoryState의 슬롯 내용과 예비 탄약을 적용한다.
        // 입력: server - 서버가 보낸 인벤토리 상태.
        // 출력: 반환값 없음. 무기가 바뀐 슬롯과 현재가 아닌 슬롯의 탄창, 예비 탄약(입력 기록 포함)이 갱신되고, 현재 슬롯 무기가 바뀌면 재장전이 취소된다.
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

        // 기능: 예비 탄약을 새 권위 값으로 바꾸고 입력 기록의 예비 탄약도 같은 차이만큼 옮긴다.
        // 입력: light - 새 Light 예비 탄약, medium - 새 Medium 예비 탄약, heavy - 새 Heavy 예비 탄약.
        // 출력: 반환값 없음. 차이가 있으면 _reserve와 기록 64개가 갱신된다.
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

        // 기능: 예측 입력 하나를 시뮬레이션 1 Tick으로 적용한다(오래된 입력부터).
        // 입력: seq - 입력 순번, buttons - 입력 버튼, actionsAllowed - 현재 이동 모드에서 행동 가능 여부(false면 발사 버튼 유지 상태만 갱신).
        // 출력: 서버도 이 입력에서 발사할 것으로 예측되면 true, 아니면 false.
        // One predicted input, oldest first. Returns true when the server is expected to fire it.
        // actionsAllowed false (Phase 12 D12: riding, falling, gliding, vaulting): the input acts on nothing, but the fire
        // button's held state still follows it, like the server's FireHeld, so landing with Fire held does not fire a
        // semi-automatic weapon without a new press.
        public bool Step(uint seq, InputButtons buttons, bool actionsAllowed = true)
        {
            return Run(seq, buttons, !actionsAllowed, _step++);
        }

        // 기능: 한 Step을 실행하고 결과를 입력 기록에 남긴다(재장전 완료 처리 → 행동 적용 → 기록).
        // 입력: seq - 입력 순번, buttons - 입력 버튼, gated - 행동 금지 Step 여부, now - 이 Step의 번호.
        // 출력: 발사했으면 true, 아니면 false.
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

        // 기능: Snapshot self 블록의 서버 무기 상태를 같은 입력 시점의 예측과 비교해, 다르면 서버 값에서 다시 시작하고 이후 입력을 재실행한다.
        // 입력: server - Snapshot self 블록, ackSeq - 서버가 처리한 마지막 입력 순번.
        // 출력: 반환값 없음. 불일치하면 슬롯·탄약·재장전·예비 탄약과 ackSeq 이후 기록이 서버 기준으로 다시 계산된다. ackSeq가 0이거나 슬롯이 범위 밖이면 무시한다.
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

        // 기능: 서버와 같은 순서(무기 전환 → 재장전 → 발사)로 입력을 적용한다.
        // 입력: buttons - 입력 버튼, now - 현재 Step 번호.
        // 출력: 발사했으면 true, 아니면 false. 슬롯·탄약·재장전·발사 쿨다운 상태가 바뀔 수 있다.
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

        // 기능: 예비 탄약이 있으면 재장전을 시작한다.
        // 입력: weapon - 현재 무기, now - 현재 Step 번호.
        // 출력: 반환값 없음. 예비 탄약이 있으면 Reloading이 true가 되고 재장전 종료 Step이 정해진다.
        private void TryStartReload(WeaponInfo weapon, long now)
        {
            if (GetReserve(weapon.AmmoType) == 0) return;
            Reloading = true;
            _reloadEndStep = now + weapon.ReloadTicks;
        }

        // 기능: 재장전 완료 시 예비 탄약에서 탄창으로 탄을 옮긴다.
        // 입력: 없음.
        // 출력: 반환값 없음. 현재 슬롯 탄약이 늘고 같은 종류 예비 탄약이 준다.
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

        // 기능: 무기 ID의 카탈로그 인덱스를 찾는다.
        // 입력: weaponId - 찾을 무기 ID.
        // 출력: 카탈로그 인덱스, 없으면 -1.
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
