using System;
using System.Text.Json;

namespace ProjectH.Server.Game;

// Shared by the data file loaders (weapons.json, items.json, loot.json, zones.json). Startup only.
internal static class DataJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // 기능: 초 단위 시간을 simHz 기준의 정수 Tick 수로 바꾼다(반올림, 최소 1: 30 Hz에서 1.25 s = 37.5 -> 38).
    // 입력: seconds - 초 단위 시간, simHz - 서버 Tick 속도, ticks - 변환된 Tick 수(실패하면 0).
    // 출력: seconds가 유한한 양수이고 결과가 ushort 범위면 true와 Tick 수, 아니면 false.
    // Seconds -> whole ticks at simHz, at least 1: 1.25 s at 30 Hz = 37.5 -> 38.
    public static bool TryTicks(double seconds, int simHz, out ushort ticks)
    {
        ticks = 0;
        if (!double.IsFinite(seconds) || seconds <= 0) return false;
        double value = Math.Round(seconds * simHz, MidpointRounding.AwayFromZero);
        if (value > ushort.MaxValue) return false;
        ticks = (ushort)Math.Max(1, value);
        return true;
    }

    // 기능: JSON 문자열을 T로 역직렬화한다. JsonException은 밖으로 던지지 않고 오류 문자열로 바꾼다.
    // 입력: json - 데이터 파일 내용, value - 역직렬화된 객체(실패하면 null), error - 실패 이유(성공하면 null).
    // 출력: 객체를 얻으면 true, JSON이 잘못됐거나 결과가 null이면 false와 error.
    // Deserialize without throwing past the loader: a JsonException becomes an error string.
    public static bool TryDeserialize<T>(string json, out T? value, out string? error) where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, Options);
            error = value == null ? "the file is empty or null." : null;
            return value != null;
        }
        catch (JsonException ex)
        {
            value = null;
            error = "invalid JSON: " + ex.Message;
            return false;
        }
    }
}
