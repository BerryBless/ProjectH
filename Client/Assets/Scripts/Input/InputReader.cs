using System;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ProjectH.Client.Input
{
    // The only place that talks to the Unity Input System; game code reads plain values from here.
    // Actions are created in code (no .inputactions asset dependency) and disposed in Dispose().
    public sealed class InputReader : IDisposable
    {
        private readonly InputAction _move;
        private readonly InputAction _look;
        private readonly InputAction _jump;
        private readonly InputAction _sprint;
        private readonly InputAction _fire;
        private readonly InputAction _aim;
        private readonly InputAction _reload;
        private readonly InputAction _slot1;
        private readonly InputAction _slot2;
        private readonly InputAction _slot3;
        private readonly InputAction _interact;
        private readonly InputAction _drop;
        private readonly InputAction _useMedkit;
        private readonly InputAction _useShieldCell;
        private readonly InputAction _escape;
        private readonly InputAction _debugToggle;
        private readonly InputAction _crouchToggle;
        private readonly InputAction _crouchHold;
        // Phase 13 D16: Q build mode, F the harvest tool; in build mode Z wall, X floor, V ramp, B roof, T material (R
        // rotates instead of reloading: GameClient decides).
        private readonly InputAction _toolBuild;
        private readonly InputAction _toolHarvest;
        private readonly InputAction _pieceWall;
        private readonly InputAction _pieceFloor;
        private readonly InputAction _pieceRamp;
        private readonly InputAction _pieceRoof;
        private readonly InputAction _material;
        // Phase 13.5 D10: H starts edit mode on the aimed piece and confirms it (G is the drop key).
        private readonly InputAction _edit;
        // Phase 15 D4, D6: M opens and closes the full map, the middle button pings, and the pointer position is where a
        // full-map click lands (the cursor is free while the map is open).
        private readonly InputAction _map;
        private readonly InputAction _ping;
        private readonly InputAction _point;
        // Phase 17 D9: 6 throws a grenade (a press, queued like the heal keys).
        private readonly InputAction _throwGrenade;
        // Phase 12 D7: C turns crouch on and off; a jump or a sprint press turns it off again.
        private bool _crouchToggled;

        // 기능: 게임 입력 Action을 코드로 만들고 켠다(Phase 13.5: H 편집, Phase 15: M 지도·가운데 버튼 Ping·포인터 위치, Phase 17: 6 수류탄 포함).
        // 입력: 없음.
        // 출력: 모든 Action이 켜진 InputReader(Dispose가 해제한다).
        public InputReader()
        {
            _move = new InputAction("Move", InputActionType.Value);
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            _look = new InputAction("Look", InputActionType.Value, "<Mouse>/delta");
            _jump = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
            _sprint = new InputAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
            // Left click: locks the cursor while it is free, fires while it is locked (D12, GameClient).
            _fire = new InputAction("Fire", InputActionType.Button, "<Mouse>/leftButton");
            _aim = new InputAction("Aim", InputActionType.Button, "<Mouse>/rightButton");
            _reload = new InputAction("Reload", InputActionType.Button, "<Keyboard>/r");
            _slot1 = new InputAction("Slot1", InputActionType.Button, "<Keyboard>/1");
            _slot2 = new InputAction("Slot2", InputActionType.Button, "<Keyboard>/2");
            // Phase 4 (D8, D11, D12): 3 = third slot, E = pick up, G = drop, 4 = Medkit, 5 = Shield Cell.
            _slot3 = new InputAction("Slot3", InputActionType.Button, "<Keyboard>/3");
            _interact = new InputAction("Interact", InputActionType.Button, "<Keyboard>/e");
            _drop = new InputAction("Drop", InputActionType.Button, "<Keyboard>/g");
            _useMedkit = new InputAction("UseMedkit", InputActionType.Button, "<Keyboard>/4");
            _useShieldCell = new InputAction("UseShieldCell", InputActionType.Button, "<Keyboard>/5");
            // Phase 11 D5: Esc opens and closes the menu (UiFlow decides; the cursor follows it). F1: the debug line (D4).
            _escape = new InputAction("Escape", InputActionType.Button, "<Keyboard>/escape");
            _debugToggle = new InputAction("DebugToggle", InputActionType.Button, "<Keyboard>/f1");
            // Phase 12 D7: C toggles crouch, Ctrl holds it (the Crouch button is sent as held either way).
            _crouchToggle = new InputAction("CrouchToggle", InputActionType.Button, "<Keyboard>/c");
            _crouchHold = new InputAction("CrouchHold", InputActionType.Button, "<Keyboard>/leftCtrl");
            _toolBuild = new InputAction("ToolBuild", InputActionType.Button, "<Keyboard>/q");
            _toolHarvest = new InputAction("ToolHarvest", InputActionType.Button, "<Keyboard>/f");
            _pieceWall = new InputAction("PieceWall", InputActionType.Button, "<Keyboard>/z");
            _pieceFloor = new InputAction("PieceFloor", InputActionType.Button, "<Keyboard>/x");
            _pieceRamp = new InputAction("PieceRamp", InputActionType.Button, "<Keyboard>/v");
            _pieceRoof = new InputAction("PieceRoof", InputActionType.Button, "<Keyboard>/b");
            _material = new InputAction("Material", InputActionType.Button, "<Keyboard>/t");
            _edit = new InputAction("Edit", InputActionType.Button, "<Keyboard>/h");
            _map = new InputAction("Map", InputActionType.Button, "<Keyboard>/m");
            _ping = new InputAction("Ping", InputActionType.Button, "<Mouse>/middleButton");
            _point = new InputAction("Point", InputActionType.Value, "<Mouse>/position");
            _throwGrenade = new InputAction("ThrowGrenade", InputActionType.Button, "<Keyboard>/6");

            _move.Enable();
            _look.Enable();
            _jump.Enable();
            _sprint.Enable();
            _fire.Enable();
            _aim.Enable();
            _reload.Enable();
            _slot1.Enable();
            _slot2.Enable();
            _slot3.Enable();
            _interact.Enable();
            _drop.Enable();
            _useMedkit.Enable();
            _useShieldCell.Enable();
            _escape.Enable();
            _debugToggle.Enable();
            _crouchToggle.Enable();
            _crouchHold.Enable();
            _toolBuild.Enable();
            _toolHarvest.Enable();
            _pieceWall.Enable();
            _pieceFloor.Enable();
            _pieceRamp.Enable();
            _pieceRoof.Enable();
            _material.Enable();
            _edit.Enable();
            _map.Enable();
            _ping.Enable();
            _point.Enable();
            _throwGrenade.Enable();
        }

        public Vector2 Move => _move.ReadValue<Vector2>();
        public Vector2 LookDelta => _look.ReadValue<Vector2>();
        public bool Sprint => _sprint.IsPressed();
        public bool FirePressed => _fire.WasPressedThisFrame();
        // Phase 14 D7: E is down now (sent as InteractHeld in every input; the press stays the queued Interact).
        public bool InteractHeld => _interact.IsPressed();
        public bool FireHeld => _fire.IsPressed();
        public bool AimHeld => _aim.IsPressed();
        // Phase 13.5 D10: right click this frame (edit mode's Reset) and H this frame (start or confirm an edit).
        public bool AimPressed => _aim.WasPressedThisFrame();
        public bool EditPressed => _edit.WasPressedThisFrame();
        // Phase 15 D4, D6: M and the middle button this frame (UI-only presses, never queued into an input), and the pointer
        // in screen pixels (bottom-left origin).
        public bool MapPressed => _map.WasPressedThisFrame();
        public bool PingPressed => _ping.WasPressedThisFrame();
        public Vector2 PointerPosition => _point.ReadValue<Vector2>();
        public bool EscapePressed => _escape.WasPressedThisFrame();
        public bool DebugTogglePressed => _debugToggle.WasPressedThisFrame();
        // Phase 12 D7: the Crouch button: toggled with C or held with Ctrl.
        public bool CrouchHeld => _crouchToggled || _crouchHold.IsPressed();

        // A respawn or a death starts standing.
        public void ResetCrouch() => _crouchToggled = false;

        // Phase 13 D16: this frame's piece key (Z X V B), or -1; T this frame. Read by GameClient once per frame.
        public int PiecePressed =>
            _pieceWall.WasPressedThisFrame() ? (int)BuildPieceType.Wall :
            _pieceFloor.WasPressedThisFrame() ? (int)BuildPieceType.Floor :
            _pieceRamp.WasPressedThisFrame() ? (int)BuildPieceType.Ramp :
            _pieceRoof.WasPressedThisFrame() ? (int)BuildPieceType.Roof : -1;
        public bool MaterialPressed => _material.WasPressedThisFrame();

        // Jump, Reload, Slot1-3, Interact, Drop, the two heal presses, the tool keys and (Phase 17) the grenade throw since the
        // last simulation step that used them. Rendering runs
        // faster than the fixed simulation, so a press between two steps must be remembered, not lost;
        // LocalPlayerPredictor.Advance clears the bits it puts into an input.
        public InputButtons QueuedButtons { get; set; }

        // Call once per rendered frame. gameInputBlocked: a screen is up or the cursor is free; C then does not toggle the
        // crouch (Ctrl is not sent either, GameClient), so a key typed into a menu does not crouch the character.
        // 기능: 이번 프레임의 웅크리기 토글과 누른 순간 버튼(Phase 17: 6 = ThrowGrenade 포함)을 QueuedButtons에 모은다.
        // 입력: gameInputBlocked - 화면이 열렸거나 커서가 풀렸는지(웅크리기 토글만 막는다; 대기 버튼은 GameClient가 비운다).
        // 출력: 반환값 없음. QueuedButtons와 웅크리기 토글이 바뀐다.
        public void Update(bool gameInputBlocked)
        {
            if (!gameInputBlocked)
            {
                if (_crouchToggle.WasPressedThisFrame()) _crouchToggled = !_crouchToggled;
                if (_jump.WasPressedThisFrame() || _sprint.WasPressedThisFrame()) _crouchToggled = false;
            }
            if (_jump.WasPressedThisFrame()) QueuedButtons |= InputButtons.Jump;
            if (_reload.WasPressedThisFrame()) QueuedButtons |= InputButtons.Reload;
            if (_slot1.WasPressedThisFrame()) QueuedButtons |= InputButtons.Slot1;
            if (_slot2.WasPressedThisFrame()) QueuedButtons |= InputButtons.Slot2;
            if (_slot3.WasPressedThisFrame()) QueuedButtons |= InputButtons.Slot3;
            if (_interact.WasPressedThisFrame()) QueuedButtons |= InputButtons.Interact;
            if (_drop.WasPressedThisFrame()) QueuedButtons |= InputButtons.Drop;
            if (_useMedkit.WasPressedThisFrame()) QueuedButtons |= InputButtons.UseMedkit;
            if (_useShieldCell.WasPressedThisFrame()) QueuedButtons |= InputButtons.UseShieldCell;
            if (_toolBuild.WasPressedThisFrame()) QueuedButtons |= InputButtons.ToolBuild;       // Phase 13 D5
            if (_toolHarvest.WasPressedThisFrame()) QueuedButtons |= InputButtons.ToolHarvest;
            if (_throwGrenade.WasPressedThisFrame()) QueuedButtons |= InputButtons.ThrowGrenade;   // Phase 17 D9
        }

        // 기능: 만든 Input Action을 모두 해제한다(Phase 15의 지도·Ping·포인터, Phase 17의 수류탄 키 포함).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            _move.Dispose();
            _look.Dispose();
            _jump.Dispose();
            _sprint.Dispose();
            _fire.Dispose();
            _aim.Dispose();
            _reload.Dispose();
            _slot1.Dispose();
            _slot2.Dispose();
            _slot3.Dispose();
            _interact.Dispose();
            _drop.Dispose();
            _useMedkit.Dispose();
            _useShieldCell.Dispose();
            _escape.Dispose();
            _debugToggle.Dispose();
            _crouchToggle.Dispose();
            _crouchHold.Dispose();
            _toolBuild.Dispose();
            _toolHarvest.Dispose();
            _pieceWall.Dispose();
            _pieceFloor.Dispose();
            _pieceRamp.Dispose();
            _pieceRoof.Dispose();
            _material.Dispose();
            _edit.Dispose();
            _map.Dispose();
            _ping.Dispose();
            _point.Dispose();
            _throwGrenade.Dispose();
        }
    }
}
