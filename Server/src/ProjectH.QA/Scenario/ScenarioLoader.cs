using System.Text.Json;

namespace ProjectH.QA;

// Reads a scenario file into the DTOs. Shape errors (wrong JSON type, missing steps) are collected, not thrown, so
// `validate` lists them all; malformed JSON is one error with its line. Nothing here knows the actions: that is
// ScenarioValidator's job.
public static class ScenarioLoader
{
    public const int MaxFileBytes = 1024 * 1024;   // a scenario is a small hand-written file; refuse anything larger
    public const int MaxSteps = 2000;

    private static readonly JsonDocumentOptions s_options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 32,
    };

    public static ScenarioLoadResult LoadFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return ScenarioLoadResult.Fail($"File not found: {path}");
        if (info.Length > MaxFileBytes) return ScenarioLoadResult.Fail($"File too large ({info.Length} bytes, max {MaxFileBytes}).");
        return Parse(File.ReadAllText(path), Path.GetFullPath(path));
    }

    public static ScenarioLoadResult Parse(string json, string sourcePath = "")
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, s_options);
        }
        catch (JsonException e)
        {
            return ScenarioLoadResult.Fail($"Malformed JSON: {e.Message}");
        }

        using (document)
        {
            var errors = new List<string>();
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ScenarioLoadResult.Fail("The scenario must be a JSON object.");

            int schema = ReadInt(root, "schemaVersion", errors) ?? 0;
            string name = ReadString(root, "name", errors) ?? Path.GetFileNameWithoutExtension(sourcePath);
            string description = ReadString(root, "description", errors) ?? string.Empty;
            var tags = new List<string>();
            if (root.TryGetProperty("tags", out JsonElement tagsElement))
            {
                if (tagsElement.ValueKind != JsonValueKind.Array) errors.Add("'tags' must be an array of strings.");
                else
                {
                    foreach (JsonElement tag in tagsElement.EnumerateArray())
                    {
                        if (tag.ValueKind == JsonValueKind.String) tags.Add(tag.GetString()!);
                        else errors.Add("'tags' must be an array of strings.");
                    }
                }
            }
            int? seed = ReadInt(root, "seed", errors);
            double timeout = ReadDouble(root, "timeoutSeconds", errors) ?? ScenarioDefinition.DefaultTimeoutSeconds;
            bool stress = ReadBool(root, "stress", errors) ?? false;

            ServerSpec server = ReadServer(root, errors);

            var variables = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (root.TryGetProperty("variables", out JsonElement vars))
            {
                if (vars.ValueKind != JsonValueKind.Object) errors.Add("'variables' must be an object.");
                else foreach (JsonProperty p in vars.EnumerateObject()) variables[p.Name] = p.Value.Clone();
            }

            List<JsonElement> parameters = ReadParameters(root, errors);
            (List<string> baselineValues, double warnPercent) = ReadBaseline(root, errors);

            var actors = new List<ActorSpec>();
            if (root.TryGetProperty("actors", out JsonElement actorsElement))
            {
                if (actorsElement.ValueKind != JsonValueKind.Array) errors.Add("'actors' must be an array.");
                else
                {
                    int i = 0;
                    foreach (JsonElement a in actorsElement.EnumerateArray())
                    {
                        if (a.ValueKind != JsonValueKind.Object)
                        {
                            errors.Add($"actors[{i}] must be an object.");
                        }
                        else
                        {
                            var actorErrors = new List<string>();
                            string? id = ReadString(a, "id", actorErrors);
                            string type = ReadString(a, "type", actorErrors) ?? ActorSpec.HeadlessClient;
                            foreach (string e in actorErrors) errors.Add($"actors[{i}]: {e}");
                            if (string.IsNullOrEmpty(id)) errors.Add($"actors[{i}]: 'id' is required.");
                            int actorErrorsBefore = actorErrors.Count;
                            bool proxy = ReadActorNetwork(a, actorErrors);
                            UnitySpec? unity = ReadActorUnity(a, actorErrors);
                            foreach (string e in actorErrors.Skip(actorErrorsBefore)) errors.Add($"actors[{i}]: {e}");
                            if (!string.IsNullOrEmpty(id)) actors.Add(new ActorSpec(id, type) { Proxy = proxy, Unity = unity });
                        }
                        i++;
                    }
                }
            }

            var steps = new List<StepDefinition>();
            if (!root.TryGetProperty("steps", out JsonElement stepsElement) || stepsElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add("'steps' must be an array.");
            }
            else
            {
                string? phase = null;
                int index = 0;
                foreach (JsonElement s in stepsElement.EnumerateArray())
                {
                    if (index >= MaxSteps)
                    {
                        errors.Add($"Too many steps (max {MaxSteps}).");
                        break;
                    }
                    StepDefinition? step = ReadStep(s, index, ref phase, errors, actors);
                    if (step != null) steps.Add(step);
                    index++;
                }
            }

            var scenario = new ScenarioDefinition
            {
                SchemaVersion = schema,
                Name = name,
                Description = description,
                Tags = tags,
                Seed = seed,
                TimeoutSeconds = timeout,
                Server = server,
                Variables = variables,
                Actors = actors,
                Steps = steps,
                SourcePath = sourcePath,
                Parameters = parameters,
                BaselineValues = baselineValues,
                BaselineWarnPercent = warnPercent,
                Stress = stress,
            };
            return new ScenarioLoadResult(scenario, errors);
        }
    }

    // D31: `parameters` is an array of objects (at most MaxParameterSets). The names are checked by the validator.
    private static List<JsonElement> ReadParameters(JsonElement root, List<string> errors)
    {
        var list = new List<JsonElement>();
        if (!root.TryGetProperty("parameters", out JsonElement p) || p.ValueKind == JsonValueKind.Null) return list;
        if (p.ValueKind != JsonValueKind.Array)
        {
            errors.Add("'parameters' must be an array of objects, like [ { \"ping\": 0 }, { \"ping\": 100 } ].");
            return list;
        }
        int i = 0;
        foreach (JsonElement set in p.EnumerateArray())
        {
            if (i >= ScenarioDefinition.MaxParameterSets)
            {
                errors.Add($"Too many parameter sets (max {ScenarioDefinition.MaxParameterSets}).");
                break;
            }
            if (set.ValueKind != JsonValueKind.Object) errors.Add($"parameters[{i}] must be an object.");
            else list.Add(set.Clone());
            i++;
        }
        if (i == 0) errors.Add("'parameters' is empty: remove it or give at least one set.");
        return list;
    }

    // D33: `baseline: { "values": ["name", ...] }` and `baselineWarnPercent` (default 50). Unknown baseline fields are
    // errors so a typo does not silently record nothing.
    private static (List<string> Values, double WarnPercent) ReadBaseline(JsonElement root, List<string> errors)
    {
        var values = new List<string>();
        double percent = ReadDouble(root, "baselineWarnPercent", errors) ?? ScenarioDefinition.DefaultBaselineWarnPercent;
        if (!root.TryGetProperty("baseline", out JsonElement b) || b.ValueKind == JsonValueKind.Null) return (values, percent);
        if (b.ValueKind != JsonValueKind.Object)
        {
            errors.Add("'baseline' must be an object like { \"values\": [\"tickP95\"] }.");
            return (values, percent);
        }
        foreach (JsonProperty prop in b.EnumerateObject())
        {
            if (prop.Name != "values")
            {
                errors.Add($"Unknown 'baseline' field '{prop.Name}' (known: values).");
                continue;
            }
            if (prop.Value.ValueKind != JsonValueKind.Array)
            {
                errors.Add("'baseline.values' must be an array of variable names.");
                continue;
            }
            foreach (JsonElement v in prop.Value.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.String) values.Add(v.GetString()!);
                else errors.Add("'baseline.values' must be an array of variable names.");
            }
        }
        return (values, percent);
    }

    private static ServerSpec ReadServer(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("server", out JsonElement s)) return new ServerSpec();
        if (s.ValueKind != JsonValueKind.Object)
        {
            errors.Add("'server' must be an object.");
            return new ServerSpec();
        }
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (s.TryGetProperty("options", out JsonElement o))
        {
            if (o.ValueKind != JsonValueKind.Object) errors.Add("'server.options' must be an object.");
            else
            {
                foreach (JsonProperty p in o.EnumerateObject())
                {
                    options[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
                }
            }
        }
        return new ServerSpec
        {
            Mode = ReadString(s, "mode", errors) ?? ServerSpec.Launch,
            QaUrl = ReadString(s, "qaUrl", errors),
            Host = ReadString(s, "host", errors),
            GamePort = ReadInt(s, "gamePort", errors),
            Options = options,
        };
    }

    // `"unity": { "exe"?, "attachPort"?, "width"?, "height"? }` (QA-4). Unknown fields are errors.
    private static UnitySpec? ReadActorUnity(JsonElement actor, List<string> errors)
    {
        if (!actor.TryGetProperty("unity", out JsonElement u) || u.ValueKind == JsonValueKind.Null) return null;
        if (u.ValueKind != JsonValueKind.Object)
        {
            errors.Add("'unity' must be an object like { \"exe\": \"...\" } or { \"attachPort\": 18777 }.");
            return null;
        }
        foreach (JsonProperty p in u.EnumerateObject())
        {
            if (p.Name is not ("exe" or "attachPort" or "width" or "height")) errors.Add($"Unknown 'unity' field '{p.Name}' (known: exe, attachPort, width, height).");
        }
        return new UnitySpec(ReadString(u, "exe", errors), ReadInt(u, "attachPort", errors), ReadInt(u, "width", errors), ReadInt(u, "height", errors));
    }

    private static StepDefinition? ReadStep(JsonElement s, int index, ref string? phase, List<string> errors, List<ActorSpec> actors)
    {
        string where = $"steps[{index}]";
        if (s.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{where} must be an object.");
            return null;
        }
        var stepErrors = new List<string>();
        string? action = ReadString(s, "action", stepErrors);
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty p in s.EnumerateObject())
        {
            if (Array.IndexOf(StepDefinition.CommonFields, p.Name) >= 0) continue;
            parameters[p.Name] = p.Value.Clone();
        }
        // Shorthand { "assert": "player.health", ... } = { "action": "assert", "path": "player.health", ... }.
        if (parameters.TryGetValue("assert", out JsonElement shorthand))
        {
            if (action != null)
            {
                stepErrors.Add("'assert' shorthand cannot be combined with 'action'.");
            }
            else if (shorthand.ValueKind != JsonValueKind.String)
            {
                stepErrors.Add("'assert' must be a path string.");
            }
            else
            {
                action = "assert";
                parameters.Remove("assert");
                if (parameters.ContainsKey("path")) stepErrors.Add("'assert' shorthand and 'path' both given.");
                else parameters["path"] = shorthand;
            }
        }
        if (string.IsNullOrEmpty(action)) stepErrors.Add("'action' is required.");

        string? declared = ReadString(s, "phase", stepErrors);
        if (declared != null) phase = declared.ToLowerInvariant();
        string id = ReadString(s, "id", stepErrors) ?? $"{index + 1:00}-{action ?? "step"}";
        foreach (string e in stepErrors) errors.Add($"{where} ({id}): {e}");
        if (string.IsNullOrEmpty(action)) return null;

        string? actorId = ReadString(s, "actor", errors);
        return new StepDefinition
        {
            Index = index,
            Id = id,
            Action = action,
            Actor = actorId,
            ActorType = actors.FirstOrDefault(a => a.Id == actorId)?.Type,
            Phase = declared,
            EffectivePhase = phase,
            TimeoutMilliseconds = ReadInt(s, "timeoutMilliseconds", errors),
            ContinueOnFailure = ReadBool(s, "continueOnFailure", errors) ?? false,
            Breakpoint = ReadBool(s, "breakpoint", errors) ?? false,
            SaveAs = ReadString(s, "saveAs", errors),
            Description = ReadString(s, "description", errors),
            Params = parameters,
        };
    }

    // An actor's `"network": { "proxy": true }` (QA-3). Only `proxy` is known; anything else is an error so a typo does
    // not silently run without the proxy.
    private static bool ReadActorNetwork(JsonElement actor, List<string> errors)
    {
        if (!actor.TryGetProperty("network", out JsonElement n) || n.ValueKind == JsonValueKind.Null) return false;
        if (n.ValueKind != JsonValueKind.Object)
        {
            errors.Add("'network' must be an object like { \"proxy\": true }.");
            return false;
        }
        bool proxy = false;
        foreach (JsonProperty p in n.EnumerateObject())
        {
            if (p.Name != "proxy") errors.Add($"Unknown 'network' field '{p.Name}' (known: proxy).");
            else if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) proxy = p.Value.GetBoolean();
            else errors.Add("'network.proxy' must be true or false.");
        }
        return proxy;
    }

    private static string? ReadString(JsonElement e, string name, List<string> errors)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        errors.Add($"'{name}' must be a string.");
        return null;
    }

    private static int? ReadInt(JsonElement e, string name, List<string> errors)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
        errors.Add($"'{name}' must be an integer.");
        return null;
    }

    private static double? ReadDouble(JsonElement e, string name, List<string> errors)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        errors.Add($"'{name}' must be a number.");
        return null;
    }

    private static bool? ReadBool(JsonElement e, string name, List<string> errors)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        errors.Add($"'{name}' must be true or false.");
        return null;
    }
}

public sealed class ScenarioLoadResult
{
    public ScenarioLoadResult(ScenarioDefinition? scenario, IReadOnlyList<string> errors)
    {
        Scenario = scenario;
        Errors = errors;
    }

    public ScenarioDefinition? Scenario { get; }
    public IReadOnlyList<string> Errors { get; }
    // Unparseable: no scenario at all (malformed JSON, not an object, unreadable file).
    public bool Malformed => Scenario == null;

    public static ScenarioLoadResult Fail(string error) => new(null, new[] { error });
}
