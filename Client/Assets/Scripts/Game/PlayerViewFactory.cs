using ProjectH.Shared.Simulation;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // One character on screen (Phase 12 D14). Phase 14 D3, D14: a teammate is green and has no active hit box (shots pass). The root sits at the feet, never rotates and carries the remote hit box,
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
        private bool _teammate;   // Phase 14 D14: drawn green
        private bool _hidden;
        private PlayerPose _pose;
        private bool _hasPose;

        // 기능: PlayerViewFactory.Create가 만든 객체들을 묶어 캐릭터 뷰를 만든다.
        // 입력: root - 발 위치의 뿌리(회전하지 않음), body - 캡슐, wings - 글라이더 객체, collider - 원격 피격 상자(내 뷰면 null), isLocal - 내 캐릭터인지.
        // 출력: 살아 있고 자세가 아직 없는 뷰(첫 Place가 모양을 적용한다).
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

        // 기능: 생존 상태를 바꾼다(D13, Phase 3: 죽으면 회색으로 눕고 원격 플레이어의 피격 상자가 꺼진다).
        // 입력: alive - 살아 있는지.
        // 출력: 반환값 없음. 몸 Material과 다음 Place의 자세가 바뀐다.
        public void SetAlive(bool alive)
        {
            if (alive == _alive) return;
            _alive = alive;
            _bodyRenderer.sharedMaterial = PlayerViewFactory.BodyMaterial(_isLocal, _teammate, alive);
            _hasPose = false;   // the pose depends on it
        }

        // 기능: 같은 팀 표시를 바꾼다(Phase 14 D14: 팀원 초록, 적 주황; 내 몸은 그대로 파랑).
        // 입력: teammate - 우리 팀 구성원인지.
        // 출력: 반환값 없음. 바뀌면 몸 Material이 바뀌고, 다음 Place에서 피격 상자가 켜지거나 꺼진다(팀원은 조준 광선이 지나간다, D3).
        public void SetTeammate(bool teammate)
        {
            if (teammate == _teammate) return;
            _teammate = teammate;
            _bodyRenderer.sharedMaterial = PlayerViewFactory.BodyMaterial(_isLocal, teammate, _alive);
            _hasPose = false;   // the collider depends on it
        }

        // Every frame: where the feet are drawn, the facing, and the mode's pose.
        // 기능: 발 위치·방향·모드의 자세로 캐릭터를 놓는다(Phase 19: 앉아 있으면 앉은 자세, 피격 상자 끔).
        // 입력: feet - 그리는 발 위치, yaw - 방향(앉아 있으면 차량 방향), mode - 이동 모드, sprinting - 질주 중, seated - 차량에 앉아 있는지.
        // 출력: 반환값 없음. 자세가 바뀔 때만 모양·충돌 상자를 고친다.
        public void Place(Vector3 feet, float yaw, MovementMode mode, bool sprinting, bool seated = false)
        {
            Root.position = feet;
            PlayerPose pose = PlayerPose.For(mode, sprinting, _alive, seated);
            if (!_hasPose || pose.BodyHeight != _pose.BodyHeight || pose.Prone != _pose.Prone || pose.Wings != _pose.Wings ||
                pose.Hidden != _pose.Hidden || pose.HitHeight != _pose.HitHeight || pose.Seated != _pose.Seated)
            {
                ApplyShape(pose);
            }
            _pose = pose;
            _hasPose = true;
            float pitch = pose.Prone ? 90f : pose.Lean;
            _body.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            if (pose.Wings) _wingsTransform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        }

        // 기능: 자세의 모양을 적용한다(몸 높이·엎드림·날개·숨김, 원격이면 피격 상자 크기와 켜짐. Phase 19: 앉으면 피격 상자를 끈다).
        // 입력: pose - 적용할 자세.
        // 출력: 반환값 없음.
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
                // Phase 14 D3: shots pass through teammates, so the aim ray must too (the aim point lands on the enemy behind).
                // Phase 19 D5: shots pass seated players (the vehicle takes them), so the aim ray does too.
                _collider.enabled = _alive && !pose.Hidden && !_teammate && !pose.Seated;
            }
        }

        // 기능: 뷰의 GameObject(몸·글라이더 포함)를 파괴한다(UnityObjects.Destroy: Play 밖의 EditMode 테스트에서도 지워진다).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Destroy()
        {
            if (Root != null) UnityObjects.Destroy(Root.gameObject);
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
        private static Material _teamMaterial;   // Phase 14 D14
        private static Material _deadMaterial;
        private static Material _wingMaterial;

        // 기능: 캐릭터 뷰(뿌리 + 충돌체 없는 캡슐 + 숨긴 글라이더)를 만든다. 원격이면 뿌리에 RemoteHitLayer의 BoxCollider를 둔다(D7: 서버 AABB와
        //   같은 피격 상자). 공유 Material이 없으면 만든다.
        // 입력: name - 뿌리 GameObject 이름, isLocal - 내 캐릭터인지(내 뷰는 피격 상자 없음: 조준 광선이 옆에서 시작한다).
        // 출력: 원점에 Ground 자세로 놓인 뷰.
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

        // 기능: 몸 Material을 고른다(나 파랑, 팀원 초록, 적 주황, 죽음 회색).
        // 입력: isLocal - 내 몸인지, teammate - 팀원인지(내 몸이면 무시), alive - 살아 있는지.
        // 출력: 공유 Material.
        internal static Material BodyMaterial(bool isLocal, bool teammate, bool alive) =>
            !alive ? _deadMaterial : isLocal ? _localMaterial : teammate ? _teamMaterial : _remoteMaterial;

        // 기능: 캐시한 Material을 모두 파괴한다(GameClient.OnDestroy가 부른다: Material은 Client와 수명이 같다).
        // 입력: 없음.
        // 출력: 반환값 없음. 다음 Create가 다시 만든다.
        public static void ReleaseMaterials()
        {
            UnityObjects.Destroy(_localMaterial);
            UnityObjects.Destroy(_remoteMaterial);
            UnityObjects.Destroy(_teamMaterial);
            UnityObjects.Destroy(_deadMaterial);
            UnityObjects.Destroy(_wingMaterial);
            _localMaterial = null;
            _remoteMaterial = null;
            _teamMaterial = null;
            _deadMaterial = null;
            _wingMaterial = null;
        }

        // 기능: 없는 공유 Material을 만든다(Phase 14: 팀원 초록 포함).
        // 입력: template - 복사할 Lit Material.
        // 출력: 반환값 없음.
        private static void EnsureMaterials(Material template)
        {
            // Explicit == null (not ??=): Unity's null check also catches destroyed materials.
            if (_localMaterial == null) _localMaterial = Tinted(template, new Color(0.2f, 0.6f, 1f));
            if (_remoteMaterial == null) _remoteMaterial = Tinted(template, new Color(1f, 0.45f, 0.2f));
            if (_teamMaterial == null) _teamMaterial = Tinted(template, new Color(0.3f, 0.85f, 0.35f));
            if (_deadMaterial == null) _deadMaterial = Tinted(template, new Color(0.45f, 0.45f, 0.45f));
            if (_wingMaterial == null) _wingMaterial = Tinted(template, new Color(0.95f, 0.85f, 0.25f));
        }

        // 기능: Lit Material(LitMaterial)을 복사해 색을 입힌다(Shader가 빌드에 들어 있음이 보장된다).
        // 입력: template - 복사할 Material, color - 입힐 색.
        // 출력: 새 Material(호출자가 파괴한다).
        private static Material Tinted(Material template, Color color)
        {
            return new Material(template) { color = color };
        }
    }
}
