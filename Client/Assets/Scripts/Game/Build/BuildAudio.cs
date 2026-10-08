using ProjectH.Client.Game.Audio;
using UnityEngine;

namespace ProjectH.Client.Game
{
    // Phase 13 D16 (request §48): the sounds building and harvesting make. GameClient calls Play at each moment; since Phase 18
    // they go to the game's sound system (GameAudio), which mixes them with every other sound. Main thread only.
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
        private readonly GameAudio _audio;

        // 기능: 건설·채집 소리를 GameAudio로 보내는 훅을 만든다(Phase 18).
        // 입력: audio - 게임 소리 시스템(GameClient가 소유).
        // 출력: 훅 객체.
        public BuildAudio(GameAudio audio)
        {
            _audio = audio;
        }

        // 기능: 건설·채집 소리 하나를 낸다(Phase 18: 내 행동인 설치·거절·휘두르기는 2D, 세상에서 나는 파괴·채집 타격은 3D).
        // 입력: sound - 소리, position - 위치(3D 소리), source - 중복 판단 소스(조각 id 등, 0 = 없음).
        // 출력: 반환값 없음. 소리 요청이 큐에 들어간다.
        public void Play(BuildSound sound, Vector3 position, uint source = 0)
        {
            switch (sound)
            {
                case BuildSound.Placed: _audio.Play2D(SoundKind.BuildPlace, source); break;
                case BuildSound.Refused: _audio.Play2D(SoundKind.BuildRefused); break;
                case BuildSound.Swing: _audio.Play2D(SoundKind.HarvestSwing); break;
                case BuildSound.PieceDestroyed: _audio.Play3D(SoundKind.BuildDestroy, position, source); break;
                case BuildSound.HarvestHit: _audio.Play3D(SoundKind.HarvestHit, position, source); break;
                case BuildSound.WeakPointHit: _audio.Play3D(SoundKind.HarvestWeakPoint, position, source); break;
                case BuildSound.HarvestDestroyed: _audio.Play3D(SoundKind.HarvestDestroyed, position, source); break;
            }
        }
    }
}
