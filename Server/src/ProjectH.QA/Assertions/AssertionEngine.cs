using System.Text.Json;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// D10: turns an assertion path into a value. Game state comes from the server's QA API (server authoritative,
// request §165): player.*, match.*, build.*, server.*. What only the client side knows is actor.* and network.*
// (the actor's own view and connection). event.<Type> counts the run's events, var.<name> reads a variable.
// One query per evaluation; waitFor repeats it at the run's poll interval (request §29, §149).
public static class AssertionEngine
{
    public static readonly string[] Roots = { "player", "match", "build", "loot", "projectiles", "vehicle", "server", "network", "actor", "event", "var", "group" };

    // Paths whose numeric values are protocol enums (the server sends names; numbers are mapped for comparisons).
    private static readonly Dictionary<string, Type> s_enumPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["match.state"] = typeof(MatchFlowState),
        ["player.mode"] = typeof(MovementMode),
        ["player.tool"] = typeof(ToolKind),
    };

    private static readonly Dictionary<string, string[][]> s_metricAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tickP50Ms"] = new[] { new[] { "tickP50Ms" }, new[] { "tick", "p50" }, new[] { "tick", "p50Ms" } },
        ["tickP95Ms"] = new[] { new[] { "tickP95Ms" }, new[] { "tick", "p95" }, new[] { "tick", "p95Ms" } },
        ["tickP99Ms"] = new[] { new[] { "tickP99Ms" }, new[] { "tick", "p99" }, new[] { "tick", "p99Ms" } },
        ["tickMaxMs"] = new[] { new[] { "tickMaxMs" }, new[] { "tick", "max" }, new[] { "tick", "maxMs" } },
        ["memoryMB"] = new[] { new[] { "workingSetMB" }, new[] { "memoryMB" } },
        ["activeSessions"] = new[] { new[] { "activeSessions" }, new[] { "sessions" } },
    };

    public static Type? EnumHint(string path) => s_enumPaths.TryGetValue(path, out Type? t) ? t : null;

    public static bool NeedsActor(string path)
    {
        string root = path.Split('.')[0];
        return root is "player" or "network" or "actor";
    }

    public static string? CheckPath(string path)
    {
        string root = path.Split('.')[0];
        if (Array.IndexOf(Roots, root) < 0) return $"Unknown path root '{root}' (use {string.Join(", ", Roots)}).";
        if (root is "event" or "var" or "group" && path.Split('.').Length < 2) return $"'{path}' needs a name after '{root}.'.";
        return null;
    }

    // 기능: 검증 경로 하나를 값으로 바꾼다(Phase 16: loot.* = GET /qa/loot, at·radius 선택. Phase 17: projectiles.* = GET /qa/projectiles.
    //   Phase 19: vehicle.* = GET /qa/vehicles, vehicleId를 주면 그 차량의 필드, vehicle.<id>.<필드>도 된다).
    // 입력: path - 점으로 나눈 경로, ctx - Step 문맥, token - 취소.
    // 출력: 그 경로의 JSON 값, 없으면 null.
    public static async Task<JsonElement?> ResolveAsync(string path, StepContext ctx, CancellationToken token)
    {
        string[] seg = path.Split('.');
        RunContext run = ctx.Run;
        string[] rest = seg[1..];
        switch (seg[0])
        {
            case "player":
            {
                IQaActor actor = ctx.Actor();
                JsonElement? player = await run.Server.GetPlayerAsync(actor.DevPlayerId, token).ConfigureAwait(false);
                if (rest.Length == 1 && rest[0].Equals("state", StringComparison.OrdinalIgnoreCase) && player != null
                    && JsonPath.Child(player.Value, "state") == null)
                    return JsonPath.From(DerivedPlayerState(player.Value));
                if (rest.Length == 1 && rest[0].Equals("exists", StringComparison.OrdinalIgnoreCase)) return JsonPath.From(player != null);
                return JsonPath.Get(player, rest);
            }
            case "match":
            {
                JsonElement match = await run.Server.GetMatchAsync(token).ConfigureAwait(false);
                if (rest.Length == 1)
                {
                    switch (rest[0].ToLowerInvariant())
                    {
                        case "playercount": return JsonPath.Get(match, "players");
                        case "alivecount": return JsonPath.Get(match, "alive");
                        case "zonephase": return JsonPath.Get(match, "zone.phase");
                        case "playing":
                        {
                            JsonElement? state = Comparison.Exists(JsonPath.Get(match, "state")) ? JsonPath.Get(match, "state") : null;
                            string text = state == null ? string.Empty : NameOf(state.Value, typeof(MatchFlowState));
                            return JsonPath.From(text.Equals("Playing", StringComparison.OrdinalIgnoreCase) || text.Equals("FinalPhase", StringComparison.OrdinalIgnoreCase));
                        }
                    }
                }
                return JsonPath.Get(match, rest);
            }
            case "build":
                return await ResolveBuildAsync(rest, ctx, token).ConfigureAwait(false);
            case "loot":
            {
                // Phase 16: GET /qa/loot; 'at' + 'radius' limit loot.items to the world items around a point.
                QaPosition? at = ctx.Position("at");
                float? radius = ctx.Double("radius") is double r ? (float)r : null;
                if (radius != null && at == null) throw new QaStepException("loot.* with 'radius' needs 'at'.");
                JsonElement loot = await run.Server.GetLootAsync(radius != null ? at?.X : null, radius != null ? at?.Z : null, radius, token).ConfigureAwait(false);
                return JsonPath.Get(loot, rest);
            }
            case "projectiles":
            {
                // Phase 17: GET /qa/projectiles (projectiles[], explosions[] oldest first, launched, refused, explosionsTotal, grenadesThrown).
                JsonElement projectiles = await run.Server.GetProjectilesAsync(token).ConfigureAwait(false);
                return JsonPath.Get(projectiles, rest);
            }
            case "vehicle":
            {
                // Phase 19: GET /qa/vehicles. With 'vehicleId' the path reads that vehicle (vehicle.health, vehicle.exists, ...);
                // without it the root (vehicle.count, vehicle.<id>.health, vehicle.wrecked, ...).
                JsonElement vehicles = await run.Server.GetVehiclesAsync(token).ConfigureAwait(false);
                if (ctx.Param("vehicleId") is JsonElement id)
                {
                    string key = Comparison.TryNumber(id, out double n) ? ((long)n).ToString(System.Globalization.CultureInfo.InvariantCulture) : id.ToString();
                    JsonElement? vehicle = JsonPath.Child(vehicles, key);
                    if (rest.Length == 1 && rest[0].Equals("exists", StringComparison.OrdinalIgnoreCase)) return JsonPath.From(vehicle != null);
                    return JsonPath.Get(vehicle, rest);
                }
                return JsonPath.Get(vehicles, rest);
            }
            case "server":
                return await ResolveServerAsync(rest, ctx, token).ConfigureAwait(false);
            case "network":
            {
                // QA-3: the actor's proxy (counters of its current connection attempt and the fault settings).
                if (rest.Length >= 1 && rest[0].Equals("proxy", StringComparison.OrdinalIgnoreCase))
                    return JsonPath.Get(JsonPath.From(run.Network.Describe(ctx.ActorAlias) ?? new { enabled = false }), rest[1..]);
                ActorState s = ctx.Actor().State;
                if (rest.Length != 1) return JsonPath.Get(JsonPath.From(s), rest);
                return rest[0].ToLowerInvariant() switch
                {
                    "connected" => JsonPath.From(s.Connected),
                    "joined" => JsonPath.From(s.Joined),
                    "disconnected" => JsonPath.From(s.Disconnected),
                    "rtt" or "rttms" => JsonPath.From(s.RttMs),
                    "packetsin" or "packetcount" => JsonPath.From(s.PacketsIn),
                    "bytesin" => JsonPath.From(s.BytesIn),
                    "inputssent" => JsonPath.From(s.InputsSent),
                    "disconnectreason" => JsonPath.From(s.DisconnectReason),
                    "disconnectcode" => JsonPath.From(s.DisconnectCode),
                    _ => JsonPath.Get(JsonPath.From(s), rest),
                };
            }
            case "actor":
                return JsonPath.Get(JsonPath.From(ctx.Actor().State), rest);
            case "event":
            {
                if (run.Events == null) throw new QaStepException("No event source in this run.");
                await run.Events.FetchAsync(token).ConfigureAwait(false);
                string type = rest[0];
                string? player = ctx.Step.Actor != null ? ctx.Actor().DevPlayerId : null;
                return JsonPath.From(run.Events.Count(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase)
                    && (player == null || string.Equals(e.Player, player, StringComparison.Ordinal))));
            }
            case "var":
            {
                if (!run.Variables.TryGetValue(rest[0], out JsonElement v)) return null;
                return JsonPath.Get(v, rest[1..]);
            }
            case "group":
                // Stress (D38): group.<name>.size / joined / alive / role / stats.<counter> (tool-side, no server query).
                return JsonPath.Get(run.Groups.Describe(rest[0]), rest[1..]);
            default:
                throw new QaStepException(CheckPath(path) ?? $"Unknown path '{path}'.");
        }
    }

    private static async Task<JsonElement?> ResolveBuildAsync(string[] rest, StepContext ctx, CancellationToken token)
    {
        QaPosition? at = ctx.Position("at");
        float? radius = ctx.Double("radius") is double r ? (float)r : null;
        JsonElement build = await ctx.Run.Server.GetBuildAsync(at?.X, at?.Z, radius, null, token).ConfigureAwait(false);
        if (rest.Length == 0) return build;
        string head = rest[0].ToLowerInvariant();
        if (head is "exists" or "piece")
        {
            JsonElement? id = ctx.Param("pieceId") ?? throw new QaStepException($"build.{rest[0]} needs 'pieceId'.");
            JsonElement? piece = null;
            if (JsonPath.Child(build, "pieces") is { ValueKind: JsonValueKind.Array } pieces)
            {
                foreach (JsonElement p in pieces.EnumerateArray())
                {
                    if (Comparison.AreEqual(JsonPath.Child(p, "id"), id.Value)) piece = p;
                }
            }
            if (head == "exists") return JsonPath.From(piece != null);
            return JsonPath.Get(piece, rest[1..]);
        }
        return JsonPath.Get(build, rest);
    }

    private static async Task<JsonElement?> ResolveServerAsync(string[] rest, StepContext ctx, CancellationToken token)
    {
        RunContext run = ctx.Run;
        if (rest.Length == 0) throw new QaStepException("'server' needs a field (running, tickP95Ms, memoryMB, activeSessions, health.*, metrics.*).");
        string head = rest[0];
        if (head.Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                JsonElement health = await run.Server.GetHealthAsync(token).ConfigureAwait(false);
                return JsonPath.From(JsonPath.Child(health, "ok") is not { ValueKind: JsonValueKind.False });
            }
            catch (QaApiException)
            {
                return JsonPath.From(false);
            }
        }
        if (head.Equals("health", StringComparison.OrdinalIgnoreCase))
            return JsonPath.Get(await run.Server.GetHealthAsync(token).ConfigureAwait(false), rest[1..]);
        int? window = ctx.Int("windowSeconds", 1, 120);
        JsonElement metrics = await run.Server.GetMetricsAsync(window, token).ConfigureAwait(false);
        if (head.Equals("metrics", StringComparison.OrdinalIgnoreCase)) return JsonPath.Get(metrics, rest[1..]);
        if (rest.Length == 1 && s_metricAliases.TryGetValue(head, out string[][]? candidates))
        {
            foreach (string[] c in candidates)
            {
                JsonElement? v = JsonPath.Get(metrics, c);
                if (v != null) return v;
            }
            if (head.Equals("activeSessions", StringComparison.OrdinalIgnoreCase))
                return JsonPath.Get(await run.Server.GetHealthAsync(token).ConfigureAwait(false), "activeSessions");
            return null;
        }
        return JsonPath.Get(metrics, rest);
    }

    // §28 `player.state`: Alive / Dead / Graced (kept for reconnect) / Disconnected, from the player DTO's flags.
    // 기능: 서버 플레이어 DTO에서 player.state 값을 만든다(Graced, Disconnected, Phase 14 Downed, Alive, Dead 순서로 본다).
    // 입력: player - /qa/players의 한 플레이어.
    // 출력: 상태 이름.
    private static string DerivedPlayerState(JsonElement player)
    {
        bool Flag(string name) => JsonPath.Child(player, name) is { ValueKind: JsonValueKind.True };
        if (Flag("graced")) return "Graced";
        if (JsonPath.Child(player, "connected") is { ValueKind: JsonValueKind.False }) return "Disconnected";
        if (Flag("downed")) return "Downed";   // Phase 14: knocked down (alive)
        return Flag("alive") ? "Alive" : "Dead";
    }

    private static string NameOf(JsonElement value, Type enumType)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        if (value.TryGetInt32(out int n) && n >= 0 && n <= byte.MaxValue) return Enum.GetName(enumType, Enum.ToObject(enumType, n)) ?? n.ToString();
        return value.GetRawText();
    }
}
