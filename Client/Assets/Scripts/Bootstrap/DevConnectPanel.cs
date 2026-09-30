using ProjectH.Client.Game;
using ProjectH.Client.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ProjectH.Client.Bootstrap
{
    // Development-only connect UI (IMGUI). IMGUI allocates while drawing, which is acceptable for a
    // dev panel; it hides itself after joining (F1 toggles) and must not become the game UI.
    [RequireComponent(typeof(GameClient))]
    public sealed class DevConnectPanel : MonoBehaviour
    {
        private GameClient _client;
        private string _host;
        private string _port;
        private string _devId;
        private bool _visible = true;
        private bool _wasJoined;

        private void Awake()
        {
            _client = GetComponent<GameClient>();
            LaunchArgs args = LaunchArgs.FromCommandLine();
            _host = args.Host;
            _port = args.Port.ToString();
            _devId = args.DevPlayerId;
            if (args.AutoConnect) _client.Connect(_host, args.Port, _devId);
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f1Key.wasPressedThisFrame) _visible = !_visible;

            bool joined = _client.State == ClientState.Joined;
            if (joined && !_wasJoined) _visible = false;
            if (!joined && _wasJoined) _visible = true;
            _wasJoined = joined;
        }

        private void OnGUI()
        {
            if (!_visible) return;

            GUILayout.BeginArea(new Rect(10, 10, 340, 190), GUI.skin.box);
            GUILayout.Label($"State: {_client.State}   RTT: {_client.RoundTripMs} ms   Entity: {_client.MyEntityId}");
            if (!string.IsNullOrEmpty(_client.LastError)) GUILayout.Label(_client.LastError);

            if (_client.State == ClientState.Disconnected)
            {
                _host = Field("Host", _host);
                _port = Field("Port", _port);
                _devId = Field("DevPlayerId", _devId);
                if (GUILayout.Button("Connect") && int.TryParse(_port, out int port)) _client.Connect(_host, port, _devId);
            }
            else if (GUILayout.Button("Disconnect"))
            {
                _client.Disconnect();
            }

            GUILayout.Label("F1: panel   Left click: lock mouse   Esc: unlock");
            GUILayout.EndArea();
        }

        private static string Field(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(90));
            value = GUILayout.TextField(value);
            GUILayout.EndHorizontal();
            return value;
        }
    }
}
