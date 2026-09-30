using ProjectH.Client.Game;
using UnityEngine;

namespace ProjectH.Client.Bootstrap
{
    public static class GameBootstrap
    {
        // Runs after the first scene loads, in any scene, so the prototype needs no scene or prefab
        // edits. Creates exactly one GameClient that lives for the whole session.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Object.FindAnyObjectByType<GameClient>() != null) return;
            var go = new GameObject("GameClient");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<GameClient>();
            go.AddComponent<DevConnectPanel>();
        }
    }
}
