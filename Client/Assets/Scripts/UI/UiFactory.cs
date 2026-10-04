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

        // 기능: Scene에 EventSystem이 없으면 Input System UI 모듈과 함께 만든다.
        // 입력: 없음.
        // 출력: 새로 만든 EventSystem GameObject(DontDestroyOnLoad). 비활성 포함 이미 있으면 null.
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

        // 기능: 1920 x 1080 기준으로 크기가 맞춰지는 Screen Space Overlay Canvas를 만든다.
        // 입력: name - GameObject 이름, sortingOrder - 그리기 순서, interactive - GraphicRaycaster 추가 여부.
        // 출력: 만든 Canvas GameObject.
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

        // 기능: 부모 아래에 고정 앵커의 자식 RectTransform을 만든다.
        // 입력: name - 이름, parent - 부모, anchor - 앵커이자 Pivot, position - 앵커 기준 위치, size - 크기.
        // 출력: 만든 RectTransform.
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

        // 기능: 부모 전체를 덮는 화면 RectTransform을 만들고 필요하면 반투명 배경을 깐다.
        // 입력: name - 이름, parent - 부모, dim - 반투명 배경 Image 추가 여부.
        // 출력: 만든 RectTransform. dim이면 Raycast를 막는 배경 Image가 붙는다.
        // A full-screen layer. With dim it is a raycast target, so a click beside a panel does not reach anything under it.
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

        // 기능: 부모 중앙에 패널 배경 Image를 가진 RectTransform을 만든다.
        // 입력: name - 이름, parent - 부모, size - 크기.
        // 출력: 만든 패널 RectTransform(Raycast 대상 아님).
        public static RectTransform CreatePanel(string name, Transform parent, Vector2 size)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = PanelColor;
            image.raycastTarget = false;
            return rect;
        }

        // 기능: 공용 폰트와 기본 색을 쓰는 Text를 만든다(Rich Text 끔, Raycast 대상 아님).
        // 입력: name - 이름, parent - 부모, text - 초기 문자열, fontSize - 글자 크기, alignment - 정렬, anchor - 앵커이자 Pivot, position - 앵커 기준 위치, size - 크기.
        // 출력: 만든 Text.
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

        // 기능: 버튼과 라벨을 만들고 클릭 Handler를 연결한다.
        // 입력: name - 이름, parent - 부모, label - 버튼 문자열, position - 부모 중앙 기준 위치, size - 크기, onClick - 클릭 시 호출할 Handler.
        // 출력: 만든 Button.
        // A button centred at position in its parent. onClick runs on the main thread from the EventSystem's update.
        public static Button CreateButton(string name, Transform parent, string label, Vector2 position, Vector2 size, UnityAction onClick)
        {
            RectTransform rect = CreateRect(name, parent, new Vector2(0.5f, 0.5f), position, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = ButtonColor;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);
            Text text = CreateText("Label", rect, label, 24, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return button;
        }

        // 기능: 텍스트와 Placeholder를 가진 한 줄 InputField를 만든다.
        // 입력: name - 이름, parent - 부모, position - 부모 중앙 기준 위치, size - 크기, characterLimit - 최대 글자 수, contentType - 입력 형식.
        // 출력: 만든 InputField.
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

        // 기능: InputField 안에 들어갈 여백 있는 Text를 만든다.
        // 입력: name - 이름, parent - InputField의 RectTransform, fontSize - 글자 크기.
        // 출력: 만든 Text.
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
