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
    public static readonly string[] Roots = { "player", "match", "build", "server", "network", "actor", "event", "var" };

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
        if (root is "event" or "var" && path.Split('.').Length < 2) return $"'{path}' needs a name after '{root}.'.";
        return null;
    }

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
            case "server":
                return await ResolveServerAsync(rest, ctx, token).ConfigureAwait(false);
            case "network":
            {
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
    private static string DerivedPlayerState(JsonElement player)
    {
        bool Flag(string name) => JsonPath.Child(player, name) is { ValueKind: JsonValueKind.True };
        if (Flag("graced")) return "Graced";
        if (JsonPath.Child(player, "connected") is { ValueKind: JsonValueKind.False }) return "Disconnected";
        return Flag("alive") ? "Alive" : "Dead";
    }

    private static string NameOf(JsonElement value, Type enumType)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        if (value.TryGetInt32(out int n) && n >= 0 && n <= byte.MaxValue) return Enum.GetName(enumType, Enum.ToObject(enumType, n)) ?? n.ToString();
        return value.GetRawText();
    }
}
