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

        // 기능: 수송기 박스 GameObject와 전용 Material을 만든다(Collider 제거).
        // 입력: 없음.
        // 출력: 경로 없이 숨겨진 TransportView.
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

        // 기능: 수송기 경로를 저장하고 진행 방향으로 회전시킨다.
        // 입력: route - 서버가 정한 수송기 경로.
        // 출력: 반환값 없음. 경로가 기록되고 회전이 바뀐다. 표시와 위치는 다음 Tick에서 갱신된다.
        public void SetRoute(in DropRoute route)
        {
            _route = route;
            _hasRoute = true;
            var direction = new Vector3(route.EndX - route.StartX, 0f, route.EndZ - route.StartZ);
            if (direction.sqrMagnitude > 1e-6f) _root.transform.rotation = Quaternion.LookRotation(direction);
        }

        // 기능: 경로를 지우고 수송기를 숨긴다.
        // 입력: 없음.
        // 출력: 반환값 없음. 경로가 없어지고 수송기가 비활성화된다.
        public void Clear()
        {
            _hasRoute = false;
            SetVisible(false);
        }

        // 기능: 매 프레임 렌더 Tick이 경로 비행 구간이면 수송기를 경로 위치에 놓고, 아니면 숨긴다.
        // 입력: renderTick - 수송기를 그릴 서버 Tick(소수). 보통 보간 Render Tick, 로컬 플레이어 탑승 중에는 예측 Render Tick(첫 ack 전에는 최신 Snapshot Tick).
        // 출력: 반환값 없음. 수송기 표시 여부와 위치가 갱신된다.
        // renderTick: the server tick the transport is drawn at this frame (the world's render tick, or our rider's tick
        // while riding, so the transport and the rider move together).
        public void Tick(double renderTick)
        {
            if (_root == null) return;
            bool flying = _hasRoute && renderTick >= _route.StartTick && renderTick <= _route.EndTick;
            SetVisible(flying);
            if (!flying) return;
            System.Numerics.Vector3 at = _route.PositionAt(renderTick);
            _root.transform.position = new Vector3(at.X, at.Y + Lift, at.Z);
        }

        // 기능: 수송기 표시 여부를 바꾼다.
        // 입력: visible - 표시 여부.
        // 출력: 반환값 없음. 값이 바뀐 경우에만 GameObject가 켜지거나 꺼진다.
        private void SetVisible(bool visible)
        {
            if (_root == null || visible == _visible) return;
            _visible = visible;
            _root.SetActive(visible);
        }

        // 기능: 수송기 GameObject와 Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 생성한 오브젝트와 Material이 해제된다.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_material != null) Object.Destroy(_material);
        }
    }
}
