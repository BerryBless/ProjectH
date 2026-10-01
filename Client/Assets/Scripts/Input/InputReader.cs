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
        // Phase 12 D7: C turns crouch on and off; a jump or a sprint press turns it off again.
        private bool _crouchToggled;

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
        }

        public Vector2 Move => _move.ReadValue<Vector2>();
        public Vector2 LookDelta => _look.ReadValue<Vector2>();
        public bool Sprint => _sprint.IsPressed();
        public bool FirePressed => _fire.WasPressedThisFrame();
        public bool FireHeld => _fire.IsPressed();
        public bool AimHeld => _aim.IsPressed();
        public bool EscapePressed => _escape.WasPressedThisFrame();
        public bool DebugTogglePressed => _debugToggle.WasPressedThisFrame();
        // Phase 12 D7: the Crouch button: toggled with C or held with Ctrl.
        public bool CrouchHeld => _crouchToggled || _crouchHold.IsPressed();

        // A respawn or a death starts standing.
        public void ResetCrouch() => _crouchToggled = false;

        // Jump, Reload, Slot1-3, Interact, Drop and the two heal presses since the last simulation step that
        // used them. Rendering runs
        // faster than the fixed simulation, so a press between two steps must be remembered, not lost;
        // LocalPlayerPredictor.Advance clears the bits it puts into an input.
        public InputButtons QueuedButtons { get; set; }

        // Call once per rendered frame. gameInputBlocked: a screen is up or the cursor is free; C then does not toggle the
        // crouch (Ctrl is not sent either, GameClient), so a key typed into a menu does not crouch the character.
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
        }

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
        }
    }
}
