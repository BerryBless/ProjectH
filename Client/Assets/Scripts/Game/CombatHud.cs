using ProjectH.Client.UI;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game
{
    // D13: combat HUD built in code on one Screen Space Overlay canvas (UGUI legacy Text with UiFont, Phase 11 D2; no
    // TextMeshPro, which needs imported assets). No GraphicRaycaster and nothing is a raycast target.
    // Strings are rebuilt only when a shown value changes, so an idle HUD allocates nothing per frame.
    // Dispose destroys the canvas.
    public sealed class CombatHud : System.IDisposable
    {
        // Display only. Must match the server's CombatRules.RespawnSeconds (the server decides the real time).
        public const float RespawnSeconds = 3f;
        private const float HitMarkerSeconds = 0.15f;
        private const float DamageIndicatorSeconds = 1f;
        private const float DamageIndicatorRadius = 90f;
        private const int FontSize = 22;
        // Phase 12 D14: the energy bar above the vitals line (full width = full energy).
        private const float EnergyBarWidth = 220f;
        private const float EnergyBarHeight = 8f;

        private readonly GameObject _root;
        private readonly Text _vitals;
        private readonly Text _weapon;
        private readonly Text _center;
        private readonly Text _hint;
        private readonly GameObject _energyBar;
        private readonly RectTransform _energyFill;
        private readonly Image _energyFillImage;
        private readonly GameObject _hitMarker;
        private readonly Image[] _hitBars = new Image[4];
        private readonly RectTransform _damageIndicator;

        private bool _visible;
        private int _health = -1;
        private int _shield = -1;
        private string _weaponName;
        private int _ammo = -1;
        private int _reserve = -1;
        private bool _reloading;
        private float _hitHideTime;
        private float _damageHideTime;
        private float _damageYaw;       // world yaw of the attacker direction, degrees
        private float _respawnAt = -1f; // local time of the expected respawn; < 0 when alive
        private int _countdown = -1;
        private string _hintText;
        private int _energyPixels = -1;
        private bool _energyExhausted;

        // 기능: 전투 HUD Canvas와 체력·무기·중앙·힌트 Text, 에너지 바, 히트 마커, 피격 방향 표시를 코드로 만든다.
        // 입력: 없음.
        // 출력: 모든 요소가 생성되고 Root가 비활성인 CombatHud.
        public CombatHud()
        {
            _root = new GameObject("CombatHud");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 90;   // under the crosshair (100)

            Font font = UiFont.Get();
            _vitals = CreateText("Vitals", font, new Vector2(0f, 0f), new Vector2(20f, 20f), TextAnchor.LowerLeft);
            _weapon = CreateText("Weapon", font, new Vector2(1f, 0f), new Vector2(-20f, 20f), TextAnchor.LowerRight);
            _center = CreateText("Center", font, new Vector2(0.5f, 0.5f), new Vector2(0f, -80f), TextAnchor.MiddleCenter);
            // Phase 12 D14: the hint line ("[Space] 뛰어내리기", "[E] 문 열기") under the centre, and the energy bar.
            _hint = CreateText("Hint", font, new Vector2(0.5f, 0.5f), new Vector2(0f, -130f), TextAnchor.MiddleCenter);
            _hint.color = new Color(1f, 0.95f, 0.6f);
            Image back = CreateBar((RectTransform)_root.transform, Vector2.zero, new Vector2(EnergyBarWidth, EnergyBarHeight), 0f);
            back.color = new Color(0f, 0f, 0f, 0.5f);
            var backRect = back.rectTransform;
            backRect.anchorMin = Vector2.zero;
            backRect.anchorMax = Vector2.zero;
            backRect.pivot = Vector2.zero;
            backRect.anchoredPosition = new Vector2(20f, 66f);
            _energyBar = back.gameObject;
            _energyFillImage = CreateBar(backRect, Vector2.zero, new Vector2(EnergyBarWidth, EnergyBarHeight), 0f);
            _energyFill = _energyFillImage.rectTransform;
            _energyFill.anchorMin = Vector2.zero;
            _energyFill.anchorMax = Vector2.zero;
            _energyFill.pivot = Vector2.zero;
            _energyFill.anchoredPosition = Vector2.zero;
            _energyFillImage.color = new Color(0.4f, 0.85f, 1f);
            _energyBar.SetActive(false);

            _hitMarker = new GameObject("HitMarker", typeof(RectTransform));
            var markerRect = (RectTransform)_hitMarker.transform;
            markerRect.SetParent(_root.transform, false);
            markerRect.anchorMin = new Vector2(0.5f, 0.5f);
            markerRect.anchorMax = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < 4; i++)
            {
                float angle = 45f + 90f * i;
                float rad = angle * Mathf.Deg2Rad;
                _hitBars[i] = CreateBar(markerRect, new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * 14f, new Vector2(10f, 2f), angle);
            }
            _hitMarker.SetActive(false);

            Image indicator = CreateBar((RectTransform)_root.transform, Vector2.zero, new Vector2(40f, 6f), 0f);
            indicator.color = new Color(1f, 0.2f, 0.2f, 0.85f);
            _damageIndicator = indicator.rectTransform;
            _damageIndicator.anchorMin = new Vector2(0.5f, 0.5f);
            _damageIndicator.anchorMax = new Vector2(0.5f, 0.5f);
            _damageIndicator.gameObject.SetActive(false);

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

        // 기능: 체력·실드 줄을 값이 바뀌었을 때만 다시 만든다.
        // 입력: health - 현재 체력, shield - 현재 실드.
        // 출력: 반환값 없음. 값이 바뀌면 체력 Text가 갱신된다.
        public void SetVitals(int health, int shield)
        {
            if (_root == null || (health == _health && shield == _shield)) return;
            _health = health;
            _shield = shield;
            _vitals.text = "체력 " + health + "   실드 " + shield;
        }

        // 기능: 들고 있는 무기 이름과 탄창/예비 탄약, 재장전 상태를 값이 바뀌었을 때만 표시한다.
        // 입력: name - 무기 이름(참조로 비교), ammo - 탄창 탄약, reserve - 해당 탄종 예비 탄약, reloading - 재장전 중 여부.
        // 출력: 반환값 없음. 값이 바뀌면 무기 Text가 갱신된다.
        // Phase 4: magazine / reserve rounds of the weapon's ammo type.
        public void SetWeapon(string name, int ammo, int reserve, bool reloading)
        {
            if (_root == null) return;
            if (ReferenceEquals(name, _weaponName) && ammo == _ammo && reserve == _reserve && reloading == _reloading) return;
            _weaponName = name;
            _ammo = ammo;
            _reserve = reserve;
            _reloading = reloading;
            _weapon.text = reloading ? name + "   재장전 중..." : name + "   " + ammo + " / " + reserve;
        }

        // 기능: 무기 줄을 비운다(무기가 없을 때).
        // 입력: 없음.
        // 출력: 반환값 없음. 무기 Text와 캐시된 무기 이름이 비워진다.
        public void ClearWeapon()
        {
            if (_root == null || _weaponName == null) return;
            _weaponName = null;
            _weapon.text = string.Empty;
        }

        // 기능: 에너지 바의 표시 여부, 길이(픽셀 단위로 바뀔 때만), 탈진 색을 갱신한다.
        // 입력: fraction - 남은 에너지 비율(0~1), visible - 바를 보일지 여부, exhausted - 탈진 상태 여부.
        // 출력: 반환값 없음. 에너지 바의 활성 상태·크기·색이 바뀐다.
        // Phase 12 D14: energy 0..1; hidden when full and not sprinting. Exhausted (no sprint until 20) shows orange. The
        // bar only changes size when a whole pixel changes.
        public void SetEnergy(float fraction, bool visible, bool exhausted)
        {
            if (_root == null) return;
            if (_energyBar.activeSelf != visible) _energyBar.SetActive(visible);
            if (!visible) return;
            int pixels = Mathf.RoundToInt(Mathf.Clamp01(fraction) * EnergyBarWidth);
            if (pixels != _energyPixels)
            {
                _energyPixels = pixels;
                _energyFill.sizeDelta = new Vector2(pixels, EnergyBarHeight);
            }
            if (exhausted != _energyExhausted)
            {
                _energyExhausted = exhausted;
                _energyFillImage.color = exhausted ? new Color(1f, 0.55f, 0.2f) : new Color(0.4f, 0.85f, 1f);
            }
        }

        // 기능: 중앙 아래 힌트 줄을 참조가 바뀌었을 때만 바꾼다.
        // 입력: hint - UiText의 상수 힌트 문자열, 없으면 null.
        // 출력: 반환값 없음. 힌트 Text가 바뀐다(null이면 빈 문자열).
        // Phase 12 D14: one of UiText's constant hints, or null. Set only when the reference changes.
        public void SetHint(string hint)
        {
            if (_root == null || ReferenceEquals(hint, _hintText)) return;
            _hintText = hint;
            _hint.text = hint ?? string.Empty;
        }

        // 기능: 명중 확인(HitConfirmed) 히트 마커를 짧게 표시한다.
        // 입력: killed - 처치 여부(빨간색), now - 현재 로컬 시간(초).
        // 출력: 반환값 없음. 히트 마커가 켜지고 숨길 시각이 기록된다.
        // HitConfirmed: white cross, red on a kill.
        public void ShowHit(bool killed, float now)
        {
            if (_root == null) return;
            Color color = killed ? new Color(1f, 0.2f, 0.2f) : Color.white;
            for (int i = 0; i < _hitBars.Length; i++) _hitBars[i].color = color;
            _hitMarker.SetActive(true);
            _hitHideTime = now + HitMarkerSeconds;
        }

        // 기능: 피격(DamageTaken) 방향 표시를 켜고 공격 방향의 월드 yaw를 기록한다. 수평 성분이 거의 없으면 무시한다.
        // 입력: fromDirection - 공격이 온 월드 방향, now - 현재 로컬 시간(초).
        // 출력: 반환값 없음. 피격 표시가 켜지고 숨길 시각이 기록된다.
        // DamageTaken: a bar around the crosshair pointing to where the shot came from.
        public void ShowDamage(Vector3 fromDirection, float now)
        {
            if (_root == null) return;
            if (fromDirection.x * fromDirection.x + fromDirection.z * fromDirection.z < 1e-6f) return;
            _damageYaw = Mathf.Atan2(fromDirection.x, fromDirection.z) * Mathf.Rad2Deg;
            _damageIndicator.gameObject.SetActive(true);
            _damageHideTime = now + DamageIndicatorSeconds;
        }

        // 기능: 사망 시 부활 카운트다운을 시작한다(표시용, 실제 부활 시각은 서버가 정한다).
        // 입력: now - 현재 로컬 시간(초).
        // 출력: 반환값 없음. 예상 부활 시각이 기록되고 카운트다운이 초기화된다.
        public void ShowDeath(float now)
        {
            _respawnAt = now + RespawnSeconds;
            _countdown = -1;
        }

        // 기능: 부활 카운트다운을 끝내고 중앙 줄을 비운다.
        // 입력: 없음.
        // 출력: 반환값 없음. 카운트다운 상태와 중앙 Text가 초기화된다.
        public void HideDeath()
        {
            _respawnAt = -1f;
            _countdown = -1;
            if (_center != null) _center.text = string.Empty;
        }

        // 기능: 매 프레임 히트 마커·피격 표시의 만료를 처리하고, 피격 표시를 카메라 기준 방향으로 돌리고, 부활 카운트다운 초가 바뀌면 중앙 줄을 갱신한다.
        // 입력: cameraYaw - 현재 카메라 yaw(도), now - 현재 로컬 시간(초).
        // 출력: 반환값 없음. 히트 마커·피격 표시·카운트다운 Text가 갱신된다.
        // Once per frame (LateUpdate, after the camera): timers, indicator direction, countdown.
        public void Tick(float cameraYaw, float now)
        {
            if (_root == null) return;
            if (_hitMarker.activeSelf && now >= _hitHideTime) _hitMarker.SetActive(false);

            if (_damageIndicator.gameObject.activeSelf)
            {
                if (now >= _damageHideTime)
                {
                    _damageIndicator.gameObject.SetActive(false);
                }
                else
                {
                    // Relative to where the camera looks: 0 = in front (top of the screen), 90 = right.
                    float relative = (_damageYaw - cameraYaw) * Mathf.Deg2Rad;
                    _damageIndicator.anchoredPosition = new Vector2(Mathf.Sin(relative), Mathf.Cos(relative)) * DamageIndicatorRadius;
                    _damageIndicator.localRotation = Quaternion.Euler(0f, 0f, -relative * Mathf.Rad2Deg);
                }
            }

            if (_respawnAt >= 0f)
            {
                int seconds = Mathf.Max(0, Mathf.CeilToInt(_respawnAt - now));
                if (seconds != _countdown)
                {
                    _countdown = seconds;
                    _center.text = "사망   " + seconds + "초 뒤 부활";
                }
            }
        }

        // 기능: HUD Canvas를 파괴한다.
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
            rect.sizeDelta = new Vector2(420f, 40f);

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

        // 기능: 스프라이트 없는 단색 사각형 Image를 부모 아래에 만든다.
        // 입력: parent - 부모 RectTransform, offset - 앵커 기준 위치, size - 크기, angle - Z축 회전 각도(도).
        // 출력: Raycast 대상이 아닌 Image.
        private static Image CreateBar(RectTransform parent, Vector2 offset, Vector2 size, float angle)
        {
            var go = new GameObject("Bar", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = size;
            rect.anchoredPosition = offset;
            rect.localRotation = Quaternion.Euler(0f, 0f, angle);
            var image = go.AddComponent<Image>();   // no sprite: draws a solid rectangle
            image.raycastTarget = false;
            return image;
        }
    }
}
