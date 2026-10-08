using ProjectH.Client.UI;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game
{
    // D15: inventory HUD and pickup prompt on one Screen Space Overlay canvas built in code (UGUI legacy Text
    // with UiFont, like CombatHud). No GraphicRaycaster; nothing is a raycast target. Text is set
    // only when InventoryHudText rebuilt a string, so an unchanged HUD allocates nothing per frame; the heal
    // bar changes only a RectTransform size. Dispose destroys the canvas.
    public sealed class InventoryHud : System.IDisposable
    {
        private const int FontSize = 18;
        private const float BarWidth = 200f;
        private const float NoticeSeconds = 1.5f;

        // Rarity colors (D15): Common, Uncommon, Rare, Epic, Legendary. Shared with WorldItemViews.
        public static readonly Color[] RarityColors =
        {
            new Color(0.8f, 0.8f, 0.8f),
            new Color(0.35f, 0.85f, 0.35f),
            new Color(0.3f, 0.55f, 1f),
            new Color(0.7f, 0.35f, 0.95f),
            new Color(1f, 0.65f, 0.15f),
        };

        private readonly InventoryHudText _text = new InventoryHudText();
        private readonly GameObject _root;
        private readonly Text[] _slots = new Text[WeaponState.SlotCount];
        private readonly Text _consumables;
        private readonly Text _prompt;
        private readonly Text _notice;
        private readonly GameObject _bar;
        private readonly RectTransform _barFill;
        private bool _visible;
        private bool _slotsHidden;   // Phase 19: the weapon slot lines are hidden while seated
        private float _noticeHideTime = -1f;

        public InventoryHud()
        {
            _root = new GameObject("InventoryHud");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 91;   // above CombatHud (90), under the crosshair (100)

            Font font = UiFont.Get();
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = CreateText("Slot" + (i + 1), font, new Vector2(0.5f, 0f), new Vector2(0f, 88f - 22f * i), TextAnchor.LowerCenter);
            _consumables = CreateText("Consumables", font, new Vector2(0.5f, 0f), new Vector2(0f, 20f), TextAnchor.LowerCenter);
            _prompt = CreateText("Prompt", font, new Vector2(0.5f, 0.5f), new Vector2(0f, -120f), TextAnchor.MiddleCenter);
            _notice = CreateText("Notice", font, new Vector2(0.5f, 0.5f), new Vector2(0f, -145f), TextAnchor.MiddleCenter);
            _notice.color = new Color(1f, 0.8f, 0.3f);

            // Heal channel bar (D11): a dark frame and a fill whose width is the progress.
            _bar = new GameObject("UseBar", typeof(RectTransform));
            var barRect = (RectTransform)_bar.transform;
            barRect.SetParent(_root.transform, false);
            barRect.anchorMin = barRect.anchorMax = new Vector2(0.5f, 0.5f);
            barRect.anchoredPosition = new Vector2(0f, -60f);
            barRect.sizeDelta = new Vector2(BarWidth + 4f, 12f);
            var frame = _bar.AddComponent<Image>();
            frame.color = new Color(0f, 0f, 0f, 0.6f);
            frame.raycastTarget = false;
            var fill = new GameObject("Fill", typeof(RectTransform));
            _barFill = (RectTransform)fill.transform;
            _barFill.SetParent(barRect, false);
            _barFill.anchorMin = _barFill.anchorMax = _barFill.pivot = new Vector2(0f, 0.5f);
            _barFill.anchoredPosition = new Vector2(2f, 0f);
            _barFill.sizeDelta = new Vector2(0f, 8f);
            var fillImage = fill.AddComponent<Image>();
            fillImage.color = new Color(0.4f, 0.9f, 0.5f);
            fillImage.raycastTarget = false;
            _bar.SetActive(false);

            _root.SetActive(false);
        }

        public void SetVisible(bool visible)
        {
            // Unity null: the root can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 무기 슬롯 줄 3개를 보이거나 숨긴다(Phase 19: 차량에 앉아 있으면 무기를 쓸 수 없고 차량 HUD가 같은 자리에 그려진다). 소모품 줄은
        //   그대로 둔다. 바뀔 때만 SetActive를 부른다.
        // 입력: visible - 슬롯 줄을 보일지.
        // 출력: 반환값 없음.
        public void SetSlotsVisible(bool visible)
        {
            if (_root == null || visible != _slotsHidden) return;
            _slotsHidden = !visible;
            for (int i = 0; i < _slots.Length; i++) _slots[i].gameObject.SetActive(visible);
        }

        public void SetSlot(int slot, bool selected, string weapon, string rarityName, int rarity, int ammo, int reserve)
        {
            if (_root == null || !_text.SetSlot(slot, selected, weapon, rarityName, ammo, reserve)) return;
            _slots[slot].text = _text.Slot(slot);
            _slots[slot].color = weapon == null ? new Color(1f, 1f, 1f, 0.5f) : RarityColors[rarity];
        }

        // 기능: 소모품 줄(Phase 17: 수류탄 수 포함)을 값이 바뀔 때만 화면에 쓴다.
        // 입력: medkits·shieldCells·grenades - 가진 개수.
        // 출력: 반환값 없음.
        public void SetConsumables(int medkits, int shieldCells, int grenades)
        {
            if (_root == null || !_text.SetConsumables(medkits, shieldCells, grenades)) return;
            _consumables.text = _text.Consumables;
        }

        public void SetPrompt(ushort itemId, ushort amount, string name, string rarity)
        {
            if (_root == null || !_text.SetPrompt(itemId, amount, name, rarity)) return;
            _prompt.text = _text.Prompt;
        }

        // progress < 0 hides the bar.
        public void SetUseProgress(float progress)
        {
            if (_root == null) return;
            bool show = progress >= 0f;
            if (_bar.activeSelf != show) _bar.SetActive(show);
            if (show) _barFill.sizeDelta = new Vector2(BarWidth * Mathf.Clamp01(progress), 8f);
        }

        // PickupResult feedback. message must be a constant string (no per-call allocation).
        public void ShowNotice(string message, float now)
        {
            if (_root == null) return;
            _notice.text = message;
            _noticeHideTime = now + NoticeSeconds;
        }

        public void Tick(float now)
        {
            if (_root == null || _noticeHideTime < 0f || now < _noticeHideTime) return;
            _noticeHideTime = -1f;
            _notice.text = string.Empty;
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }

        private Text CreateText(string name, Font font, Vector2 anchor, Vector2 offset, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_root.transform, false);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(520f, 24f);

            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = FontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;
            text.supportRichText = false;   // Phase 11: every UI text is plain text
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.text = string.Empty;
            return text;
        }
    }
}
