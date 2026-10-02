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
        // Where a clip for this sound will play, at position.
        public void Play(BuildSound sound, Vector3 position)
        {
        }
    }
}
