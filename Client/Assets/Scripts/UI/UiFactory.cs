using ProjectH.Client.Game.Audio;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace ProjectH.Client.UI
{
    // Phase 11 D1: builds the code-made UGUI pieces of the new UI (no scene, prefab or asset). Every object is a child of
    // a canvas its caller owns, so destroying that canvas destroys the pieces and their button listeners with it. Only
    // what the player clicks or types into is a raycast target.
    public static class UiFactory
    {
        public static readonly Color PanelColor = new Color(0.08f, 0.09f, 0.11f, 0.94f);
        public static readonly Color DimColor = new Color(0f, 0f, 0f, 0.55f);
        public static readonly Color ButtonColor = new Color(0.24f, 0.27f, 0.33f, 1f);
        public static readonly Color FieldColor = new Color(0.15f, 0.16f, 0.19f, 1f);
        public static readonly Color TextColor = new Color(0.95f, 0.95f, 0.95f, 1f);
        public static readonly Color ErrorColor = new Color(1f, 0.55f, 0.45f, 1f);
        public static readonly Color AccentColor = new Color(1f, 0.85f, 0.3f, 1f);

        // D1: the EventSystem the buttons and fields need, with the Input System's UI module (the project runs the Input
        // System only). The module assigns its default UI actions in OnEnable when it has none and releases them in
        // OnDisable (Input System 1.20). Returns the object it made, or null when one already exists (then it is not the
        // caller's to destroy).
        public static GameObject EnsureEventSystem()
        {
            // Inactive ones count too: a second EventSystem next to a disabled scene one would fight it once enabled.
            if (Object.FindAnyObjectByType<EventSystem>(FindObjectsInactive.Include) != null) return null;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            Object.DontDestroyOnLoad(go);
            return go;
        }

        // D1: a Screen Space Overlay canvas scaled from 1920 x 1080 (width and height weighted equally). interactive adds
        // the GraphicRaycaster: only screens with buttons or fields have one.
        public static GameObject CreateCanvas(string name, int sortingOrder, bool interactive)
        {
            var go = new GameObject(name);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            if (interactive) go.AddComponent<GraphicRaycaster>();
            return go;
        }

        // A child rectangle: anchor is also its pivot; position is from that anchor.
        public static RectTransform CreateRect(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        // A full-screen dim layer. It is a raycast target, so a click beside a panel does not reach anything under it.
        public static RectTransform CreateScreen(string name, Transform parent, bool dim)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            if (dim)
            {
                var image = go.AddComponent<Image>();   // no sprite: a solid rectangle
                image.color = DimColor;
            }
            return rect;
        }

        public static RectTransform CreatePanel(string name, Transform parent, Vector2 size)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = PanelColor;
            image.raycastTarget = false;
            return rect;
        }

        public static Text CreateText(string name, Transform parent, string text, int fontSize, TextAnchor alignment,
            Vector2 anchor, Vector2 position, Vector2 size)
        {
            RectTransform rect = CreateRect(name, parent, anchor, position, size);
            var label = rect.gameObject.AddComponent<Text>();
            label.font = UiFont.Get();
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = TextColor;
            label.raycastTarget = false;
            // Player names reach the kill feed, the result and the spectating line: "<color=red>" in a name must show as
            // typed, not as markup. No UI string uses tags.
            label.supportRichText = false;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.text = text;
            return label;
        }

        // A button centred at position in its parent. onClick runs on the main thread from the EventSystem's update.
        // 기능: 부모 가운데 기준 위치에 글자 버튼을 만든다. Phase 18 D9: 모든 버튼이 클릭음(UiSound.Click)을 낸다(클릭음은 여기 한 곳).
        // 입력: name - GameObject 이름, parent - 부모, label - 글자, position·size - 위치·크기, onClick - 클릭 처리.
        // 출력: 만든 Button. 리스너는 버튼 GameObject와 함께 사라진다.
        public static Button CreateButton(string name, Transform parent, string label, Vector2 position, Vector2 size, UnityAction onClick)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(0.5f, 0.5f), position, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = ButtonColor;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(UiSound.Click);
            button.onClick.AddListener(onClick);
            Text text = CreateText("Label", rect, label, 24, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return button;
        }

        // The legacy UGUI InputField (D1: no TextMeshPro), laid out like DefaultControls.CreateInputField.
        public static InputField CreateInputField(string name, Transform parent, Vector2 position, Vector2 size, int characterLimit,
            InputField.ContentType contentType)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(0.5f, 0.5f), position, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = FieldColor;
            var field = rect.gameObject.AddComponent<InputField>();
            field.targetGraphic = image;

            Text text = CreateFieldText("Text", rect, 24);
            Text placeholder = CreateFieldText("Placeholder", rect, 24);
            placeholder.fontStyle = FontStyle.Italic;
            placeholder.color = new Color(1f, 1f, 1f, 0.35f);

            field.textComponent = text;
            field.placeholder = placeholder;
            field.characterLimit = characterLimit;
            field.contentType = contentType;
            field.lineType = InputField.LineType.SingleLine;
            return field;
        }

        private static Text CreateFieldText(string name, RectTransform parent, int fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(12f, 6f);
            rect.offsetMax = new Vector2(-12f, -6f);
            var text = go.AddComponent<Text>();
            text.font = UiFont.Get();
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = TextColor;
            text.raycastTarget = false;
            text.supportRichText = false;   // InputField requires it on its text; plain text everywhere (see CreateText)
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.text = string.Empty;
            return text;
        }
    }
}
