using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 12 D14: the drop transport, one long box flying the route (DropRoute.PositionAt at the render tick, the same
    // formula the server places riders with). Shown from the route's start tick to its end tick. Built once; Tick only
    // moves it. No collider: nothing collides with it. Dispose destroys the object and its material.
    public sealed class TransportView : System.IDisposable
    {
        private static readonly Vector3 Size = new Vector3(4f, 2f, 14f);
        private const float Lift = 2.5f;   // drawn above the riders' feet, so a rider's camera sits under its belly

        private readonly GameObject _root;
        private readonly Material _material;
        private bool _hasRoute;
        private DropRoute _route;
        private bool _visible;

        // 기능: 수송기 상자(충돌체 없음)와 그 Material을 만든다(숨긴 채).
        // 입력: 없음.
        // 출력: 경로가 없고 숨겨진 TransportView(Dispose가 해제한다).
        public TransportView()
        {
            _root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _root.name = "DropTransport";
            Object.DestroyImmediate(_root.GetComponent<Collider>());
            _root.transform.localScale = Size;
            var renderer = _root.GetComponent<Renderer>();
            _material = new Material(LitMaterial.Source(renderer.sharedMaterial)) { color = new Color(0.25f, 0.28f, 0.32f) };
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            _root.SetActive(false);
        }

        // 기능: 이번 경기의 수송기 경로를 받아 기억하고 상자를 진행 방향으로 돌린다.
        // 입력: route - 서버가 보낸 수송기 경로.
        // 출력: 반환값 없음. 다음 Tick부터 경로 구간 안에서 보인다.
        public void SetRoute(in DropRoute route)
        {
            _route = route;
            _hasRoute = true;
            var direction = new Vector3(route.EndX - route.StartX, 0f, route.EndZ - route.StartZ);
            if (direction.sqrMagnitude > 1e-6f) _root.transform.rotation = Quaternion.LookRotation(direction);
        }

        // 기능: 경로를 잊고 상자를 숨긴다(끊김·경기 종료).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Clear()
        {
            _hasRoute = false;
            SetVisible(false);
        }

        // 기능: 렌더 Tick이 경로의 시작~끝 Tick 안이면 상자를 보이고 DropRoute.PositionAt 위치(Lift만큼 위)에 놓는다. 밖이면 숨긴다.
        // 입력: renderTick - 이번 프레임 세계를 그리는 서버 Tick.
        // 출력: 반환값 없음. 상자의 표시 여부와 위치가 바뀐다.
        public void Tick(double renderTick)
        {
            if (_root == null) return;
            bool flying = _hasRoute && renderTick >= _route.StartTick && renderTick <= _route.EndTick;
            SetVisible(flying);
            if (!flying) return;
            System.Numerics.Vector3 at = _route.PositionAt(renderTick);
            _root.transform.position = new Vector3(at.X, at.Y + Lift, at.Z);
        }

        // 기능: 상자를 보이거나 숨긴다(값이 바뀔 때만 SetActive, 파괴됐으면 무시).
        // 입력: visible - 보일지.
        // 출력: 반환값 없음.
        private void SetVisible(bool visible)
        {
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 상자와 Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_material != null) Object.Destroy(_material);
        }
    }
}
