using UnityEngine;

namespace ProjectH.Client.Game
{
    // Review fix D3/D4 tests: EditMode tests build real views (RemotePlayers, BuildPieceViews) outside Play mode, where
    // Object.Destroy only logs an error and destroys nothing (the objects would stay in the open scene). In Play mode this is
    // exactly Object.Destroy (deferred to the end of the frame), so the game behaves as before.
    public static class UnityObjects
    {
        // 기능: Unity Object를 파괴한다. Play 중이면 Object.Destroy, Editor의 Play 밖(EditMode 테스트)이면 DestroyImmediate.
        // 입력: target - 파괴할 Object(null이면 아무것도 하지 않는다).
        // 출력: 반환값 없음.
        public static void Destroy(Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }
    }
}
