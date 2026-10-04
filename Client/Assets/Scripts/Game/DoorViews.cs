using ProjectH.Client.Net;
using ProjectH.Shared.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectH.Client.Game
{
    // Phase 12 D9, D14: one box per Shared GameMap door, shown while the door is closed as predicted (PredictedDoors).
    // Its collider (default layer) stops the camera and the aim ray like the server's shots stop at a closed door; an
    // open door is simply not there. Built once; Tick changes them only when PredictedDoors.Version changes. Dispose
    // destroys the objects and the material.
    public sealed class DoorViews : System.IDisposable
    {
        private readonly GameObject[] _doors = new GameObject[GameMap.DoorCount];
        private readonly Material _material;
        private int _shownVersion = -1;

        // 기능: GameMap의 문마다 Collider가 있는 상자를 만들고 공유 Material 하나를 입힌다.
        // 입력: 없음.
        // 출력: 모든 문 상자가 활성(닫힘)으로 생성된 DoorViews.
        public DoorViews()
        {
            Material source = null;
            for (int i = 0; i < _doors.Length; i++)
            {
                Box box = GameMap.Doors[i];
                var door = GameObject.CreatePrimitive(PrimitiveType.Cube);
                door.name = "Door " + i;
                door.transform.position = box.Center.ToUnity();
                door.transform.localScale = box.Size.ToUnity();
                var renderer = door.GetComponent<Renderer>();
                if (source == null) source = LitMaterial.Source(renderer.sharedMaterial);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                _doors[i] = door;
            }
            _material = new Material(source) { color = new Color(0.45f, 0.3f, 0.18f) };
            for (int i = 0; i < _doors.Length; i++) _doors[i].GetComponent<Renderer>().sharedMaterial = _material;
        }

        // 기능: 예측된 문 상태의 Version이 바뀌었을 때만 닫힌 문은 보이고 열린 문은 숨긴다.
        // 입력: doors - 예측된 문 열림 상태(PredictedDoors).
        // 출력: 반환값 없음. 문 GameObject의 활성 상태가 바뀐다.
        public void Tick(PredictedDoors doors)
        {
            if (doors.Version == _shownVersion) return;
            _shownVersion = doors.Version;
            for (int i = 0; i < _doors.Length; i++)
            {
                bool closed = !doors.IsOpen(i);
                if (_doors[i] != null && _doors[i].activeSelf != closed) _doors[i].SetActive(closed);
            }
        }

        // 기능: 문 GameObject와 동적으로 만든 Material을 파괴한다.
        // 입력: 없음.
        // 출력: 반환값 없음. 문 오브젝트와 Material이 파괴된다.
        public void Dispose()
        {
            for (int i = 0; i < _doors.Length; i++)
            {
                if (_doors[i] != null) Object.Destroy(_doors[i]);
            }
            if (_material != null) Object.Destroy(_material);
        }
    }
}
