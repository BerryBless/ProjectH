using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 13 D6, D7, D16 (request §18-§22): harvesting on screen, all presentation (the server decides hits and gains).
    //  - The weak point: a small marker where HarvestHit says it now is, until the target falls or a few seconds pass.
    //  - A hit: the marker flashes bigger on a weak point hit.
    //  - A fall: a puff (a cube that grows and vanishes) where a harvestable or a piece was.
    //  - The local swing: a tool head that sweeps in front of the player each swing interval while F's tool fires.
    // Created once, no colliders, nothing allocated per hit. Dispose destroys the objects and the materials.
    public sealed class HarvestEffects : System.IDisposable
    {
        public const float MarkerSeconds = 4f;
        private const float MarkerSize = 0.3f;
        private const float FlashSize = 0.55f;
        private const float FlashSeconds = 0.15f;
        private const float PuffSeconds = 0.4f;
        private const int PuffCount = 4;
        private const float SwingSeconds = 0.2f;

        private readonly GameObject _root;
        private readonly Material _markerMaterial;
        private readonly Material _puffMaterial;
        private readonly Transform _marker;
        private readonly Transform _tool;
        private readonly Transform[] _puffs = new Transform[PuffCount];
        private readonly float[] _puffStart = new float[PuffCount];
        private readonly float[] _puffSize = new float[PuffCount];
        private readonly RingCursor _nextPuff = new RingCursor(PuffCount);
        private int _markerTarget = -1;
        private float _markerHideAt;
        private float _flashUntil;
        private float _swingStart = -1f;
        private Vector3 _swingFeet;
        private float _swingYaw;

        public HarvestEffects(Material source)
        {
            _root = new GameObject("HarvestEffects");
            _markerMaterial = new Material(source) { color = new Color(1f, 0.85f, 0.1f) };
            _puffMaterial = new Material(source) { color = new Color(0.7f, 0.66f, 0.6f) };
            _marker = CreateCube("WeakPoint", _markerMaterial, MarkerSize);
            _tool = CreateCube("HarvestTool", _markerMaterial, 0.18f);
            for (int i = 0; i < PuffCount; i++) _puffs[i] = CreateCube("Puff", _puffMaterial, 1f);
        }

        public void OnHit(in HarvestHit hit, float now)
        {
            if (_root == null) return;
            if (hit.Destroyed)
            {
                if (hit.TargetId < GameMap.Harvestables.Length)
                {
                    Box bounds = GameMap.Harvestables[hit.TargetId].Bounds;
                    Puff(bounds.Center.ToUnity(), Mathf.Max(bounds.Size.X, Mathf.Max(bounds.Size.Y, bounds.Size.Z)), now);
                }
                if (_markerTarget == hit.TargetId) HideMarker();
                return;
            }
            if (!hit.HasWeakPoint)
            {
                if (_markerTarget == hit.TargetId) HideMarker();
                return;
            }
            _markerTarget = hit.TargetId;
            _markerHideAt = now + MarkerSeconds;
            _marker.position = hit.WeakPoint.ToUnity();
            if (hit.WeakPointHit) _flashUntil = now + FlashSeconds;
            _marker.gameObject.SetActive(true);
        }

        // A harvestable gone by another player's hit (HarvestStates) loses its marker too.
        public void OnStates(ulong destroyed)
        {
            if (_markerTarget >= 0 && (destroyed & (1UL << _markerTarget)) != 0) HideMarker();
        }

        public void Puff(Vector3 center, float size, float now)
        {
            if (_root == null) return;
            int i = _nextPuff.Next();
            _puffStart[i] = now;
            _puffSize[i] = Mathf.Clamp(size, 0.5f, 5f);
            _puffs[i].position = center;
            _puffs[i].gameObject.SetActive(true);
        }

        public void Swing(Vector3 feet, float yaw, float now)
        {
            if (_root == null) return;
            _swingStart = now;
            _swingFeet = feet;
            _swingYaw = yaw;
            _tool.gameObject.SetActive(true);
        }

        public void Tick(float now)
        {
            if (_root == null) return;
            if (_markerTarget >= 0)
            {
                if (now >= _markerHideAt) HideMarker();
                else
                {
                    float size = now < _flashUntil ? FlashSize : MarkerSize;
                    _marker.localScale = new Vector3(size, size, size);
                }
            }
            for (int i = 0; i < PuffCount; i++)
            {
                if (!_puffs[i].gameObject.activeSelf) continue;
                float t = (now - _puffStart[i]) / PuffSeconds;
                if (t >= 1f)
                {
                    _puffs[i].gameObject.SetActive(false);
                    continue;
                }
                float s = _puffSize[i] * (0.6f + 0.6f * t) * (1f - t);
                _puffs[i].localScale = new Vector3(s, s, s);
            }
            if (_swingStart >= 0f)
            {
                float t = (now - _swingStart) / SwingSeconds;
                if (t >= 1f)
                {
                    _swingStart = -1f;
                    _tool.gameObject.SetActive(false);
                    return;
                }
                // From up and right to down and centre, half a metre in front of the chest.
                float angle = Mathf.Lerp(60f, -30f, t);
                Quaternion facing = Quaternion.Euler(0f, _swingYaw, 0f);
                Vector3 local = new Vector3(0.3f * (1f - t), 1.3f + 0.6f * Mathf.Sin(angle * Mathf.Deg2Rad), 0.6f);
                _tool.position = _swingFeet + facing * local;
            }
        }

        public void HideAll()
        {
            if (_root == null) return;
            HideMarker();
            for (int i = 0; i < PuffCount; i++) _puffs[i].gameObject.SetActive(false);
            _swingStart = -1f;
            _tool.gameObject.SetActive(false);
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_markerMaterial != null) Object.Destroy(_markerMaterial);
            if (_puffMaterial != null) Object.Destroy(_puffMaterial);
        }

        private void HideMarker()
        {
            _markerTarget = -1;
            _marker.gameObject.SetActive(false);
        }

        private Transform CreateCube(string name, Material material, float size)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            Object.Destroy(cube.GetComponent<Collider>());   // effects never stop the camera, the aim or a shot
            var renderer = cube.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            cube.transform.SetParent(_root.transform, false);
            cube.transform.localScale = new Vector3(size, size, size);
            cube.SetActive(false);
            return cube.transform;
        }
    }
}
