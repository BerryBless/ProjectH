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
    //  - A fall: a dust puff where a harvestable or a piece was: a few small translucent cubes (Sprites/Default, like the
    //    build ghosts) that spread out, rise and shrink away in a short time.
    //  - The local swing: a tool head that sweeps in front of the player each swing interval while F's tool fires.
    // Created once, no colliders, nothing allocated per hit. Dispose destroys the objects and the materials.
    public sealed class HarvestEffects : System.IDisposable
    {
        public const float MarkerSeconds = 4f;
        private const float MarkerSize = 0.3f;
        private const float FlashSize = 0.55f;
        private const float FlashSeconds = 0.15f;
        private const float PuffSeconds = 0.35f;
        private const int PuffCount = 4;
        // Each puff is a few motes spread around its centre (fixed offsets: no randomness, nothing allocated).
        private const int MotesPerPuff = 5;
        private static readonly Vector3[] MoteOffsets =
        {
            new Vector3(0f, 0.1f, 0f), new Vector3(0.35f, 0f, 0.15f), new Vector3(-0.3f, 0.05f, 0.3f), new Vector3(0.1f, -0.05f, -0.35f),
            new Vector3(-0.25f, 0.2f, -0.2f),
        };
        private const float SwingSeconds = 0.2f;

        private readonly GameObject _root;
        private readonly Material _markerMaterial;
        private readonly Material _puffMaterial;
        private readonly Transform _marker;
        private readonly Transform _tool;
        private readonly Transform[] _puffs = new Transform[PuffCount];
        private readonly Transform[] _motes = new Transform[PuffCount * MotesPerPuff];
        private readonly Vector3[] _puffCentre = new Vector3[PuffCount];
        private readonly float[] _puffStart = new float[PuffCount];
        private readonly float[] _puffSize = new float[PuffCount];
        private readonly RingCursor _nextPuff = new RingCursor(PuffCount);
        private int _markerTarget = -1;
        private float _markerHideAt;
        private float _flashUntil;
        private float _swingStart = -1f;
        private Vector3 _swingFeet;
        private float _swingYaw;

        // 기능: 약점 표시, 도구 머리, 먼지 Puff 큐브와 그 Material을 한 번 만든다.
        // 입력: source - 복제할 기본 Material.
        // 출력: 모든 효과가 숨겨진 HarvestEffects.
        public HarvestEffects(Material source)
        {
            _root = new GameObject("HarvestEffects");
            _markerMaterial = new Material(source) { color = new Color(1f, 0.85f, 0.1f) };
            // Dust: translucent where Sprites/Default is in the build (the ghosts' shader), else the opaque fallback.
            Shader sprite = Shader.Find("Sprites/Default");
            _puffMaterial = sprite != null ? new Material(sprite) { color = new Color(0.75f, 0.7f, 0.62f, 0.35f) }
                : new Material(source) { color = new Color(0.7f, 0.66f, 0.6f) };
            _marker = CreateCube("WeakPoint", _markerMaterial, MarkerSize);
            _tool = CreateCube("HarvestTool", _markerMaterial, 0.18f);
            for (int i = 0; i < PuffCount; i++)
            {
                _puffs[i] = new GameObject("Puff").transform;
                _puffs[i].SetParent(_root.transform, false);
                _puffs[i].gameObject.SetActive(false);
                for (int k = 0; k < MotesPerPuff; k++)
                {
                    Transform mote = CreateCube("Mote", _puffMaterial, 1f);
                    mote.SetParent(_puffs[i], false);
                    mote.gameObject.SetActive(true);
                    _motes[i * MotesPerPuff + k] = mote;
                }
            }
        }

        // 기능: Server HarvestHit에 따라 약점 표시를 옮기거나 키우고, 대상이 쓰러지면 먼지를 띄운다.
        // 입력: hit - 수신한 채집 타격 결과, now - 현재 시간(초).
        // 출력: 반환값 없음. 약점 Marker와 먼지 Puff 상태가 갱신된다.
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

        // 기능: 파괴 Bit Mask에 약점 표시 대상이 있으면 표시를 숨긴다.
        // 입력: destroyed - 파괴된 채집 대상 Bit Mask.
        // 출력: 반환값 없음. 대상이 파괴되었으면 Marker가 숨겨진다.
        // A harvestable gone by another player's hit (HarvestStates) loses its marker too.
        public void OnStates(ulong destroyed)
        {
            if (_markerTarget >= 0 && _markerTarget < 64 && (destroyed & (1UL << _markerTarget)) != 0) HideMarker();
        }

        // 기능: 지정 위치에 먼지 Puff 하나를 시작한다(슬롯을 순환 재사용).
        // 입력: center - 먼지 중심, size - 대상 크기(0.5~5로 제한), now - 현재 시간(초).
        // 출력: 반환값 없음. Puff 슬롯 하나가 활성화된다.
        public void Puff(Vector3 center, float size, float now)
        {
            if (_root == null) return;
            int i = _nextPuff.Next();
            _puffStart[i] = now;
            _puffSize[i] = Mathf.Clamp(size, 0.5f, 5f);
            _puffCentre[i] = center;
            _puffs[i].position = center;
            _puffs[i].gameObject.SetActive(true);
        }

        // 기능: 로컬 플레이어의 채집 휘두르기 효과를 시작한다.
        // 입력: feet - 발 위치, yaw - 수평 시선 각도(도), now - 현재 시간(초).
        // 출력: 반환값 없음. 도구 머리가 활성화된다.
        public void Swing(Vector3 feet, float yaw, float now)
        {
            if (_root == null) return;
            _swingStart = now;
            _swingFeet = feet;
            _swingYaw = yaw;
            _tool.gameObject.SetActive(true);
        }

        // 기능: 약점 표시 크기·만료, 먼지 Puff 퍼짐·소멸, 휘두르기 위치를 진행한다.
        // 입력: now - 현재 시간(초).
        // 출력: 반환값 없음. 효과 Transform과 활성 상태가 갱신된다.
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
                // Motes spread out to the puff's size and rise a little while they shrink to nothing.
                float spread = _puffSize[i] * (0.4f + 0.6f * t);
                float s = Mathf.Min(0.35f, _puffSize[i] * 0.15f) * (1f - t);
                _puffs[i].position = _puffCentre[i] + new Vector3(0f, 0.6f * t, 0f);
                for (int k = 0; k < MotesPerPuff; k++)
                {
                    Transform mote = _motes[i * MotesPerPuff + k];
                    mote.localPosition = MoteOffsets[k] * spread;
                    mote.localScale = new Vector3(s, s, s);
                }
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

        // 기능: 약점 표시, 먼지, 도구 머리를 모두 숨긴다.
        // 입력: 없음.
        // 출력: 반환값 없음. 모든 효과가 비활성화된다.
        public void HideAll()
        {
            if (_root == null) return;
            HideMarker();
            for (int i = 0; i < PuffCount; i++) _puffs[i].gameObject.SetActive(false);
            _swingStart = -1f;
            _tool.gameObject.SetActive(false);
        }

        // 기능: 효과 GameObject와 생성한 Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 루트 GameObject와 Material이 파괴된다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_markerMaterial != null) Object.Destroy(_markerMaterial);
            if (_puffMaterial != null) Object.Destroy(_puffMaterial);
        }

        // 기능: 약점 표시를 숨기고 대상을 비운다.
        // 입력: 없음.
        // 출력: 반환값 없음. Marker가 비활성화된다.
        private void HideMarker()
        {
            _markerTarget = -1;
            _marker.gameObject.SetActive(false);
        }

        // 기능: Collider를 제거한 큐브를 루트 아래에 비활성 상태로 만든다.
        // 입력: name - GameObject 이름, material - 적용할 Material, size - 한 변 크기.
        // 출력: 만든 큐브의 Transform.
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
