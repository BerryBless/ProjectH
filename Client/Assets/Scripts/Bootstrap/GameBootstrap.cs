using ProjectH.Client.Game;
using ProjectH.Client.UI;
using UnityEngine;

namespace ProjectH.Client.Bootstrap
{
    public static class GameBootstrap
    {
        // 기능: 첫 Scene 로드 직후 Session 전체에서 쓰는 GameClient와 게임 UI(UiRoot)를 하나만 만든다.
        // 입력: 없음.
        // 출력: 반환값 없음. runInBackground가 켜지고, GameClient가 없을 때만 DontDestroyOnLoad GameObject에 GameClient와 UiRoot가 붙는다.
        // Runs after the first scene loads, in any scene, so the prototype needs no scene or prefab
        // edits. Creates exactly one GameClient that lives for the whole session, and its game UI (Phase 11).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            // A networked client must keep simulating, sending input and receiving snapshots while its
            // window is unfocused; otherwise the other Multiplayer Play Mode player (or a background
            // window) freezes. Set in code so every Editor clone and build gets it regardless of settings.
            Application.runInBackground = true;

            if (Object.FindAnyObjectByType<GameClient>() != null) return;
            var go = new GameObject("GameClient");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<GameClient>();
            go.AddComponent<UiRoot>();
        }
    }
}
