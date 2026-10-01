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
        if (StatsIntervalSeconds < 1) return "--stats-interval must be at least 1.";
        return null;
    }

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

    private static bool Set(string value, Action<string> set)
    {
        set(value);
        return true;
    }

    private static bool SetInt(string value, Action<int> set)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return false;
        set(parsed);
        return true;
    }

    // DevPlayerId of bot i (0-based): "bot-001". Within ProtocolConstants.MaxDevPlayerIdBytes.
    public string BotName(int index) => $"{NamePrefix}-{index + 1:000}";
}
