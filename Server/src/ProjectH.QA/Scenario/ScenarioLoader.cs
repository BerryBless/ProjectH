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

    // 기능: 시나리오 파일의 존재·크기(MaxFileBytes)를 확인한 뒤 읽어 파싱한다.
    // 입력: path - 시나리오 파일 경로.
    // 출력: 파싱 결과. 파일이 없거나 너무 크면 시나리오 없이 오류 하나만 담긴 결과.
    public static ScenarioLoadResult LoadFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return ScenarioLoadResult.Fail($"File not found: {path}");
        if (info.Length > MaxFileBytes) return ScenarioLoadResult.Fail($"File too large ({info.Length} bytes, max {MaxFileBytes}).");
        return Parse(File.ReadAllText(path), Path.GetFullPath(path));
    }

    // 기능: 시나리오 JSON을 DTO로 읽는다. 형식 오류는 모아서 돌려주고(멈추지 않음), Action 검증은 하지 않는다.
    // 입력: json - 시나리오 JSON 텍스트, sourcePath - 파일 경로(이름 기본값과 SourcePath에 쓰임, 테스트 텍스트는 빈 문자열).
    // 출력: 시나리오와 오류 목록. JSON이 깨졌거나 객체가 아니면 시나리오 없이 오류 하나.
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

    // 기능: `parameters` 배열을 파라미터 세트 목록으로 읽는다.
    // 입력: root - 시나리오 JSON 객체, errors - 형식 오류를 모을 목록.
    // 출력: 복제된 세트 객체 목록(MaxParameterSets까지). 없으면 빈 목록. 배열이 아니거나 비었거나 객체가 아닌 항목은 errors에 기록된다.
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

    // 기능: `baseline.values`와 `baselineWarnPercent`를 읽는다.
    // 입력: root - 시나리오 JSON 객체, errors - 형식 오류를 모을 목록.
    // 출력: (기준선 값 이름 목록, 경고 비율). 없으면 (빈 목록, 기본 50). 모르는 baseline 필드·잘못된 형식은 errors에 기록된다.
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

    // 기능: `server` 객체(mode, qaUrl, host, gamePort, options)를 ServerSpec으로 읽는다.
    // 입력: root - 시나리오 JSON 객체, errors - 형식 오류를 모을 목록.
    // 출력: ServerSpec. server가 없거나 객체가 아니면 기본값(launch). options 값은 문자열이 아니면 원문 JSON 텍스트로 보관된다.
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

    // 기능: 액터의 `unity` 객체를 UnitySpec으로 읽는다.
    // 입력: actor - 액터 JSON 객체, errors - 형식 오류를 모을 목록.
    // 출력: UnitySpec. unity가 없거나 null이거나 객체가 아니면 null. 모르는 필드·잘못된 형식은 errors에 기록된다.
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

    // 기능: 단계 객체를 StepDefinition으로 읽는다. 공통 필드 외는 Params로 모으고, "assert" 약식을 action=assert·path로 바꾸며, phase 선언을 다음 선언까지 이어 EffectivePhase를 정한다.
    // 입력: s - 단계 JSON, index - 단계 인덱스, phase - 현재 유효한 phase(선언이 있으면 갱신됨), errors - 형식 오류를 모을 목록, actors - 선언된 액터(ActorType 조회).
    // 출력: StepDefinition. 객체가 아니거나 action이 없으면 null(오류는 errors에).
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

    // 기능: 액터의 `network.proxy` 값을 읽는다.
    // 입력: actor - 액터 JSON 객체, errors - 형식 오류를 모을 목록.
    // 출력: proxy가 true면 true. 없거나 false이거나 형식 오류면 false(오류는 errors에).
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

    // 기능: 객체의 문자열 필드를 읽는다.
    // 입력: e - JSON 객체, name - 필드 이름, errors - 형식 오류를 모을 목록.
    // 출력: 문자열 값. 없거나 null이면 null, 문자열이 아니면 errors에 기록하고 null.
    private static string? ReadString(JsonElement e, string name, List<string> errors)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        errors.Add($"'{name}' must be a string.");
        return null;
    }

    // 기능: 객체의 정수 필드를 읽는다.
    // 입력: e - JSON 객체, name - 필드 이름, errors - 형식 오류를 모을 목록.
    // 출력: int 값. 없거나 null이면 null, 32비트 정수가 아니면 errors에 기록하고 null.
    private static int? ReadInt(JsonElement e, string name, List<string> errors)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
        errors.Add($"'{name}' must be an integer.");
        return null;
    }

    // 기능: 객체의 숫자 필드를 읽는다.
    // 입력: e - JSON 객체, name - 필드 이름, errors - 형식 오류를 모을 목록.
    // 출력: double 값. 없거나 null이면 null, 숫자가 아니면 errors에 기록하고 null.
    private static double? ReadDouble(JsonElement e, string name, List<string> errors)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        errors.Add($"'{name}' must be a number.");
        return null;
    }

    // 기능: 객체의 bool 필드를 읽는다.
    // 입력: e - JSON 객체, name - 필드 이름, errors - 형식 오류를 모을 목록.
    // 출력: bool 값. 없거나 null이면 null, true/false가 아니면 errors에 기록하고 null.
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
    // 기능: 파싱 결과를 만든다.
    // 입력: scenario - 읽힌 시나리오(파싱 불가면 null), errors - 형식 오류 목록.
    // 출력: 시나리오와 오류가 담긴 결과.
    public ScenarioLoadResult(ScenarioDefinition? scenario, IReadOnlyList<string> errors)
    {
        Scenario = scenario;
        Errors = errors;
    }

    public ScenarioDefinition? Scenario { get; }
    public IReadOnlyList<string> Errors { get; }
    // Unparseable: no scenario at all (malformed JSON, not an object, unreadable file).
    public bool Malformed => Scenario == null;

    // 기능: 시나리오 없이 오류 하나만 담긴 결과를 만든다.
    // 입력: error - 오류 메시지.
    // 출력: Malformed가 true인 결과.
    public static ScenarioLoadResult Fail(string error) => new(null, new[] { error });
}
