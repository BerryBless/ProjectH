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

        // 기능: 타이틀 화면을 캔버스 아래에 한 번 만든다(주소·포트·이름 입력 칸, 접속·취소·종료 버튼, 오류 문구, 조작법).
        // 입력: canvas - 부모 캔버스, onConnect - 검사를 통과한 주소·포트·이름으로 접속을 시작하는 처리, onCancel - 접속 취소 처리, onQuit - 종료 처리.
        // 출력: 보이는 상태(취소 버튼은 숨김)로 만들어진 TitleScreen.
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

        // 기능: 입력 칸에 주소·포트·이름을 채운다.
        // 입력: host - 주소(null이면 빈칸), port - 포트(0 이하면 빈칸), name - 이름(null이면 빈칸).
        // 출력: 반환값 없음. 세 입력 칸의 글자가 바뀐다.
        public void Fill(string host, int port, string name)
        {
            _host.text = host ?? string.Empty;
            _port.text = port > 0 ? port.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            _name.text = name ?? string.Empty;
        }

        // 기능: 화면을 보이거나 숨긴다.
        // 입력: visible - 보이면 true.
        // 출력: 반환값 없음. 값이 바뀔 때만 루트 GameObject가 켜지거나 꺼진다.
        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 오류 문구(거절된 입력이나 지난 접속이 끝난 이유)를 오류 색으로 보인다.
        // 입력: message - 보일 문구(null이나 빈 문자열이면 지운다).
        // 출력: 반환값 없음. 문구가 바뀌고 규칙 표시 상태(_ruleShown)가 풀린다.
        public void SetMessage(string message)
        {
            _ruleShown = false;
            _message.color = UiFactory.ErrorColor;
            _message.text = message ?? string.Empty;
        }

        // 기능: 거절된 입력의 규칙을 보이고 규칙 표시 상태로 둔다(어느 칸이든 입력하거나 다음 접속 클릭에 지워진다).
        // 입력: rule - 규칙 문구.
        // 출력: 반환값 없음. 오류 문구와 _ruleShown이 바뀐다.
        private void ShowRule(string rule)
        {
            SetMessage(rule);
            _ruleShown = true;
        }

        // 기능: 입력 칸 값 변경 Handler. 규칙 문구가 떠 있으면 지운다.
        // 입력: _ - 바뀐 값(쓰지 않음).
        // 출력: 반환값 없음.
        private void OnFieldChanged(string _)
        {
            if (_ruleShown) SetMessage(string.Empty);
        }

        // 기능: 접속 중 모드를 켜거나 끈다(입력 칸 잠금, 접속 대신 취소 버튼, 켤 때 "접속하는 중..." 문구).
        // 입력: connecting - 접속 중이면 true.
        // 출력: 반환값 없음. 값이 바뀔 때만 버튼·입력 칸·문구가 바뀐다.
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

        // 기능: 접속 버튼을 누를 수 있게 하거나 막는다.
        // 입력: enabled - 누를 수 있으면 true.
        // 출력: 반환값 없음. 값이 바뀔 때만 버튼의 interactable이 바뀐다.
        // Off while the previous connection is still closing (GameClient.Connect would ignore the click).
        public void SetConnectEnabled(bool enabled)
        {
            if (enabled == _connectEnabled) return;
            _connectEnabled = enabled;
            _connect.interactable = enabled;
        }

        // 기능: 접속 버튼 클릭 Handler. 주소·포트·이름을 UiText 규칙(서버 규칙)으로 검사하고 통과하면 onConnect를 부른다.
        // 입력: 없음(입력 칸의 글자를 읽는다).
        // 출력: 반환값 없음. 거절되면 해당 규칙 문구가 보이고, 통과하면 이름 칸이 다듬은 이름으로 바뀌고 onConnect가 불린다.
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
