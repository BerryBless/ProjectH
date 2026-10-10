using Microsoft.AspNetCore.Http;
using ProjectH.Monitoring.Storage;

namespace ProjectH.Monitoring.Api;

public sealed record Thresholds(double TickP95WarningMs, long MemoryWarningBytes, int OfflineThresholdSeconds, int HistoryMinutes);
public sealed record ServerDetail(string ServerId, ServerState Status, DateTimeOffset LastReceivedAt, DateTimeOffset LastObservedAt,
    IReadOnlyList<string> Warnings, Contracts.ServerMonitoringSnapshot Latest, Thresholds Thresholds);
public sealed record HistoryResponse(string ServerId, int Minutes, IReadOnlyList<MetricSample> Samples);

// Monitoring D12: what the dashboard reads. Snapshots pass through as posted.
public static class ServersEndpoints
{
    private const int DefaultMinutes = 5;

    // 기능: /health와 /api/servers 경로들을 등록한다.
    // 입력: app - 앱.
    // 출력: 반환값 없음. GET /health, /api/servers, /api/servers/{serverId}, /api/servers/{serverId}/metrics가 연결된다.
    public static void MapServersApi(this WebApplication app)
    {
        app.MapGet("/health", (MetricStore store) => Results.Ok(new { status = "ok", servers = store.Count }));

        app.MapGet("/api/servers", (MetricStore store, TimeProvider time) => Results.Ok(store.List(time.GetUtcNow())));

        app.MapGet("/api/servers/{serverId}", (string serverId, MetricStore store, TimeProvider time, MonitoringServerOptions o) =>
        {
            ServerSummary? s = store.Get(serverId, time.GetUtcNow());
            if (s == null) return Results.NotFound();
            return Results.Ok(new ServerDetail(s.ServerId, s.Status, s.LastReceivedAt, s.LastObservedAt, s.Warnings, s.Latest,
                new Thresholds(o.TickP95WarningMs, o.MemoryWarningBytes, o.OfflineThresholdSeconds, o.HistoryMinutes)));
        });

        // minutes: a bad or missing value is the default, never a 400; the store clamps it to 1-HistoryMinutes.
        app.MapGet("/api/servers/{serverId}/metrics", (string serverId, HttpRequest request, MetricStore store, TimeProvider time, MonitoringServerOptions o) =>
        {
            int minutes = int.TryParse(request.Query["minutes"], out int m) ? Math.Clamp(m, 1, o.HistoryMinutes) : DefaultMinutes;
            IReadOnlyList<MetricSample>? samples = store.History(serverId, minutes, time.GetUtcNow());
            return samples == null ? Results.NotFound() : Results.Ok(new HistoryResponse(serverId, minutes, samples));
        });
    }
}
