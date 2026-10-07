using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game
{
    // Phase 14 D14: the squad HUD on its own canvas (no GraphicRaycaster; nothing is a raycast target):
    //   - top left under the place name: one row per team member (name, status, a health bar),
    //   - under the rows: the reboot cards we hold,
    //   - above the centre: "기절 · 출혈 n초" and a red bar while we are downed,
    //   - below the centre: the revive or reboot progress bar with its label (ours, or a revive on us).
    // All objects are made once here (at most 4 rows); texts change only when SquadHudText rebuilt a string, bars only when
    // a whole pixel changes, so an unchanged HUD allocates nothing per frame. Dispose destroys the canvas.
    public sealed class SquadHud : System.IDisposable
    {
        private const int RowFontSize = 18;
        private const float RowHeight = 40f;
        private const float RowsTop = -60f;   // under PoiLabel (top left, 20 px)
        private const float RowBarWidth = 160f;
        private const float CenterBarWidth = 260f;
        private const float BarHeight = 8f;

        private readonly SquadHudText _text = new SquadHudText();
        private readonly GameObject _root;
        private readonly GameObject[] _rows = new GameObject[SquadHudText.MaxRows];
        private readonly Text[] _rowTexts = new Text[SquadHudText.MaxRows];
        private readonly RectTransform[] _rowFills = new RectTransform[SquadHudText.MaxRows];
        private readonly Image[] _rowFillImages = new Image[SquadHudText.MaxRows];
        private readonly int[] _rowPixels = new int[SquadHudText.MaxRows];
        private readonly bool[] _rowDowned = new bool[SquadHudText.MaxRows];
        private readonly Text _cards;
        private readonly Text _bleed;
        private readonly GameObject _bleedBar;
        private readonly RectTransform _bleedFill;
        private readonly Text _channel;
        private readonly GameObject _channelBar;
        private readonly RectTransform _channelFill;
        private bool _visible;
        private int _bleedPixels = -1;
        private int _channelPixels = -1;
        private string _channelLabel;

        // 기능: Canvas와 줄 4개, 카드 줄, 기절 막대, 진행 막대를 만든다(모두 숨긴 채).
        // 입력: 없음.
        // 출력: 숨겨진 SquadHud(Dispose가 Canvas를 파괴한다).
        public SquadHud()
        {
            _root = UiFactory.CreateCanvas("SquadHud", 95, interactive: false);   // above KillFeed (94), under the crosshair (100)
            Vector2 topLeft = new Vector2(0f, 1f);
            for (int i = 0; i < _rows.Length; i++)
            {
                RectTransform row = UiFactory.CreateRect("Member" + i, _root.transform, topLeft, new Vector2(20f, RowsTop - RowHeight * i),
                    new Vector2(420f, RowHeight));
                _rows[i] = row.gameObject;
                _rowTexts[i] = UiFactory.CreateText("Name", row, string.Empty, RowFontSize, TextAnchor.UpperLeft, topLeft, Vector2.zero,
                    new Vector2(420f, 24f));
                _rowTexts[i].horizontalOverflow = HorizontalWrapMode.Overflow;
                _rowFills[i] = CreateBar(row, topLeft, new Vector2(0f, -26f), RowBarWidth, new Color(0.4f, 0.9f, 0.45f), out _rowFillImages[i]);
                _rowPixels[i] = -1;
                _rows[i].SetActive(false);
            }
            _cards = UiFactory.CreateText("Cards", _root.transform, string.Empty, RowFontSize, TextAnchor.UpperLeft, topLeft,
                new Vector2(20f, RowsTop - RowHeight * SquadHudText.MaxRows), new Vector2(420f, 24f));
            _cards.color = new Color(0.45f, 1f, 0.75f);

            Vector2 center = new Vector2(0.5f, 0.5f);
            _bleed = UiFactory.CreateText("Bleed", _root.transform, string.Empty, 24, TextAnchor.MiddleCenter, center, new Vector2(0f, 150f),
                new Vector2(520f, 32f));
            _bleed.color = new Color(1f, 0.45f, 0.4f);
            RectTransform bleedFrame = UiFactory.CreateRect("BleedBar", _root.transform, center, new Vector2(0f, 125f),
                new Vector2(CenterBarWidth + 4f, BarHeight + 4f));
            _bleedBar = bleedFrame.gameObject;
            _bleedFill = CreateBar(bleedFrame, new Vector2(0f, 0.5f), new Vector2(2f, 0f), CenterBarWidth, new Color(0.95f, 0.25f, 0.2f), out _);
            _bleedBar.SetActive(false);

            _channel = UiFactory.CreateText("Channel", _root.transform, string.Empty, 20, TextAnchor.MiddleCenter, center, new Vector2(0f, -175f),
                new Vector2(520f, 28f));
            RectTransform channelFrame = UiFactory.CreateRect("ChannelBar", _root.transform, center, new Vector2(0f, -198f),
                new Vector2(CenterBarWidth + 4f, BarHeight + 4f));
            _channelBar = channelFrame.gameObject;
            _channelFill = CreateBar(channelFrame, new Vector2(0f, 0.5f), new Vector2(2f, 0f), CenterBarWidth, new Color(0.45f, 0.85f, 1f), out _);
            _channelBar.SetActive(false);

            _root.SetActive(false);
        }

        // 기능: HUD 전체를 보이거나 숨긴다(값이 바뀔 때만 SetActive).
        // 입력: visible - 보일지.
        // 출력: 반환값 없음.
        public void SetVisible(bool visible)
        {
            // Unity null: the root can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 구성원 한 줄을 갱신한다(문자열·막대는 바뀔 때만).
        // 입력: index - 줄 번호, entityId·name·state·flags·self - 구성원 값, health - 10 단위 체력(0..100).
        // 출력: 반환값 없음.
        public void SetRow(int index, ushort entityId, string name, TeamMemberState state, int health, TeamMemberFlags flags, bool self)
        {
            if (_root == null) return;
            if (!_rows[index].activeSelf) _rows[index].SetActive(true);
            if (_text.SetRow(index, entityId, name, state, flags, self)) _rowTexts[index].text = _text.Row(index);
            int pixels = Mathf.RoundToInt(Mathf.Clamp01(health / 100f) * RowBarWidth);
            if (pixels != _rowPixels[index])
            {
                _rowPixels[index] = pixels;
                _rowFills[index].sizeDelta = new Vector2(pixels, BarHeight);
            }
            bool downed = state == TeamMemberState.Downed;
            if (downed != _rowDowned[index])
            {
                _rowDowned[index] = downed;
                _rowFillImages[index].color = downed ? new Color(0.95f, 0.3f, 0.25f) : new Color(0.4f, 0.9f, 0.45f);
            }
        }

        // 기능: index번째부터 끝까지의 줄을 숨긴다(팀이 작거나 없을 때).
        // 입력: index - 첫 숨길 줄.
        // 출력: 반환값 없음.
        public void HideRowsFrom(int index)
        {
            if (_root == null) return;
            for (int i = index; i < _rows.Length; i++)
            {
                if (_rows[i].activeSelf) _rows[i].SetActive(false);
            }
        }

        // 기능: 소지 카드 줄을 갱신한다.
        // 입력: cards - 소지 카드 수(0이면 빈 줄).
        // 출력: 반환값 없음.
        public void SetCards(int cards)
        {
            if (_root != null && _text.SetCards(cards)) _cards.text = _text.Cards;
        }

        // 기능: 기절 줄과 막대를 갱신한다.
        // 입력: seconds - 출혈 탈락까지 남은 초(음수면 숨김), health - 기절 체력(0..100, 막대 길이).
        // 출력: 반환값 없음.
        public void SetBleed(int seconds, int health)
        {
            if (_root == null) return;
            if (_text.SetBleed(seconds)) _bleed.text = _text.Bleed;
            bool show = seconds >= 0;
            if (_bleedBar.activeSelf != show) _bleedBar.SetActive(show);
            if (!show) return;
            int pixels = Mathf.RoundToInt(Mathf.Clamp01(health / SquadPrompt.DownedHealth) * CenterBarWidth);
            if (pixels == _bleedPixels) return;
            _bleedPixels = pixels;
            _bleedFill.sizeDelta = new Vector2(pixels, BarHeight);
        }

        // 기능: 소생·재투입 진행 막대를 갱신한다.
        // 입력: label - UiText의 상수 문구(null이면 숨김), progress - 0..1.
        // 출력: 반환값 없음.
        public void SetChannel(string label, float progress)
        {
            if (_root == null) return;
            if (!ReferenceEquals(label, _channelLabel))
            {
                _channelLabel = label;
                _channel.text = label ?? string.Empty;
            }
            bool show = label != null;
            if (_channelBar.activeSelf != show) _channelBar.SetActive(show);
            if (!show) return;
            int pixels = Mathf.RoundToInt(Mathf.Clamp01(progress) * CenterBarWidth);
            if (pixels == _channelPixels) return;
            _channelPixels = pixels;
            _channelFill.sizeDelta = new Vector2(pixels, BarHeight);
        }

        // 기능: Canvas를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }

        // 기능: 반투명 검은 바탕과 그 위의 채움 막대를 만든다(왼쪽 기준, 채움 너비 = 진행).
        // 입력: parent - 부모, anchor - 기준점(왼쪽), position - 기준점에서의 위치, width - 가득 찬 너비(처음 크기), color - 채움 색,
        //   image - 만든 채움 Image.
        // 출력: 채움 막대의 RectTransform.
        private static RectTransform CreateBar(RectTransform parent, Vector2 anchor, Vector2 position, float width, Color color, out Image image)
        {
            RectTransform back = UiFactory.CreateRect("Back", parent, anchor, position, new Vector2(width, BarHeight));
            var backImage = back.gameObject.AddComponent<Image>();   // no sprite: a solid rectangle
            backImage.color = new Color(0f, 0f, 0f, 0.5f);
            backImage.raycastTarget = false;
            RectTransform fill = UiFactory.CreateRect("Fill", back, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(width, BarHeight));
            image = fill.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return fill;
        }
    }
}
