using ProjectH.Shared.Protocol;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 14 D14 (request §33): a small diamond over each teammate in play: green while up, red while downed. A fixed pool
    // of Capacity (a team of 4 has 3 others), made once and reused until Dispose; two shared materials (sharedMaterial
    // only) and the built-in cube mesh. No colliders: markers never block the camera or the aim ray. Per frame only the
    // position and rotation of the shown markers change; the material only when the downed state does.
    public sealed class TeammateMarkers : System.IDisposable
    {
        public const int Capacity = SquadConstants.MaxTeamSize - 1;
        private const float Lift = 0.5f;   // above the drawn body
        private static readonly Vector3 Size = new Vector3(0.3f, 0.3f, 0.06f);

        private readonly GameObject _root;
        private readonly Transform[] _markers = new Transform[Capacity];
        private readonly Renderer[] _renderers = new Renderer[Capacity];
        private readonly bool[] _downed = new bool[Capacity];
        private readonly Material _upMaterial;
        private readonly Material _downedMaterial;
        private int _shown;

        // 기능: 표지 풀과 Material 두 개를 만든다(모두 숨긴 채).
        // 입력: source - 복사할 Lit Material(LitMaterial로 고른 것).
        // 출력: 숨겨진 표지 Capacity개를 가진 객체(Dispose가 해제한다).
        public TeammateMarkers(Material source)
        {
            _root = new GameObject("TeammateMarkers");
            _upMaterial = new Material(source) { color = new Color(0.3f, 0.95f, 0.4f) };
            _downedMaterial = new Material(source) { color = new Color(1f, 0.2f, 0.2f) };
            for (int i = 0; i < Capacity; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Marker " + i;
                // Immediate: a deferred destroy would leave its collider for this frame's camera and aim rays.
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.transform.SetParent(_root.transform, false);
                go.transform.localScale = Size;
                var renderer = go.GetComponent<Renderer>();
                renderer.sharedMaterial = _upMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                go.SetActive(false);
                _markers[i] = go.transform;
                _renderers[i] = renderer;
            }
        }

        // 기능: index번째 표지를 팀원의 머리 위에 놓고 보인다. 화면을 향하는 마름모(Z축 45도)로 돌린다.
        // 입력: index - 0..Capacity-1(범위 밖이면 무시), feet - 팀원이 그려지는 발, bodyHeight - 그려지는 몸 높이,
        //   downed - 기절했는지(빨강), cameraYaw - 카메라 방향(도).
        // 출력: 반환값 없음. 할당 없음.
        public void Show(int index, Vector3 feet, float bodyHeight, bool downed, float cameraYaw)
        {
            if (_root == null || index < 0 || index >= Capacity) return;
            Transform marker = _markers[index];
            if (!marker.gameObject.activeSelf) marker.gameObject.SetActive(true);
            marker.SetPositionAndRotation(feet + new Vector3(0f, bodyHeight + Lift, 0f), Quaternion.Euler(0f, cameraYaw, 45f));
            if (downed != _downed[index])
            {
                _downed[index] = downed;
                _renderers[index].sharedMaterial = downed ? _downedMaterial : _upMaterial;
            }
            if (index >= _shown) _shown = index + 1;
        }

        // 기능: index번째부터 끝까지의 표지를 숨긴다(이번 프레임에 보인 표지 다음부터).
        // 입력: index - 첫 숨길 표지.
        // 출력: 반환값 없음.
        public void HideFrom(int index)
        {
            if (_root == null) return;
            if (index < 0) index = 0;
            for (int i = index; i < _shown; i++) _markers[i].gameObject.SetActive(false);
            if (index < _shown) _shown = index;
        }

        // 기능: 표지(뿌리의 자식)와 Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_upMaterial != null) Object.Destroy(_upMaterial);
            if (_downedMaterial != null) Object.Destroy(_downedMaterial);
        }
    }
}
