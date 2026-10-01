using ProjectH.Client.Net;
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
        private bool _visible;
        private ClientState _state = (ClientState)(-1);
        private int _rtt = -1;
        private ushort _entity;

        public DebugOverlay()
        {
            _root = UiFactory.CreateCanvas("DebugOverlay", 120, interactive: false);
            _text = UiFactory.CreateText("Line", _root.transform, string.Empty, 20, TextAnchor.UpperLeft, new Vector2(0f, 1f),
                new Vector2(24f, -60f), new Vector2(900f, 30f));
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _root.SetActive(false);
        }

        public void Toggle()
        {
            if (_root == null) return;
            _visible = !_visible;
            _root.SetActive(_visible);
            _rtt = -1;   // rebuild on the next Tick
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

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
