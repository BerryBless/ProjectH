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
