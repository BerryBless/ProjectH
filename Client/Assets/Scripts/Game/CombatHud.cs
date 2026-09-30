using UnityEngine;
using UnityEngine.UI;

namespace ProjectH.Client.Game
{
    // D13: combat HUD built in code on one Screen Space Overlay canvas (UGUI legacy Text, built-in font; no
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

        private readonly GameObject _root;
        private readonly Text _vitals;
        private readonly Text _weapon;
        private readonly Text _center;
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

        public CombatHud()
        {
            _root = new GameObject("CombatHud");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 90;   // under the crosshair (100)

            // Unity 6 built-in font; Arial.ttf is no longer a built-in resource.
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _vitals = CreateText("Vitals", font, new Vector2(0f, 0f), new Vector2(20f, 20f), TextAnchor.LowerLeft);
            _weapon = CreateText("Weapon", font, new Vector2(1f, 0f), new Vector2(-20f, 20f), TextAnchor.LowerRight);
            _center = CreateText("Center", font, new Vector2(0.5f, 0.5f), new Vector2(0f, -80f), TextAnchor.MiddleCenter);

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
            _vitals.text = "HP " + health + "   SH " + shield;
        }

        // Phase 4: magazine / reserve rounds of the weapon's ammo type.
        public void SetWeapon(string name, int ammo, int reserve, bool reloading)
        {
            if (_root == null) return;
            if (ReferenceEquals(name, _weaponName) && ammo == _ammo && reserve == _reserve && reloading == _reloading) return;
            _weaponName = name;
            _ammo = ammo;
            _reserve = reserve;
            _reloading = reloading;
            _weapon.text = reloading ? name + "   reloading..." : name + "   " + ammo + " / " + reserve;
        }

        public void ClearWeapon()
        {
            if (_root == null || _weaponName == null) return;
            _weaponName = null;
            _weapon.text = string.Empty;
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
                    _center.text = "DEAD   respawn in " + seconds;
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
