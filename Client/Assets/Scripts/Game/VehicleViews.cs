using ProjectH.Client.Net;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 19 D11: the vehicles on screen, a fixed pool of VehicleSettings.MaxVehicles views made once (one per VehicleStore
    // slot). Each is a body box and four wheel cylinders (the front two turn with the steering, smoothed for display only), tilted
    // with the terrain under the axles (display only: the simulation has no tilt), dark when wrecked, with a grey smoke sphere
    // below SmokeHealthFraction of VehiclePrompt.MaxHealth. Built-in cube, cylinder and sphere meshes (sharedMesh), four shared
    // materials. No colliders: a default-layer collider would stop the camera's sphere cast and the aim ray at the car. Dispose
    // destroys the objects and the materials.
    public sealed class VehicleViews : System.IDisposable
    {
        public const float SmokeHealthFraction = 0.3f;
        private const float BodyLength = 4.4f;
        private const float WheelRadius = 0.4f;
        private const float WheelWidth = 0.3f;
        private const float WheelInset = 1.3f;        // along the length from the centre
        private const float MaxWheelSteer = 30f;      // degrees shown at full steer
        private const float SteerSharpness = 10f;
        private const float SmokeSize = 0.9f;

        private readonly GameObject _root;
        private readonly Transform[] _views = new Transform[VehicleSettings.MaxVehicles];
        private readonly Renderer[] _bodies = new Renderer[VehicleSettings.MaxVehicles];
        private readonly Transform[] _frontLeft = new Transform[VehicleSettings.MaxVehicles];
        private readonly Transform[] _frontRight = new Transform[VehicleSettings.MaxVehicles];
        private readonly Transform[] _smoke = new Transform[VehicleSettings.MaxVehicles];
        private readonly float[] _steer = new float[VehicleSettings.MaxVehicles];
        private readonly bool[] _wrecked = new bool[VehicleSettings.MaxVehicles];
        private readonly Material _body;
        private readonly Material _wreck;
        private readonly Material _wheel;
        private readonly Material _smokeMaterial;

        // 기능: 차량 뷰 8개(몸통·바퀴 4·연기)와 공유 Material 4개를 만든다(모두 숨긴 채).
        // 입력: source - 복사할 Lit Material(LitMaterial로 고른 것).
        // 출력: 숨겨진 풀(Dispose가 해제한다).
        public VehicleViews(Material source)
        {
            _root = new GameObject("Vehicles");
            _body = new Material(source) { color = new Color(0.85f, 0.75f, 0.2f) };
            _wreck = new Material(source) { color = new Color(0.12f, 0.12f, 0.12f) };
            _wheel = new Material(source) { color = new Color(0.08f, 0.08f, 0.08f) };
            _smokeMaterial = new Material(source) { color = new Color(0.5f, 0.5f, 0.5f) };
            Mesh cube = BuiltinMesh(PrimitiveType.Cube);
            Mesh cylinder = BuiltinMesh(PrimitiveType.Cylinder);
            Mesh sphere = BuiltinMesh(PrimitiveType.Sphere);
            float bodyHeight = VehicleSettings.BodyTop - VehicleSettings.BodyBottom;
            float halfWidth = VehicleSettings.BodySquareSize * 0.5f;
            for (int i = 0; i < _views.Length; i++)
            {
                var root = new GameObject("Vehicle " + i).transform;
                root.SetParent(_root.transform, false);
                _bodies[i] = Part("Body", root, cube, new Vector3(0f, VehicleSettings.BodyBottom + bodyHeight * 0.5f, 0f),
                    Quaternion.identity, new Vector3(VehicleSettings.BodySquareSize, bodyHeight, BodyLength), _body);
                _frontLeft[i] = Wheel(root, cylinder, new Vector3(-halfWidth, WheelRadius, WheelInset), true);
                _frontRight[i] = Wheel(root, cylinder, new Vector3(halfWidth, WheelRadius, WheelInset), true);
                Wheel(root, cylinder, new Vector3(-halfWidth, WheelRadius, -WheelInset), false);
                Wheel(root, cylinder, new Vector3(halfWidth, WheelRadius, -WheelInset), false);
                _smoke[i] = Part("Smoke", root, sphere, new Vector3(0f, VehicleSettings.BodyTop + 0.4f, WheelInset), Quaternion.identity,
                    Vector3.one * SmokeSize, _smokeMaterial).transform;
                _smoke[i].gameObject.SetActive(false);
                root.gameObject.SetActive(false);
                _views[i] = root;
            }
        }

        // 기능: 한 슬롯의 차량을 그린다: 위치·방향·지형 기울기, 앞바퀴 조향(부드럽게), 파괴면 검게, 체력이 낮으면 연기. 할당 없음.
        // 입력: slot - VehicleStore 슬롯, record - 이번 프레임 표본(내 차량은 예측), now - 현재 시각(연기 맥동), deltaTime - 프레임 시간.
        // 출력: 반환값 없음.
        public void Draw(int slot, in VehicleRecord record, float now, float deltaTime)
        {
            if (_root == null) return;
            Transform view = _views[slot];
            SetActive(view.gameObject, true);
            Vector3 p = record.Position.ToUnity();
            Tilt(p.x, p.z, record.Heading, out float pitch, out float roll);
            view.SetPositionAndRotation(p, Quaternion.Euler(pitch, record.Heading, roll));

            bool wrecked = record.State == VehicleState.Wrecked;
            if (wrecked != _wrecked[slot])
            {
                _wrecked[slot] = wrecked;
                _bodies[slot].sharedMaterial = wrecked ? _wreck : _body;
            }
            float target = wrecked ? 0f : Mathf.Clamp(record.Steer, -1f, 1f) * MaxWheelSteer;
            _steer[slot] = Mathf.Lerp(_steer[slot], target, 1f - Mathf.Exp(-SteerSharpness * deltaTime));
            Quaternion steer = Quaternion.Euler(0f, _steer[slot], 0f);
            _frontLeft[slot].localRotation = steer;
            _frontRight[slot].localRotation = steer;

            bool smoke = wrecked || record.Health < VehiclePrompt.MaxHealth * SmokeHealthFraction;
            Transform puff = _smoke[slot];
            SetActive(puff.gameObject, smoke);
            if (smoke) puff.localScale = Vector3.one * (SmokeSize * (1f + 0.15f * Mathf.Sin(now * 6f + slot)));
        }

        // 기능: 한 슬롯을 숨긴다.
        // 입력: slot - 슬롯 번호.
        // 출력: 반환값 없음.
        public void Hide(int slot)
        {
            if (_root == null) return;
            SetActive(_views[slot].gameObject, false);
        }

        // 기능: 모두 숨긴다(끊김).
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void HideAll()
        {
            for (int i = 0; i < _views.Length; i++) Hide(i);
        }

        // 기능: 지금 보이는 차량 뷰 수를 센다(QA).
        // 입력: 없음.
        // 출력: 켜진 뷰 수.
        public int Drawn
        {
            get
            {
                if (_root == null) return 0;
                int n = 0;
                for (int i = 0; i < _views.Length; i++)
                {
                    if (_views[i].gameObject.activeSelf) n++;
                }
                return n;
            }
        }

        // 기능: 뷰와 Material을 파괴한다. 내장 Mesh는 에셋이라 파괴하지 않는다.
        // 입력: 없음.
        // 출력: 반환값 없음.
        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_body != null) Object.Destroy(_body);
            if (_wreck != null) Object.Destroy(_wreck);
            if (_wheel != null) Object.Destroy(_wheel);
            if (_smokeMaterial != null) Object.Destroy(_smokeMaterial);
        }

        // 기능: 앞·뒤 축과 좌우의 지형 높이로 차체 기울기를 구한다(표시만).
        // 입력: x·z - 차량 중심, heading - 방향(도), pitch·roll - 결과(도, Unity Euler: pitch 양수 = 앞이 내려감, roll 양수 = 오른쪽이 올라감).
        // 출력: 반환값 없음.
        private static void Tilt(float x, float z, float heading, out float pitch, out float roll)
        {
            float rad = heading * Mathf.Deg2Rad;
            float sin = Mathf.Sin(rad);
            float cos = Mathf.Cos(rad);
            const float axle = VehicleSettings.AxleOffset;
            const float half = VehicleSettings.BodySquareSize * 0.5f;
            HeightField terrain = GameMap.Terrain;
            float front = terrain.Height(x + sin * axle, z + cos * axle);
            float rear = terrain.Height(x - sin * axle, z - cos * axle);
            float right = terrain.Height(x + cos * half, z - sin * half);
            float left = terrain.Height(x - cos * half, z + sin * half);
            pitch = Mathf.Atan2(rear - front, 2f * axle) * Mathf.Rad2Deg;
            roll = Mathf.Atan2(right - left, 2f * half) * Mathf.Rad2Deg;
        }

        // 기능: 바퀴 하나를 만든다(앞바퀴는 조향 축 아래에 둔다).
        // 입력: parent - 차량 뿌리, cylinder - 원기둥 Mesh, position - 차량 기준 위치, front - 앞바퀴인지.
        // 출력: 앞바퀴면 조향 축 Transform, 뒷바퀴면 바퀴 Transform.
        private Transform Wheel(Transform parent, Mesh cylinder, Vector3 position, bool front)
        {
            var pivot = new GameObject(front ? "SteerPivot" : "Axle").transform;
            pivot.SetParent(parent, false);
            pivot.localPosition = position;
            // The cylinder mesh stands 2 m tall along Y: turned onto its side and scaled to the wheel.
            Part("Wheel", pivot, cylinder, Vector3.zero, Quaternion.Euler(0f, 0f, 90f),
                new Vector3(WheelRadius * 2f, WheelWidth * 0.5f, WheelRadius * 2f), _wheel);
            return pivot;
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
        // 출력: 만든 조각의 Renderer.
        private static Renderer Part(string name, Transform parent, Mesh mesh, Vector3 position, Quaternion rotation, Vector3 scale, Material material)
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
            return renderer;
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
