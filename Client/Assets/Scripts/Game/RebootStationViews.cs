using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Phase 14 D10, D14: a pillar on each Shared RebootStations point, made once (the built-in cylinder mesh, shared). Two
    // shared materials: ready (cyan) and cooling down (grey). A station is not a collider on the server, so the pillars
    // have none either (players walk through, shots pass). Apply changes materials only for stations whose bit changed.
    // Dispose destroys the objects and the materials.
    public sealed class RebootStationViews : System.IDisposable
    {
        private const float Radius = 0.5f;
        private const float Height = 2.4f;

        private readonly GameObject _root;
        private readonly Renderer[] _renderers = new Renderer[RebootStations.Count];
        private readonly Material _readyMaterial;
        private readonly Material _coolingMaterial;
        private byte _shownMask;

        // 기능: 스테이션 기둥 4개와 Material 두 개를 만든다(모두 사용 가능 색).
        // 입력: source - 복사할 Lit Material(LitMaterial로 고른 것).
        // 출력: 맵에 기둥이 놓인 객체(Dispose가 해제한다).
        public RebootStationViews(Material source)
        {
            _root = new GameObject("RebootStations");
            _readyMaterial = new Material(source) { color = new Color(0.3f, 0.9f, 0.95f) };
            _coolingMaterial = new Material(source) { color = new Color(0.45f, 0.45f, 0.48f) };
            var points = RebootStations.All;
            for (int i = 0; i < points.Length; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = "Reboot Station " + i;
                // Immediate: a deferred destroy would leave its collider for this frame's camera and aim rays.
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.transform.SetParent(_root.transform, false);
                // The cylinder mesh is 2 m tall around its middle.
                go.transform.localPosition = points[i].ToUnity() + new Vector3(0f, Height * 0.5f, 0f);
                go.transform.localScale = new Vector3(2f * Radius, Height * 0.5f, 2f * Radius);
                _renderers[i] = go.GetComponent<Renderer>();
                _renderers[i].sharedMaterial = _readyMaterial;
            }
        }

        // 기능: 서버의 대기 마스크를 기둥 색에 반영한다(바뀐 비트만).
        // 입력: state - 최신 RebootStations 상태(default = 모두 사용 가능).
        // 출력: 반환값 없음.
        public void Apply(in RebootStationsState state)
        {
            if (_root == null || state.CooldownMask == _shownMask) return;
            for (int i = 0; i < _renderers.Length; i++)
            {
                bool cooling = state.IsCoolingDown(i);
                if (cooling == ((_shownMask & (1 << i)) != 0)) continue;
                _renderers[i].sharedMaterial = cooling ? _coolingMaterial : _readyMaterial;
            }
            _shownMask = state.CooldownMask;
        }

        // 기능: 기둥(뿌리의 자식)과 Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_readyMaterial != null) Object.Destroy(_readyMaterial);
            if (_coolingMaterial != null) Object.Destroy(_coolingMaterial);
        }
    }
}
