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
        private float _noticeHideTime = -1f;

        // 기능: 인벤토리 HUD Canvas와 슬롯·소모품·줍기 안내·알림 Text, 치료 진행 바를 코드로 만든다.
        // 입력: 없음.
        // 출력: 모든 요소가 생성되고 Root가 비활성인 InventoryHud.
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

        // 기능: HUD 전체의 표시 여부를 바꾼다. 값이 같거나 Root가 이미 파괴됐으면 아무것도 하지 않는다.
        // 입력: visible - 보일지 여부.
        // 출력: 반환값 없음. Root GameObject의 활성 상태가 바뀐다.
        public void SetVisible(bool visible)
        {
            // Unity null: the root can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 무기 슬롯 한 줄을 문자열이 다시 만들어졌을 때만 화면에 반영하고 희귀도 색을 입힌다.
        // 입력: slot - 슬롯 인덱스, selected - 손에 든 슬롯 여부, weapon - 무기 이름(빈 슬롯이면 null), rarityName - 희귀도 이름, rarity - 희귀도 색 인덱스, ammo - 탄창 탄약, reserve - 예비 탄약.
        // 출력: 반환값 없음. 값이 바뀌면 해당 슬롯 Text와 색이 갱신된다.
        public void SetSlot(int slot, bool selected, string weapon, string rarityName, int rarity, int ammo, int reserve)
        {
            if (_root == null || !_text.SetSlot(slot, selected, weapon, rarityName, ammo, reserve)) return;
            _slots[slot].text = _text.Slot(slot);
            _slots[slot].color = weapon == null ? new Color(1f, 1f, 1f, 0.5f) : RarityColors[rarity];
        }

        // 기능: 구급상자·실드 셀 개수 줄을 값이 바뀌었을 때만 화면에 반영한다.
        // 입력: medkits - 구급상자 개수, shieldCells - 실드 셀 개수.
        // 출력: 반환값 없음. 값이 바뀌면 소모품 Text가 갱신된다.
        public void SetConsumables(int medkits, int shieldCells)
        {
            if (_root == null || !_text.SetConsumables(medkits, shieldCells)) return;
            _consumables.text = _text.Consumables;
        }

        // 기능: 줍기 안내 줄을 대상이나 수량이 바뀌었을 때만 화면에 반영한다.
        // 입력: itemId - 대상 아이템 ID(0이면 대상 없음), amount - 대상 수량, name - 아이템 이름, rarity - 희귀도 이름(묶음 아이템이면 null).
        // 출력: 반환값 없음. 값이 바뀌면 안내 Text가 갱신된다.
        public void SetPrompt(ushort itemId, ushort amount, string name, string rarity)
        {
            if (_root == null || !_text.SetPrompt(itemId, amount, name, rarity)) return;
            _prompt.text = _text.Prompt;
        }

        // 기능: 치료 진행 바를 보이거나 숨기고 진행도에 맞게 채운다.
        // 입력: progress - 진행도(0~1), 0보다 작으면 바를 숨긴다.
        // 출력: 반환값 없음. 진행 바의 활성 상태와 채움 폭이 바뀐다.
        // progress < 0 hides the bar.
        public void SetUseProgress(float progress)
        {
            if (_root == null) return;
            bool show = progress >= 0f;
            if (_bar.activeSelf != show) _bar.SetActive(show);
            if (show) _barFill.sizeDelta = new Vector2(BarWidth * Mathf.Clamp01(progress), 8f);
        }

        // 기능: 줍기 결과(PickupResult) 알림을 NoticeSeconds 동안 표시한다.
        // 입력: message - 표시할 상수 문자열, now - 현재 로컬 시간(초).
        // 출력: 반환값 없음. 알림 Text와 숨길 시각이 설정된다.
        // PickupResult feedback. message must be a constant string (no per-call allocation).
        public void ShowNotice(string message, float now)
        {
            if (_root == null) return;
            _notice.text = message;
            _noticeHideTime = now + NoticeSeconds;
        }

        // 기능: 매 프레임 알림 표시 시간이 지났는지 확인해 지났으면 알림을 지운다.
        // 입력: now - 현재 로컬 시간(초).
        // 출력: 반환값 없음. 만료되면 알림 Text가 비워진다.
        public void Tick(float now)
        {
            if (_root == null || _noticeHideTime < 0f || now < _noticeHideTime) return;
            _noticeHideTime = -1f;
            _notice.text = string.Empty;
        }

        // 기능: 인벤토리 HUD Canvas를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. Root와 모든 자식 UI가 파괴된다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }

        // 기능: Root 아래에 Raycast 대상이 아닌 일반 Text 하나를 만든다.
        // 입력: name - GameObject 이름, font - 사용할 글꼴, anchor - 앵커·피벗 위치, offset - 앵커 기준 위치, alignment - 글자 정렬.
        // 출력: 빈 문자열로 초기화된 Text.
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
