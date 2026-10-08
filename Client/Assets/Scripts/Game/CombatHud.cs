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
        // Phase 19 D11: the vehicle line at the bottom centre while seated: the speed and the vehicle health bar.
        private const float VehicleBarWidth = 260f;
        private const float VehicleBarHeight = 10f;

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
        private readonly GameObject _vehicleRoot;
        private readonly Text _vehicleSpeed;
        private readonly RectTransform _vehicleFill;
        private readonly Image _vehicleFillImage;

        private bool _visible;
        private int _health = -1;
        private int _shield = -1;
        private string _weaponName;
        private string _ammoName;
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
        private int _vehicleKmh = -1;
        private int _vehiclePixels = -1;
        private bool _vehicleLow;

        // 기능: HUD 캔버스와 줄·막대·표시를 만든다(Phase 19: 차량 속도·체력 줄 포함, 모두 숨긴 채).
        // 입력: 없음.
        // 출력: 숨겨진 HUD(Dispose가 캔버스를 파괴한다).
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

            // Phase 19 D11: the vehicle line (hidden until seated).
            _vehicleRoot = new GameObject("Vehicle", typeof(RectTransform));
            var vehicleRect = (RectTransform)_vehicleRoot.transform;
            vehicleRect.SetParent(_root.transform, false);
            vehicleRect.anchorMin = new Vector2(0.5f, 0f);
            vehicleRect.anchorMax = new Vector2(0.5f, 0f);
            vehicleRect.pivot = new Vector2(0.5f, 0f);
            vehicleRect.anchoredPosition = new Vector2(0f, 60f);
            vehicleRect.sizeDelta = new Vector2(VehicleBarWidth, 50f);
            _vehicleSpeed = CreateText("Speed", font, new Vector2(0.5f, 0.5f), new Vector2(0f, 14f), TextAnchor.MiddleCenter);
            _vehicleSpeed.rectTransform.SetParent(vehicleRect, false);
            Image vehicleBack = CreateBar(vehicleRect, Vector2.zero, new Vector2(VehicleBarWidth, VehicleBarHeight), 0f);
            vehicleBack.color = new Color(0f, 0f, 0f, 0.5f);
            var vehicleBackRect = vehicleBack.rectTransform;
            vehicleBackRect.anchorMin = new Vector2(0f, 0f);
            vehicleBackRect.anchorMax = new Vector2(0f, 0f);
            vehicleBackRect.pivot = Vector2.zero;
            vehicleBackRect.anchoredPosition = Vector2.zero;
            _vehicleFillImage = CreateBar(vehicleBackRect, Vector2.zero, new Vector2(VehicleBarWidth, VehicleBarHeight), 0f);
            _vehicleFill = _vehicleFillImage.rectTransform;
            _vehicleFill.anchorMin = Vector2.zero;
            _vehicleFill.anchorMax = Vector2.zero;
            _vehicleFill.pivot = Vector2.zero;
            _vehicleFill.anchoredPosition = Vector2.zero;
            _vehicleFillImage.color = new Color(0.45f, 0.9f, 0.45f);
            _vehicleRoot.SetActive(false);

            _root.SetActive(false);
        }

        // 기능: 탄 동안의 차량 줄을 보이거나 숨긴다(Phase 19 D11: 속도 km/h와 차량 체력 막대, 30 % 아래면 빨강). 보이는 값이 바뀔 때만
        //   문자열·크기를 고친다.
        // 입력: visible - 앉아 있는지, speed - 차량 속도(m/s, 부호 무시), healthFraction - 체력 비율(0..1).
        // 출력: 반환값 없음.
        public void SetVehicle(bool visible, float speed, float healthFraction)
        {
            if (_root == null) return;
            if (_vehicleRoot.activeSelf != visible) _vehicleRoot.SetActive(visible);
            if (!visible) return;
            int kmh = Mathf.RoundToInt(Mathf.Abs(speed) * 3.6f);
            if (kmh != _vehicleKmh)
            {
                _vehicleKmh = kmh;
                _vehicleSpeed.text = UiText.VehicleSpeed(kmh);
            }
            float fraction = Mathf.Clamp01(healthFraction);
            int pixels = Mathf.RoundToInt(fraction * VehicleBarWidth);
            if (pixels != _vehiclePixels)
            {
                _vehiclePixels = pixels;
                _vehicleFill.sizeDelta = new Vector2(pixels, VehicleBarHeight);
            }
            bool low = fraction < VehicleViews.SmokeHealthFraction;
            if (low != _vehicleLow)
            {
                _vehicleLow = low;
                _vehicleFillImage.color = low ? new Color(1f, 0.3f, 0.25f) : new Color(0.45f, 0.9f, 0.45f);
            }
        }

        public void SetVisible(bool visible)
        {
            // Unity null: the root can be destroyed on teardown before the owner's OnDestroy runs this.
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        public void SetVitals(int health, int shield)
        {
            if (_root == null || (health == _health && shield == _shield)) return;
            _health = health;
            _shield = shield;
            _vitals.text = "체력 " + health + "   실드 " + shield;
        }

        // Phase 4: magazine / reserve rounds of the weapon's ammo type.
        // 기능: 무기 줄("Vesper AR   30 / 120  Medium Rounds")을 보이는 값이 바뀔 때만 다시 만든다(Phase 17: 탄 종류 이름 포함).
        // 입력: name - 카탈로그 무기 이름, ammo - 탄창, reserve - 그 탄 종류의 예비탄, reloading - 재장전 중, ammoName - 탄 종류 이름
        //   (아이템 카탈로그 문자열, 없으면 null). 이름은 참조로 비교한다(입장 때 한 번 받는 문자열).
        // 출력: 반환값 없음.
        public void SetWeapon(string name, int ammo, int reserve, bool reloading, string ammoName = null)
        {
            if (_root == null) return;
            if (ReferenceEquals(name, _weaponName) && ammo == _ammo && reserve == _reserve && reloading == _reloading &&
                ReferenceEquals(ammoName, _ammoName))
                return;
            _weaponName = name;
            _ammo = ammo;
            _reserve = reserve;
            _reloading = reloading;
            _ammoName = ammoName;
            string line = reloading ? name + "   재장전 중..." : name + "   " + ammo + " / " + reserve;
            _weapon.text = ammoName == null ? line : line + "  " + ammoName;
        }

        public void ClearWeapon()
        {
            if (_root == null || _weaponName == null) return;
            _weaponName = null;
            _weapon.text = string.Empty;
        }

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

        // Phase 12 D14: one of UiText's constant hints, or null. Set only when the reference changes.
        public void SetHint(string hint)
        {
            if (_root == null || ReferenceEquals(hint, _hintText)) return;
            _hintText = hint;
            _hint.text = hint ?? string.Empty;
        }

        // HitConfirmed: white cross, red on a kill.
        public void ShowHit(bool killed, float now)
        {
            if (_root == null) return;
            Color color = killed ? new Color(1f, 0.2f, 0.2f) : Color.white;
            for (int i = 0; i < _hitBars.Length; i++) _hitBars[i].color = color;
            _hitMarker.SetActive(true);
            _hitHideTime = now + HitMarkerSeconds;
        }

        // DamageTaken: a bar around the crosshair pointing to where the shot came from.
        public void ShowDamage(Vector3 fromDirection, float now)
        {
            if (_root == null) return;
            if (fromDirection.x * fromDirection.x + fromDirection.z * fromDirection.z < 1e-6f) return;
            _damageYaw = Mathf.Atan2(fromDirection.x, fromDirection.z) * Mathf.Rad2Deg;
            _damageIndicator.gameObject.SetActive(true);
            _damageHideTime = now + DamageIndicatorSeconds;
        }

        public void ShowDeath(float now)
        {
            _respawnAt = now + RespawnSeconds;
            _countdown = -1;
        }

        public void HideDeath()
        {
            _respawnAt = -1f;
            _countdown = -1;
            if (_center != null) _center.text = string.Empty;
        }

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
