using System;
using ProjectH.Monitoring.Contracts;

namespace ProjectH.Server.Monitoring;

// Monitoring D6: bound from the "Monitoring" section of appsettings.json. Off by default: nothing is created. The token
// comes from the environment variable Monitoring__Token, never from appsettings.json (request §62).
public sealed class MonitoringOptions
{
    public bool Enabled { get; set; }
    // The monitoring server's base address; the ingest path is appended (IngestUri).
    public string Endpoint { get; set; } = "http://127.0.0.1:5080";
    public string ServerId { get; set; } = "dev-server-01";
    // A snapshot every IntervalSeconds (request §5: 1-5 s recommended; 5 s is finer than the 10 s Stats line).
    public int IntervalSeconds { get; set; } = 5;
    // Each post gives up after this long; at most the interval, so posts never overlap.
    public int TimeoutSeconds { get; set; } = 2;
    public string Token { get; set; } = "";

    // 기능: Endpoint에 Ingest 경로를 붙인 URI를 만든다(Validate를 통과한 Endpoint 전제).
    // 입력: 없음.
    // 출력: POST할 URI(Endpoint 끝의 '/' 유무와 관계없이 경로가 한 번만 붙는다).
    public Uri IngestUri => new(new Uri(Endpoint.TrimEnd('/') + "/"), MonitoringContract.IngestPath.TrimStart('/'));

    // 기능: 시작 때 설정 값을 검사한다. Enabled가 아니면 주기·Timeout만 본다.
    // 입력: 없음.
    // 출력: 맞으면 null, 틀리면 이유 문장(호스트가 시작하지 않는다).
    public string? Validate()
    {
        if (IntervalSeconds < 1 || IntervalSeconds > 60) return "Monitoring:IntervalSeconds must be 1-60.";
        if (TimeoutSeconds < 1 || TimeoutSeconds > 30) return "Monitoring:TimeoutSeconds must be 1-30.";
        if (TimeoutSeconds > IntervalSeconds) return "Monitoring:TimeoutSeconds must not exceed Monitoring:IntervalSeconds (posts must not overlap).";
        if (!Enabled) return null;
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "Monitoring:Endpoint must be an absolute http or https URL when Monitoring:Enabled is true.";
        if (!MonitoringContract.IsValidServerId(ServerId))
            return $"Monitoring:ServerId must be 1-{MonitoringContract.MaxServerIdLength} characters of [A-Za-z0-9._-].";
        return null;
    }
}
