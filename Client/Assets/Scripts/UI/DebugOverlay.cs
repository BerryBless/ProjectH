using ProjectH.Client.Net;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D4: the development line that used to be in the IMGUI panel (connection state, RTT, entity id), toggled with
    // F1 and hidden at start. One Text on its own canvas (no GraphicRaycaster), so the RTT changing redraws only this
    // canvas. The string is rebuilt only when a shown value changes, and nothing is built while it is hidden.
    public sealed class DebugOverlay : System.IDisposable
    {
        private readonly GameObject _root;
        private readonly Text _text;
        private readonly Text _movement;
        private readonly Text _route;
        private bool _visible;
        private ClientState _state = (ClientState)(-1);
        private int _rtt = -1;
        private ushort _entity;
        // Phase 12 D14: the shown movement digits; the line is rebuilt when one changes, at most 10 times a second. Two
        // values outside MovementMode stand for "nothing shown yet" (rebuild on the next tick) and "dead" (the line empty).
        private const MovementMode ModeUnknown = (MovementMode)255;
        private const MovementMode ModeDead = (MovementMode)254;
        private MovementMode _mode = ModeUnknown;
        private int _horizontal;
        private int _vertical;
        private int _energy;
        private int _correction;
        private float _nextMovementAt;
        private bool _hasRoute;
        private DropRoute _shownRoute;

        public DebugOverlay()
        {
            _root = UiFactory.CreateCanvas("DebugOverlay", 120, interactive: false);
            _text = UiFactory.CreateText("Line", _root.transform, string.Empty, 20, TextAnchor.UpperLeft, new Vector2(0f, 1f),
                new Vector2(24f, -60f), new Vector2(900f, 30f));
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _movement = UiFactory.CreateText("Movement", _root.transform, string.Empty, 20, TextAnchor.UpperLeft, new Vector2(0f, 1f),
                new Vector2(24f, -90f), new Vector2(900f, 30f));
            _movement.horizontalOverflow = HorizontalWrapMode.Overflow;
            _route = UiFactory.CreateText("Route", _root.transform, string.Empty, 20, TextAnchor.UpperLeft, new Vector2(0f, 1f),
                new Vector2(24f, -120f), new Vector2(900f, 30f));
            _route.horizontalOverflow = HorizontalWrapMode.Overflow;
            _root.SetActive(false);
        }

        public void Toggle()
        {
            if (_root == null) return;
            _visible = !_visible;
            _root.SetActive(_visible);
            _rtt = -1;   // rebuild on the next Tick
            _mode = ModeUnknown;
            _hasRoute = false;
            _route.text = string.Empty;
        }

        public void Tick(ClientState state, int roundTripMs, ushort entityId)
        {
            if (_root == null || !_visible) return;
            if (state == _state && roundTripMs == _rtt && entityId == _entity) return;
            _state = state;
            _rtt = roundTripMs;
            _entity = entityId;
            _text.text = UiText.DebugLine(state.ToString(), roundTripMs, entityId);
        }

        // Phase 12 D14: the local player's mode, speeds, energy and the last prediction correction, and the transport route.
        public void TickMovement(float now, bool alive, MovementMode mode, float horizontal, float vertical, float energy, float correction,
            bool hasRoute, in DropRoute route)
        {
            if (_root == null || !_visible) return;
            if (hasRoute != _hasRoute || (hasRoute && !SameRoute(route, _shownRoute)))
            {
                _hasRoute = hasRoute;
                _shownRoute = route;
                _route.text = hasRoute ? UiText.RouteLine(route.StartX, route.StartZ, route.EndX, route.EndZ) : string.Empty;
            }
            if (!alive)
            {
                if (_mode != ModeDead) _movement.text = string.Empty;
                _mode = ModeDead;
                return;
            }
            if (now < _nextMovementAt) return;
            int h = Mathf.RoundToInt(horizontal * 10f);
            int v = Mathf.RoundToInt(vertical * 10f);
            int e = Mathf.RoundToInt(energy);
            int c = Mathf.RoundToInt(correction * 100f);
            if (mode == _mode && h == _horizontal && v == _vertical && e == _energy && c == _correction) return;
            _mode = mode;
            _horizontal = h;
            _vertical = v;
            _energy = e;
            _correction = c;
            _nextMovementAt = now + 0.1f;
            _movement.text = UiText.MovementLine(ModeName(mode), h, v, e, c);
        }

        private static bool SameRoute(in DropRoute a, in DropRoute b) =>
            a.StartX == b.StartX && a.StartZ == b.StartZ && a.EndX == b.EndX && a.EndZ == b.EndZ && a.StartTick == b.StartTick;

        // Constant names: no allocation.
        private static string ModeName(MovementMode mode)
        {
            switch (mode)
            {
                case MovementMode.Crouch: return "Crouch";
                case MovementMode.Slide: return "Slide";
                case MovementMode.Vault: return "Vault";
                case MovementMode.Freefall: return "Freefall";
                case MovementMode.Glide: return "Glide";
                case MovementMode.Transport: return "Transport";
                default: return "Ground";
            }
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
