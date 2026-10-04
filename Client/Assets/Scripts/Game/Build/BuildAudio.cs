using UnityEngine;

namespace ProjectH.Client.Game
{
    // Phase 13 D16 (request §48): the sounds building and harvesting will make. The project has no audio clips yet, so
    // this is the one place they will be played from: GameClient calls Play at each moment, and for now it does nothing
    // (final review: the unread play counts were removed). Adding sound later changes only this class. Main thread only.
    public enum BuildSound : byte
    {
        Placed = 0,
        Refused = 1,
        PieceDestroyed = 2,
        HarvestHit = 3,
        WeakPointHit = 4,
        HarvestDestroyed = 5,
        Swing = 6,
    }

    public sealed class BuildAudio
    {
        // 기능: 건설·채집 효과음을 지정한 위치에서 재생한다. 아직 오디오 클립이 없어 지금은 아무 동작도 하지 않는다.
        // 입력: sound - 재생할 효과음 종류, position - 재생할 월드 위치.
        // 출력: 반환값 없음. 현재는 바뀌는 상태가 없다.
        // Where a clip for this sound will play, at position.
        public void Play(BuildSound sound, Vector3 position)
        {
        }
    }
}
