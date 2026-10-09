using UnityEngine;
using UnityEngine.Events;

namespace ProjectH.Client.UI
{
    // Phase 11 D5: the Esc menu. No pause (the server keeps running the match): the character stands still while it is
    // open because UiRoot blocks game input. Nothing on it changes after creation.
    public sealed class MenuScreen
    {
        private readonly GameObject _root;
        private bool _visible = true;

        // 기능: Esc 메뉴(제목과 계속하기·내 전적·접속 끊기·게임 종료 버튼)를 Canvas 아래에 만든다.
        // 입력: canvas - 부모 Canvas, onContinue·onStats·onDisconnect·onQuit - 각 버튼의 클릭 처리.
        // 출력: 보이는 상태로 시작하는 MenuScreen.
        public MenuScreen(Transform canvas, UnityAction onContinue, UnityAction onStats, UnityAction onDisconnect, UnityAction onQuit)
        {
            _root = UiFactory.CreateScreen("Menu", canvas, dim: true).gameObject;
            RectTransform panel = UiFactory.CreatePanel("Panel", _root.transform, new Vector2(480f, 460f));
            UiFactory.CreateText("Title", panel, "메뉴", 40, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), new Vector2(0f, 170f), new Vector2(440f, 60f));
            UiFactory.CreateButton("Continue", panel, "계속하기", new Vector2(0f, 80f), new Vector2(320f, 60f), onContinue);
            UiFactory.CreateButton("Stats", panel, "내 전적", new Vector2(0f, 5f), new Vector2(320f, 60f), onStats);
            UiFactory.CreateButton("Disconnect", panel, "접속 끊기", new Vector2(0f, -70f), new Vector2(320f, 60f), onDisconnect);
            UiFactory.CreateButton("Quit", panel, "게임 종료", new Vector2(0f, -145f), new Vector2(320f, 60f), onQuit);
        }

        // 기능: 메뉴를 보이거나 숨긴다.
        // 입력: visible - 보일지.
        // 출력: 반환값 없음. 바뀔 때만 루트 GameObject가 켜지거나 꺼진다.
        public void SetVisible(bool visible)
        {
            if (visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }
    }
}
