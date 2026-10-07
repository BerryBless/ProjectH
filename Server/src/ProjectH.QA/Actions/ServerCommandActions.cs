using System.Text.Json;

namespace ProjectH.QA;

// D11, request §22-23: Arrange commands forwarded to POST /qa/command. The step's parameters become `args` as written
// (after `${var}` substitution); a `position` parameter (marker name or {x,y?,z}) is expanded to x, y?, z (and yaw
// from a marker). `player` is the actor's DevPlayerId. saveAs stores the command's `result` object.
public static class ServerCommandActions
{
    private const int MaxRetries = 2;   // 503 (queue full) and 504 (not executed in time) are safe to retry

    public static void Register(ActionRegistry r)
    {
        r.Add(Command("mark", ActorUse.Optional, new[] { "text" }));
        r.Add(Command("setPosition", ActorUse.Required, new[] { "position|x" }, new[] { "y", "z", "yaw" }, positions: true));
        r.Add(Command("setHealth", ActorUse.Required, new[] { "value" }));
        r.Add(Command("setShield", ActorUse.Required, new[] { "value" }));
        r.Add(Command("giveWeapon", ActorUse.Required, new[] { "weapon" }, new[] { "rarity", "slot", "select" }));
        r.Add(Command("giveAmmo", ActorUse.Required, new[] { "type", "amount" }));
        r.Add(Command("giveItem", ActorUse.Required, new[] { "item", "count" }));
        r.Add(Command("giveResource", ActorUse.Required, new[] { "material", "amount" }));
        r.Add(Command("damagePlayer", ActorUse.Required, new[] { "amount" }, arrangeOnly: true));
        r.Add(Command("killPlayer", ActorUse.Required, Array.Empty<string>(), arrangeOnly: true));
        r.Add(Command("forceMatchState", ActorUse.None, new[] { "state" }));
        // 'zonePhase' is sent as args.phase: a step's own 'phase' field is arrange/act/assert.
        r.Add(Command("setZone", ActorUse.None, new[] { "zonePhase|advance" }, new[] { "zonePhase", "advance" }));
        r.Add(Command("spawnLoot", ActorUse.None, new[] { "kind", "position|x" }, new[] { "id", "rarity", "amount", "y", "z" }, positions: true));
        r.Add(Command("spawnBuildPiece", ActorUse.None, new[] { "piece", "material", "position|x|cellX" },
            new[] { "y", "z", "cellZ", "level", "rotation" }, positions: true));
        r.Add(Command("damageBuild", ActorUse.None, new[] { "pieceId", "amount" }));
        r.Add(Command("editBuild", ActorUse.None, new[] { "pieceId", "edit" }, new[] { "rotation" }));   // Phase 13.5
        // Phase 14: knock a player down at once (needs a standing teammate), give it an eliminated teammate's reboot card
        // ('owner' = that teammate's DevPlayerId), set a reboot station's cooldown (0 = ready).
        r.Add(Command("downPlayer", ActorUse.Required, Array.Empty<string>(), arrangeOnly: true));
        r.Add(Command("giveRebootCard", ActorUse.Required, new[] { "owner" }, arrangeOnly: true));
        r.Add(Command("setStationCooldown", ActorUse.None, new[] { "station", "seconds" }));
    }

    private static DelegateAction Command(string name, ActorUse actor, string[] required, string[]? optional = null,
        bool positions = false, bool arrangeOnly = false) =>
        new(new ActionSpec
        {
            Name = name,
            Actor = actor,
            Required = required,
            Optional = optional ?? Array.Empty<string>(),
            PositionParams = positions ? new[] { "position" } : Array.Empty<string>(),
            ServerCommand = true,
            ArrangeOnly = arrangeOnly,
        }, (ctx, token) => RunAsync(name, ctx, token));

    public static JsonElement BuildArgs(StepContext ctx)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (string key in ctx.Step.Params.Keys)
        {
            if (key == "position") continue;
            args[key == "zonePhase" ? "phase" : key] = ctx.Param(key)!.Value;
        }
        if (ctx.Position("position") is QaPosition p)
        {
            args["x"] = JsonPath.From(p.X);
            args["z"] = JsonPath.From(p.Z);
            if (p.Y != null) args["y"] = JsonPath.From(p.Y.Value);
            if (p.Yaw != null && !args.ContainsKey("yaw") && ctx.Step.Action == "setPosition") args["yaw"] = JsonPath.From(p.Yaw.Value);
        }
        return JsonPath.From(args);
    }

    private static async Task<StepOutcome> RunAsync(string command, StepContext ctx, CancellationToken token)
    {
        string? player = ctx.Step.Actor != null ? ctx.Actor().DevPlayerId : null;
        JsonElement args = BuildArgs(ctx);
        CommandResponse response;
        int attempt = 0;
        while (true)
        {
            response = await ctx.Run.Server.CommandAsync(command, player, args, ctx.Run.RunId, token).ConfigureAwait(false);
            if (response.Ok || response.StatusCode is not (503 or 504) || attempt++ >= MaxRetries || ctx.TimedOut) break;
            await Task.Delay(ctx.Run.PollIntervalMs, token).ConfigureAwait(false);
        }
        if (response.Ok)
            return StepOutcome.Pass(response.Result != null ? JsonPath.Truncate(QaJson.Text(response.Result.Value), 200) : null, response.Result);
        return StepOutcome.Fail($"{command} refused: {response.Error} (HTTP {response.StatusCode})", "ok", response.Error);
    }
}
