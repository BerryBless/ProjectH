using System.Globalization;
using System.Text;
using ProjectH.Shared.Protocol;

namespace ProjectH.Bots;

// Phase 7 D9: command-line options of the bot runner. Validate() runs before anything connects.
public sealed class BotOptions
{
    public const int MaxCount = ProtocolConstants.MaxSnapshotEntities;

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7777;
    public int Count { get; set; } = 1;
    public int Seed { get; set; } = 1;
    public int DurationSeconds { get; set; }          // 0 = until Ctrl+C
    public int ConnectIntervalMs { get; set; } = 100;
    public string NamePrefix { get; set; } = "bot";
    public int StatsIntervalSeconds { get; set; } = 10;
    // Phase 10 D11: reconnect after a retryable disconnect (off by default, so load numbers stay comparable).
    public bool Reconnect { get; set; }
    // Phase 13 D17 (request §136, §137): build requests per second per bot (0 = off). Load tests run the server with
    // --Server:BuildInfiniteResources=true.
    public int BuildSpam { get; set; }
    // Phase 13: false = no building at all (load scenario A, comparable with Phase 12: not even defence walls).
    public bool Build { get; set; } = true;

    // 기능: 연결 전에 모든 옵션 값의 범위와 봇 이름 규칙(서버의 DevPlayerId 길이·문자 규칙)을 검사한다.
    // 입력: 없음.
    // 출력: 모두 유효하면 null, 아니면 첫 번째로 걸린 옵션의 오류 메시지.
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) return "--host must not be empty.";
        if (Port < 1 || Port > 65535) return "--port must be 1-65535.";
        if (Count < 1 || Count > MaxCount) return $"--count must be 1-{MaxCount} (a match holds at most {MaxCount} players).";
        if (Seed < 0) return "--seed must be 0 or more.";
        if (DurationSeconds < 0) return "--duration must be 0 (until Ctrl+C) or more.";
        if (ConnectIntervalMs < 0 || ConnectIntervalMs > 10000) return "--connect-interval-ms must be 0-10000.";
        if (string.IsNullOrEmpty(NamePrefix) || NamePrefix.Length > 20) return "--name-prefix must be 1-20 characters.";
        // The name goes into the connect request as DevPlayerId; the server rejects one longer than the limit in UTF-8 bytes.
        if (Encoding.UTF8.GetByteCount(BotName(Count - 1)) > ProtocolConstants.MaxDevPlayerIdBytes)
            return $"--name-prefix is too long: the longest bot name must be at most {ProtocolConstants.MaxDevPlayerIdBytes} UTF-8 bytes.";
        // The server's whole name rule (no control characters, no unpaired surrogate), so a bad prefix fails here, not at connect.
        if (!ProtocolConstants.IsValidPlayerName(BotName(Count - 1)))
            return "--name-prefix must not contain control characters.";
        if (StatsIntervalSeconds < 1) return "--stats-interval must be at least 1.";
        if (BuildSpam < 0 || BuildSpam > 20) return "--build-spam must be 0-20 (the server refuses more than 20 a second).";
        if (!Build && BuildSpam > 0) return "--build-spam needs --build true.";
        return null;
    }

    // 기능: 명령줄 인자를 "--name value" 쌍으로 읽어 옵션을 만들고 Validate()로 검사한다.
    // 입력: args - 명령줄 인자 배열.
    // 출력: 성공하면 true와 파싱된 options(error는 null), 실패하면 false와 error 메시지(options는 일부만 채워진 객체).
    // "--name value" pairs. Unknown names and malformed numbers are errors, so a typo never runs a different test.
    public static bool TryParse(string[] args, out BotOptions options, out string? error)
    {
        var parsed = new BotOptions();
        options = parsed;
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (i + 1 >= args.Length)
            {
                error = $"{name} needs a value.";
                return false;
            }
            string value = args[++i];
            bool ok = name switch
            {
                "--host" => Set(value, v => parsed.Host = v),
                "--port" => SetInt(value, v => parsed.Port = v),
                "--count" => SetInt(value, v => parsed.Count = v),
                "--seed" => SetInt(value, v => parsed.Seed = v),
                "--duration" => SetInt(value, v => parsed.DurationSeconds = v),
                "--connect-interval-ms" => SetInt(value, v => parsed.ConnectIntervalMs = v),
                "--name-prefix" => Set(value, v => parsed.NamePrefix = v),
                "--stats-interval" => SetInt(value, v => parsed.StatsIntervalSeconds = v),
                "--reconnect" => SetBool(value, v => parsed.Reconnect = v),
                "--build-spam" => SetInt(value, v => parsed.BuildSpam = v),
                "--build" => SetBool(value, v => parsed.Build = v),
                _ => false,
            };
            if (!ok)
            {
                error = $"Unknown option or bad value: {name} {value}";
                return false;
            }
        }
        error = parsed.Validate();
        return error == null;
    }

    // 기능: 문자열 옵션 값을 그대로 적용한다.
    // 입력: value - 인자 값, set - 값을 옵션에 넣는 대입 함수.
    // 출력: 항상 true. 해당 옵션이 value로 바뀐다.
    private static bool Set(string value, Action<string> set)
    {
        set(value);
        return true;
    }

    // 기능: 옵션 값을 bool로 파싱해 적용한다.
    // 입력: value - 인자 값("true"/"false"), set - 값을 옵션에 넣는 대입 함수.
    // 출력: 파싱되어 적용되면 true, 형식이 틀리면 false(옵션은 그대로).
    private static bool SetBool(string value, Action<bool> set)
    {
        if (!bool.TryParse(value, out bool parsed)) return false;
        set(parsed);
        return true;
    }

    // 기능: 옵션 값을 InvariantCulture 정수로 파싱해 적용한다.
    // 입력: value - 인자 값, set - 값을 옵션에 넣는 대입 함수.
    // 출력: 파싱되어 적용되면 true, 정수가 아니면 false(옵션은 그대로).
    private static bool SetInt(string value, Action<int> set)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return false;
        set(parsed);
        return true;
    }

    // 기능: 봇 번호로 접속에 쓸 이름(DevPlayerId)을 만든다.
    // 입력: index - 0부터 시작하는 봇 번호.
    // 출력: "{NamePrefix}-{index+1:000}" 형식의 이름(예: "bot-001").
    // DevPlayerId of bot i (0-based): "bot-001". Within ProtocolConstants.MaxDevPlayerIdBytes.
    public string BotName(int index) => $"{NamePrefix}-{index + 1:000}";
}
