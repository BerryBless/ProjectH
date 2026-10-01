using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D4: the title screen, which replaces the development IMGUI panel: address, port and name, Connect and Quit,
    // the last error, and the controls. While connecting (UiScreen.Connecting) the same screen shows "접속하는 중..."
    // and a Cancel button instead of Connect. Built once under the screens canvas; hidden with SetActive (D11). Input is
    // checked here with UiText's rules (the server's rules) before onConnect is called.
    public sealed class TitleScreen
    {
        private const string Controls =
            "WASD 이동   Shift 달리기   Space 점프   왼쪽 클릭 사격   오른쪽 클릭 조준   R 재장전\n" +
            "1/2/3 무기   E 줍기   G 무기 버리기   4 구급상자   5 실드 셀   Esc 메뉴   F1 디버그 정보";

        private readonly GameObject _root;
        private readonly InputField _host;
        private readonly InputField _port;
        private readonly InputField _name;
        private readonly Text _message;
        private readonly Button _connect;
        private readonly Button _cancel;
        private readonly Action<string, int, string> _onConnect;
        private bool _visible = true;
        private bool _connecting;
        private bool _connectEnabled = true;
        // A refused input's rule is on screen: typing into any field, or the next Connect, clears it.
        private bool _ruleShown;

        public TitleScreen(Transform canvas, Action<string, int, string> onConnect, UnityAction onCancel, UnityAction onQuit)
        {
            _onConnect = onConnect;
            _root = UiFactory.CreateScreen("Title", canvas, dim: true).gameObject;
            RectTransform panel = UiFactory.CreatePanel("Panel", _root.transform, new Vector2(760f, 640f));
            Vector2 center = new Vector2(0.5f, 0.5f);

            Text title = UiFactory.CreateText("Title", panel, "ProjectH", 60, TextAnchor.MiddleCenter, center, new Vector2(0f, 250f), new Vector2(700f, 80f));
            title.color = UiFactory.AccentColor;

            UiFactory.CreateText("HostLabel", panel, "주소", 26, TextAnchor.MiddleRight, center, new Vector2(-230f, 140f), new Vector2(140f, 50f));
            _host = UiFactory.CreateInputField("Host", panel, new Vector2(70f, 140f), new Vector2(420f, 50f), 253, InputField.ContentType.Standard);
            UiFactory.CreateText("PortLabel", panel, "포트", 26, TextAnchor.MiddleRight, center, new Vector2(-230f, 75f), new Vector2(140f, 50f));
            _port = UiFactory.CreateInputField("Port", panel, new Vector2(70f, 75f), new Vector2(420f, 50f), 5, InputField.ContentType.IntegerNumber);
            UiFactory.CreateText("NameLabel", panel, "이름", 26, TextAnchor.MiddleRight, center, new Vector2(-230f, 10f), new Vector2(140f, 50f));
            _name = UiFactory.CreateInputField("Name", panel, new Vector2(70f, 10f), new Vector2(420f, 50f), ProjectH.Shared.Protocol.ProtocolConstants.MaxDevPlayerIdBytes, InputField.ContentType.Standard);
            ((Text)_name.placeholder).text = "1-32바이트";
            // The listeners live as long as the fields (destroyed with the canvas).
            _host.onValueChanged.AddListener(OnFieldChanged);
            _port.onValueChanged.AddListener(OnFieldChanged);
            _name.onValueChanged.AddListener(OnFieldChanged);

            _message = UiFactory.CreateText("Message", panel, string.Empty, 22, TextAnchor.MiddleCenter, center, new Vector2(0f, -60f), new Vector2(700f, 60f));
            _message.color = UiFactory.ErrorColor;

            _connect = UiFactory.CreateButton("Connect", panel, "접속", new Vector2(-110f, -140f), new Vector2(200f, 60f), OnConnectClicked);
            _cancel = UiFactory.CreateButton("Cancel", panel, "취소", new Vector2(-110f, -140f), new Vector2(200f, 60f), onCancel);
            _cancel.gameObject.SetActive(false);
            UiFactory.CreateButton("Quit", panel, "종료", new Vector2(110f, -140f), new Vector2(200f, 60f), onQuit);

            Text help = UiFactory.CreateText("Controls", panel, Controls, 18, TextAnchor.MiddleCenter, center, new Vector2(0f, -250f), new Vector2(720f, 70f));
            help.color = new Color(1f, 1f, 1f, 0.6f);
        }

        public void Fill(string host, int port, string name)
        {
            _host.text = host ?? string.Empty;
            _port.text = port > 0 ? port.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            _name.text = name ?? string.Empty;
        }

        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // An error to show (a refused input, or why the last connection ended), or empty.
        public void SetMessage(string message)
        {
            _ruleShown = false;
            _message.color = UiFactory.ErrorColor;
            _message.text = message ?? string.Empty;
        }

        private void ShowRule(string rule)
        {
            SetMessage(rule);
            _ruleShown = true;
        }

        private void OnFieldChanged(string _)
        {
            if (_ruleShown) SetMessage(string.Empty);
        }

        // UiScreen.Connecting: fields locked, Cancel instead of Connect.
        public void SetConnecting(bool connecting)
        {
            if (connecting == _connecting) return;
            _connecting = connecting;
            _connect.gameObject.SetActive(!connecting);
            _cancel.gameObject.SetActive(connecting);
            _host.interactable = !connecting;
            _port.interactable = !connecting;
            _name.interactable = !connecting;
            if (connecting)
            {
                _message.color = UiFactory.TextColor;
                _message.text = UiText.Connecting;
            }
        }

        // Off while the previous connection is still closing (GameClient.Connect would ignore the click).
        public void SetConnectEnabled(bool enabled)
        {
            if (enabled == _connectEnabled) return;
            _connectEnabled = enabled;
            _connect.interactable = enabled;
        }

        private void OnConnectClicked()
        {
            if (_ruleShown) SetMessage(string.Empty);
            if (!UiText.TryNormalizeHost(_host.text, out string host))
            {
                ShowRule(UiText.HostRule);
                return;
            }
            if (!UiText.TryParsePort(_port.text, out int port))
            {
                ShowRule(UiText.PortRule);
                return;
            }
            if (!UiText.TryNormalizeName(_name.text, out string name))
            {
                ShowRule(UiText.NameRule);
                return;
            }
            _name.text = name;
            _onConnect(host, port, name);
        }
    }
}
