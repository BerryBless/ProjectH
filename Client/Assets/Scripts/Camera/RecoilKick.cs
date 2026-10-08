using System;

namespace ProjectH.Client.CameraControl
{
    // Phase 17 D3: the camera kick of our own shots, presentation only. A shot adds the weapon's RecoilDegrees (WeaponCatalog)
    // at once; the offset then eases back to zero over a few frames (exponential, RecoverPerSecond). ShoulderCamera adds it
    // on top of the mouse pitch, so the crosshair, the aim ray and the aim sent to the server all follow what the camera
    // shows; the server never models recoil. Pure (no UnityEngine) and allocation free.
    public sealed class RecoilKick
    {
        // Above this the camera would leave the target entirely; a long automatic burst or a hitch frame never adds more.
        public const float MaxOffsetDegrees = 12f;
        // e^(-12 t): about 30 % left after 0.1 s, under 3 % after 0.3 s.
        public const float RecoverPerSecond = 12f;
        // Below this the offset snaps to zero, so an idle camera holds an exact value.
        private const float SnapDegrees = 0.01f;

        // Degrees the camera is kicked up right now (0..MaxOffsetDegrees).
        public float Offset { get; private set; }

        // 기능: 한 번 쏜 만큼 카메라를 위로 찬다.
        // 입력: degrees - 무기의 RecoilDegrees(음수·NaN·무한은 무시).
        // 출력: 반환값 없음. Offset이 늘어난다(최대 MaxOffsetDegrees).
        public void Kick(float degrees)
        {
            if (!(degrees > 0f) || float.IsInfinity(degrees)) return;
            Offset = Math.Min(MaxOffsetDegrees, Offset + degrees);
        }

        // 기능: 프레임 시간만큼 Offset을 0 쪽으로 되돌린다.
        // 입력: deltaTime - 프레임 시간(초, 0 이하·NaN이면 그대로).
        // 출력: 반환값 없음. Offset이 줄고, SnapDegrees 아래면 0이 된다.
        public void Step(float deltaTime)
        {
            if (Offset == 0f || !(deltaTime > 0f)) return;
            Offset *= (float)Math.Exp(-RecoverPerSecond * deltaTime);
            if (Offset < SnapDegrees) Offset = 0f;
        }

        // 기능: 반동을 없앤다(사망·부활·끊김).
        // 입력: 없음.
        // 출력: 반환값 없음. Offset이 0이 된다.
        public void Reset() => Offset = 0f;
    }
}
