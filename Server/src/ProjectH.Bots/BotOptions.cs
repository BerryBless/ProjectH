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
    // Review fix B1: the server's public key (RSA.ToXmlString(false) text) the bots encrypt their session keys with.
    // --server-public-key <path> reads it from a file; the default is the development key (DevServerPublicKey.Xml).
    public string ServerPublicKeyXml { get; set; } = DevServerPublicKey.Xml;

    // 기능: 설정을 검사한다(리뷰 수정 B1: 서버 공개키가 RSA-2048 XML인지 포함).
    // 입력: 없음.
    // 출력: 맞으면 null, 틀리면 이유.
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) return "--host must not be empty.";
        if (!IsPublicKey(ServerPublicKeyXml)) return "--server-public-key must be an RSA-2048 public key in RSA.ToXmlString form.";
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

    // 기능: 명령줄을 읽는다(리뷰 수정 B1: --server-public-key 파일 포함).
    // 입력: args - 명령줄, options·error - 결과를 받을 곳.
    // 출력: 맞으면 true와 설정, 아니면 false와 이유.
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
                "--server-public-key" => SetFile(value, v => parsed.ServerPublicKeyXml = v),
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

    // 기능: 파일 내용을 읽어 넘긴다(--server-public-key). 파일이 없거나 읽지 못하면 false.
    // 입력: path - 파일 경로, set - 받을 곳.
    // 출력: 읽었으면 true.
    private static bool SetFile(string path, Action<string> set)
    {
        try
        {
            set(File.ReadAllText(path).Trim());
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SetBool(string value, Action<bool> set)
    {
        if (!bool.TryParse(value, out bool parsed)) return false;
        set(parsed);
        return true;
    }

    private static bool SetInt(string value, Action<int> set)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return false;
        set(parsed);
        return true;
    }

    // 기능: 문자열이 RSA-2048 공개키 XML인지 본다.
    // 입력: xml - 검사할 문자열.
    // 출력: 읽히고 2048비트면 true.
    private static bool IsPublicKey(string xml)
    {
        try
        {
            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.FromXmlString(xml);
            return rsa.KeySize == 2048;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // DevPlayerId of bot i (0-based): "bot-001". Within ProtocolConstants.MaxDevPlayerIdBytes.
    public string BotName(int index) => $"{NamePrefix}-{index + 1:000}";
}
