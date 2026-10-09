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
            public bool LaunchBlocked;  // Phase 17: a projectile weapon could not launch in this step (match state)
            public int Slot;
            public int Ammo;
            public bool Reloading;
            public long NextFireStep;   // of Slot, after this step
            public long SwitchReadyStep;   // review fix C2: the equip wait after this step
            public int Light;           // reserves after this step (one per AmmoType), restored before a replay
            public int Medium;
            public int Heavy;
            public int Shells;          // Phase 17 D13
            public int Rockets;
        }

        private readonly WeaponInfo[] _catalog;
        private readonly Held[] _slots = new Held[SlotCount];
        private readonly int[] _reserve = new int[ItemConstants.AmmoTypeCount];   // index = AmmoType - 1
        private readonly Record[] _history = new Record[HistorySize];
        private long _step;
        private long _reloadEndStep;
        private bool _fireHeld;
        // Review fix C2 (server WeaponRules.Equip, PlayerEntity.SwitchReadyTick): no shot before this step after the weapon in
        // hand changed by a predicted slot switch. Recorded per step, so a replay from the ack restores it.
        private long _switchReadyStep;
        // Review fix C2: the same wait for a change the client learns from InventoryState (a pickup or swap into the hand). Not
        // part of the replay (InventoryState is not tied to an input), so it is kept apart and only ever delays a shot.
        private long _inventoryReadyStep;
        // True from Clear until the first InventoryState: that one is the join or respawn inventory, which the server fills
        // before it resets the wait (WeaponRules.ResetState), so it starts no wait.
        private bool _inventoryFresh;

        // 기능: 서버 무기 카탈로그의 복사본으로 예측 무기 상태를 만든다.
        // 입력: catalog - 서버 WeaponCatalog의 모든 무기(아이템·슬롯이 id로 가리킨다). null이거나 비어 있으면 ArgumentException.
        // 출력: 빈손(Clear 상태)의 WeaponState.
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

        // 기능: 탄 종류의 예측 예비탄을 읽는다(Phase 17 D13: 탄 종류 5개, 색인 = 종류 - 1).
        // 입력: type - 탄 종류.
        // 출력: 예비탄 수. None이나 범위 밖 값이면 0.
        public int GetReserve(AmmoType type) => type == AmmoType.None || (int)type > _reserve.Length ? 0 : _reserve[(int)type - 1];

        // 기능: 한 슬롯의 무기·희귀도·탄창을 읽는다(HUD).
        // 입력: slot - 슬롯 번호(0..SlotCount-1), weapon - 그 슬롯의 무기(비어 있으면 default), rarity - 희귀도, ammo - 탄창의 탄.
        // 출력: 슬롯에 무기가 있으면 true, 비어 있으면 false.
        public bool TryGetSlot(int slot, out WeaponInfo weapon, out int rarity, out int ammo)
        {
            Held held = _slots[slot];
            weapon = held.Catalog >= 0 ? _catalog[held.Catalog] : default;
            rarity = held.Rarity;
            ammo = held.Ammo;
            return held.Catalog >= 0;
        }

        // Join, death and respawn: empty-handed (D1) until the server's InventoryState says otherwise.
        // 기능: 빈손 상태로 되돌린다(슬롯 비움, 예비탄 5종 0, 재장전·발사 버튼 상태 해제, 리뷰 수정 C2: 교체 대기 0, 다음 인벤토리는 대기 없이 받음).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Clear()
        {
            Slot = 0;
            for (int i = 0; i < SlotCount; i++) _slots[i] = new Held { Catalog = -1 };
            SetReserves(0, 0, 0, 0, 0);
            Reloading = false;
            _fireHeld = false;
            _switchReadyStep = 0;
            _inventoryReadyStep = 0;
            _inventoryFresh = true;
        }

        // Review fix C2: steps until a predicted shot is allowed after the last change of the weapon in hand (0 = none). For
        // the HUD and tests.
        public int SwitchRemainingSteps => (int)Math.Max(0, Math.Max(_switchReadyStep, _inventoryReadyStep) - _step);

        // 기능: 지금 손에 든 무기의 교체 대기 Tick(서버 WeaponRules.Equip과 같다).
        // 입력: 없음.
        // 출력: 무기가 있으면 카탈로그의 EquipTicks, 빈손이면 0.
        private int EquipTicksInHand() => HasWeapon ? Current.EquipTicks : 0;

        // The server's inventory (Reliable, only when it changed). Slot contents and reserves are taken as they
        // are. A slot's magazine is taken when the weapon in it changed or the slot is not the current one;
        // the current slot's magazine belongs to the snapshot, whose ack-based check would otherwise never
        // correct a value set here (the two packets arrive in any order). The current slot index also comes
        // from the snapshot.
        // 기능: 서버 InventoryState의 슬롯 내용과 예비탄 5종(Phase 17: Shells·Rockets 포함)을 적용한다.
        //   리뷰 수정 C2: 지금 슬롯의 무기가 바뀌었으면(빈손에 줍기, 가득 찬 상태의 교환) 그 무기의 교체 대기를 지금 Step부터 둔다.
        //   버리기로 빈손이 되면 대기는 없다. Clear 뒤 첫 인벤토리(입장·부활)는 대기를 두지 않는다.
        // 입력: server - 받은 인벤토리 상태.
        // 출력: 반환값 없음. 슬롯·예비탄이 바뀌고, 지금 슬롯의 무기가 바뀌었으면 재장전이 끝나고 교체 대기가 생길 수 있다.
        public void ApplyInventory(in InventoryState server)
        {
            bool fresh = _inventoryFresh;
            _inventoryFresh = false;
            for (int i = 0; i < SlotCount; i++)
            {
                InventorySlotState s = server.GetSlot(i);
                int catalog = s.IsEmpty ? -1 : IndexOf(s.WeaponId);
                bool same = catalog == _slots[i].Catalog && (catalog < 0 || s.Rarity == _slots[i].Rarity);
                if (!same)
                {
                    _slots[i] = new Held { Catalog = catalog, Rarity = s.Rarity, Ammo = catalog < 0 ? 0 : s.MagAmmo };
                    if (i == Slot)
                    {
                        Reloading = false;
                        // The server equipped it at its tick, before this arrived: waiting from the current predicted step
                        // is later than the server, so a shot is never predicted that the server refuses.
                        if (!fresh && catalog >= 0) _inventoryReadyStep = Math.Max(_inventoryReadyStep, _step + _catalog[catalog].EquipTicks);
                    }
                }
                else if (i != Slot && catalog >= 0)
                {
                    _slots[i].Ammo = s.MagAmmo;
                }
            }
            SetReserves(server.LightAmmo, server.MediumAmmo, server.HeavyAmmo, server.ShellsAmmo, server.RocketsAmmo);
        }

        // New authoritative reserves (InventoryState, or 0 on Clear). The history records the reserves after each
        // step for the replay in ApplyServer; left alone, a later mismatch would restore the values from before
        // this change and undo it (e.g. an ammo pickup). So every record moves by the same per-type difference
        // between the new value and the current prediction: the replay then starts from the new authority, and
        // reload rounds predicted after the ack stay counted once (the difference already includes them).
        // Records of other seqs are shifted too; they are never read unless their seq matches. 64 x 5 adds, no
        // allocation, only when the inventory changes.
        // 기능: 서버가 정한 예비탄 5종(Phase 17 D13: Shells·Rockets 포함)을 적용하고 기록의 예비탄을 같은 차이만큼 옮긴다.
        // 입력: light·medium·heavy·shells·rockets - 탄 종류별 새 예비탄.
        // 출력: 반환값 없음. _reserve와 모든 기록의 예비탄이 바뀐다(값이 같으면 아무것도 하지 않는다).
        private void SetReserves(int light, int medium, int heavy, int shells, int rockets)
        {
            int dLight = light - _reserve[0];
            int dMedium = medium - _reserve[1];
            int dHeavy = heavy - _reserve[2];
            int dShells = shells - _reserve[3];
            int dRockets = rockets - _reserve[4];
            if (dLight == 0 && dMedium == 0 && dHeavy == 0 && dShells == 0 && dRockets == 0) return;
            for (int i = 0; i < HistorySize; i++)
            {
                _history[i].Light += dLight;
                _history[i].Medium += dMedium;
                _history[i].Heavy += dHeavy;
                _history[i].Shells += dShells;
                _history[i].Rockets += dRockets;
            }
            _reserve[0] = light;
            _reserve[1] = medium;
            _reserve[2] = heavy;
            _reserve[3] = shells;
            _reserve[4] = rockets;
        }

        // 기능: 경기 상태가 투사체 발사를 허용하는지 정한다(서버 CanLaunch의 상태 부분: WaitingForPlayers·Playing·FinalPhase만. 칸 부족은 모른다).
        // 입력: hasMatch - MatchState를 받았는지(개발 모드는 받지 않는다), state - 지금 MatchState.
        // 출력: 허용이면 true. MatchState가 없으면 true.
        public static bool LaunchAllowed(bool hasMatch, MatchFlowState state) =>
            !hasMatch || state == MatchFlowState.WaitingForPlayers || state == MatchFlowState.Playing || state == MatchFlowState.FinalPhase;

        // One predicted input, oldest first. Returns true when the server is expected to fire it.
        // actionsAllowed false (Phase 12 D12: riding, falling, gliding, vaulting): the input acts on nothing, but the fire
        // button's held state still follows it, like the server's FireHeld, so landing with Fire held does not fire a
        // semi-automatic weapon without a new press.
        // 기능: 예측 입력 하나를 한 Step으로 처리한다(오래된 것부터).
        // 입력: seq - 입력 순번, buttons - 버튼, actionsAllowed - 행동 가능 모드이고 무기 도구인지, launchAllowed - Phase 17: 투사체 무기가
        //   발사될 수 있는 경기 상태인지(서버 CanLaunch의 상태 부분. false면 투사체 무기는 탄·간격을 쓰지 않고 자동 재장전도 하지 않는다).
        // 출력: 서버가 이 입력으로 쏠 것으로 보면 true.
        public bool Step(uint seq, InputButtons buttons, bool actionsAllowed = true, bool launchAllowed = true)
        {
            return Run(seq, buttons, !actionsAllowed, !launchAllowed, _step++);
        }

        // 기능: 입력 하나를 한 Step으로 처리하고 그 결과(예비탄 5종, 투사체 발사 막힘 포함)를 기록에 남긴다.
        // 입력: seq - 입력 순번, buttons - 버튼, gated - 행동 불가 모드(발사 버튼 상태만 따른다), launchBlocked - 투사체 무기 발사 불가
        //   (Phase 17), now - 이 입력의 Step.
        // 출력: 서버가 이 입력으로 쏠 것으로 보면 true.
        private bool Run(uint seq, InputButtons buttons, bool gated, bool launchBlocked, long now)
        {
            if (Reloading && now >= _reloadEndStep)
            {
                Reloading = false;
                FinishReload();
            }

            bool fired = false;
            if (gated) _fireHeld = (buttons & InputButtons.Fire) != 0;
            else fired = Apply(buttons, launchBlocked, now);
            _history[seq % HistorySize] = new Record
            {
                Seq = seq, Step = now, Buttons = buttons, Gated = gated, LaunchBlocked = launchBlocked, Slot = Slot, Ammo = Ammo, Reloading = Reloading,
                NextFireStep = _slots[Slot].NextFireStep, SwitchReadyStep = _switchReadyStep, Light = _reserve[0], Medium = _reserve[1],
                Heavy = _reserve[2], Shells = _reserve[3], Rockets = _reserve[4],
            };
            return fired;
        }

        // The server's values after it processed input ackSeq (snapshot self block). If they equal what this
        // copy had after the same input, the newer local steps stand. Otherwise the server wins at that
        // point: the state restarts from its values and the inputs after ackSeq are replayed, rewriting their
        // history, so the next snapshot compares against consistent records and ReloadRemainingTicks is
        // measured from the ack point (Phase 3 D12).
        // 기능: 서버의 무기 상태(ackSeq 입력 뒤)와 기록을 비교하고 다르면 서버 값에서 다시 돌린다(Phase 17: 예비탄 5종을 되돌린다,
        //   리뷰 수정 C2: 교체 대기도 기록에서 되돌린다. 서버가 든 칸이 예측과 다르면 그 칸의 무기가 ack 시점에 손에 들어온 것으로 본다).
        // 입력: server - Snapshot의 자기 정보, ackSeq - 서버가 처리한 마지막 입력 순번.
        // 출력: 반환값 없음. 다르면 슬롯·탄창·재장전·예비탄·교체 대기가 서버 값에서 다시 계산된다.
        public void ApplyServer(in SnapshotSelf server, uint ackSeq)
        {
            if (ackSeq == 0) return;                                  // no input processed yet: nothing to compare
            if (server.WeaponSlot >= SlotCount) return;              // untrusted value outside the inventory

            bool serverReloading = server.ReloadRemainingTicks > 0;
            Record local = _history[ackSeq % HistorySize];
            bool known = local.Seq == ackSeq;
            if (known && local.Slot == server.WeaponSlot && local.Ammo == server.Ammo && local.Reloading == serverReloading)
                return;

            int slotBefore = Slot;
            Slot = server.WeaponSlot;
            if (HasWeapon) _slots[Slot].Ammo = Math.Min(server.Ammo, (int)Current.MagazineSize);
            Reloading = serverReloading && HasWeapon;

            if (!known)
            {
                // The ack is older than the history: no replay possible, restart from the server's values. A slot the
                // prediction did not hold came into the hand at some point before now: wait from now (never earlier than the server).
                _reloadEndStep = _step + server.ReloadRemainingTicks;
                if (slotBefore != Slot) _switchReadyStep = _step + EquipTicksInHand();
                return;
            }

            long stepAfterAck = local.Step + 1;
            _reloadEndStep = stepAfterAck + server.ReloadRemainingTicks;
            if (local.Slot == Slot) _slots[Slot].NextFireStep = local.NextFireStep;
            // Review fix C2: the server's hand differs from the prediction at the ack (e.g. a pickup into the first empty slot
            // while empty-handed): its equip happened at or before the ack, so the wait from the ack step is never too early.
            _switchReadyStep = local.Slot == Slot ? local.SwitchReadyStep : local.Step + EquipTicksInHand();
            _reserve[0] = local.Light;
            _reserve[1] = local.Medium;
            _reserve[2] = local.Heavy;
            _reserve[3] = local.Shells;
            _reserve[4] = local.Rockets;
            _fireHeld = (local.Buttons & InputButtons.Fire) != 0;
            _history[ackSeq % HistorySize] = new Record
            {
                Seq = ackSeq, Step = local.Step, Buttons = local.Buttons, Gated = local.Gated, LaunchBlocked = local.LaunchBlocked, Slot = Slot, Ammo = Ammo, Reloading = Reloading,
                NextFireStep = _slots[Slot].NextFireStep, SwitchReadyStep = _switchReadyStep, Light = local.Light, Medium = local.Medium,
                Heavy = local.Heavy, Shells = local.Shells, Rockets = local.Rockets,
            };

            long end = _step;
            _step = stepAfterAck;
            for (uint seq = ackSeq + 1; _step < end; seq++)
            {
                Record r = _history[seq % HistorySize];
                if (r.Seq != seq) break;                               // cannot happen while the ack record is valid
                Run(seq, r.Buttons, r.Gated, r.LaunchBlocked, _step++);
            }
            _step = end;
        }

        // Same order as the server: switch -> reload -> fire.
        // 기능: 무기 전환 → 재장전 → 발사를 서버 순서대로 한 Step 적용한다. 리뷰 수정 C2: 칸이 바뀌면 새로 든 무기의 교체 대기를 두고
        //   (빈 칸은 대기 없음, 같은 칸은 새로 두지 않음), 대기 중의 방아쇠는 발사 간격 안과 같다(누름만 쓰고 탄·간격·자동 재장전 없음, R은 된다).
        // 입력: buttons - 버튼, launchBlocked - Phase 17: 투사체 무기 발사 불가(서버 CanLaunch false처럼 방아쇠는 소비하되 탄·간격·자동
        //   재장전 없음), now - 이 입력의 Step.
        // 출력: 서버가 쏠 것으로 보면 true.
        private bool Apply(InputButtons buttons, bool launchBlocked, long now)
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
                _switchReadyStep = now + EquipTicksInHand();
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
            if (!trigger || Reloading || now < held.NextFireStep || now < _switchReadyStep || now < _inventoryReadyStep) return false;
            // Phase 17: the server's CanLaunch false is an invalid aim: the press is spent (FireHeld above), nothing else.
            if (launchBlocked && weapon.Projectile != ProjectileKind.None) return false;

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

        // 기능: 그 무기 탄 종류의 예비탄이 있으면 재장전을 시작한다(없으면 아무것도 하지 않는다).
        // 입력: weapon - 손에 든 무기, now - 이 입력의 Step.
        // 출력: 반환값 없음. 시작했으면 Reloading이 켜지고 끝 Step이 now + ReloadTicks가 된다.
        private void TryStartReload(WeaponInfo weapon, long now)
        {
            if (GetReserve(weapon.AmmoType) == 0) return;
            Reloading = true;
            _reloadEndStep = now + weapon.ReloadTicks;
        }

        // 기능: 재장전이 끝나 예비탄에서 탄창을 채운다(부족하면 있는 만큼). 빈손이거나 탄 종류가 None이면 아무것도 하지 않는다.
        // 입력: 없음.
        // 출력: 반환값 없음. 지금 슬롯의 탄창이 늘고 그 종류의 예비탄이 같은 만큼 준다.
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

        // 기능: 무기 id로 카탈로그 색인을 찾는다.
        // 입력: weaponId - 무기 id.
        // 출력: 카탈로그 색인, 없으면 -1.
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
