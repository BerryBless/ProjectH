using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 17 D7, D14: the projectiles and explosions on screen, presentation only.
    //  - One view per ProjectileTracks slot (32, made once): a grenade is a small dark sphere, a rocket a grey cylinder along
    //    its flight with an orange tail behind it. Every frame each active slot is placed where ProjectileTracks extrapolates
    //    it to (no physics, nothing sent).
    //  - Explosions: a fixed ring of 8 translucent orange spheres that grow to the kind's explosion radius and vanish in
    //    ExplosionSeconds (Sprites/Default like the harvest dust, the opaque Lit copy where it is missing).
    // Built-in sphere and cylinder meshes (sharedMesh), four shared materials, no colliders (the aim and ping rays and the
    // camera must not hit them). Nothing is allocated per frame or per event. Dispose destroys the objects and materials.
    public sealed class ProjectileViews : System.IDisposable
    {
        public const int ExplosionPoolSize = 8;
        public const float ExplosionSeconds = 0.4f;
        private const float GrenadeSize = 0.22f;
        private const float RocketRadius = 0.12f;
        private const float RocketLength = 0.8f;
        private const float TailLength = 0.9f;
        private const float TailWidth = 0.1f;

        private readonly GameObject _root;
        private readonly Transform[] _views = new Transform[ProjectileTracks.Capacity];
        private readonly GameObject[] _grenades = new GameObject[ProjectileTracks.Capacity];
        private readonly GameObject[] _rockets = new GameObject[ProjectileTracks.Capacity];
        private readonly Transform[] _explosions = new Transform[ExplosionPoolSize];
        private readonly float[] _explosionStart = new float[ExplosionPoolSize];
        private readonly float[] _explosionRadius = new float[ExplosionPoolSize];
        private readonly RingCursor _nextExplosion = new RingCursor(ExplosionPoolSize);
        private readonly Material _grenadeMaterial;
        private readonly Material _rocketMaterial;
        private readonly Material _tailMaterial;
        private readonly Material _explosionMaterial;

        // 기능: 투사체 뷰 32개(수류탄 구, 로켓 원기둥과 꼬리)와 폭발 구 8개, 공유 Material 4개를 만든다(모두 숨긴 채).
        // 입력: source - 복사할 Lit Material(LitMaterial로 고른 것).
        // 출력: 숨겨진 풀(Dispose가 해제한다).
        public ProjectileViews(Material source)
        {
            _root = new GameObject("Projectiles");
            _grenadeMaterial = new Material(source) { color = new Color(0.25f, 0.32f, 0.15f) };
            _rocketMaterial = new Material(source) { color = new Color(0.55f, 0.56f, 0.6f) };
            _tailMaterial = new Material(source) { color = new Color(1f, 0.55f, 0.1f) };
            Shader sprite = Shader.Find("Sprites/Default");
            _explosionMaterial = sprite != null ? new Material(sprite) { color = new Color(1f, 0.55f, 0.15f, 0.35f) }
                : new Material(source) { color = new Color(1f, 0.55f, 0.15f) };
            Mesh sphere = BuiltinMesh(PrimitiveType.Sphere);
            Mesh cylinder = BuiltinMesh(PrimitiveType.Cylinder);
            // The cylinder mesh is 2 m tall along +Y around its middle; the rocket's forward is the view's +Z.
            Quaternion alongZ = Quaternion.Euler(90f, 0f, 0f);
            for (int i = 0; i < _views.Length; i++)
            {
                var view = new GameObject("Projectile " + i).transform;
                view.SetParent(_root.transform, false);
                _grenades[i] = Part("Grenade", view, sphere, Vector3.zero, Quaternion.identity, new Vector3(GrenadeSize, GrenadeSize, GrenadeSize), _grenadeMaterial);
                var rocket = new GameObject("Rocket").transform;
                rocket.SetParent(view, false);
                Part("Body", rocket, cylinder, Vector3.zero, alongZ, new Vector3(RocketRadius * 2f, RocketLength * 0.5f, RocketRadius * 2f), _rocketMaterial);
                Part("Tail", rocket, cylinder, new Vector3(0f, 0f, -(RocketLength + TailLength) * 0.5f), alongZ,
                    new Vector3(TailWidth, TailLength * 0.5f, TailWidth), _tailMaterial);
                _rockets[i] = rocket.gameObject;
                view.gameObject.SetActive(false);
                _views[i] = view;
            }
            for (int i = 0; i < _explosions.Length; i++)
            {
                _explosions[i] = Part("Explosion", _root.transform, sphere, Vector3.zero, Quaternion.identity, Vector3.one, _explosionMaterial).transform;
                _explosions[i].gameObject.SetActive(false);
            }
        }

        // How many projectiles were drawn in the last Draw (QA status).
        public int Drawn { get; private set; }

        // 기능: 모든 활성 투사체를 tick 시점의 외삽 위치에 그리고(로켓은 비행 방향으로 돌린다), 빈 칸의 뷰는 숨긴다. 할당 없음.
        // 입력: tracks - 투사체 상태, tick - 그릴 서버 Tick(지금 추정), simHz - 서버 SimHz.
        // 출력: 반환값 없음. Drawn이 그린 수가 된다.
        public void Draw(ProjectileTracks tracks, double tick, int simHz)
        {
            if (_root == null) return;
            int drawn = 0;
            for (int i = 0; i < _views.Length; i++)
            {
                if (!tracks.IsActive(i))
                {
                    SetActive(_views[i].gameObject, false);
                    continue;
                }
                tracks.Sample(i, tick, simHz, out System.Numerics.Vector3 p, out System.Numerics.Vector3 v);
                bool rocket = tracks.KindAt(i) == ProjectH.Shared.Protocol.ProjectileKind.Rocket;
                SetActive(_grenades[i], !rocket);
                SetActive(_rockets[i], rocket);
                Transform view = _views[i];
                view.localPosition = new Vector3(p.X, p.Y, p.Z);
                if (rocket && v.LengthSquared() > 1e-6f) view.localRotation = Quaternion.LookRotation(new Vector3(v.X, v.Y, v.Z));
                SetActive(view.gameObject, true);
                drawn++;
            }
            Drawn = drawn;
        }

        // 기능: 폭발 효과 하나를 시작한다(풀이 차면 가장 오래된 것을 다시 쓴다).
        // 입력: position - 폭발 지점, radius - 폭발 반지름(m, 0 이하면 1), now - 현재 시각.
        // 출력: 반환값 없음.
        public void Explode(Vector3 position, float radius, float now)
        {
            if (_root == null) return;
            int i = _nextExplosion.Next();
            _explosionStart[i] = now;
            _explosionRadius[i] = radius > 0f ? radius : 1f;
            _explosions[i].localPosition = position;
            _explosions[i].localScale = Vector3.zero;
            SetActive(_explosions[i].gameObject, true);
        }

        // 기능: 폭발 구를 키우고 시간이 다 된 것을 숨긴다(30 %에서 반지름까지). 할당 없음.
        // 입력: now - 현재 시각.
        // 출력: 반환값 없음.
        public void Tick(float now)
        {
            if (_root == null) return;
            for (int i = 0; i < _explosions.Length; i++)
            {
                if (!_explosions[i].gameObject.activeSelf) continue;
                float t = (now - _explosionStart[i]) / ExplosionSeconds;
                if (t >= 1f || t < 0f)
                {
                    _explosions[i].gameObject.SetActive(false);
                    continue;
                }
                float diameter = 2f * _explosionRadius[i] * (0.3f + 0.7f * t);
                _explosions[i].localScale = new Vector3(diameter, diameter, diameter);
            }
        }

        // 기능: 모든 투사체와 폭발을 숨긴다(끊김, 리셋).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void HideAll()
        {
            if (_root == null) return;
            for (int i = 0; i < _views.Length; i++) SetActive(_views[i].gameObject, false);
            for (int i = 0; i < _explosions.Length; i++) SetActive(_explosions[i].gameObject, false);
            Drawn = 0;
        }

        // 기능: 뷰와 Material을 파괴한다. 내장 Mesh는 에셋이라 파괴하지 않는다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_grenadeMaterial != null) Object.Destroy(_grenadeMaterial);
            if (_rocketMaterial != null) Object.Destroy(_rocketMaterial);
            if (_tailMaterial != null) Object.Destroy(_tailMaterial);
            if (_explosionMaterial != null) Object.Destroy(_explosionMaterial);
        }

        // 기능: 바뀔 때만 SetActive를 부른다.
        // 입력: go - 대상, active - 켤지.
        // 출력: 반환값 없음.
        private static void SetActive(GameObject go, bool active)
        {
            if (go.activeSelf != active) go.SetActive(active);
        }

        // 기능: 충돌체 없는 조각 하나를 만든다(MeshFilter + MeshRenderer, 그림자 없음).
        // 입력: name - 이름, parent - 부모, mesh - 공유 Mesh, position·rotation·scale - 부모 기준 배치, material - 공유 Material.
        // 출력: 만든 조각의 GameObject.
        private static GameObject Part(string name, Transform parent, Mesh mesh, Vector3 position, Quaternion rotation, Vector3 scale, Material material)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            go.transform.localScale = scale;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        // 기능: 내장 Primitive Mesh를 얻는다(임시 Object는 바로 파괴한다).
        // 입력: type - Primitive 종류.
        // 출력: 내장 Mesh(에셋: 파괴하지 않는다).
        private static Mesh BuiltinMesh(PrimitiveType type)
        {
            var probe = GameObject.CreatePrimitive(type);
            Mesh mesh = probe.GetComponent<MeshFilter>().sharedMesh;
            // Immediate: a deferred destroy would leave its collider in the world for this frame's rays.
            Object.DestroyImmediate(probe);
            return mesh;
        }
    }
}
