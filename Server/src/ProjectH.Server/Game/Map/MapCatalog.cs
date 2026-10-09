using System;
using System.IO;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Game.Map;

// Phase 15 D7, D9: map.json, the ping and waypoint numbers. Loaded and validated once at startup like the other data files
// (a bad file stops the server); immutable afterwards, so the game loop and LiteNetLib's receive thread read it without
// locks. Lifetimes are already in simulation ticks.
public sealed class MapCatalog
{
    public const string FileName = "map.json";

    // 기능: 검증을 마친 값들로 카탈로그를 만든다(TryParse만 호출한다).
    // 입력: simHz - Tick 속도, root - 파싱된 JSON, pingTicks - 일반 핑 유지 Tick, enemyPingTicks - 적 핑 유지 Tick.
    // 출력: 모든 값이 채워진 불변 MapCatalog.
    private MapCatalog(int simHz, MapJson root, uint pingTicks, uint enemyPingTicks)
    {
        SimHz = simHz;
        PingTicks = pingTicks;
        EnemyPingTicks = enemyPingTicks;
        PingsPerPlayer = root.PingsPerPlayer;
        PingsPerTeam = root.PingsPerTeam;
        EnemyPingRange = (float)root.EnemyPingRange;
        ItemPingRange = (float)root.ItemPingRange;
        PingsPerSecond = root.PingsPerSecond;
        PingBurst = root.PingBurst;
        MaxMarkerPacketsPerSecond = root.MaxMarkerPacketsPerSecond;
    }

    public int SimHz { get; }
    // D9: how long a Location, Item or Danger ping stays, and an Enemy ping.
    public uint PingTicks { get; }
    public uint EnemyPingTicks { get; }
    // D9: active pings per player and per team (the oldest is replaced). PingsPerPlayer <= PingsPerTeam <= MaxTeamPings.
    public int PingsPerPlayer { get; }
    public int PingsPerTeam { get; }
    // D8: an Enemy ping's target within this distance of the sender's eye; an Item ping's item within this of its feet.
    public float EnemyPingRange { get; }
    public float ItemPingRange { get; }
    // D7: the receive thread's token bucket per connection, and the per-second count above which packets are invalid.
    public int PingsPerSecond { get; }
    public int PingBurst { get; }
    public int MaxMarkerPacketsPerSecond { get; }

    // 기능: 배포하는 기본값(map.json과 같다, MapCatalogTests가 둘을 묶는다)으로 카탈로그를 만든다. 파일 없이 GameData를 만드는 테스트용.
    // 입력: simHz - 서버 Tick 속도.
    // 출력: 기본 수치의 MapCatalog. 기본값이 틀리면 예외.
    public static MapCatalog Default(int simHz)
    {
        if (!TryParse(DefaultJson, simHz, out var catalog, out string? error))
            throw new InvalidOperationException("The default map data is invalid: " + error);
        return catalog!;
    }

    // 기능: map.json 파일을 읽고 검증한다(시작 때 한 번).
    // 입력: path - 파일 경로, simHz - 서버 Tick 속도.
    // 출력: 검증된 MapCatalog. 파일이 없거나 틀리면 InvalidOperationException(서버가 시작하지 않는다).
    public static MapCatalog LoadFile(string path, int simHz)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Map data not found: {path}");
        if (!TryParse(File.ReadAllText(path), simHz, out var catalog, out string? error))
            throw new InvalidOperationException($"Invalid map data {path}: {error}");
        return catalog!;
    }

    // 기능: map.json 내용을 읽고 모든 값을 범위 검사한다.
    // 입력: json - 파일 내용, simHz - 서버 Tick 속도.
    // 출력: 성공하면 true와 카탈로그, 실패하면 false와 이유.
    public static bool TryParse(string json, int simHz, out MapCatalog? catalog, out string? error)
    {
        catalog = null;
        if (simHz < 1) return Fail("SimHz must be positive.", out error);
        if (!DataJson.TryDeserialize(json, out MapJson? root, out error)) return false;
        if (!Seconds(root!.PingSeconds, simHz, out uint ping)) return Fail("pingSeconds must be 1-60.", out error);
        if (!Seconds(root.EnemyPingSeconds, simHz, out uint enemy)) return Fail("enemyPingSeconds must be 1-60.", out error);
        if (root.PingsPerTeam < 1 || root.PingsPerTeam > MapMarkerConstants.MaxTeamPings)
            return Fail($"pingsPerTeam must be 1-{MapMarkerConstants.MaxTeamPings} (the TeamMarkers limit).", out error);
        if (root.PingsPerPlayer < 1 || root.PingsPerPlayer > root.PingsPerTeam) return Fail("pingsPerPlayer must be 1-pingsPerTeam.", out error);
        if (!(root.EnemyPingRange >= 1 && root.EnemyPingRange <= 500)) return Fail("enemyPingRange must be 1-500.", out error);
        if (!(root.ItemPingRange >= 1 && root.ItemPingRange <= 500)) return Fail("itemPingRange must be 1-500.", out error);
        if (root.PingsPerSecond < 1 || root.PingsPerSecond > 20) return Fail("pingsPerSecond must be 1-20.", out error);
        if (root.PingBurst < 1 || root.PingBurst > 20) return Fail("pingBurst must be 1-20.", out error);
        if (root.MaxMarkerPacketsPerSecond < Math.Max(root.PingsPerSecond, root.PingBurst) || root.MaxMarkerPacketsPerSecond > 100)
            return Fail("maxMarkerPacketsPerSecond must be at least pingsPerSecond and pingBurst, and at most 100.", out error);
        catalog = new MapCatalog(simHz, root, ping, enemy);
        error = null;
        return true;
    }

    // 기능: 1-60초를 Tick으로 바꾼다(최소 1).
    // 입력: seconds - 초, simHz - Tick 속도.
    // 출력: 범위 안이면 true와 Tick 수.
    private static bool Seconds(double seconds, int simHz, out uint ticks)
    {
        ticks = 0;
        if (!double.IsFinite(seconds) || seconds < 1 || seconds > 60) return false;
        ticks = (uint)Math.Max(1, Math.Round(seconds * simHz, MidpointRounding.AwayFromZero));
        return true;
    }

    // 기능: 검증 실패 이유를 error에 담고 false를 돌려준다(TryParse의 한 줄 반환용).
    // 입력: message - 실패 이유, error - 결과.
    // 출력: 항상 false와 error = message.
    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    // The shipped file without its comment (map.json).
    public const string DefaultJson = """
        {
          "pingSeconds": 8,
          "enemyPingSeconds": 4,
          "pingsPerPlayer": 3,
          "pingsPerTeam": 8,
          "enemyPingRange": 150,
          "itemPingRange": 60,
          "pingsPerSecond": 2,
          "pingBurst": 4,
          "maxMarkerPacketsPerSecond": 20
        }
        """;

    // Missing numbers stay 0 and fail validation, so every field must be written.
    private sealed class MapJson
    {
        public double PingSeconds { get; set; }
        public double EnemyPingSeconds { get; set; }
        public int PingsPerPlayer { get; set; }
        public int PingsPerTeam { get; set; }
        public double EnemyPingRange { get; set; }
        public double ItemPingRange { get; set; }
        public int PingsPerSecond { get; set; }
        public int PingBurst { get; set; }
        public int MaxMarkerPacketsPerSecond { get; set; }
    }
}
