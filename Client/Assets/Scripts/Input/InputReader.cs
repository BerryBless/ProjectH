using System;
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
        private readonly InputAction _unlockCursor;

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
            _unlockCursor = new InputAction("UnlockCursor", InputActionType.Button, "<Keyboard>/escape");

            _move.Enable();
            _look.Enable();
            _jump.Enable();
            _sprint.Enable();
            _fire.Enable();
            _aim.Enable();
            _unlockCursor.Enable();
        }

        public Vector2 Move => _move.ReadValue<Vector2>();
        public Vector2 LookDelta => _look.ReadValue<Vector2>();
        public bool Sprint => _sprint.IsPressed();
        public bool FirePressed => _fire.WasPressedThisFrame();
        public bool FireHeld => _fire.IsPressed();
        public bool AimHeld => _aim.IsPressed();
        public bool UnlockCursorPressed => _unlockCursor.WasPressedThisFrame();

        // Set when Jump is pressed, cleared by the simulation step that uses it. Rendering runs faster
        // than the fixed simulation, so a press between two steps must be remembered, not lost.
        public bool JumpQueued { get; set; }

        // Call once per rendered frame.
        public void Update()
        {
            if (_jump.WasPressedThisFrame()) JumpQueued = true;
        }

        public void Dispose()
        {
            _move.Dispose();
            _look.Dispose();
            _jump.Dispose();
            _sprint.Dispose();
            _fire.Dispose();
            _aim.Dispose();
            _unlockCursor.Dispose();
        }
    }
}
