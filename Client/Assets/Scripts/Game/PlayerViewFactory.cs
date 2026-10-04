using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // One character on screen (Phase 12 D14). The root sits at the feet, never rotates and carries the remote hit box,
    // so the box stays axis-aligned like the server's AABB (D7) at the mode's height (D13). The capsule child shows the
    // pose (PlayerPose: height, lean, lying down), a flat wing child the glider. Transforms change only when the pose
    // does, besides the per-frame position and facing. Created on spawn and destroyed on despawn.
    public sealed class PlayerView
    {
        private const float WingLift = 0.3f;

        private readonly bool _isLocal;
        private readonly Transform _body;
        private readonly Renderer _bodyRenderer;
        private readonly GameObject _wings;
        private readonly Transform _wingsTransform;
        private readonly BoxCollider _collider;   // null for the local player
        private bool _alive = true;
        private bool _hidden;
        private PlayerPose _pose;
        private bool _hasPose;

        // 기능: 팩토리가 만든 루트·몸체·글라이더·히트 박스로 캐릭터 뷰를 구성한다.
        // 입력: root - 발 위치에 놓이는 회전하지 않는 루트, body - 자세를 표시하는 캡슐, wings - 글라이더 날개 오브젝트, collider - 원격 히트 박스(로컬 플레이어는 null), isLocal - 로컬 플레이어 뷰 여부.
        // 출력: 살아 있고 자세가 아직 적용되지 않은 PlayerView.
        internal PlayerView(Transform root, Transform body, GameObject wings, BoxCollider collider, bool isLocal)
        {
            Root = root;
            _body = body;
            _bodyRenderer = body.GetComponent<Renderer>();
            _wings = wings;
            _wingsTransform = wings.transform;
            _collider = collider;
            _isLocal = isLocal;
        }

        public Transform Root { get; }

        // 기능: 생존 여부를 바꾸고 몸체 Material을 생존/사망 색으로 교체한다.
        // 입력: alive - 새 생존 여부.
        // 출력: 반환값 없음. 값이 바뀌면 공유 Material이 교체되고 다음 Place에서 자세와 히트 박스가 다시 적용된다.
        // D13 (Phase 3): dead players are grey and lying down, and a dead remote player's hit box is off.
        public void SetAlive(bool alive)
        {
            if (alive == _alive) return;
            _alive = alive;
            _bodyRenderer.sharedMaterial = PlayerViewFactory.BodyMaterial(_isLocal, alive);
            _hasPose = false;   // the pose depends on it
        }

        // 기능: 매 프레임 캐릭터를 발 위치에 놓고 방향과 이동 모드의 자세를 적용한다.
        // 입력: feet - 그릴 발 위치, yaw - 바라보는 방향(도), mode - 이동 모드, sprinting - 달리기 여부.
        // 출력: 반환값 없음. 루트 위치와 몸체·날개 회전이 갱신되고, 자세가 바뀐 경우에만 모양과 히트 박스가 다시 적용된다.
        // Every frame: where the feet are drawn, the facing, and the mode's pose.
        public void Place(Vector3 feet, float yaw, MovementMode mode, bool sprinting)
        {
            Root.position = feet;
            PlayerPose pose = PlayerPose.For(mode, sprinting, _alive);
            if (!_hasPose || pose.BodyHeight != _pose.BodyHeight || pose.Prone != _pose.Prone || pose.Wings != _pose.Wings ||
                pose.Hidden != _pose.Hidden || pose.HitHeight != _pose.HitHeight)
            {
                ApplyShape(pose);
            }
            _pose = pose;
            _hasPose = true;
            float pitch = pose.Prone ? 90f : pose.Lean;
            _body.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            if (pose.Wings) _wingsTransform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        }

        // 기능: 자세에 맞게 몸체 높이·위치, 숨김, 날개 표시, 히트 박스 크기를 적용한다.
        // 입력: pose - 적용할 자세.
        // 출력: 반환값 없음. 몸체·날개 Transform과 Renderer·Collider 상태가 바뀐다.
        private void ApplyShape(in PlayerPose pose)
        {
            float half = pose.BodyHeight * 0.5f;
            // Lying down, the capsule's middle is half its radius (0.5 m) above the feet.
            _body.localPosition = new Vector3(0f, pose.Prone ? 0.5f : half, 0f);
            _body.localScale = new Vector3(1f, pose.Prone ? 1f : half, 1f);
            if (_hidden != pose.Hidden)
            {
                _hidden = pose.Hidden;
                _bodyRenderer.enabled = !pose.Hidden;
            }
            if (_wings.activeSelf != pose.Wings) _wings.SetActive(pose.Wings);
            _wingsTransform.localPosition = new Vector3(0f, pose.BodyHeight + WingLift, 0f);
            if (_collider != null)
            {
                _collider.size = new Vector3(2f * MoveSettings.HalfWidth, pose.HitHeight, 2f * MoveSettings.HalfWidth);
                _collider.center = new Vector3(0f, pose.HitHeight * 0.5f, 0f);
                _collider.enabled = _alive && !pose.Hidden;
            }
        }

        // 기능: 뷰의 루트 GameObject를 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 루트와 자식(몸체·날개)이 파괴된다. 공유 Material은 파괴하지 않는다.
        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root.gameObject);
        }
    }

    // Views are created on spawn and destroyed on despawn (rare events), so no pooling.
    // Materials are created once per color and shared through sharedMaterial: never use
    // renderer.material, which silently clones a material per object.
    public static class PlayerViewFactory
    {
        // Built-in "Ignore Raycast" layer. Remote views keep a collider there so the crosshair ray can land on a
        // player (the aim point must be on the target, D2). The camera SphereCast uses DefaultRaycastLayers, which
        // excludes this layer, so other players never push the camera; aim and fire rays add it via AimRaycastMask.
        public const int RemoteHitLayer = 2;
        public const int AimRaycastMask = Physics.DefaultRaycastLayers | (1 << RemoteHitLayer);

        private static readonly Vector3 WingSize = new Vector3(3f, 0.08f, 1f);

        private static Material _localMaterial;
        private static Material _remoteMaterial;
        private static Material _deadMaterial;
        private static Material _wingMaterial;

        // 기능: 캐릭터 뷰(루트, 캡슐 몸체, 글라이더 날개, 원격이면 히트 박스)를 만든다.
        // 입력: name - GameObject 이름, isLocal - 로컬 플레이어 여부(원격만 BoxCollider와 RemoteHitLayer를 가진다).
        // 출력: 원점에 Ground 자세로 놓인 새 PlayerView.
        public static PlayerView Create(string name, bool isLocal)
        {
            var root = new GameObject(name);
            BoxCollider collider = null;
            if (!isLocal)
            {
                // D7: the server's hit box is the movement AABB, so the aim ray uses the same box (on the root, which never
                // rotates) instead of the primitive's capsule, which would miss the corners. The local view has none: the
                // aim ray starts next to it and must never hit it.
                collider = root.AddComponent<BoxCollider>();
                root.layer = RemoteHitLayer;
            }

            // DestroyImmediate, not Destroy: a deferred destroy would leave the primitives' colliders for this frame's
            // camera and aim rays.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(root.transform, false);
            var bodyRenderer = body.GetComponent<Renderer>();
            EnsureMaterials(LitMaterial.Source(bodyRenderer.sharedMaterial));
            bodyRenderer.sharedMaterial = isLocal ? _localMaterial : _remoteMaterial;

            var wings = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wings.name = "Glider";
            Object.DestroyImmediate(wings.GetComponent<Collider>());
            wings.transform.SetParent(root.transform, false);
            wings.transform.localScale = WingSize;
            wings.GetComponent<Renderer>().sharedMaterial = _wingMaterial;
            wings.SetActive(false);

            var view = new PlayerView(root.transform, body.transform, wings, collider, isLocal);
            view.Place(root.transform.position, 0f, MovementMode.Ground, false);
            return view;
        }

        // 기능: 로컬/원격과 생존 여부에 맞는 공유 몸체 Material을 고른다.
        // 입력: isLocal - 로컬 플레이어 여부, alive - 생존 여부.
        // 출력: 생존이면 로컬/원격 색 Material, 사망이면 회색 Material.
        internal static Material BodyMaterial(bool isLocal, bool alive) =>
            alive ? (isLocal ? _localMaterial : _remoteMaterial) : _deadMaterial;

        // 기능: 캐시한 공유 Material을 모두 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 캐시가 null로 비워지고 다음 Create에서 다시 만들어진다.
        // Called by GameClient.OnDestroy: the cached materials live exactly as long as the client.
        public static void ReleaseMaterials()
        {
            if (_localMaterial != null) Object.Destroy(_localMaterial);
            if (_remoteMaterial != null) Object.Destroy(_remoteMaterial);
            if (_deadMaterial != null) Object.Destroy(_deadMaterial);
            if (_wingMaterial != null) Object.Destroy(_wingMaterial);
            _localMaterial = null;
            _remoteMaterial = null;
            _deadMaterial = null;
            _wingMaterial = null;
        }

        // 기능: 아직 없거나 파괴된 공유 Material을 템플릿으로부터 만든다.
        // 입력: template - 복사할 URP Lit Material.
        // 출력: 반환값 없음. 비어 있던 Material 캐시가 채워진다.
        private static void EnsureMaterials(Material template)
        {
            // Explicit == null (not ??=): Unity's null check also catches destroyed materials.
            if (_localMaterial == null) _localMaterial = Tinted(template, new Color(0.2f, 0.6f, 1f));
            if (_remoteMaterial == null) _remoteMaterial = Tinted(template, new Color(1f, 0.45f, 0.2f));
            if (_deadMaterial == null) _deadMaterial = Tinted(template, new Color(0.45f, 0.45f, 0.45f));
            if (_wingMaterial == null) _wingMaterial = Tinted(template, new Color(0.95f, 0.85f, 0.25f));
        }

        // 기능: 템플릿을 복사해 지정 색의 새 Material을 만든다.
        // 입력: template - 복사할 Material, color - 적용할 색.
        // 출력: 새로 만든 Material(ReleaseMaterials에서 파괴).
        // Copies the Lit material (LitMaterial), so the shader is guaranteed to be in the build.
        private static Material Tinted(Material template, Color color)
        {
            return new Material(template) { color = color };
        }
    }
}
