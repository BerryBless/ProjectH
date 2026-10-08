# Monitoring Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Game Server가 5초마다 Metric Snapshot을 별도 프로세스인 Monitoring Server로 Push하고, 정적 Web Dashboard에서 Online/Offline·Players·Tick·CPU·Memory·Network·Error를 보는 첫 버전을 만든다.

**Architecture:** `ProjectH.Monitoring.Contracts`(DTO만)를 양쪽이 참조한다. Game Server는 Game Loop 스레드의 `MonitoringCollector`가 자기 `TickMetrics` 창으로 Snapshot을 만들어 용량 1 슬롯에 넣고, `MonitoringSender`(BackgroundService)가 짧은 Timeout으로 POST한다(실패는 상태 변화 때만 로그). Monitoring Server는 ASP.NET Core Minimal API로 검증 → `lock` 하나의 `MetricStore`(서버별 링) → REST → `wwwroot` 정적 페이지(uPlot)다.

**Tech Stack:** .NET 10, ASP.NET Core(Minimal API, Kestrel), System.Text.Json, xunit 2.9.3, uPlot 1.6.31(wwwroot/lib에 고정), 새 NuGet 패키지 없음.

**Spec:** `Docs/specs/2026-10-08-monitoring-design.md`(D1–D18). 요청 원문: `Docs/requests/2026-10-08-monitoring-request.md`(§1–§92).

## Global Constraints

- 브랜치 `monitoring`(ef7bb7b에서 이어서). Commit은 M1(Task 1–5), M2(Task 6–10), M3(Task 11), M4(Task 12–13) 끝에 한다. **Push하지 않는다.** 커밋 메시지 끝에는 세션의 system reminder가 준 attribution 줄을 붙인다. 사용자의 하네스 수정분(`.claude/`, `CLAUDE.md`, `AGENTS.md`, `.agents/`, `.codex/`)은 **절대 add하지 않는다** — 파일을 이름으로 지정해 add한다.
- `ProjectH.Monitoring`과 `ProjectH.Monitoring.Contracts`는 `ProjectH.Server`·`ProjectH.Shared`를 참조하지 않는다(D1, §4). Contracts는 패키지·참조가 0개다.
- 새 NuGet 패키지를 추가하지 않는다. 테스트는 기존 xunit 2.9.3 / Microsoft.NET.Test.Sdk 17.14.1 / xunit.runner.visualstudio 3.1.4 / coverlet.collector 6.0.4를 그대로 쓴다.
- 새로 쓰거나 고친 모든 C# 함수·생성자에 `// 기능: / // 입력: / // 출력:` 세 줄 주석(`code-comments` 스킬, §70). 기존 영어 주석 스타일의 파일에서도 새 함수는 이 형식이다.
- `game-core-rules`: Game Loop에서 Lock·I/O·await 없음, 무한 증가 Collection 없음(링·슬롯만), 외부 입력 검증(Ingest), Lock은 `MetricStore` 하나뿐이고 안에서 로그·I/O·콜백을 부르지 않는다.
- 설정 섹션 이름은 `Monitoring`(Game Server)과 `MonitoringServer`(Monitoring Server). Token은 appsettings.json에 키 자체를 두지 않는다(환경변수 `Monitoring__Token`, `MonitoringServer__IngestToken`).
- Monitoring Server 기본 Bind `http://127.0.0.1:5080`. Ingest Body 상한 16 KB. 기본값: Interval 5 s, Timeout 2 s, History 10 min, Offline 15 s, MaxServers 32, TickP95Warning 16.7 ms, MemoryWarning 0(꺼짐).
- 테스트 실행은 `dotnet test <csproj>`로 프로젝트 하나씩. 전체는 `dotnet test Server/ProjectH.Server.slnx`.
- 실측값만 보고한다(§90·§92). 측정하지 않은 수치를 문서에 쓰지 않는다.

## Review Focus

1. **Game Loop가 멈췄는데 Sender는 살아 있는 경우** — 새 Snapshot이 없으니 아무것도 보내지 않아야 하고, Dashboard는 Offline이 돼야 한다(살아 있는 척하는 Heartbeat 금지). Task 9 테스트 "슬롯이 비어 있으면 요청이 없다"가 고정한다.
2. **Monitoring Server 응답이 느릴 때 요청이 겹치거나 밀리는 경우** — Timeout(2 s) 안에 실패로 끝나고 다음 주기에 새 Snapshot만 보낸다. Task 9 "느린 응답은 Timeout 안에 실패" 테스트.
3. **`MatchHistoryQueue.Count`가 −1(CanCount false)인 경우** — Validator가 음수를 거부해 모든 Snapshot이 400으로 사라진다. Task 8 Collector가 `Math.Max(0, …)`로 막고 테스트한다.
4. **첫 Publish(창 0초)·샘플 0개** — NaN/Infinity가 JSON 직렬화 예외를 내 Sender 루프가 매 주기 예외를 던진다. Task 8 "창 0초에도 유한값" 테스트 + Task 9 Sender는 모든 예외를 실패로 잡는다.
5. **UI가 `minutes`에 큰 값이나 문자를 넣는 경우** — 서버는 Clamp(1–HistoryMinutes)하고 400이 아니라 Clamp된 값으로 답한다. Task 5 E2E "minutes=9999는 HistoryMinutes로 Clamp".

---

### Task 1: Contracts 프로젝트와 Monitoring 테스트 프로젝트 골격

**Files:**
- Create: `Server/src/ProjectH.Monitoring.Contracts/ProjectH.Monitoring.Contracts.csproj`
- Create: `Server/src/ProjectH.Monitoring.Contracts/ServerMonitoringSnapshot.cs`
- Create: `Server/src/ProjectH.Monitoring.Contracts/MonitoringContract.cs`
- Create: `Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`
- Create: `Server/tests/ProjectH.Monitoring.Tests/Contracts/MonitoringContractTests.cs`
- Modify: `Server/ProjectH.Server.slnx`

**Interfaces:**
- Produces: `ProjectH.Monitoring.Contracts.ServerMonitoringSnapshot`(아래 필드 전부, `required string ServerId`), `MonitoringContract.IngestPath = "/api/ingest/metrics"`, `MonitoringContract.TokenHeader = "X-Monitoring-Token"`, `MonitoringContract.MaxBodyBytes = 16 * 1024`, `MonitoringContract.MaxServerIdLength = 64`, `MonitoringContract.MaxTextLength = 64`, `static bool MonitoringContract.IsValidServerId(string? id)`.

- [ ] **Step 1: Contracts csproj**

`Server/src/ProjectH.Monitoring.Contracts/ProjectH.Monitoring.Contracts.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <!-- Monitoring D1: the only thing the game server and the monitoring server share. No package, no project reference. -->

</Project>
```

- [ ] **Step 2: Snapshot DTO**

`Server/src/ProjectH.Monitoring.Contracts/ServerMonitoringSnapshot.cs`:

```csharp
namespace ProjectH.Monitoring.Contracts;

// Monitoring D2: one game server's state at ObservedAt. Rates (PerSecond) and tick percentiles are over the last
// WindowSeconds; GcGen*, Exceptions, InvalidPackets and Disconnects are totals since the server started. Every field is a
// value the server already has; nothing is estimated. Adding a metric = a property here + one line in
// MonitoringCollector.Publish + (if it needs a range check) one line in SnapshotValidator + the UI.
public sealed record ServerMonitoringSnapshot
{
    // Server
    public required string ServerId { get; init; }
    public string Version { get; init; } = "";
    public int ProtocolVersion { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public double UptimeSeconds { get; init; }
    public double WindowSeconds { get; init; }

    // Gameplay (one match per server)
    public int ConnectedPeers { get; init; }
    public int Players { get; init; }
    public int Graced { get; init; }
    public string MatchState { get; init; } = "";
    public int Round { get; init; }

    // Tick (this window)
    public int TickSamples { get; init; }
    public double TickP50Ms { get; init; }
    public double TickP95Ms { get; init; }
    public double TickP99Ms { get; init; }
    public double TickMaxMs { get; init; }

    // Process. CpuPercent: this process, all logical cores = 100 %.
    public double CpuPercent { get; init; }
    public long ManagedMemoryBytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public int GcGen0 { get; init; }
    public int GcGen1 { get; init; }
    public int GcGen2 { get; init; }

    // Network (this window, application-level LiteNetLib payloads)
    public double PacketsInPerSecond { get; init; }
    public double PacketsOutPerSecond { get; init; }
    public double BytesInPerSecond { get; init; }
    public double BytesOutPerSecond { get; init; }

    // Errors (totals). Exceptions = tick + loop + player + callback failures.
    public long Exceptions { get; init; }
    public long InvalidPackets { get; init; }
    public long Disconnects { get; init; }

    // Queue: the match history (database) queue.
    public int DbQueueCount { get; init; }
    public int DbQueueCapacity { get; init; }
}
```

- [ ] **Step 3: 상수와 ServerId 규칙**

`Server/src/ProjectH.Monitoring.Contracts/MonitoringContract.cs`:

```csharp
namespace ProjectH.Monitoring.Contracts;

// Monitoring D1, D8: what both sides must agree on besides the snapshot shape.
public static class MonitoringContract
{
    public const string IngestPath = "/api/ingest/metrics";
    public const string TokenHeader = "X-Monitoring-Token";
    // Kestrel body limit on the monitoring server; a snapshot is about 1 KB.
    public const int MaxBodyBytes = 16 * 1024;
    public const int MaxServerIdLength = 64;
    // Version and MatchState.
    public const int MaxTextLength = 64;

    // 기능: ServerId가 규칙(1-64자, [A-Za-z0-9._-])에 맞는지 본다. Game Server는 시작 때, Monitoring Server는 Ingest 때 같은 규칙을 쓴다.
    // 입력: id - 검사할 값(null 허용).
    // 출력: 맞으면 true.
    public static bool IsValidServerId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxServerIdLength) return false;
        foreach (char c in id)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
            if (!ok) return false;
        }
        return true;
    }
}
```

- [ ] **Step 4: 테스트 프로젝트**

`Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`(Monitoring 프로젝트 참조는 Task 2에서 더한다):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\ProjectH.Monitoring.Contracts\ProjectH.Monitoring.Contracts.csproj" />
  </ItemGroup>

</Project>
```

`Server/tests/ProjectH.Monitoring.Tests/Contracts/MonitoringContractTests.cs`:

```csharp
using ProjectH.Monitoring.Contracts;

namespace ProjectH.Monitoring.Tests.Contracts;

public class MonitoringContractTests
{
    [Theory]
    [InlineData("dev-server-01")]
    [InlineData("a")]
    [InlineData("GameServer_02.eu")]
    public void IsValidServerId_AcceptsLettersDigitsDotUnderscoreDash(string id) => Assert.True(MonitoringContract.IsValidServerId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/id")]
    [InlineData("한글")]
    public void IsValidServerId_RejectsEmptyAndOtherCharacters(string? id) => Assert.False(MonitoringContract.IsValidServerId(id));

    [Fact]
    public void IsValidServerId_RejectsOver64Characters()
    {
        Assert.True(MonitoringContract.IsValidServerId(new string('a', 64)));
        Assert.False(MonitoringContract.IsValidServerId(new string('a', 65)));
    }

    [Fact]
    public void Snapshot_RoundTripsThroughJson_WithEveryField()
    {
        var s = new ServerMonitoringSnapshot { ServerId = "dev-server-01", Version = "1.0.0", ProtocolVersion = 18, StartedAt = DateTimeOffset.UnixEpoch,
            ObservedAt = DateTimeOffset.UnixEpoch.AddSeconds(5), UptimeSeconds = 5, WindowSeconds = 5, ConnectedPeers = 3, Players = 2, Graced = 1,
            MatchState = "Playing", Round = 4, TickSamples = 150, TickP50Ms = 0.2, TickP95Ms = 0.5, TickP99Ms = 0.9, TickMaxMs = 3.1, CpuPercent = 4.2,
            ManagedMemoryBytes = 1, WorkingSetBytes = 2, GcGen0 = 3, GcGen1 = 4, GcGen2 = 5, PacketsInPerSecond = 6, PacketsOutPerSecond = 7,
            BytesInPerSecond = 8, BytesOutPerSecond = 9, Exceptions = 10, InvalidPackets = 11, Disconnects = 12, DbQueueCount = 1, DbQueueCapacity = 16 };
        string json = System.Text.Json.JsonSerializer.Serialize(s, System.Text.Json.JsonSerializerOptions.Web);
        var back = System.Text.Json.JsonSerializer.Deserialize<ServerMonitoringSnapshot>(json, System.Text.Json.JsonSerializerOptions.Web);
        Assert.Equal(s, back);
        Assert.True(json.Length < MonitoringContract.MaxBodyBytes / 8, $"a snapshot must stay small; this one is {json.Length} bytes");
    }

    [Fact]
    public void Snapshot_WithoutServerId_FailsToDeserialize()
    {
        Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<ServerMonitoringSnapshot>("{\"players\":1}", System.Text.Json.JsonSerializerOptions.Web));
    }
}
```

- [ ] **Step 5: slnx에 추가**

`Server/ProjectH.Server.slnx`의 `/src/` 폴더에 `<Project Path="src/ProjectH.Monitoring.Contracts/ProjectH.Monitoring.Contracts.csproj" />`, `/tests/` 폴더에 `<Project Path="tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj" />`를 더한다(알파벳 순서 유지).

- [ ] **Step 6: 테스트 실행**

Run: `dotnet test Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`
Expected: 4개 테스트(Theory 포함 11 케이스) PASS. `Snapshot_WithoutServerId_FailsToDeserialize`가 실패하면 .NET의 `required` 처리가 바뀐 것이니 `[JsonRequired]`를 `ServerId`에 더한다.

---

### Task 2: Monitoring Server 프로젝트와 `MonitoringServerOptions`

**Files:**
- Create: `Server/src/ProjectH.Monitoring/ProjectH.Monitoring.csproj`
- Create: `Server/src/ProjectH.Monitoring/MonitoringServerOptions.cs`
- Create: `Server/src/ProjectH.Monitoring/appsettings.json`
- Create: `Server/src/ProjectH.Monitoring/Program.cs`(임시: Task 5에서 `MonitoringApp`으로 바꾼다)
- Modify: `Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`
- Create: `Server/tests/ProjectH.Monitoring.Tests/MonitoringServerOptionsTests.cs`
- Modify: `Server/ProjectH.Server.slnx`

**Interfaces:**
- Produces: `ProjectH.Monitoring.MonitoringServerOptions { int HistoryMinutes=10; int ExpectedIntervalSeconds=5; int OfflineThresholdSeconds=15; int SweepIntervalSeconds=5; int MaxServers=32; int MaxClockSkewSeconds=300; double TickP95WarningMs=16.7; long MemoryWarningBytes=0; string IngestToken=""; int HistoryCapacity (계산); string? Validate() }`.

- [ ] **Step 1: 프로젝트**

`Server/src/ProjectH.Monitoring/ProjectH.Monitoring.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <!-- Monitoring D1: the DTO only. Never ProjectH.Server or ProjectH.Shared. -->
    <ProjectReference Include="..\ProjectH.Monitoring.Contracts\ProjectH.Monitoring.Contracts.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- The content root is the build output (MonitoringApp.Create), as in ProjectH.Server: run from anywhere. -->
    <Content Update="wwwroot\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="ProjectH.Monitoring.Tests" />
  </ItemGroup>

</Project>
```

`Server/src/ProjectH.Monitoring/appsettings.json`(Token 키 없음):

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    },
    "Console": {
      "FormatterName": "simple",
      "FormatterOptions": {
        "TimestampFormat": "yyyy-MM-dd HH:mm:ss.fff "
      }
    }
  },
  "Urls": "http://127.0.0.1:5080",
  "MonitoringServer": {
    "HistoryMinutes": 10,
    "ExpectedIntervalSeconds": 5,
    "OfflineThresholdSeconds": 15,
    "SweepIntervalSeconds": 5,
    "MaxServers": 32,
    "MaxClockSkewSeconds": 300,
    "TickP95WarningMs": 16.7,
    "MemoryWarningBytes": 0
  }
}
```

임시 `Program.cs`(Task 5가 바꾼다):

```csharp
var app = WebApplication.CreateBuilder(args).Build();
app.Run();
```

- [ ] **Step 2: 실패하는 Options 테스트**

테스트 csproj에 `<ProjectReference Include="..\..\src\ProjectH.Monitoring\ProjectH.Monitoring.csproj" />`와 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`(새 ItemGroup)를 더한다.

`Server/tests/ProjectH.Monitoring.Tests/MonitoringServerOptionsTests.cs`:

```csharp
namespace ProjectH.Monitoring.Tests;

public class MonitoringServerOptionsTests
{
    [Fact]
    public void Defaults_AreValid_AndHold120Samples()
    {
        var o = new MonitoringServerOptions();
        Assert.Null(o.Validate());
        Assert.Equal(120, o.HistoryCapacity);   // 10 min × 60 / 5 s
        Assert.Equal(0, o.MemoryWarningBytes);  // off until measured (D11)
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void Validate_RejectsHistoryMinutesOutOfRange(int minutes) => Assert.NotNull(new MonitoringServerOptions { HistoryMinutes = minutes }.Validate());

    [Fact]
    public void Validate_RequiresOfflineThresholdOfAtLeastTwoIntervals()
    {
        Assert.NotNull(new MonitoringServerOptions { ExpectedIntervalSeconds = 5, OfflineThresholdSeconds = 9 }.Validate());
        Assert.Null(new MonitoringServerOptions { ExpectedIntervalSeconds = 5, OfflineThresholdSeconds = 10 }.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void Validate_RejectsMaxServersOutOfRange(int max) => Assert.NotNull(new MonitoringServerOptions { MaxServers = max }.Validate());

    [Fact]
    public void Validate_RejectsNegativeThresholds()
    {
        Assert.NotNull(new MonitoringServerOptions { TickP95WarningMs = -1 }.Validate());
        Assert.NotNull(new MonitoringServerOptions { MemoryWarningBytes = -1 }.Validate());
        Assert.NotNull(new MonitoringServerOptions { MaxClockSkewSeconds = 0 }.Validate());
        Assert.NotNull(new MonitoringServerOptions { SweepIntervalSeconds = 0 }.Validate());
    }

    [Fact]
    public void HistoryCapacity_IsAtLeastOne()
    {
        Assert.Equal(2, new MonitoringServerOptions { HistoryMinutes = 1, ExpectedIntervalSeconds = 30, OfflineThresholdSeconds = 60 }.HistoryCapacity);
        Assert.Equal(1, new MonitoringServerOptions { HistoryMinutes = 1, ExpectedIntervalSeconds = 60, OfflineThresholdSeconds = 120 }.HistoryCapacity);
    }
}
```

- [ ] **Step 3: 실패 확인**

Run: `dotnet test Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`
Expected: 컴파일 오류(`MonitoringServerOptions` 없음).

- [ ] **Step 4: Options 구현**

`Server/src/ProjectH.Monitoring/MonitoringServerOptions.cs`:

```csharp
namespace ProjectH.Monitoring;

// Monitoring D11: bound from the "MonitoringServer" section. Validate() runs when the app is created, so a bad value stops
// the process at start instead of producing an empty ring or a server that is never offline.
public sealed class MonitoringServerOptions
{
    public int HistoryMinutes { get; set; } = 10;
    // How often a game server is expected to post (its Monitoring:IntervalSeconds); sizes the ring with HistoryMinutes.
    public int ExpectedIntervalSeconds { get; set; } = 5;
    // No snapshot for this long = offline. 3 intervals by default: one failed post (2 s timeout) does not flip the state.
    public int OfflineThresholdSeconds { get; set; } = 15;
    public int SweepIntervalSeconds { get; set; } = 5;
    public int MaxServers { get; set; } = 32;
    // ObservedAt farther than this from the monitoring clock is rejected (a wrong clock would misplace the sample).
    public int MaxClockSkewSeconds { get; set; } = 300;
    // 16.7 ms = half of the 33.3 ms tick budget at 30 Hz (D11).
    public double TickP95WarningMs { get; set; } = 16.7;
    // 0 = off until the 50-player working set is measured (D11, request §90).
    public long MemoryWarningBytes { get; set; }
    // Empty = ingest is not authenticated. Set with the environment variable MonitoringServer__IngestToken, never in appsettings.
    public string IngestToken { get; set; } = "";

    // 기능: 서버별 링 용량을 계산한다(HistoryMinutes × 60 / ExpectedIntervalSeconds, 최소 1).
    // 입력: 없음.
    // 출력: 샘플 수.
    public int HistoryCapacity => Math.Max(1, HistoryMinutes * 60 / Math.Max(1, ExpectedIntervalSeconds));

    // 기능: 시작 때 설정 값을 검사한다.
    // 입력: 없음.
    // 출력: 맞으면 null, 틀리면 이유.
    public string? Validate()
    {
        if (HistoryMinutes < 1 || HistoryMinutes > 120) return "MonitoringServer:HistoryMinutes must be 1-120.";
        if (ExpectedIntervalSeconds < 1 || ExpectedIntervalSeconds > 60) return "MonitoringServer:ExpectedIntervalSeconds must be 1-60.";
        if (OfflineThresholdSeconds < ExpectedIntervalSeconds * 2 || OfflineThresholdSeconds > 3600)
            return "MonitoringServer:OfflineThresholdSeconds must be at least 2 × ExpectedIntervalSeconds and at most 3600.";
        if (SweepIntervalSeconds < 1 || SweepIntervalSeconds > 60) return "MonitoringServer:SweepIntervalSeconds must be 1-60.";
        if (MaxServers < 1 || MaxServers > 1000) return "MonitoringServer:MaxServers must be 1-1000.";
        if (MaxClockSkewSeconds < 1 || MaxClockSkewSeconds > 86400) return "MonitoringServer:MaxClockSkewSeconds must be 1-86400.";
        if (!double.IsFinite(TickP95WarningMs) || TickP95WarningMs < 0) return "MonitoringServer:TickP95WarningMs must be 0 or more.";
        if (MemoryWarningBytes < 0) return "MonitoringServer:MemoryWarningBytes must be 0 (off) or more.";
        return null;
    }
}
```

- [ ] **Step 5: 통과 확인, slnx**

slnx `/src/`에 `<Project Path="src/ProjectH.Monitoring/ProjectH.Monitoring.csproj" />` 추가.
Run: `dotnet test Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`
Expected: 모두 PASS.

---

### Task 3: `SnapshotValidator`

**Files:**
- Create: `Server/src/ProjectH.Monitoring/Ingest/SnapshotValidator.cs`
- Create: `Server/tests/ProjectH.Monitoring.Tests/Ingest/SnapshotValidatorTests.cs`

**Interfaces:**
- Consumes: `ServerMonitoringSnapshot`, `MonitoringContract.IsValidServerId/MaxTextLength`.
- Produces: `static string? SnapshotValidator.Validate(ServerMonitoringSnapshot s, DateTimeOffset now, int maxClockSkewSeconds)` — null = 유효.

- [ ] **Step 1: 실패하는 테스트**

`Server/tests/ProjectH.Monitoring.Tests/Ingest/SnapshotValidatorTests.cs`:

```csharp
using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Ingest;

namespace ProjectH.Monitoring.Tests.Ingest;

public class SnapshotValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    // A snapshot that passes; tests change one field.
    internal static ServerMonitoringSnapshot Valid(string serverId = "dev-server-01", DateTimeOffset? observedAt = null) => new()
    {
        ServerId = serverId, Version = "1.0.0", ProtocolVersion = 18, StartedAt = Now.AddMinutes(-10), ObservedAt = observedAt ?? Now,
        UptimeSeconds = 600, WindowSeconds = 5, ConnectedPeers = 2, Players = 2, Graced = 0, MatchState = "Playing", Round = 1,
        TickSamples = 150, TickP50Ms = 0.2, TickP95Ms = 0.4, TickP99Ms = 0.8, TickMaxMs = 2, CpuPercent = 3, ManagedMemoryBytes = 50_000_000,
        WorkingSetBytes = 120_000_000, GcGen0 = 10, GcGen1 = 2, GcGen2 = 0, PacketsInPerSecond = 60, PacketsOutPerSecond = 30,
        BytesInPerSecond = 3000, BytesOutPerSecond = 9000, Exceptions = 0, InvalidPackets = 0, Disconnects = 1, DbQueueCount = 0, DbQueueCapacity = 16,
    };

    [Fact]
    public void Valid_ReturnsNull() => Assert.Null(SnapshotValidator.Validate(Valid(), Now, 300));

    [Theory]
    [InlineData("")]
    [InlineData("bad id")]
    public void BadServerId_IsRejected(string id) => Assert.Contains("serverId", SnapshotValidator.Validate(Valid(id), Now, 300));

    [Fact]
    public void MissingObservedAt_IsRejected() => Assert.Contains("observedAt", SnapshotValidator.Validate(Valid() with { ObservedAt = default }, Now, 300));

    [Theory]
    [InlineData(301)]
    [InlineData(-301)]
    public void ObservedAtOutsideClockSkew_IsRejected(int seconds) =>
        Assert.Contains("observedAt", SnapshotValidator.Validate(Valid(observedAt: Now.AddSeconds(seconds)), Now, 300));

    [Fact]
    public void ObservedAtInsideClockSkew_IsAccepted() => Assert.Null(SnapshotValidator.Validate(Valid(observedAt: Now.AddSeconds(-299)), Now, 300));

    [Fact]
    public void StartedAtAfterObservedAt_IsRejected() => Assert.Contains("startedAt", SnapshotValidator.Validate(Valid() with { StartedAt = Now.AddSeconds(1) }, Now, 300));

    [Fact]
    public void NegativeCount_IsRejected()
    {
        Assert.NotNull(SnapshotValidator.Validate(Valid() with { Players = -1 }, Now, 300));
        Assert.NotNull(SnapshotValidator.Validate(Valid() with { DbQueueCount = -1 }, Now, 300));
        Assert.NotNull(SnapshotValidator.Validate(Valid() with { WorkingSetBytes = -1 }, Now, 300));
        Assert.NotNull(SnapshotValidator.Validate(Valid() with { Exceptions = -1 }, Now, 300));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.5)]
    public void NonFiniteOrNegativeDouble_IsRejected(double value)
    {
        Assert.Contains("tickP95Ms", SnapshotValidator.Validate(Valid() with { TickP95Ms = value }, Now, 300));
        Assert.Contains("cpuPercent", SnapshotValidator.Validate(Valid() with { CpuPercent = value }, Now, 300));
        Assert.Contains("bytesOutPerSecond", SnapshotValidator.Validate(Valid() with { BytesOutPerSecond = value }, Now, 300));
    }

    [Fact]
    public void CpuAbove100_IsAccepted() => Assert.Null(SnapshotValidator.Validate(Valid() with { CpuPercent = 100.4 }, Now, 300));

    [Fact]
    public void LongText_IsRejected()
    {
        Assert.Contains("version", SnapshotValidator.Validate(Valid() with { Version = new string('v', 65) }, Now, 300));
        Assert.Contains("matchState", SnapshotValidator.Validate(Valid() with { MatchState = new string('m', 65) }, Now, 300));
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`
Expected: 컴파일 오류(`SnapshotValidator` 없음).

- [ ] **Step 3: 구현**

`Server/src/ProjectH.Monitoring/Ingest/SnapshotValidator.cs`:

```csharp
using ProjectH.Monitoring.Contracts;

namespace ProjectH.Monitoring.Ingest;

// Monitoring D8: the one place a posted snapshot is checked. JSON shape errors (NaN tokens, a missing ServerId) are
// System.Text.Json's; this checks the values. Field names in the messages are camelCase, as in the JSON.
public static class SnapshotValidator
{
    // 기능: Snapshot 값을 검사한다(ServerId 규칙, 문자열 길이, 시각, 음수, 비유한값).
    // 입력: s - 받은 Snapshot, now - Monitoring Server의 지금, maxClockSkewSeconds - ObservedAt이 now에서 벗어날 수 있는 초.
    // 출력: 맞으면 null, 틀리면 이유(첫 번째 것).
    public static string? Validate(ServerMonitoringSnapshot s, DateTimeOffset now, int maxClockSkewSeconds)
    {
        if (!MonitoringContract.IsValidServerId(s.ServerId))
            return $"serverId must be 1-{MonitoringContract.MaxServerIdLength} characters of [A-Za-z0-9._-]";
        if (s.Version == null || s.Version.Length > MonitoringContract.MaxTextLength) return $"version is missing or longer than {MonitoringContract.MaxTextLength} characters";
        if (s.MatchState == null || s.MatchState.Length > MonitoringContract.MaxTextLength) return $"matchState is missing or longer than {MonitoringContract.MaxTextLength} characters";
        if (s.ObservedAt == default) return "observedAt is missing";
        if (Math.Abs((now - s.ObservedAt).TotalSeconds) > maxClockSkewSeconds) return $"observedAt is more than {maxClockSkewSeconds} s away from the monitoring clock";
        if (s.StartedAt == default || s.StartedAt > s.ObservedAt) return "startedAt is missing or after observedAt";
        if (s.ProtocolVersion < 0 || s.ConnectedPeers < 0 || s.Players < 0 || s.Graced < 0 || s.Round < 0 || s.TickSamples < 0
            || s.GcGen0 < 0 || s.GcGen1 < 0 || s.GcGen2 < 0 || s.DbQueueCount < 0 || s.DbQueueCapacity < 0
            || s.ManagedMemoryBytes < 0 || s.WorkingSetBytes < 0 || s.Exceptions < 0 || s.InvalidPackets < 0 || s.Disconnects < 0)
            return "a count is negative";
        // CpuPercent has no upper bound: it is normalized to all cores and rounding can put it a little over 100.
        return Check("uptimeSeconds", s.UptimeSeconds) ?? Check("windowSeconds", s.WindowSeconds)
            ?? Check("tickP50Ms", s.TickP50Ms) ?? Check("tickP95Ms", s.TickP95Ms) ?? Check("tickP99Ms", s.TickP99Ms) ?? Check("tickMaxMs", s.TickMaxMs)
            ?? Check("cpuPercent", s.CpuPercent)
            ?? Check("packetsInPerSecond", s.PacketsInPerSecond) ?? Check("packetsOutPerSecond", s.PacketsOutPerSecond)
            ?? Check("bytesInPerSecond", s.BytesInPerSecond) ?? Check("bytesOutPerSecond", s.BytesOutPerSecond);
    }

    // 기능: double 하나가 유한한 0 이상인지 본다.
    // 입력: name - JSON 이름, value - 값.
    // 출력: 맞으면 null, 아니면 이유.
    private static string? Check(string name, double value) =>
        double.IsFinite(value) && value >= 0 ? null : $"{name} is not a finite non-negative number";
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`
Expected: 모두 PASS.

---

### Task 4: 저장소 — `MetricStore`, `ServerHistory`, `ServerStatus`

**Files:**
- Create: `Server/src/ProjectH.Monitoring/Storage/MetricSample.cs`
- Create: `Server/src/ProjectH.Monitoring/Storage/ServerHistory.cs`
- Create: `Server/src/ProjectH.Monitoring/Storage/MetricStore.cs`
- Create: `Server/src/ProjectH.Monitoring/Status/ServerStatus.cs`
- Create: `Server/tests/ProjectH.Monitoring.Tests/Storage/MetricStoreTests.cs`
- Create: `Server/tests/ProjectH.Monitoring.Tests/ManualTime.cs`

**Interfaces:**
- Consumes: `MonitoringServerOptions`, `ServerMonitoringSnapshot`, `SnapshotValidatorTests.Valid(...)`(테스트 fixture).
- Produces:
  - `readonly record struct MetricSample(DateTimeOffset ReceivedAt, ServerMonitoringSnapshot Snapshot)`
  - `enum IngestOutcome { Stored, FirstSeen, Returned, TooManyServers }`
  - `enum ServerState { Online, Warning, Offline }`
  - `sealed record ServerSummary(string ServerId, ServerState Status, DateTimeOffset LastReceivedAt, DateTimeOffset LastObservedAt, IReadOnlyList<string> Warnings, ServerMonitoringSnapshot Latest)`
  - `MetricStore(MonitoringServerOptions)`: `IngestOutcome Add(ServerMonitoringSnapshot, DateTimeOffset receivedAt)`, `IReadOnlyList<ServerSummary> List(DateTimeOffset now)`, `ServerSummary? Get(string, DateTimeOffset now)`, `IReadOnlyList<MetricSample>? History(string, int minutes, DateTimeOffset now)`(null = 모르는 서버; minutes는 1–HistoryMinutes로 Clamp), `IReadOnlyList<(string ServerId, DateTimeOffset LastSeen)> SweepOffline(DateTimeOffset now)`, `int Count`.
  - `internal static ServerState ServerStatus.Of(ServerHistory h, DateTimeOffset now, MonitoringServerOptions o, List<string> warnings)`

- [ ] **Step 1: 테스트용 ManualTime**

`Server/tests/ProjectH.Monitoring.Tests/ManualTime.cs`(Server.Tests의 것과 같은 모양; 테스트 프로젝트는 서로 참조하지 않는다):

```csharp
using System.Threading;

namespace ProjectH.Monitoring.Tests;

// A clock that moves only when a test says so. Timestamps are TimeSpan ticks.
public sealed class ManualTime : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(GetTimestamp());

    // 기능: 시계를 앞으로 돌린다.
    // 입력: by - 더할 시간.
    // 출력: 반환값 없음.
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
```

- [ ] **Step 2: 실패하는 테스트**

`Server/tests/ProjectH.Monitoring.Tests/Storage/MetricStoreTests.cs`:

```csharp
using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Storage;
using ProjectH.Monitoring.Tests.Ingest;

namespace ProjectH.Monitoring.Tests.Storage;

public class MetricStoreTests
{
    // Ring of 2 (1 min / 30 s), offline after 60 s.
    private static MonitoringServerOptions Small(int maxServers = 32) =>
        new() { HistoryMinutes = 1, ExpectedIntervalSeconds = 30, OfflineThresholdSeconds = 60, MaxServers = maxServers };

    private static ServerMonitoringSnapshot At(ManualTime time, string id = "s1", double p95 = 0.4) =>
        SnapshotValidatorTests.Valid(id, time.GetUtcNow()) with { StartedAt = time.GetUtcNow().AddMinutes(-1), TickP95Ms = p95 };

    [Fact]
    public void FirstSnapshot_RegistersTheServer_AndIsTheLatest()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());
        Assert.Equal(IngestOutcome.FirstSeen, store.Add(At(time), time.GetUtcNow()));
        Assert.Equal(1, store.Count);
        ServerSummary s = Assert.Single(store.List(time.GetUtcNow()));
        Assert.Equal("s1", s.ServerId);
        Assert.Equal(ServerState.Online, s.Status);
        Assert.Equal(time.GetUtcNow(), s.LastReceivedAt);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void SecondSnapshot_IsStored_AndReplacesTheLatest()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());
        store.Add(At(time), time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(IngestOutcome.Stored, store.Add(At(time) with { Players = 7 }, time.GetUtcNow()));
        Assert.Equal(7, store.Get("s1", time.GetUtcNow())!.Latest.Players);
    }

    [Fact]
    public void Ring_KeepsOnlyTheNewestCapacitySamples_InOrder()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());   // capacity 2
        for (int i = 1; i <= 3; i++)
        {
            store.Add(At(time) with { Round = i }, time.GetUtcNow());
            time.Advance(TimeSpan.FromSeconds(10));
        }
        IReadOnlyList<MetricSample> h = store.History("s1", 1, time.GetUtcNow())!;
        Assert.Equal(new[] { 2, 3 }, h.Select(s => s.Snapshot.Round));
        Assert.True(h[0].ReceivedAt < h[1].ReceivedAt);
    }

    [Fact]
    public void History_ReturnsOnlyTheRequestedMinutes_AndClampsTheRequest()
    {
        var time = new ManualTime();
        var store = new MetricStore(new MonitoringServerOptions { HistoryMinutes = 10, ExpectedIntervalSeconds = 5, OfflineThresholdSeconds = 15 });
        for (int i = 0; i < 12; i++)   // one per minute for 11 minutes
        {
            store.Add(At(time) with { Round = i }, time.GetUtcNow());
            time.Advance(TimeSpan.FromMinutes(1));
        }
        // now = 12 min; the last sample was at 11 min. 2 minutes back = samples at 10 and 11.
        Assert.Equal(new[] { 10, 11 }, store.History("s1", 2, time.GetUtcNow())!.Select(s => s.Snapshot.Round));
        // 9999 clamps to HistoryMinutes (10): samples at 2..11.
        Assert.Equal(10, store.History("s1", 9999, time.GetUtcNow())!.Count);
        // 0 clamps to 1: the sample at 11 only.
        Assert.Equal(new[] { 11 }, store.History("s1", 0, time.GetUtcNow())!.Select(s => s.Snapshot.Round));
        Assert.Null(store.History("unknown", 1, time.GetUtcNow()));
    }

    [Fact]
    public void Servers_AreSeparated_AndListedByName()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());
        store.Add(At(time, "zeta") with { Players = 1 }, time.GetUtcNow());
        store.Add(At(time, "alpha") with { Players = 2 }, time.GetUtcNow());
        IReadOnlyList<ServerSummary> list = store.List(time.GetUtcNow());
        Assert.Equal(new[] { "alpha", "zeta" }, list.Select(s => s.ServerId));
        Assert.Equal(2, store.Get("alpha", time.GetUtcNow())!.Latest.Players);
        Assert.Equal(1, store.Get("zeta", time.GetUtcNow())!.Latest.Players);
        Assert.Single(store.History("alpha", 1, time.GetUtcNow())!);
    }

    [Fact]
    public void MaxServers_RefusesANewServer_ButNotAKnownOne()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small(maxServers: 1));
        Assert.Equal(IngestOutcome.FirstSeen, store.Add(At(time, "s1"), time.GetUtcNow()));
        Assert.Equal(IngestOutcome.TooManyServers, store.Add(At(time, "s2"), time.GetUtcNow()));
        Assert.Equal(IngestOutcome.Stored, store.Add(At(time, "s1"), time.GetUtcNow()));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Offline_AfterTheThreshold_WithLastSeen_AndSweepReportsItOnce()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());
        store.Add(At(time), time.GetUtcNow());
        DateTimeOffset lastSeen = time.GetUtcNow();
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(ServerState.Online, store.Get("s1", time.GetUtcNow())!.Status);   // exactly the threshold: still online
        Assert.Empty(store.SweepOffline(time.GetUtcNow()));
        time.Advance(TimeSpan.FromSeconds(1));
        ServerSummary s = store.Get("s1", time.GetUtcNow())!;
        Assert.Equal(ServerState.Offline, s.Status);
        Assert.Equal(lastSeen, s.LastReceivedAt);
        Assert.Contains(s.Warnings, w => w.StartsWith("offline"));
        var swept = Assert.Single(store.SweepOffline(time.GetUtcNow()));
        Assert.Equal(("s1", lastSeen), swept);
        Assert.Empty(store.SweepOffline(time.GetUtcNow()));   // logged once
    }

    [Fact]
    public void ASnapshotAfterOffline_IsReturned_AndSweepCanFireAgainLater()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());
        store.Add(At(time), time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(61));
        store.SweepOffline(time.GetUtcNow());
        Assert.Equal(IngestOutcome.Returned, store.Add(At(time), time.GetUtcNow()));
        Assert.Equal(ServerState.Online, store.Get("s1", time.GetUtcNow())!.Status);
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Single(store.SweepOffline(time.GetUtcNow()));
    }

    [Fact]
    public void Returned_IsDecidedByTheGap_EvenWithoutASweep()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());
        store.Add(At(time), time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(IngestOutcome.Returned, store.Add(At(time), time.GetUtcNow()));
    }

    [Fact]
    public void Warning_WhenTickP95OrMemoryReachesTheThreshold()
    {
        var time = new ManualTime();
        var o = Small();
        o.TickP95WarningMs = 16.7;
        o.MemoryWarningBytes = 1_000_000_000;
        var store = new MetricStore(o);
        store.Add(At(time, p95: 16.7), time.GetUtcNow());
        ServerSummary s = store.Get("s1", time.GetUtcNow())!;
        Assert.Equal(ServerState.Warning, s.Status);
        Assert.Contains(s.Warnings, w => w.Contains("tick p95"));
        store.Add(At(time, p95: 1) with { WorkingSetBytes = 1_000_000_000 }, time.GetUtcNow());
        s = store.Get("s1", time.GetUtcNow())!;
        Assert.Equal(ServerState.Warning, s.Status);
        Assert.Contains(s.Warnings, w => w.Contains("working set"));
        store.Add(At(time, p95: 1), time.GetUtcNow());
        Assert.Equal(ServerState.Online, store.Get("s1", time.GetUtcNow())!.Status);
    }

    [Fact]
    public void MemoryWarningOff_NeverWarnsOnMemory()
    {
        var time = new ManualTime();
        var store = new MetricStore(Small());   // MemoryWarningBytes 0
        store.Add(At(time) with { WorkingSetBytes = long.MaxValue }, time.GetUtcNow());
        Assert.Equal(ServerState.Online, store.Get("s1", time.GetUtcNow())!.Status);
    }

    [Fact]
    public void ConcurrentAdds_FromManyServers_LoseNothing()
    {
        var time = new ManualTime();
        var store = new MetricStore(new MonitoringServerOptions { HistoryMinutes = 10, ExpectedIntervalSeconds = 1, OfflineThresholdSeconds = 15, MaxServers = 64 });
        Parallel.For(0, 32, server =>
        {
            for (int i = 0; i < 100; i++) store.Add(At(time, $"s{server}") with { Round = i }, time.GetUtcNow());
        });
        Assert.Equal(32, store.Count);
        foreach (ServerSummary s in store.List(time.GetUtcNow()))
        {
            Assert.Equal(99, s.Latest.Round);
            Assert.Equal(100, store.History(s.ServerId, 10, time.GetUtcNow())!.Count);
        }
    }
}
```

- [ ] **Step 3: 실패 확인**

Run: `dotnet test Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`
Expected: 컴파일 오류.

- [ ] **Step 4: 구현**

`Server/src/ProjectH.Monitoring/Storage/MetricSample.cs`:

```csharp
using ProjectH.Monitoring.Contracts;

namespace ProjectH.Monitoring.Storage;

// Monitoring D9: one stored snapshot. ReceivedAt is this server's clock (request §21); Snapshot.ObservedAt is the game
// server's.
public readonly record struct MetricSample(DateTimeOffset ReceivedAt, ServerMonitoringSnapshot Snapshot);

public enum IngestOutcome
{
    Stored,          // a known, online server
    FirstSeen,       // a new ServerId (logged as "first seen")
    Returned,        // a known server that had been offline for OfflineThresholdSeconds (logged as "online again")
    TooManyServers,  // a new ServerId when MaxServers are already known (refused)
}

public enum ServerState
{
    Online,
    Warning,
    Offline,
}

// What the API shows for one server. Latest is the snapshot as posted (opaque to the store and the API, D12).
public sealed record ServerSummary(string ServerId, ServerState Status, DateTimeOffset LastReceivedAt, DateTimeOffset LastObservedAt,
    IReadOnlyList<string> Warnings, ServerMonitoringSnapshot Latest);
```

`Server/src/ProjectH.Monitoring/Storage/ServerHistory.cs`:

```csharp
using ProjectH.Monitoring.Contracts;

namespace ProjectH.Monitoring.Storage;

// Monitoring D9: one server's ring of the newest samples. Owned by MetricStore and touched only under its lock.
internal sealed class ServerHistory
{
    private readonly MetricSample[] _ring;
    private int _next;
    private int _count;

    // 기능: 첫 샘플을 담은 링을 만든다.
    // 입력: capacity - 샘플 수(1 이상), first - 첫 샘플.
    // 출력: 샘플 하나가 든 ServerHistory.
    public ServerHistory(int capacity, MetricSample first)
    {
        _ring = new MetricSample[capacity];
        LastReceivedAt = first.ReceivedAt;
        Latest = first.Snapshot;
        Add(first);
    }

    public int Count => _count;
    public DateTimeOffset LastReceivedAt { get; private set; }
    public ServerMonitoringSnapshot Latest { get; private set; }
    // Set by SweepOffline when "offline" was logged; cleared by the next snapshot.
    public bool OfflineLogged { get; set; }

    // 기능: 샘플을 링에 넣는다(가득 차면 가장 오래된 것을 덮는다).
    // 입력: sample - 받은 샘플.
    // 출력: 반환값 없음.
    public void Add(MetricSample sample)
    {
        _ring[_next] = sample;
        _next = (_next + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
        LastReceivedAt = sample.ReceivedAt;
        Latest = sample.Snapshot;
    }

    // 기능: from 이후에 받은 샘플을 오래된 순서로 복사한다.
    // 입력: from - 이 시각 이상만, into - 담을 목록.
    // 출력: 반환값 없음(into에 더한다).
    public void CopySince(DateTimeOffset from, List<MetricSample> into)
    {
        int oldest = (_next - _count + _ring.Length) % _ring.Length;
        for (int i = 0; i < _count; i++)
        {
            MetricSample s = _ring[(oldest + i) % _ring.Length];
            if (s.ReceivedAt >= from) into.Add(s);
        }
    }
}
```

`Server/src/ProjectH.Monitoring/Status/ServerStatus.cs`:

```csharp
using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Storage;

namespace ProjectH.Monitoring.Status;

// Monitoring D9, D10: the state is computed when read, from the last receipt time and the configured thresholds.
internal static class ServerStatus
{
    // 기능: 서버 하나의 상태(Online/Warning/Offline)를 정하고 경고 문장을 모은다.
    // 입력: h - 서버 기록, now - 지금, o - 임계값 설정, warnings - 경고를 담을 목록.
    // 출력: 상태.
    public static ServerState Of(ServerHistory h, DateTimeOffset now, MonitoringServerOptions o, List<string> warnings)
    {
        double silent = (now - h.LastReceivedAt).TotalSeconds;
        if (silent > o.OfflineThresholdSeconds)
        {
            warnings.Add($"offline: no snapshot for {silent:0} s (threshold {o.OfflineThresholdSeconds} s)");
            return ServerState.Offline;
        }
        ServerMonitoringSnapshot s = h.Latest;
        if (s.TickP95Ms >= o.TickP95WarningMs) warnings.Add($"tick p95 {s.TickP95Ms:0.00} ms is at or above {o.TickP95WarningMs} ms");
        if (o.MemoryWarningBytes > 0 && s.WorkingSetBytes >= o.MemoryWarningBytes)
            warnings.Add($"working set {s.WorkingSetBytes / 1048576.0:0} MB is at or above {o.MemoryWarningBytes / 1048576.0:0} MB");
        return warnings.Count > 0 ? ServerState.Warning : ServerState.Online;
    }
}
```

`Server/src/ProjectH.Monitoring/Storage/MetricStore.cs`:

```csharp
using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Status;

namespace ProjectH.Monitoring.Storage;

// Monitoring D9: every server's ring behind one lock. Writers: ingest requests (one per game server every few seconds).
// Readers: the UI's polling. Nothing inside the lock logs, waits or calls out, and no other lock exists in this process,
// so there is no deadlock to consider. Lifetime: the process (restart = empty, D10).
public sealed class MetricStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ServerHistory> _servers = new(StringComparer.Ordinal);
    private readonly MonitoringServerOptions _options;

    // 기능: 설정에 맞는 빈 저장소를 만든다.
    // 입력: options - 검증이 끝난 설정.
    // 출력: MetricStore.
    public MetricStore(MonitoringServerOptions options) => _options = options;

    public int Count
    {
        get { lock (_gate) return _servers.Count; }
    }

    // 기능: 검증이 끝난 Snapshot을 그 서버의 링에 넣는다(새 서버는 등록, MaxServers면 거절).
    // 입력: snapshot - 받은 값, receivedAt - 받은 시각(이 서버의 시계).
    // 출력: 호출자가 로그를 남길 결과(Lock 밖에서).
    public IngestOutcome Add(ServerMonitoringSnapshot snapshot, DateTimeOffset receivedAt)
    {
        var sample = new MetricSample(receivedAt, snapshot);
        lock (_gate)
        {
            if (!_servers.TryGetValue(snapshot.ServerId, out ServerHistory? history))
            {
                if (_servers.Count >= _options.MaxServers) return IngestOutcome.TooManyServers;
                _servers.Add(snapshot.ServerId, new ServerHistory(_options.HistoryCapacity, sample));
                return IngestOutcome.FirstSeen;
            }
            bool returned = (receivedAt - history.LastReceivedAt).TotalSeconds > _options.OfflineThresholdSeconds || history.OfflineLogged;
            history.OfflineLogged = false;
            history.Add(sample);
            return returned ? IngestOutcome.Returned : IngestOutcome.Stored;
        }
    }

    // 기능: 모든 서버의 요약을 이름순으로 돌려준다(최신 Snapshot만, History는 복사하지 않는다).
    // 입력: now - 지금.
    // 출력: 요약 목록(호출자 소유).
    public IReadOnlyList<ServerSummary> List(DateTimeOffset now)
    {
        lock (_gate)
        {
            var result = new List<ServerSummary>(_servers.Count);
            foreach ((string id, ServerHistory h) in _servers) result.Add(Summary(id, h, now));
            result.Sort((a, b) => string.CompareOrdinal(a.ServerId, b.ServerId));
            return result;
        }
    }

    // 기능: 서버 하나의 요약을 돌려준다.
    // 입력: serverId - 서버, now - 지금.
    // 출력: 요약, 모르는 서버면 null.
    public ServerSummary? Get(string serverId, DateTimeOffset now)
    {
        lock (_gate) return _servers.TryGetValue(serverId, out ServerHistory? h) ? Summary(serverId, h, now) : null;
    }

    // 기능: 최근 minutes분의 샘플을 오래된 순서로 복사한다. minutes는 1-HistoryMinutes로 Clamp한다(무제한 조회 불가, D12).
    // 입력: serverId - 서버, minutes - 구간, now - 지금.
    // 출력: 샘플 목록(호출자 소유), 모르는 서버면 null.
    public IReadOnlyList<MetricSample>? History(string serverId, int minutes, DateTimeOffset now)
    {
        minutes = Math.Clamp(minutes, 1, _options.HistoryMinutes);
        DateTimeOffset from = now - TimeSpan.FromMinutes(minutes);
        lock (_gate)
        {
            if (!_servers.TryGetValue(serverId, out ServerHistory? h)) return null;
            var result = new List<MetricSample>(h.Count);
            h.CopySince(from, result);
            return result;
        }
    }

    // 기능: 새로 Offline이 된 서버를 찾아 표시한다(서버마다 한 번만 돌려준다; 다음 Snapshot이 표시를 지운다).
    // 입력: now - 지금.
    // 출력: (ServerId, 마지막 수신 시각) 목록. 없으면 빈 목록.
    public IReadOnlyList<(string ServerId, DateTimeOffset LastSeen)> SweepOffline(DateTimeOffset now)
    {
        List<(string, DateTimeOffset)>? result = null;
        lock (_gate)
        {
            foreach ((string id, ServerHistory h) in _servers)
            {
                if (h.OfflineLogged || (now - h.LastReceivedAt).TotalSeconds <= _options.OfflineThresholdSeconds) continue;
                h.OfflineLogged = true;
                (result ??= new List<(string, DateTimeOffset)>()).Add((id, h.LastReceivedAt));
            }
        }
        return result ?? (IReadOnlyList<(string, DateTimeOffset)>)Array.Empty<(string, DateTimeOffset)>();
    }

    // 기능: 서버 기록 하나를 요약으로 만든다(Lock 안에서 부른다).
    // 입력: id - 서버, h - 기록, now - 지금.
    // 출력: 요약.
    private ServerSummary Summary(string id, ServerHistory h, DateTimeOffset now)
    {
        var warnings = new List<string>();
        ServerState state = ServerStatus.Of(h, now, _options, warnings);
        return new ServerSummary(id, state, h.LastReceivedAt, h.Latest.ObservedAt, warnings, h.Latest);
    }
}
```

- [ ] **Step 5: 통과 확인**

Run: `dotnet test Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`
Expected: 모두 PASS.

---

### Task 5: `MonitoringApp` — Ingest·API·Health·OfflineSweeper·E2E (M1 Commit)

**Files:**
- Create: `Server/src/ProjectH.Monitoring/MonitoringApp.cs`
- Create: `Server/src/ProjectH.Monitoring/Ingest/IngestLog.cs`
- Create: `Server/src/ProjectH.Monitoring/Ingest/IngestEndpoint.cs`
- Create: `Server/src/ProjectH.Monitoring/Status/OfflineSweeper.cs`
- Create: `Server/src/ProjectH.Monitoring/Api/ServersEndpoints.cs`
- Modify: `Server/src/ProjectH.Monitoring/Program.cs`
- Create: `Server/tests/ProjectH.Monitoring.Tests/MonitoringAppTests.cs`

**Interfaces:**
- Consumes: `MetricStore`, `SnapshotValidator`, `MonitoringServerOptions`, `MonitoringContract`.
- Produces: `static WebApplication MonitoringApp.Create(string[] args, TimeProvider? time = null)`; 경로 `POST /api/ingest/metrics`(202/400/401/413/429), `GET /health`, `GET /api/servers`, `GET /api/servers/{serverId}`, `GET /api/servers/{serverId}/metrics?minutes=N`; API DTO `ServerDetail(ServerSummary + Thresholds)`, `Thresholds(double TickP95WarningMs, long MemoryWarningBytes, int OfflineThresholdSeconds, int HistoryMinutes)`, `HistoryResponse(string ServerId, int Minutes, IReadOnlyList<MetricSample> Samples)`.

- [ ] **Step 1: 실패하는 E2E 테스트**

`Server/tests/ProjectH.Monitoring.Tests/MonitoringAppTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Tests.Ingest;

namespace ProjectH.Monitoring.Tests;

// Request §66: the real app on a loopback port, a fake game server posting, the API read back.
public class MonitoringAppTests : IAsyncLifetime
{
    private readonly ManualTime _time = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _app = MonitoringApp.Create(["--Urls=http://127.0.0.1:0", "--MonitoringServer:IngestToken=secret", "--MonitoringServer:SweepIntervalSeconds=1"], _time);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private ServerMonitoringSnapshot Snapshot(string id = "dev-server-01") =>
        SnapshotValidatorTests.Valid(id, _time.GetUtcNow()) with { StartedAt = _time.GetUtcNow().AddMinutes(-1) };

    private async Task<HttpResponseMessage> PostAsync(object body, string? token = "secret")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MonitoringContract.IngestPath) { Content = JsonContent.Create(body, options: JsonSerializerOptions.Web) };
        if (token != null) request.Headers.Add(MonitoringContract.TokenHeader, token);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task ValidPost_IsAccepted_AndReadBackThroughEveryApi()
    {
        var s = Snapshot() with { Players = 5, TickP95Ms = 0.42 };
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(s)).StatusCode);

        using JsonDocument list = JsonDocument.Parse(await _client.GetStringAsync("/api/servers"));
        JsonElement first = Assert.Single(list.RootElement.EnumerateArray());
        Assert.Equal("dev-server-01", first.GetProperty("serverId").GetString());
        Assert.Equal("online", first.GetProperty("status").GetString());
        Assert.Equal(5, first.GetProperty("latest").GetProperty("players").GetInt32());
        Assert.Equal(_time.GetUtcNow(), first.GetProperty("lastReceivedAt").GetDateTimeOffset());

        using JsonDocument detail = JsonDocument.Parse(await _client.GetStringAsync("/api/servers/dev-server-01"));
        Assert.Equal(0.42, detail.RootElement.GetProperty("latest").GetProperty("tickP95Ms").GetDouble());
        Assert.Equal(16.7, detail.RootElement.GetProperty("thresholds").GetProperty("tickP95WarningMs").GetDouble());

        using JsonDocument history = JsonDocument.Parse(await _client.GetStringAsync("/api/servers/dev-server-01/metrics?minutes=5"));
        Assert.Equal(5, history.RootElement.GetProperty("minutes").GetInt32());
        JsonElement sample = Assert.Single(history.RootElement.GetProperty("samples").EnumerateArray());
        Assert.Equal(5, sample.GetProperty("snapshot").GetProperty("players").GetInt32());
        Assert.Equal(_time.GetUtcNow(), sample.GetProperty("receivedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task UnknownServer_Is404_AndMinutesAreClamped()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/servers/nobody")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/servers/nobody/metrics")).StatusCode);
        await PostAsync(Snapshot());
        using JsonDocument history = JsonDocument.Parse(await _client.GetStringAsync("/api/servers/dev-server-01/metrics?minutes=9999"));
        Assert.Equal(10, history.RootElement.GetProperty("minutes").GetInt32());
        // A non-number is not a 400 either: the default (5) is used.
        using JsonDocument text = JsonDocument.Parse(await _client.GetStringAsync("/api/servers/dev-server-01/metrics?minutes=abc"));
        Assert.Equal(5, text.RootElement.GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task WrongOrMissingToken_Is401_AndStoresNothing()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(Snapshot(), token: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(Snapshot(), token: null)).StatusCode);
        Assert.Equal("[]", await _client.GetStringAsync("/api/servers"));
    }

    [Fact]
    public async Task InvalidJson_InvalidValue_AndNaN_Are400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MonitoringContract.IngestPath) { Content = new StringContent("{not json", Encoding.UTF8, "application/json") };
        request.Headers.Add(MonitoringContract.TokenHeader, "secret");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(request)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(Snapshot() with { Players = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(Snapshot() with { ObservedAt = _time.GetUtcNow().AddHours(1) })).StatusCode);

        string nan = JsonSerializer.Serialize(Snapshot(), JsonSerializerOptions.Web).Replace("\"tickP95Ms\":0.4", "\"tickP95Ms\":NaN");
        Assert.Contains("NaN", nan);
        var nanRequest = new HttpRequestMessage(HttpMethod.Post, MonitoringContract.IngestPath) { Content = new StringContent(nan, Encoding.UTF8, "application/json") };
        nanRequest.Headers.Add(MonitoringContract.TokenHeader, "secret");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(nanRequest)).StatusCode);
        Assert.Equal("[]", await _client.GetStringAsync("/api/servers"));
    }

    [Fact]
    public async Task OversizedBody_IsRefused()
    {
        string big = "{\"serverId\":\"dev-server-01\",\"version\":\"" + new string('x', MonitoringContract.MaxBodyBytes + 1024) + "\"}";
        var request = new HttpRequestMessage(HttpMethod.Post, MonitoringContract.IngestPath) { Content = new StringContent(big, Encoding.UTF8, "application/json") };
        request.Headers.Add(MonitoringContract.TokenHeader, "secret");
        HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task TwoServers_AreSeparate_AndOfflineIsDecidedByTime()
    {
        await PostAsync(Snapshot("a") with { Players = 1 });
        await PostAsync(Snapshot("b") with { Players = 2 });
        _time.Advance(TimeSpan.FromSeconds(10));
        await PostAsync(Snapshot("b") with { Players = 3 });
        _time.Advance(TimeSpan.FromSeconds(6));   // a: 16 s silent (> 15), b: 6 s
        using JsonDocument list = JsonDocument.Parse(await _client.GetStringAsync("/api/servers"));
        JsonElement[] servers = list.RootElement.EnumerateArray().ToArray();
        Assert.Equal("a", servers[0].GetProperty("serverId").GetString());
        Assert.Equal("offline", servers[0].GetProperty("status").GetString());
        Assert.Equal(1, servers[0].GetProperty("latest").GetProperty("players").GetInt32());
        Assert.Equal("online", servers[1].GetProperty("status").GetString());
        Assert.Equal(3, servers[1].GetProperty("latest").GetProperty("players").GetInt32());
    }

    [Fact]
    public async Task Health_Answers()
    {
        using JsonDocument health = JsonDocument.Parse(await _client.GetStringAsync("/health"));
        Assert.Equal("ok", health.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, health.RootElement.GetProperty("servers").GetInt32());
    }

    [Fact]
    public void InvalidOptions_StopTheAppAtCreate()
    {
        Assert.Throws<InvalidOperationException>(() => MonitoringApp.Create(["--Urls=http://127.0.0.1:0", "--MonitoringServer:HistoryMinutes=0"]));
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`
Expected: 컴파일 오류(`MonitoringApp` 없음).

- [ ] **Step 3: IngestLog(분당 1줄)**

`Server/src/ProjectH.Monitoring/Ingest/IngestLog.cs`:

```csharp
namespace ProjectH.Monitoring.Ingest;

// Monitoring D7: invalid posts are logged at most once a minute, with the count of the ones not logged. A game server
// posting garbage every 5 s must not fill the log.
public sealed class IngestLog
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ILogger<IngestLog> _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private long _windowStart;   // TimeProvider timestamp of the last logged line; 0 = never
    private int _suppressed;

    // 기능: 로그와 시계를 받는다.
    // 입력: logger - 로그, time - 시계.
    // 출력: IngestLog.
    public IngestLog(ILogger<IngestLog> logger, TimeProvider time)
    {
        _logger = logger;
        _time = time;
    }

    // 기능: 잘못된 Ingest 하나를 기록한다(1분에 한 줄, 나머지는 세어서 다음 줄에 붙인다). 어느 요청 스레드나 부른다.
    // 입력: reason - 거부 이유, serverId - 보낸 서버(모르면 null).
    // 출력: 반환값 없음.
    public void Invalid(string reason, string? serverId)
    {
        int suppressed;
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            if (_windowStart != 0 && _time.GetElapsedTime(_windowStart, now) < Window)
            {
                _suppressed++;
                return;
            }
            suppressed = _suppressed;
            _suppressed = 0;
            _windowStart = now;
        }
        _logger.LogWarning("Invalid ingest ({Reason}) from {ServerId}; {Suppressed} more were not logged in the last minute", reason, serverId ?? "?", suppressed);
    }
}
```

- [ ] **Step 4: Ingest Endpoint**

`Server/src/ProjectH.Monitoring/Ingest/IngestEndpoint.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Storage;

namespace ProjectH.Monitoring.Ingest;

// Monitoring D8: POST /api/ingest/metrics. Order: body size (Kestrel, 413) → token (401) → JSON (400) → values (400) →
// store (429 when a new server would exceed MaxServers) → 202. Logs happen outside the store's lock.
public static class IngestEndpoint
{
    // 기능: Ingest 경로를 등록한다.
    // 입력: app - 앱.
    // 출력: 반환값 없음.
    public static void MapIngest(this WebApplication app)
    {
        app.MapPost(MonitoringContract.IngestPath, HandleAsync);
    }

    // 기능: Snapshot 하나를 받아 검증하고 저장한다. 어떤 입력에도 예외로 끝나지 않는다.
    // 입력: context - 요청, options·store·time·log·logger - 서비스, cancellation - 요청 취소.
    // 출력: HTTP 결과.
    private static async Task<IResult> HandleAsync(HttpContext context, MonitoringServerOptions options, MetricStore store, TimeProvider time,
        IngestLog log, ILogger<MetricStore> logger, CancellationToken cancellation)
    {
        if (options.IngestToken.Length > 0 && !TokenMatches(context, options.IngestToken))
        {
            log.Invalid("unauthorized", null);
            return Results.Unauthorized();
        }

        ServerMonitoringSnapshot? snapshot;
        try
        {
            snapshot = await JsonSerializer.DeserializeAsync<ServerMonitoringSnapshot>(context.Request.Body, JsonSerializerOptions.Web, cancellation);
        }
        catch (JsonException ex)
        {
            log.Invalid("invalid json: " + ex.Message, null);
            return Results.BadRequest(new { error = "invalid json" });
        }
        catch (BadHttpRequestException)
        {
            // The body passed Kestrel's limit while being read (no Content-Length): refuse it as too large.
            log.Invalid("body too large", null);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        if (snapshot == null)
        {
            log.Invalid("empty body", null);
            return Results.BadRequest(new { error = "empty body" });
        }

        DateTimeOffset now = time.GetUtcNow();
        string? error = SnapshotValidator.Validate(snapshot, now, options.MaxClockSkewSeconds);
        if (error != null)
        {
            log.Invalid(error, snapshot.ServerId);
            return Results.BadRequest(new { error });
        }

        switch (store.Add(snapshot, now))
        {
            case IngestOutcome.FirstSeen:
                logger.LogInformation("Server {ServerId} first seen (version {Version}, protocol {Protocol})", snapshot.ServerId, snapshot.Version, snapshot.ProtocolVersion);
                break;
            case IngestOutcome.Returned:
                logger.LogInformation("Server {ServerId} online again", snapshot.ServerId);
                break;
            case IngestOutcome.TooManyServers:
                log.Invalid($"more than {options.MaxServers} servers", snapshot.ServerId);
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }
        return Results.Accepted();
    }

    // 기능: 헤더의 Token을 설정값과 고정 시간으로 비교한다.
    // 입력: context - 요청, expected - 설정된 Token.
    // 출력: 같으면 true.
    private static bool TokenMatches(HttpContext context, string expected)
    {
        if (!context.Request.Headers.TryGetValue(MonitoringContract.TokenHeader, out var values)) return false;
        byte[] given = Encoding.UTF8.GetBytes(values.ToString());
        byte[] wanted = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(given, wanted);
    }
}
```

- [ ] **Step 5: OfflineSweeper**

`Server/src/ProjectH.Monitoring/Status/OfflineSweeper.cs`:

```csharp
using ProjectH.Monitoring.Storage;

namespace ProjectH.Monitoring.Status;

// Monitoring D10: logs "offline" once per transition. The state itself is computed when read, so this service only
// owns the log line. Stops with the host (stoppingToken).
public sealed class OfflineSweeper : BackgroundService
{
    private readonly MetricStore _store;
    private readonly MonitoringServerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OfflineSweeper> _logger;

    // 기능: 저장소·설정·시계·로그를 받는다.
    // 입력: store, options, time, logger.
    // 출력: OfflineSweeper.
    public OfflineSweeper(MetricStore store, MonitoringServerOptions options, TimeProvider time, ILogger<OfflineSweeper> logger)
    {
        _store = store;
        _options = options;
        _time = time;
        _logger = logger;
    }

    // 기능: SweepIntervalSeconds마다 새로 Offline이 된 서버를 로그한다.
    // 입력: stoppingToken - 호스트 종료.
    // 출력: 종료까지의 Task.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.SweepIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach ((string serverId, DateTimeOffset lastSeen) in _store.SweepOffline(_time.GetUtcNow()))
                    _logger.LogWarning("Server {ServerId} offline (last seen {LastSeen:O})", serverId, lastSeen);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }
}
```

- [ ] **Step 6: Servers API와 Health**

`Server/src/ProjectH.Monitoring/Api/ServersEndpoints.cs`:

```csharp
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
    // 출력: 반환값 없음.
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
```

- [ ] **Step 7: MonitoringApp과 Program**

`Server/src/ProjectH.Monitoring/MonitoringApp.cs`:

```csharp
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ProjectH.Monitoring.Api;
using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Ingest;
using ProjectH.Monitoring.Status;
using ProjectH.Monitoring.Storage;

namespace ProjectH.Monitoring;

// Monitoring D7: the whole app, apart from Program so a test can start the same app on a loopback port with its own
// clock. Content root = the build output (appsettings.json and wwwroot are copied there), as in ProjectH.Server.
public static class MonitoringApp
{
    // 기능: 설정을 읽고 검증해 서비스·경로·정적 파일이 붙은 앱을 만든다(시작은 하지 않는다).
    // 입력: args - 명령줄(--Urls=…, --MonitoringServer:키=값), time - 시계(null = 시스템).
    // 출력: Run/StartAsync 전의 WebApplication. 설정이 틀리면 InvalidOperationException.
    public static WebApplication Create(string[] args, TimeProvider? time = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Services.Configure<MonitoringServerOptions>(builder.Configuration.GetSection("MonitoringServer"));
        builder.Services.AddSingleton(services =>
        {
            MonitoringServerOptions options = services.GetRequiredService<IOptions<MonitoringServerOptions>>().Value;
            string? error = options.Validate();
            if (error != null) throw new InvalidOperationException(error);
            return options;
        });
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSingleton<MetricStore>();
        builder.Services.AddSingleton<IngestLog>();
        builder.Services.AddHostedService<OfflineSweeper>();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        // D8 ①: the ingest body limit. The UI only GETs, so the limit is global.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = MonitoringContract.MaxBodyBytes);

        WebApplication app = builder.Build();
        app.Services.GetRequiredService<MonitoringServerOptions>();   // validate now, not at the first request
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapIngest();
        app.MapServersApi();
        return app;
    }
}
```

`Program.cs`:

```csharp
using ProjectH.Monitoring;

// Monitoring D7. Ctrl+C / SIGTERM stops Kestrel, the OfflineSweeper and disposes the host (request §55).
MonitoringApp.Create(args).Run();
```

`JsonNamingPolicy` needs `using System.Text.Json;` in MonitoringApp.cs.

- [ ] **Step 8: 통과 확인**

Run: `dotnet test Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`
Expected: 모두 PASS. `OversizedBody_IsRefused`가 413이 아니라 연결 오류로 실패하면: Content-Length가 있는 요청은 Kestrel이 읽기 전에 413을 보내야 한다 — `HttpResponseMessage` 대신 `HttpRequestException`이 나면 테스트를 `Assert.ThrowsAnyAsync<HttpRequestException>`로 바꾸지 말고, `IngestEndpoint`에서 `context.Request.ContentLength > MonitoringContract.MaxBodyBytes`를 먼저 검사해 413을 돌려주는 줄을 `TokenMatches` 앞에 더한다.

- [ ] **Step 9: 실행 확인과 M1 Commit**

Run: `dotnet run --project Server/src/ProjectH.Monitoring` (다른 셸에서) `curl -s http://127.0.0.1:5080/health`
Expected: `{"status":"ok","servers":0}`. Ctrl+C로 종료가 바로 된다.

```bash
git add Server/ProjectH.Server.slnx Server/src/ProjectH.Monitoring.Contracts Server/src/ProjectH.Monitoring Server/tests/ProjectH.Monitoring.Tests
git commit -m "feat(monitoring): add the Monitoring Server backend (contracts, ingest, bounded store, API) (M1)"
```

---

### Task 6: Game Server `MonitoringOptions`

**Files:**
- Modify: `Server/src/ProjectH.Server/ProjectH.Server.csproj`(Contracts 참조)
- Create: `Server/src/ProjectH.Server/Monitoring/MonitoringOptions.cs`
- Modify: `Server/src/ProjectH.Server/appsettings.json`
- Create: `Server/tests/ProjectH.Server.Tests/Monitoring/MonitoringOptionsTests.cs`

**Interfaces:**
- Consumes: `MonitoringContract.IsValidServerId`.
- Produces: `ProjectH.Server.Monitoring.MonitoringOptions { bool Enabled=false; string Endpoint="http://127.0.0.1:5080"; string ServerId="dev-server-01"; int IntervalSeconds=5; int TimeoutSeconds=2; string Token=""; string? Validate(); Uri IngestUri }`.

- [ ] **Step 1: 참조와 테스트**

`ProjectH.Server.csproj`의 ProjectReference ItemGroup에 `<ProjectReference Include="..\ProjectH.Monitoring.Contracts\ProjectH.Monitoring.Contracts.csproj" />`를 더한다.

`Server/tests/ProjectH.Server.Tests/Monitoring/MonitoringOptionsTests.cs`:

```csharp
using ProjectH.Server.Monitoring;

namespace ProjectH.Server.Tests.Monitoring;

public class MonitoringOptionsTests
{
    [Fact]
    public void Defaults_AreOff_AndValid()
    {
        var o = new MonitoringOptions();
        Assert.False(o.Enabled);
        Assert.Null(o.Validate());
        Assert.Equal(new Uri("http://127.0.0.1:5080/api/ingest/metrics"), o.IngestUri);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://127.0.0.1:5080")]
    [InlineData("")]
    public void Enabled_RequiresAnAbsoluteHttpEndpoint(string endpoint) =>
        Assert.NotNull(new MonitoringOptions { Enabled = true, Endpoint = endpoint }.Validate());

    [Fact]
    public void Disabled_DoesNotCheckTheEndpoint() => Assert.Null(new MonitoringOptions { Enabled = false, Endpoint = "not a url" }.Validate());

    [Theory]
    [InlineData("has space")]
    [InlineData("")]
    public void Enabled_RequiresAValidServerId(string id) => Assert.NotNull(new MonitoringOptions { Enabled = true, ServerId = id }.Validate());

    [Theory]
    [InlineData(0, 2)]
    [InlineData(61, 2)]
    [InlineData(5, 0)]
    [InlineData(5, 6)]    // timeout above the interval
    [InlineData(30, 31)]
    public void IntervalAndTimeout_AreBounded(int interval, int timeout) =>
        Assert.NotNull(new MonitoringOptions { IntervalSeconds = interval, TimeoutSeconds = timeout }.Validate());

    [Fact]
    public void TimeoutEqualToInterval_IsAllowed() => Assert.Null(new MonitoringOptions { IntervalSeconds = 2, TimeoutSeconds = 2 }.Validate());

    [Fact]
    public void IngestUri_AppendsThePath_WithOrWithoutATrailingSlash()
    {
        Assert.Equal("http://127.0.0.1:5080/api/ingest/metrics", new MonitoringOptions { Endpoint = "http://127.0.0.1:5080/" }.IngestUri.ToString());
        Assert.Equal("http://host:1/api/ingest/metrics", new MonitoringOptions { Endpoint = "http://host:1" }.IngestUri.ToString());
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj --filter MonitoringOptionsTests`
Expected: 컴파일 오류.

- [ ] **Step 3: 구현**

`Server/src/ProjectH.Server/Monitoring/MonitoringOptions.cs`:

```csharp
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
    // 출력: POST할 URI.
    public Uri IngestUri => new(new Uri(Endpoint.TrimEnd('/') + "/"), MonitoringContract.IngestPath.TrimStart('/'));

    // 기능: 시작 때 설정 값을 검사한다. Enabled가 아니면 주기·Timeout만 본다.
    // 입력: 없음.
    // 출력: 맞으면 null, 틀리면 이유.
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
```

`appsettings.json`의 `"Persistence"` 뒤에(Token 키 없음):

```json
  "Monitoring": {
    "Enabled": false,
    "Endpoint": "http://127.0.0.1:5080",
    "ServerId": "dev-server-01",
    "IntervalSeconds": 5,
    "TimeoutSeconds": 2
  }
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj --filter MonitoringOptionsTests`
Expected: 모두 PASS.

---

### Task 7: `MonitoringSlot`(Latest-only)

**Files:**
- Create: `Server/src/ProjectH.Server/Monitoring/MonitoringSlot.cs`
- Create: `Server/tests/ProjectH.Server.Tests/Monitoring/MonitoringSlotTests.cs`

**Interfaces:**
- Produces: `MonitoringSlot { void Publish(ServerMonitoringSnapshot); ServerMonitoringSnapshot? Take(); long Overwritten }`.

- [ ] **Step 1: 실패하는 테스트**

```csharp
using ProjectH.Monitoring.Contracts;
using ProjectH.Server.Monitoring;

namespace ProjectH.Server.Tests.Monitoring;

public class MonitoringSlotTests
{
    private static ServerMonitoringSnapshot Snap(int round) => new() { ServerId = "s", Round = round };

    [Fact]
    public void Take_ReturnsTheLatest_AndEmptiesTheSlot()
    {
        var slot = new MonitoringSlot();
        Assert.Null(slot.Take());
        slot.Publish(Snap(1));
        Assert.Equal(1, slot.Take()!.Round);
        Assert.Null(slot.Take());
    }

    [Fact]
    public void Publish_ReplacesAnUnsentSnapshot_AndCountsIt()
    {
        var slot = new MonitoringSlot();
        slot.Publish(Snap(1));
        slot.Publish(Snap(2));
        slot.Publish(Snap(3));
        Assert.Equal(3, slot.Take()!.Round);
        Assert.Equal(2, slot.Overwritten);
        Assert.Null(slot.Take());
    }

    [Fact]
    public void ManyPublishes_NeverGrowBeyondOne()
    {
        var slot = new MonitoringSlot();
        for (int i = 0; i < 10_000; i++) slot.Publish(Snap(i));
        Assert.Equal(9_999, slot.Take()!.Round);
        Assert.Equal(9_999, slot.Overwritten);
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj --filter MonitoringSlotTests`
Expected: 컴파일 오류.

- [ ] **Step 3: 구현**

```csharp
using System.Threading;
using ProjectH.Monitoring.Contracts;

namespace ProjectH.Server.Monitoring;

// Monitoring D4: capacity 1. The game loop publishes (Exchange), the sender takes (Exchange null). A slow or dead
// monitoring server costs the game server one snapshot of memory, never a queue. No lock.
public sealed class MonitoringSlot
{
    private ServerMonitoringSnapshot? _latest;
    private long _overwritten;

    // 기능: 최신 Snapshot을 넣는다(아직 보내지 않은 것은 버리고 센다). Game Loop 스레드.
    // 입력: snapshot - 방금 만든 Snapshot.
    // 출력: 반환값 없음.
    public void Publish(ServerMonitoringSnapshot snapshot)
    {
        if (Interlocked.Exchange(ref _latest, snapshot) != null) Interlocked.Increment(ref _overwritten);
    }

    // 기능: Snapshot을 꺼내고 슬롯을 비운다. Sender 스레드.
    // 입력: 없음.
    // 출력: Snapshot, 없으면 null.
    public ServerMonitoringSnapshot? Take() => Interlocked.Exchange(ref _latest, null);

    // Snapshots that were replaced before being sent (tests; the monitoring server was slower than the interval).
    public long Overwritten => Interlocked.Read(ref _overwritten);
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj --filter MonitoringSlotTests`
Expected: 모두 PASS.

---

### Task 8: `MonitoringCollector`와 GameLoop 연결

**Files:**
- Create: `Server/src/ProjectH.Server/Monitoring/MonitoringCollector.cs`
- Modify: `Server/src/ProjectH.Server/GameLoop.cs`(필드·생성자·`Run`·`RunTick`)
- Create: `Server/tests/ProjectH.Server.Tests/Monitoring/MonitoringCollectorTests.cs`

**Interfaces:**
- Consumes: `MonitoringOptions`, `MonitoringSlot`, `TickMetrics`, `HealthCounters`, `ServerStats.*Total`, `ManualTime`(테스트).
- Produces:
  - `readonly record struct MonitoringSource(int ConnectedPeers, int Players, int Graced, MatchFlowState MatchState, int Round, long PacketsIn, long PacketsOut, long BytesIn, long BytesOut, HealthCounters Health)`
  - `MonitoringCollector(MonitoringOptions options, int simHz, MonitoringSlot slot, TimeProvider time, Func<(int Count, int Capacity)> dbQueue)`: `void RecordTick(double ms)`, `bool IsDue()`, `ServerMonitoringSnapshot Publish(in MonitoringSource s)`, `string Version`.
  - `GameLoop` 생성자 인자 `MonitoringCollector? monitoring = null`(마지막), `internal MonitoringCollector? Monitoring`.

- [ ] **Step 1: 실패하는 테스트**

`Server/tests/ProjectH.Server.Tests/Monitoring/MonitoringCollectorTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Monitoring.Contracts;
using ProjectH.Server.Diagnostics;
using ProjectH.Server.Monitoring;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Tests.Monitoring;

public class MonitoringCollectorTests
{
    private static MonitoringCollector Collector(ManualTime time, MonitoringSlot slot, int interval = 5, Func<(int, int)>? db = null) =>
        new(new MonitoringOptions { Enabled = true, ServerId = "dev-server-01", IntervalSeconds = interval }, simHz: 30, slot, time, db ?? (() => (0, 16)));

    private static MonitoringSource Source(HealthCounters health, long packetsIn = 0, long packetsOut = 0, long bytesIn = 0, long bytesOut = 0) =>
        new(ConnectedPeers: 3, Players: 2, Graced: 1, MatchFlowState.Playing, Round: 4, packetsIn, packetsOut, bytesIn, bytesOut, health);

    [Fact]
    public void Publish_FillsEveryField_FromTheSources()
    {
        var time = new ManualTime();
        var slot = new MonitoringSlot();
        var health = new HealthCounters();
        health.AddTickFailure(); health.AddTickFailure(); health.AddLoopFailure(); health.AddPlayerFailure(); health.AddCallbackError();
        health.AddBadPacket(BadPacketReason.Malformed); health.AddBadPacket(BadPacketReason.UnknownId);
        health.AddDisconnect(timeout: true); health.AddDisconnect(timeout: false); health.AddDisconnect(timeout: false);
        var c = Collector(time, slot, db: () => (3, 16));
        foreach (double ms in new[] { 1.0, 2.0, 3.0, 4.0 }) c.RecordTick(ms);
        time.Advance(TimeSpan.FromSeconds(5));

        ServerMonitoringSnapshot s = c.Publish(Source(health, packetsIn: 500, packetsOut: 250, bytesIn: 50_000, bytesOut: 100_000));

        Assert.Same(s, slot.Take());
        Assert.Equal("dev-server-01", s.ServerId);
        Assert.Equal(c.Version, s.Version);
        Assert.NotEqual("", s.Version);
        Assert.Equal(ProtocolConstants.ProtocolVersion, s.ProtocolVersion);
        Assert.Equal(time.GetUtcNow().AddSeconds(-5), s.StartedAt);
        Assert.Equal(time.GetUtcNow(), s.ObservedAt);
        Assert.Equal(5, s.UptimeSeconds, 3);
        Assert.Equal(5, s.WindowSeconds, 3);
        Assert.Equal(3, s.ConnectedPeers); Assert.Equal(2, s.Players); Assert.Equal(1, s.Graced);
        Assert.Equal("Playing", s.MatchState); Assert.Equal(4, s.Round);
        Assert.Equal(4, s.TickSamples);
        Assert.Equal(2, s.TickP50Ms); Assert.Equal(4, s.TickP95Ms); Assert.Equal(4, s.TickP99Ms); Assert.Equal(4, s.TickMaxMs);   // nearest rank, as TickMetrics
        Assert.Equal(100, s.PacketsInPerSecond, 6); Assert.Equal(50, s.PacketsOutPerSecond, 6);
        Assert.Equal(10_000, s.BytesInPerSecond, 6); Assert.Equal(20_000, s.BytesOutPerSecond, 6);
        Assert.Equal(5, s.Exceptions);        // 2 tick + 1 loop + 1 player + 1 callback
        Assert.Equal(2, s.InvalidPackets);
        Assert.Equal(3, s.Disconnects);
        Assert.Equal(3, s.DbQueueCount); Assert.Equal(16, s.DbQueueCapacity);
        Assert.True(s.ManagedMemoryBytes > 0); Assert.True(s.WorkingSetBytes > 0);
        Assert.True(s.GcGen0 >= s.GcGen1 && s.GcGen1 >= s.GcGen2);
        Assert.True(double.IsFinite(s.CpuPercent) && s.CpuPercent >= 0);
    }

    [Fact]
    public void Rates_AreTheDeltaSinceThePreviousPublish_AndTheWindowResets()
    {
        var time = new ManualTime();
        var slot = new MonitoringSlot();
        var health = new HealthCounters();
        var c = Collector(time, slot);
        time.Advance(TimeSpan.FromSeconds(5));
        c.Publish(Source(health, packetsIn: 500));
        c.RecordTick(7);
        time.Advance(TimeSpan.FromSeconds(10));
        ServerMonitoringSnapshot s = c.Publish(Source(health, packetsIn: 600));
        Assert.Equal(10, s.WindowSeconds, 3);
        Assert.Equal(10, s.PacketsInPerSecond, 6);   // (600 - 500) / 10
        Assert.Equal(1, s.TickSamples);
        Assert.Equal(7, s.TickMaxMs);
        Assert.Equal(15, s.UptimeSeconds, 3);
    }

    [Fact]
    public void ZeroWindow_NoSamples_AndNegativeQueueCount_StillGiveFiniteNonNegativeValues()
    {
        var time = new ManualTime();
        var slot = new MonitoringSlot();
        var c = Collector(time, slot, db: () => (-1, 16));   // Channel.Reader.CanCount false → -1
        ServerMonitoringSnapshot s = c.Publish(Source(new HealthCounters()));   // no time passed, no ticks
        Assert.Equal(0, s.WindowSeconds);
        Assert.Equal(0, s.TickSamples);
        Assert.Equal(0, s.DbQueueCount);
        foreach (double v in new[] { s.CpuPercent, s.PacketsInPerSecond, s.PacketsOutPerSecond, s.BytesInPerSecond, s.BytesOutPerSecond,
                     s.TickP50Ms, s.TickP95Ms, s.TickP99Ms, s.TickMaxMs, s.UptimeSeconds })
            Assert.True(double.IsFinite(v) && v >= 0, $"{v}");
        // The monitoring server's validator (other test project) rejects NaN/Infinity; the JSON must never carry them.
        string json = System.Text.Json.JsonSerializer.Serialize(s, System.Text.Json.JsonSerializerOptions.Web);
        Assert.DoesNotContain("NaN", json);
        Assert.DoesNotContain("Infinity", json);
    }

    [Fact]
    public void IsDue_AfterTheInterval_NotBefore()
    {
        var time = new ManualTime();
        var c = Collector(time, new MonitoringSlot(), interval: 5);
        Assert.False(c.IsDue());
        time.Advance(TimeSpan.FromSeconds(4.9));
        Assert.False(c.IsDue());
        time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True(c.IsDue());
        c.Publish(Source(new HealthCounters()));
        Assert.False(c.IsDue());
    }

    [Fact]
    public void TheGameLoop_PublishesEveryInterval_FromItsOwnState_AndNothingWhenOff()
    {
        var time = new ManualTime();
        var slot = new MonitoringSlot();
        var c = Collector(time, slot);
        using var loop = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), NullLogger.Instance, time: time, monitoring: c);
        loop.RunTickGuarded();
        Assert.Null(slot.Take());                       // not due yet
        time.Advance(TimeSpan.FromSeconds(5));
        loop.RunTickGuarded();
        ServerMonitoringSnapshot s = slot.Take()!;
        Assert.Equal(0, s.ConnectedPeers);
        Assert.Equal("WaitingForPlayers", s.MatchState);
        Assert.Equal(0, s.TickSamples);                 // RecordTick lives in Run (which times the tick); RunTickGuarded does not
        loop.RunTickGuarded();
        Assert.Null(slot.Take());                       // once per interval

        using var off = new GameLoop(new ServerOptions { Port = 0, MaxPlayers = 4 }, TestGameData.Create(), NullLogger.Instance, time: time);
        Assert.Null(off.Monitoring);
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj --filter MonitoringCollectorTests`
Expected: 컴파일 오류.

- [ ] **Step 3: Collector 구현**

`Server/src/ProjectH.Server/Monitoring/MonitoringCollector.cs`:

```csharp
using System;
using System.Reflection;
using ProjectH.Monitoring.Contracts;
using ProjectH.Server.Diagnostics;
using ProjectH.Shared.Protocol;

namespace ProjectH.Server.Monitoring;

// What the game loop hands the collector: its own gauges and the network totals it owns the reading of. A struct: no
// allocation per publish besides the snapshot itself.
public readonly record struct MonitoringSource(int ConnectedPeers, int Players, int Graced, MatchFlowState MatchState, int Round,
    long PacketsIn, long PacketsOut, long BytesIn, long BytesOut, HealthCounters Health);

// Monitoring D3: builds the snapshot on the game loop thread, every IntervalSeconds, from values the loop already has.
// Its own TickMetrics window (the loop's is reset by the Stats line). Rates are deltas of the loop's cumulative network
// totals (never TakeDelta: that belongs to the Stats line). CPU as the Stats line computes it: process time over window
// × cores. Non-finite results (a zero window, no samples) become 0: System.Text.Json refuses NaN and so does the
// monitoring server. No logging, no lock, no I/O here.
public sealed class MonitoringCollector
{
    private readonly MonitoringOptions _options;
    private readonly MonitoringSlot _slot;
    private readonly TimeProvider _time;
    private readonly Func<(int Count, int Capacity)> _dbQueue;
    private readonly TickMetrics _ticks;
    private readonly TimeSpan _interval;
    private readonly long _startTimestamp;
    private readonly DateTimeOffset _startedAt;
    private long _lastPublish;
    private TimeSpan _cpuAtLast;
    private long _packetsIn, _packetsOut, _bytesIn, _bytesOut;

    // The server build, read once: the assembly's informational version, else its version.
    public string Version { get; }

    // 기능: 수집기를 만든다. Tick 링은 SimHz × IntervalSeconds 샘플(주기 안의 모든 Tick)을 한 번 할당한다.
    // 입력: options - 검증이 끝난 설정, simHz - Tick 주파수, slot - 보낼 슬롯, time - 시계, dbQueue - DB 큐의 (수, 용량).
    // 출력: MonitoringCollector(StartedAt = 지금).
    public MonitoringCollector(MonitoringOptions options, int simHz, MonitoringSlot slot, TimeProvider time, Func<(int Count, int Capacity)> dbQueue)
    {
        _options = options;
        _slot = slot;
        _time = time;
        _dbQueue = dbQueue;
        _interval = TimeSpan.FromSeconds(options.IntervalSeconds);
        _ticks = new TickMetrics(Math.Max(1, simHz * options.IntervalSeconds));
        _startTimestamp = _lastPublish = time.GetTimestamp();
        _startedAt = time.GetUtcNow();
        _cpuAtLast = Environment.CpuUsage.TotalTime;
        Assembly assembly = typeof(MonitoringCollector).Assembly;
        Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
    }

    // 기능: Tick 하나의 길이를 이 주기의 링에 기록한다. Game Loop 스레드, 매 Tick.
    // 입력: milliseconds - Tick 길이.
    // 출력: 반환값 없음.
    public void RecordTick(double milliseconds) => _ticks.Record(milliseconds);

    // 기능: 마지막 Publish에서 IntervalSeconds가 지났는지 본다. Game Loop 스레드, 매 Tick(GetTimestamp 한 번).
    // 입력: 없음.
    // 출력: 지났으면 true.
    public bool IsDue() => _time.GetElapsedTime(_lastPublish) >= _interval;

    // 기능: Snapshot을 만들어 슬롯에 넣고 기준값(시각·CPU·네트워크 누적)을 지금으로 옮긴다. Game Loop 스레드.
    // 입력: s - 루프의 게이지와 누적값.
    // 출력: 슬롯에 넣은 Snapshot.
    public ServerMonitoringSnapshot Publish(in MonitoringSource s)
    {
        long now = _time.GetTimestamp();
        double window = _time.GetElapsedTime(_lastPublish, now).TotalSeconds;
        TimeSpan cpu = Environment.CpuUsage.TotalTime;
        double cpuPercent = (cpu - _cpuAtLast).TotalSeconds / (window * Environment.ProcessorCount) * 100.0;
        TickStats t = _ticks.Compute();
        _ticks.Reset();
        (int dbCount, int dbCapacity) = _dbQueue();
        HealthCounters h = s.Health;

        var snapshot = new ServerMonitoringSnapshot
        {
            ServerId = _options.ServerId,
            Version = Version,
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            StartedAt = _startedAt,
            ObservedAt = _time.GetUtcNow(),
            UptimeSeconds = Finite(_time.GetElapsedTime(_startTimestamp, now).TotalSeconds),
            WindowSeconds = Finite(window),
            ConnectedPeers = s.ConnectedPeers,
            Players = s.Players,
            Graced = s.Graced,
            MatchState = s.MatchState.ToString(),
            Round = s.Round,
            TickSamples = t.SampleCount,
            TickP50Ms = Finite(t.P50),
            TickP95Ms = Finite(t.P95),
            TickP99Ms = Finite(t.P99),
            TickMaxMs = Finite(t.Max),
            CpuPercent = Finite(cpuPercent),
            ManagedMemoryBytes = GC.GetTotalMemory(false),
            WorkingSetBytes = Environment.WorkingSet,
            GcGen0 = GC.CollectionCount(0),
            GcGen1 = GC.CollectionCount(1),
            GcGen2 = GC.CollectionCount(2),
            PacketsInPerSecond = Rate(s.PacketsIn - _packetsIn, window),
            PacketsOutPerSecond = Rate(s.PacketsOut - _packetsOut, window),
            BytesInPerSecond = Rate(s.BytesIn - _bytesIn, window),
            BytesOutPerSecond = Rate(s.BytesOut - _bytesOut, window),
            Exceptions = h.TickFailures + h.LoopFailures + h.PlayerFailures + h.CallbackErrors,
            InvalidPackets = h.BadPacketsTotal,
            Disconnects = h.DisconnectTimeouts + h.DisconnectOthers,
            DbQueueCount = Math.Max(0, dbCount),       // -1 when the channel cannot count
            DbQueueCapacity = Math.Max(0, dbCapacity),
        };

        _lastPublish = now;
        _cpuAtLast = cpu;
        _packetsIn = s.PacketsIn;
        _packetsOut = s.PacketsOut;
        _bytesIn = s.BytesIn;
        _bytesOut = s.BytesOut;
        _slot.Publish(snapshot);
        return snapshot;
    }

    // 기능: NaN·Infinity·음수를 0으로 만든다.
    // 입력: v - 값.
    // 출력: 유한한 0 이상의 값.
    private static double Finite(double v) => double.IsFinite(v) && v >= 0 ? v : 0;

    // 기능: 누적 차이를 초당 값으로 만든다(창이 0이면 0).
    // 입력: delta - 누적 차이, window - 창 길이(초).
    // 출력: 초당 값.
    private static double Rate(long delta, double window) => window > 0 && delta > 0 ? delta / window : 0;
}
```

- [ ] **Step 4: GameLoop 연결**

`GameLoop.cs`:

1. 필드(`private Qa.QaControl? _qa;` 근처):
```csharp
    // Monitoring D3: the collector, attached at construction only when Monitoring:Enabled (null otherwise: one null check
    // per tick). It runs on this thread and never logs, locks or does I/O.
    private readonly MonitoringCollector? _monitoring;
```
2. 생성자 시그니처 끝에 `, MonitoringCollector? monitoring = null`을 더하고 본문(`_onFatal = onFatal;` 뒤)에 `_monitoring = monitoring;`. 생성자의 기능/입력 주석에 "monitoring - Monitoring 수집기(null = 꺼짐)"를 더한다.
3. `internal ServerStats Stats => _stats;` 옆에 `internal MonitoringCollector? Monitoring => _monitoring;`.
4. `Run()`의 `_tickMetrics.Record(_lastTickMs);` 바로 뒤에 `_monitoring?.RecordTick(_lastTickMs);`.
5. `RunTick()`의 `_qa?.OnTick(this);` 바로 **앞**에:
```csharp
        // Monitoring D3: a snapshot every IntervalSeconds into the latest-only slot; MonitoringSender posts it off this thread.
        if (_monitoring != null && _monitoring.IsDue())
            _monitoring.Publish(new MonitoringSource(_peers.Count, _match.PlayerCount, _match.GracedCount, _match.Flow.State, _match.Flow.Round,
                _stats.PacketsInTotal, _stats.PacketsOutTotal, _stats.BytesInTotal, _stats.BytesOutTotal, _health));
```
   `RunTick`의 기능 주석 끝에 "Monitoring Snapshot(주기마다)"을 더한다.
6. `using ProjectH.Server.Monitoring;` 추가.

`RecordTick`은 `Run`(Tick 길이를 아는 곳), `Publish`는 `RunTick` 끝(테스트가 `RunTickGuarded`로 부를 수 있는 곳)에 있다. Tick 하나의 길이가 그 Tick의 Publish 뒤에 기록되는 것은 의도다.

- [ ] **Step 5: 통과 확인(전체)**

Run: `dotnet test Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj`
Expected: 새 테스트 5개 PASS, 기존 테스트 변화 없음.

---

### Task 9: `MonitoringSender`(BackgroundService)

**Files:**
- Create: `Server/src/ProjectH.Server/Monitoring/MonitoringSender.cs`
- Modify: `Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj`(FrameworkReference)
- Create: `Server/tests/ProjectH.Server.Tests/Monitoring/FakeMonitoringServer.cs`
- Create: `Server/tests/ProjectH.Server.Tests/Monitoring/MonitoringSenderTests.cs`

**Interfaces:**
- Consumes: `MonitoringOptions.IngestUri/Token/IntervalSeconds/TimeoutSeconds`, `MonitoringSlot.Take()`, `ListLogger`(`ProjectH.Server.Tests.Diagnostics`).
- Produces: `MonitoringSender : BackgroundService` — 생성자 `(MonitoringOptions options, MonitoringSlot slot, ILogger logger, TimeSpan? interval = null, TimeSpan? timeout = null)`(null = 설정값), `long Sent`, `long Failed`, `bool Lost`.

- [ ] **Step 1: 테스트용 가짜 Monitoring Server**

테스트 csproj의 PackageReference ItemGroup 뒤에:

```xml
  <ItemGroup>
    <!-- Monitoring Task 9: a loopback Kestrel that plays the monitoring server for MonitoringSender tests. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

`Server/tests/ProjectH.Server.Tests/Monitoring/FakeMonitoringServer.cs`:

```csharp
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ProjectH.Monitoring.Contracts;

namespace ProjectH.Server.Tests.Monitoring;

// A loopback HTTP server that answers the ingest path with whatever the test sets: a status code and a delay.
public sealed class FakeMonitoringServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _received;
    private volatile string? _lastToken;
    private volatile int _lastBodyBytes;

    public volatile int StatusCode = StatusCodes.Status202Accepted;
    public volatile int DelayMs;

    public Uri Endpoint { get; }
    public int Received => Volatile.Read(ref _received);
    public string? LastToken => _lastToken;
    public int LastBodyBytes => _lastBodyBytes;

    private FakeMonitoringServer(WebApplication app, Uri endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    // 기능: 127.0.0.1의 빈 포트에 가짜 서버를 띄운다.
    // 입력: 없음.
    // 출력: 실행 중인 FakeMonitoringServer(Endpoint = 기본 주소).
    public static async Task<FakeMonitoringServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new[] { "--Urls=http://127.0.0.1:0" });
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        FakeMonitoringServer? self = null;
        app.MapPost(MonitoringContract.IngestPath, async (HttpContext ctx) =>
        {
            FakeMonitoringServer s = self!;
            Interlocked.Increment(ref s._received);
            s._lastToken = ctx.Request.Headers.TryGetValue(MonitoringContract.TokenHeader, out var v) ? v.ToString() : null;
            using var ms = new System.IO.MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            s._lastBodyBytes = (int)ms.Length;
            if (s.DelayMs > 0) await Task.Delay(s.DelayMs, ctx.RequestAborted);
            return Results.StatusCode(s.StatusCode);
        });
        await app.StartAsync();
        self = new FakeMonitoringServer(app, new Uri(app.Urls.First()));
        return self;
    }

    // 기능: 아무도 듣지 않는 127.0.0.1 포트의 주소를 만든다(Connection Refused 테스트).
    // 입력: 없음.
    // 출력: 닫힌 포트의 URI.
    public static Uri ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
```

- [ ] **Step 2: 실패하는 Sender 테스트**

`Server/tests/ProjectH.Server.Tests/Monitoring/MonitoringSenderTests.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProjectH.Monitoring.Contracts;
using ProjectH.Server.Monitoring;
using ProjectH.Server.Tests.Diagnostics;

namespace ProjectH.Server.Tests.Monitoring;

// Request §65, §91: send, refused, timeout, slow, restored, cancellation, shutdown. Real HTTP on the loopback.
public class MonitoringSenderTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);

    private static ServerMonitoringSnapshot Snap(int round = 1) => new()
    {
        ServerId = "dev-server-01", Version = "1.0.0", ProtocolVersion = 18, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        ObservedAt = DateTimeOffset.UtcNow, WindowSeconds = 5, MatchState = "Playing", Round = round, DbQueueCapacity = 16,
    };

    private static MonitoringSender Sender(Uri endpoint, MonitoringSlot slot, ListLogger log, string token = "") =>
        new(new MonitoringOptions { Enabled = true, Endpoint = endpoint.ToString(), Token = token }, slot, log, Interval, Timeout);

    private static async Task WaitUntilAsync(Func<bool> condition, int milliseconds = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < milliseconds, "the condition did not come true in time");
            await Task.Delay(10);
        }
    }

    private static int Count(ListLogger log, LogLevel level, string text) => log.Entries.Count(e => e.Level == level && e.Message.Contains(text));

    [Fact]
    public async Task Sends_TheSnapshot_WithTheToken_AndEmptiesTheSlot()
    {
        await using FakeMonitoringServer fake = await FakeMonitoringServer.StartAsync();
        var slot = new MonitoringSlot();
        var log = new ListLogger();
        using var sender = Sender(fake.Endpoint, slot, log, token: "t");
        slot.Publish(Snap());
        await sender.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sender.Sent == 1);
        Assert.Equal(1, fake.Received);
        Assert.Equal("t", fake.LastToken);
        Assert.True(fake.LastBodyBytes > 0 && fake.LastBodyBytes < MonitoringContract.MaxBodyBytes, $"payload {fake.LastBodyBytes} bytes");
        Assert.Null(slot.Take());
        Assert.Empty(log.Entries.Where(e => e.Level >= LogLevel.Warning));
        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnEmptySlot_SendsNothing()
    {
        await using FakeMonitoringServer fake = await FakeMonitoringServer.StartAsync();
        var log = new ListLogger();
        using var sender = Sender(fake.Endpoint, new MonitoringSlot(), log);
        await sender.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(0, fake.Received);
        Assert.Equal(0, sender.Sent);
        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConnectionRefused_LogsLostOnce_ThenStaysQuiet()
    {
        var slot = new MonitoringSlot();
        var log = new ListLogger();
        using var sender = Sender(FakeMonitoringServer.ClosedPort(), slot, log);
        await sender.StartAsync(CancellationToken.None);
        for (int i = 1; i <= 3; i++)
        {
            slot.Publish(Snap(i));
            int expected = i;
            await WaitUntilAsync(() => sender.Failed >= expected);
        }
        Assert.True(sender.Lost);
        Assert.Equal(1, Count(log, LogLevel.Warning, "Monitoring connection lost"));
        Assert.Equal(0, Count(log, LogLevel.Error, ""));
        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ServerError_IsAFailure_AndRecoveryLogsRestoredOnce()
    {
        await using FakeMonitoringServer fake = await FakeMonitoringServer.StartAsync();
        fake.StatusCode = 500;
        var slot = new MonitoringSlot();
        var log = new ListLogger();
        using var sender = Sender(fake.Endpoint, slot, log);
        await sender.StartAsync(CancellationToken.None);
        slot.Publish(Snap(1));
        await WaitUntilAsync(() => sender.Failed == 1);
        slot.Publish(Snap(2));
        await WaitUntilAsync(() => sender.Failed == 2);
        fake.StatusCode = 202;
        slot.Publish(Snap(3));
        await WaitUntilAsync(() => sender.Sent == 1);
        slot.Publish(Snap(4));
        await WaitUntilAsync(() => sender.Sent == 2);
        Assert.False(sender.Lost);
        Assert.Equal(1, Count(log, LogLevel.Warning, "Monitoring connection lost"));
        Assert.Equal(1, Count(log, LogLevel.Information, "Monitoring connection restored"));
        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ASlowServer_FailsWithinTheTimeout_AndTheLoopGoesOn()
    {
        await using FakeMonitoringServer fake = await FakeMonitoringServer.StartAsync();
        fake.DelayMs = 3000;
        var slot = new MonitoringSlot();
        var log = new ListLogger();
        using var sender = Sender(fake.Endpoint, slot, log);
        await sender.StartAsync(CancellationToken.None);
        var sw = Stopwatch.StartNew();
        slot.Publish(Snap(1));
        await WaitUntilAsync(() => sender.Failed == 1, 2000);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms; the timeout is {Timeout.TotalMilliseconds} ms");
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("timeout"));
        fake.DelayMs = 0;
        slot.Publish(Snap(2));
        await WaitUntilAsync(() => sender.Sent == 1);
        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_CancelsAnInFlightPost_AndReturnsQuickly()
    {
        await using FakeMonitoringServer fake = await FakeMonitoringServer.StartAsync();
        fake.DelayMs = 5000;
        var slot = new MonitoringSlot();
        var log = new ListLogger();
        // A long timeout so only Stop can end the post.
        using var slow = new MonitoringSender(new MonitoringOptions { Enabled = true, Endpoint = fake.Endpoint.ToString() }, slot, log, Interval, TimeSpan.FromSeconds(10));
        await slow.StartAsync(CancellationToken.None);
        slot.Publish(Snap());
        await WaitUntilAsync(() => fake.Received == 1);
        var sw = Stopwatch.StartNew();
        await slow.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"stop took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(0, slow.Sent);
        Assert.Equal(0, Count(log, LogLevel.Warning, "Monitoring connection lost"));   // shutdown is not a failure
    }

    [Fact]
    public async Task AWrongHostName_IsAFailure_NotACrash()
    {
        var slot = new MonitoringSlot();
        var log = new ListLogger();
        using var sender = Sender(new Uri("http://nonexistent.invalid:1/"), slot, log);
        await sender.StartAsync(CancellationToken.None);
        slot.Publish(Snap());
        await WaitUntilAsync(() => sender.Failed == 1, 5000);
        Assert.Equal(1, Count(log, LogLevel.Warning, "Monitoring connection lost"));
        await sender.StopAsync(CancellationToken.None);
    }
}
```

- [ ] **Step 3: 실패 확인**

Run: `dotnet test Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj --filter MonitoringSenderTests`
Expected: 컴파일 오류.

- [ ] **Step 4: Sender 구현**

`Server/src/ProjectH.Server/Monitoring/MonitoringSender.cs`:

```csharp
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProjectH.Monitoring.Contracts;

namespace ProjectH.Server.Monitoring;

// Monitoring D5: every IntervalSeconds, takes the latest snapshot out of the slot and posts it with a short timeout. A
// failure drops that snapshot (the next interval has a newer one) and is logged only when the state changes: one
// Warning when the connection is lost, one Information when it is back. Shutdown cancels the post in flight and does
// not wait to send a last snapshot. Registered after GameServerService, so it stops before the game loop. It never
// touches the game loop; only the slot is shared.
public sealed class MonitoringSender : BackgroundService
{
    private readonly MonitoringSlot _slot;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly Uri _ingest;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private long _sent;
    private long _failed;
    private bool _lost;                // sender task only
    private long _failuresSinceLost;   // sender task only

    public long Sent => Interlocked.Read(ref _sent);
    public long Failed => Interlocked.Read(ref _failed);
    public bool Lost => Volatile.Read(ref _lost);

    // 기능: 전송기를 만든다. HttpClient 하나를 소유한다(Dispose가 닫는다).
    // 입력: options - 검증이 끝난 설정, slot - Game Loop가 채우는 슬롯, logger - 로그, interval·timeout - 테스트용(null = 설정값).
    // 출력: Start 전의 MonitoringSender.
    public MonitoringSender(MonitoringOptions options, MonitoringSlot slot, ILogger logger, TimeSpan? interval = null, TimeSpan? timeout = null)
    {
        _slot = slot;
        _logger = logger;
        _ingest = options.IngestUri;
        _interval = interval ?? TimeSpan.FromSeconds(options.IntervalSeconds);
        _timeout = timeout ?? TimeSpan.FromSeconds(options.TimeoutSeconds);
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,   // the per-request token below is the timeout
        };
        if (options.Token.Length > 0) _http.DefaultRequestHeaders.Add(MonitoringContract.TokenHeader, options.Token);
    }

    // 기능: 주기마다 슬롯에서 Snapshot을 꺼내 보낸다. 비어 있으면 보내지 않는다.
    // 입력: stoppingToken - 호스트 종료.
    // 출력: 종료까지의 Task.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                ServerMonitoringSnapshot? snapshot = _slot.Take();
                if (snapshot != null) await SendAsync(snapshot, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    // 기능: Snapshot 하나를 POST한다. 어떤 실패도 예외로 나가지 않고 상태 변화 때만 로그한다.
    // 입력: snapshot - 보낼 값, stoppingToken - 호스트 종료(진행 중 요청을 취소한다).
    // 출력: Task.
    private async Task SendAsync(ServerMonitoringSnapshot snapshot, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(_timeout);
        string? failure = null;
        try
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync(_ingest, snapshot, cts.Token);
            if (!response.IsSuccessStatusCode) failure = $"HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;   // shutdown: neither sent nor failed
        }
        catch (OperationCanceledException)
        {
            failure = $"timeout after {_timeout.TotalSeconds:0.#} s";
        }
        catch (Exception ex)
        {
            // HttpRequestException (refused, DNS), JSON, anything: monitoring must never take the service down.
            failure = ex.Message;
        }

        if (failure == null)
        {
            Interlocked.Increment(ref _sent);
            if (_lost)
            {
                Volatile.Write(ref _lost, false);
                _logger.LogInformation("Monitoring connection restored after {Failures} failed posts", _failuresSinceLost);
            }
            return;
        }
        Interlocked.Increment(ref _failed);
        if (!_lost)
        {
            Volatile.Write(ref _lost, true);
            _failuresSinceLost = 0;
            _logger.LogWarning("Monitoring connection lost ({Reason}); posting to {Endpoint} is retried every {Interval:0.#} s without further logs",
                failure, _ingest, _interval.TotalSeconds);
        }
        _failuresSinceLost++;
        _logger.LogDebug("Monitoring post failed ({Reason})", failure);
    }

    // 기능: HttpClient를 닫는다.
    // 입력: 없음.
    // 출력: 반환값 없음.
    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
```

- [ ] **Step 5: 통과 확인**

Run: `dotnet test Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj --filter MonitoringSenderTests`
Expected: 7개 PASS. `ASlowServer_…`의 `"timeout"` 문구는 Warning 메시지의 `{Reason}`에 들어간다.

---

### Task 10: `MonitoringSetup` — 호스트 연결 (M2 Commit)

**Files:**
- Create: `Server/src/ProjectH.Server/Monitoring/MonitoringSetup.cs`
- Modify: `Server/src/ProjectH.Server/ServerHost.cs`
- Modify: `Server/src/ProjectH.Server/GameServerService.cs`
- Create: `Server/tests/ProjectH.Server.Tests/Monitoring/MonitoringSetupTests.cs`

**Interfaces:**
- Consumes: `MonitoringOptions`, `MonitoringSlot`, `MonitoringSender`, `MonitoringCollector`, `MatchHistoryQueue.Count`, `PersistenceOptions.QueueCapacity`.
- Produces: `static MonitoringOptions MonitoringSetup.Register(HostApplicationBuilder builder)`; `GameServerService` 생성자에 `MonitoringOptions monitoring, IOptions<PersistenceOptions> persistence, …, MonitoringSlot? monitoringSlot = null`.

- [ ] **Step 1: 실패하는 테스트**

`Server/tests/ProjectH.Server.Tests/Monitoring/MonitoringSetupTests.cs`:

```csharp
using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectH.Server.Monitoring;

namespace ProjectH.Server.Tests.Monitoring;

// Request §60: off = nothing registered. The host is built (never started: it would bind the game port).
public class MonitoringSetupTests
{
    private static int HostedServices(HostApplicationBuilder b) => b.Services.Count(d => d.ServiceType == typeof(IHostedService));

    [Fact]
    public void Disabled_RegistersNoSlotAndNoSender()
    {
        HostApplicationBuilder b = ServerHost.CreateBuilder(new[] { "--Monitoring:Enabled=false" });
        Assert.DoesNotContain(b.Services, d => d.ServiceType == typeof(MonitoringSlot));
        Assert.NotNull(b.Services.Single(d => d.ServiceType == typeof(MonitoringOptions)));
    }

    [Fact]
    public void Enabled_RegistersTheSlot_AndOneMoreHostedService_AfterTheGameServer()
    {
        HostApplicationBuilder off = ServerHost.CreateBuilder(new[] { "--Monitoring:Enabled=false" });
        HostApplicationBuilder on = ServerHost.CreateBuilder(new[] { "--Monitoring:Enabled=true", "--Monitoring:ServerId=test-01" });
        Assert.Contains(on.Services, d => d.ServiceType == typeof(MonitoringSlot));
        Assert.Equal(HostedServices(off) + 1, HostedServices(on));
        // Hosted services stop in reverse registration order: the sender (last) stops before GameServerService.
        var hosted = on.Services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
        Assert.Equal(typeof(GameServerService), hosted[^2].ImplementationType);
        Assert.Null(hosted[^1].ImplementationType);   // the sender is registered through a factory
    }

    [Fact]
    public void InvalidSettings_StopTheHostAtBuild()
    {
        Assert.Throws<InvalidOperationException>(() => ServerHost.CreateBuilder(new[] { "--Monitoring:Enabled=true", "--Monitoring:Endpoint=nope" }));
        Assert.Throws<InvalidOperationException>(() => ServerHost.CreateBuilder(new[] { "--Monitoring:IntervalSeconds=0" }));
    }

    [Fact]
    public void TheShippedSettings_HaveMonitoringOff_AndNoTokenKey()
    {
        string json = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        Assert.Contains("\"Monitoring\"", json);
        Assert.Contains("\"Enabled\": false", json);
        Assert.DoesNotContain("\"Token\"", json);
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj --filter MonitoringSetupTests`
Expected: 컴파일 오류 또는 FAIL.

- [ ] **Step 3: MonitoringSetup**

`Server/src/ProjectH.Server/Monitoring/MonitoringSetup.cs`:

```csharp
using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ProjectH.Server.Monitoring;

// Monitoring D5, D6: what monitoring adds to the host. Off = the validated options only (GameServerService then gets no
// slot and builds no collector). On = the slot and the sender. Must run after GameServerService is registered (hosted
// services stop in reverse order: the sender first, then the game loop).
public static class MonitoringSetup
{
    public const string LoggerName = "ProjectH.Server.Monitoring";

    // 기능: Monitoring 설정을 읽고 검증해 서비스를 등록한다.
    // 입력: builder - 호스트 빌더(GameServerService 등록 뒤).
    // 출력: 검증이 끝난 설정. 틀리면 InvalidOperationException(호스트가 시작하지 않는다).
    public static MonitoringOptions Register(HostApplicationBuilder builder)
    {
        MonitoringOptions options = builder.Configuration.GetSection("Monitoring").Get<MonitoringOptions>() ?? new MonitoringOptions();
        string? error = options.Validate();
        if (error != null) throw new InvalidOperationException(error);
        builder.Services.AddSingleton(options);
        if (!options.Enabled) return options;
        builder.Services.AddSingleton<MonitoringSlot>();
        builder.Services.AddHostedService(services => new MonitoringSender(options, services.GetRequiredService<MonitoringSlot>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerName)));
        return options;
    }
}
```

- [ ] **Step 4: ServerHost와 GameServerService**

`ServerHost.CreateBuilder`의 `builder.Services.AddHostedService<GameServerService>();` 바로 뒤에:

```csharp
        // Monitoring D5: after the game server, so the sender stops before the game loop (and starts after it).
        Monitoring.MonitoringSetup.Register(builder);
```

`GameServerService` 생성자를 다음으로 바꾼다(기능/입력/출력 주석 추가):

```csharp
    // 기능: Game Loop·Meter·Watchdog을 만들고 Monitoring이 켜져 있으면 수집기를 루프에 붙인다.
    // 입력: options - 서버 설정, logger, matchHistory·writer·persistence - DB 경로, statsQueries, lifetime, monitoring - Monitoring 설정,
    //       qa - QA 모드일 때만, monitoringSlot - Monitoring이 켜져 있을 때만(null = 수집기 없음).
    // 출력: Start 전의 GameServerService.
    public GameServerService(IOptions<ServerOptions> options, ILogger<GameLoop> logger, MatchHistoryQueue matchHistory,
        MatchHistoryWriter writer, StatsQueryQueue statsQueries, IHostApplicationLifetime lifetime, Monitoring.MonitoringOptions monitoring,
        IOptions<PersistenceOptions> persistence, Qa.QaControl? qa = null, Monitoring.MonitoringSlot? monitoringSlot = null)
```

본문에서 `_loop = new GameLoop(...)` 호출 앞에:

```csharp
        // Monitoring D3: the collector runs on the game loop thread and reads the DB queue's count (any thread) and capacity.
        int queueCapacity = persistence.Value.QueueCapacity;
        Monitoring.MonitoringCollector? collector = monitoringSlot == null ? null
            : new Monitoring.MonitoringCollector(monitoring, options.Value.SimHz, monitoringSlot, TimeProvider.System, () => (matchHistory.Count, queueCapacity));
```

그리고 `new GameLoop(...)`의 인자 끝에 `, monitoring: collector`.

- [ ] **Step 5: 전체 테스트 두 번**

Run: `dotnet test Server/ProjectH.Server.slnx` (두 번)
Expected: 모두 PASS, 두 번 모두. `hosted[^2]`가 `GameServerService`가 아니면 `ServerHost`의 등록 순서를 다시 본다(QaSetup은 Program에서 더 뒤에 등록되므로 `CreateBuilder`만으로는 Sender가 마지막이다).

- [ ] **Step 6: 실제 프로세스로 한 번(§67 예비)**

셸 1: `dotnet run --project Server/src/ProjectH.Monitoring`
셸 2(PowerShell): `$env:Monitoring__Enabled='true'; dotnet run --project Server/src/ProjectH.Server`
셸 3: 10초 뒤 `curl -s http://127.0.0.1:5080/api/servers`
Expected: `dev-server-01`이 `online`, `latest.tickSamples > 0`. Monitoring 로그에 `Server dev-server-01 first seen`. 셸 2 Ctrl+C로 서버가 바로 멈춘다.

- [ ] **Step 7: M2 Commit**

```bash
git add Server/src/ProjectH.Server/ProjectH.Server.csproj Server/src/ProjectH.Server/appsettings.json Server/src/ProjectH.Server/GameLoop.cs Server/src/ProjectH.Server/GameServerService.cs Server/src/ProjectH.Server/ServerHost.cs Server/src/ProjectH.Server/Monitoring Server/tests/ProjectH.Server.Tests/ProjectH.Server.Tests.csproj Server/tests/ProjectH.Server.Tests/Monitoring
git commit -m "feat(monitoring): collect snapshots on the game loop and post them off it (M2)"
```

---

### Task 11: Web Dashboard (M3 Commit)

**Files:**
- Create: `Server/src/ProjectH.Monitoring/wwwroot/index.html`
- Create: `Server/src/ProjectH.Monitoring/wwwroot/app.css`
- Create: `Server/src/ProjectH.Monitoring/wwwroot/app.js`
- Create: `Server/src/ProjectH.Monitoring/wwwroot/lib/uPlot.iife.min.js`, `wwwroot/lib/uPlot.min.css`, `wwwroot/lib/uPlot.LICENSE`
- Modify: `Server/tests/ProjectH.Monitoring.Tests/MonitoringAppTests.cs`(정적 파일 제공 테스트 1개)

**Interfaces:**
- Consumes: `GET /api/servers`(목록 + `latest`), `GET /api/servers/{id}`(`thresholds`), `GET /api/servers/{id}/metrics?minutes=5`(`samples[].receivedAt`, `samples[].snapshot.*`). 필드 이름은 Task 1의 DTO를 camelCase로.
- Produces: 정적 페이지. 서버 선택은 URL hash `#<serverId>`.

- [ ] **Step 1: uPlot 1.6.31 고정 복사(D13)**

```bash
mkdir -p Server/src/ProjectH.Monitoring/wwwroot/lib
curl -sSL -o Server/src/ProjectH.Monitoring/wwwroot/lib/uPlot.iife.min.js https://cdn.jsdelivr.net/npm/uplot@1.6.31/dist/uPlot.iife.min.js
curl -sSL -o Server/src/ProjectH.Monitoring/wwwroot/lib/uPlot.min.css   https://cdn.jsdelivr.net/npm/uplot@1.6.31/dist/uPlot.min.css
curl -sSL -o Server/src/ProjectH.Monitoring/wwwroot/lib/uPlot.LICENSE   https://cdn.jsdelivr.net/npm/uplot@1.6.31/LICENSE
head -c 200 Server/src/ProjectH.Monitoring/wwwroot/lib/uPlot.iife.min.js
```
Expected: JS 첫 줄에 `uPlot` 문자열, 파일 약 45 KB, LICENSE는 MIT. 받지 못하면(네트워크 없음) 이 Task를 멈추고 보고한다 — 다른 차트 코드를 손으로 쓰지 않는다.

- [ ] **Step 2: 정적 파일 제공 테스트**

`MonitoringAppTests`에 추가:

```csharp
    [Fact]
    public async Task TheDashboard_IsServedAtTheRoot()
    {
        HttpResponseMessage page = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("ProjectH Monitoring", html);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/lib/uPlot.iife.min.js")).StatusCode);
    }
```

Run: `dotnet test Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj --filter TheDashboard_IsServedAtTheRoot`
Expected: FAIL(404).

- [ ] **Step 3: index.html**

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>ProjectH Monitoring</title>
  <link rel="stylesheet" href="lib/uPlot.min.css">
  <link rel="stylesheet" href="app.css">
</head>
<body>
  <header class="top">
    <h1>ProjectH Monitoring</h1>
    <div id="top-status" class="top-status">
      <span id="top-server" class="top-server">—</span>
      <span id="top-state" class="pill">NO DATA</span>
    </div>
  </header>

  <main class="layout">
    <aside class="servers">
      <h2>Servers</h2>
      <ul id="server-list" class="server-list"></ul>
      <p id="list-error" class="muted"></p>
    </aside>

    <section class="detail">
      <div id="offline-banner" class="banner hidden">
        OFFLINE · last seen <span id="last-seen"></span> — the values below are the last ones received, not current.
      </div>
      <div id="warnings" class="warnings hidden"></div>

      <div id="cards" class="cards">
        <div class="card" data-card="status"><div class="label">Status</div><div class="value" id="c-status">—</div>
          <div class="sub">Uptime <span id="c-uptime">—</span> · <span id="c-version">—</span></div></div>
        <div class="card" data-card="players"><div class="label">Players</div><div class="value" id="c-players">—</div>
          <div class="sub">Peers <span id="c-peers">—</span> · Graced <span id="c-graced">—</span></div></div>
        <div class="card" data-card="match"><div class="label">Match</div><div class="value" id="c-match">—</div>
          <div class="sub">Round <span id="c-round">—</span></div></div>
        <div class="card" data-card="tick"><div class="label">Tick P95</div><div class="value" id="c-tick-p95">—</div>
          <div class="sub">P50 <span id="c-tick-p50">—</span> · P99 <span id="c-tick-p99">—</span> · Max <span id="c-tick-max">—</span> · <span id="c-tick-samples">—</span> samples</div></div>
        <div class="card" data-card="cpu"><div class="label">CPU <span class="hint">(process, all cores = 100 %)</span></div><div class="value" id="c-cpu">—</div>
          <div class="sub">window <span id="c-window">—</span></div></div>
        <div class="card" data-card="memory"><div class="label">Memory</div><div class="value" id="c-managed">—</div>
          <div class="sub">Managed · Working Set <span id="c-workingset">—</span> · GC <span id="c-gc">—</span></div></div>
        <div class="card" data-card="network"><div class="label">Network</div><div class="value" id="c-net-out">—</div>
          <div class="sub">Send · Receive <span id="c-net-in">—</span> · <span id="c-pkt">—</span></div></div>
        <div class="card" data-card="errors"><div class="label">Errors (totals)</div><div class="value" id="c-exceptions">—</div>
          <div class="sub">Exceptions · Invalid packets <span id="c-invalid">—</span> · Disconnects <span id="c-disconnects">—</span></div></div>
        <div class="card" data-card="db"><div class="label">DB Queue</div><div class="value" id="c-db">—</div>
          <div class="sub">records waiting / capacity</div></div>
      </div>

      <div class="charts">
        <div class="chart"><h3>Tick (ms) — P50 / P95 / P99</h3><div id="chart-tick"></div></div>
        <div class="chart"><h3>CPU (%) — process</h3><div id="chart-cpu"></div></div>
        <div class="chart"><h3>Memory (MB) — Managed / Working Set</h3><div id="chart-memory"></div></div>
        <div class="chart"><h3>Network (KB/s) — Send / Receive</h3><div id="chart-network"></div></div>
      </div>
      <p class="muted">Last 5 minutes · list and cards refresh every 2 s, charts every 5 s · times are this browser's local time.</p>
    </section>
  </main>

  <script src="lib/uPlot.iife.min.js"></script>
  <script src="app.js"></script>
</body>
</html>
```

- [ ] **Step 4: app.css(Dark, 읽기 우선)**

```css
:root {
  --bg: #0d1117; --panel: #161b22; --border: #30363d; --text: #e6edf3; --muted: #8b949e;
  --online: #3fb950; --warning: #d29922; --offline: #f85149; --accent: #58a6ff;
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--text); font: 14px/1.4 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
h1, h2, h3 { margin: 0; font-weight: 600; }
h1 { font-size: 18px; } h2 { font-size: 13px; color: var(--muted); text-transform: uppercase; letter-spacing: .05em; margin-bottom: 8px; }
h3 { font-size: 13px; color: var(--muted); margin-bottom: 6px; }
.top { display: flex; align-items: center; justify-content: space-between; padding: 12px 20px; border-bottom: 1px solid var(--border); background: var(--panel); }
.top-status { display: flex; align-items: center; gap: 12px; }
.top-server { font-weight: 600; }
.pill { padding: 3px 10px; border-radius: 999px; font-size: 12px; font-weight: 700; letter-spacing: .04em; background: var(--border); color: var(--text); }
.pill.online { background: var(--online); color: #04130a; }
.pill.warning { background: var(--warning); color: #1a1200; }
.pill.offline { background: var(--offline); color: #1a0505; }
.layout { display: grid; grid-template-columns: 240px 1fr; gap: 16px; padding: 16px 20px; }
.servers { background: var(--panel); border: 1px solid var(--border); border-radius: 8px; padding: 12px; align-self: start; }
.server-list { list-style: none; margin: 0; padding: 0; }
.server-list li { display: flex; align-items: center; gap: 8px; padding: 8px; border-radius: 6px; cursor: pointer; }
.server-list li:hover { background: #1f2630; }
.server-list li.selected { background: #1f2a3a; outline: 1px solid var(--accent); }
.server-list .dot { width: 10px; height: 10px; border-radius: 50%; flex: none; }
.dot.online { background: var(--online); } .dot.warning { background: var(--warning); } .dot.offline { background: var(--offline); }
.server-list .name { flex: 1; font-weight: 600; }
.server-list .meta { color: var(--muted); font-size: 12px; }
.detail { min-width: 0; }
.banner { background: #3a1416; border: 1px solid var(--offline); color: #ffb3ad; padding: 10px 14px; border-radius: 8px; margin-bottom: 12px; }
.warnings { background: #2d2308; border: 1px solid var(--warning); color: #f2d27a; padding: 10px 14px; border-radius: 8px; margin-bottom: 12px; white-space: pre-line; }
.hidden { display: none; }
.cards { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 12px; margin-bottom: 16px; }
.card { background: var(--panel); border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; min-width: 0; }
.card.warn { border-color: var(--warning); }
.card .label { color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: .05em; }
.card .hint { text-transform: none; letter-spacing: 0; }
.card .value { font-size: 26px; font-weight: 700; margin: 4px 0; font-variant-numeric: tabular-nums; }
.card .sub { color: var(--muted); font-size: 12px; font-variant-numeric: tabular-nums; }
.stale .card .value, .stale .card .sub { opacity: .55; }
.charts { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
.chart { background: var(--panel); border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px; min-width: 0; }
.muted { color: var(--muted); font-size: 12px; }
.u-legend { color: var(--muted); font-size: 12px; }
@media (max-width: 1000px) {
  .layout { grid-template-columns: 1fr; }
  .cards { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .charts { grid-template-columns: 1fr; }
}
```

- [ ] **Step 5: app.js**

```js
// ProjectH Monitoring dashboard (Monitoring D13). Vanilla JS: polls the REST API and draws four uPlot charts.
// Values are formatted with units; raw bytes are never shown (request §81).
(function () {
  'use strict';

  const LIST_POLL_MS = 2000;
  const HISTORY_POLL_MS = 5000;
  const HISTORY_MINUTES = 5;
  const CHART_HEIGHT = 180;
  const COLORS = { a: '#58a6ff', b: '#3fb950', c: '#d29922', axis: '#8b949e', grid: '#21262d' };

  const $ = (id) => document.getElementById(id);
  let selected = decodeURIComponent(location.hash.slice(1)) || null;
  let servers = [];
  let charts = null;

  // ----- formatting -----
  const fmt = {
    ms: (v) => v.toFixed(2) + ' ms',
    pct: (v) => v.toFixed(1) + ' %',
    mb: (bytes) => (bytes / 1048576).toFixed(0) + ' MB',
    kbps: (bytesPerSecond) => (bytesPerSecond / 1024).toFixed(1) + ' KB/s',
    int: (v) => Math.round(v).toLocaleString(),
    seconds: (s) => s.toFixed(1) + ' s',
    uptime: (seconds) => {
      const d = Math.floor(seconds / 86400), h = Math.floor(seconds % 86400 / 3600), m = Math.floor(seconds % 3600 / 60), s = Math.floor(seconds % 60);
      return (d ? d + 'd ' : '') + String(h).padStart(2, '0') + ':' + String(m).padStart(2, '0') + ':' + String(s).padStart(2, '0');
    },
    time: (iso) => new Date(iso).toLocaleString(),
  };

  async function getJson(url) {
    const response = await fetch(url, { cache: 'no-store' });
    if (!response.ok) throw new Error(url + ' → HTTP ' + response.status);
    return response.json();
  }

  // ----- server list -----
  function renderList() {
    const list = $('server-list');
    list.innerHTML = '';
    if (servers.length === 0) {
      list.innerHTML = '<li class="muted">No server has posted yet.</li>';
      return;
    }
    for (const s of servers) {
      const li = document.createElement('li');
      li.className = s.serverId === selected ? 'selected' : '';
      li.innerHTML = '<span class="dot ' + s.status + '"></span><span class="name"></span><span class="meta"></span>';
      li.querySelector('.name').textContent = s.serverId;
      li.querySelector('.meta').textContent = s.status === 'offline' ? 'OFFLINE' : s.latest.players + ' players';
      li.addEventListener('click', () => select(s.serverId));
      list.appendChild(li);
    }
  }

  function select(serverId) {
    if (selected === serverId) return;
    selected = serverId;
    location.hash = encodeURIComponent(serverId);
    renderList();
    const s = servers.find((x) => x.serverId === serverId);
    if (s) renderDetail(s);
    refreshHistory();
  }

  // ----- detail cards -----
  function renderDetail(s) {
    const l = s.latest;
    const offline = s.status === 'offline';
    $('top-server').textContent = s.serverId;
    const pill = $('top-state');
    pill.textContent = s.status.toUpperCase();
    pill.className = 'pill ' + s.status;

    $('offline-banner').classList.toggle('hidden', !offline);
    $('last-seen').textContent = fmt.time(s.lastReceivedAt);
    const warnings = s.warnings.filter((w) => !w.startsWith('offline'));
    $('warnings').classList.toggle('hidden', warnings.length === 0);
    $('warnings').textContent = warnings.join('\n');
    $('cards').classList.toggle('stale', offline);
    document.querySelector('[data-card="tick"]').classList.toggle('warn', warnings.some((w) => w.startsWith('tick')));
    document.querySelector('[data-card="memory"]').classList.toggle('warn', warnings.some((w) => w.startsWith('working set')));

    $('c-status').textContent = s.status.toUpperCase();
    $('c-uptime').textContent = fmt.uptime(l.uptimeSeconds);
    $('c-version').textContent = 'v' + l.version + ' · protocol ' + l.protocolVersion;
    $('c-players').textContent = fmt.int(l.players);
    $('c-peers').textContent = fmt.int(l.connectedPeers);
    $('c-graced').textContent = fmt.int(l.graced);
    $('c-match').textContent = l.matchState;
    $('c-round').textContent = '#' + l.round;
    $('c-tick-p95').textContent = fmt.ms(l.tickP95Ms);
    $('c-tick-p50').textContent = fmt.ms(l.tickP50Ms);
    $('c-tick-p99').textContent = fmt.ms(l.tickP99Ms);
    $('c-tick-max').textContent = fmt.ms(l.tickMaxMs);
    $('c-tick-samples').textContent = fmt.int(l.tickSamples);
    $('c-cpu').textContent = fmt.pct(l.cpuPercent);
    $('c-window').textContent = fmt.seconds(l.windowSeconds);
    $('c-managed').textContent = fmt.mb(l.managedMemoryBytes);
    $('c-workingset').textContent = fmt.mb(l.workingSetBytes);
    $('c-gc').textContent = l.gcGen0 + ' / ' + l.gcGen1 + ' / ' + l.gcGen2;
    $('c-net-out').textContent = fmt.kbps(l.bytesOutPerSecond);
    $('c-net-in').textContent = fmt.kbps(l.bytesInPerSecond);
    $('c-pkt').textContent = fmt.int(l.packetsOutPerSecond) + ' / ' + fmt.int(l.packetsInPerSecond) + ' pkt/s';
    $('c-exceptions').textContent = fmt.int(l.exceptions);
    $('c-invalid').textContent = fmt.int(l.invalidPackets);
    $('c-disconnects').textContent = fmt.int(l.disconnects);
    $('c-db').textContent = l.dbQueueCount + ' / ' + l.dbQueueCapacity;
  }

  function renderNoServer() {
    $('top-server').textContent = '—';
    $('top-state').textContent = 'NO DATA';
    $('top-state').className = 'pill';
  }

  // ----- charts -----
  function chartOptions(width, seriesNames, colors, format) {
    const axis = { stroke: COLORS.axis, grid: { stroke: COLORS.grid }, ticks: { stroke: COLORS.grid } };
    return {
      width, height: CHART_HEIGHT,
      scales: { x: { time: true }, y: { range: (u, min, max) => [0, max <= 0 ? 1 : max * 1.1] } },
      series: [{}].concat(seriesNames.map((name, i) => ({ label: name, stroke: colors[i], width: 1.5, value: (u, v) => v == null ? '—' : format(v) }))),
      axes: [axis, Object.assign({ size: 64, values: (u, vals) => vals.map(format) }, axis)],
      legend: { show: true },
      cursor: { drag: { x: false, y: false } },
    };
  }

  function makeCharts() {
    const width = $('chart-tick').clientWidth || 400;
    const empty = (n) => [[]].concat(Array.from({ length: n }, () => []));
    return {
      tick: new uPlot(chartOptions(width, ['P50', 'P95', 'P99'], [COLORS.b, COLORS.a, COLORS.c], (v) => v.toFixed(2)), empty(3), $('chart-tick')),
      cpu: new uPlot(chartOptions(width, ['CPU %'], [COLORS.a], (v) => v.toFixed(1)), empty(1), $('chart-cpu')),
      memory: new uPlot(chartOptions(width, ['Managed MB', 'Working Set MB'], [COLORS.b, COLORS.a], (v) => v.toFixed(0)), empty(2), $('chart-memory')),
      network: new uPlot(chartOptions(width, ['Send KB/s', 'Receive KB/s'], [COLORS.a, COLORS.b], (v) => v.toFixed(1)), empty(2), $('chart-network')),
    };
  }

  function renderHistory(samples) {
    if (!charts) charts = makeCharts();
    const x = samples.map((s) => Date.parse(s.receivedAt) / 1000);
    const pick = (f) => samples.map((s) => f(s.snapshot));
    charts.tick.setData([x, pick((s) => s.tickP50Ms), pick((s) => s.tickP95Ms), pick((s) => s.tickP99Ms)]);
    charts.cpu.setData([x, pick((s) => s.cpuPercent)]);
    charts.memory.setData([x, pick((s) => s.managedMemoryBytes / 1048576), pick((s) => s.workingSetBytes / 1048576)]);
    charts.network.setData([x, pick((s) => s.bytesOutPerSecond / 1024), pick((s) => s.bytesInPerSecond / 1024)]);
  }

  function resizeCharts() {
    if (!charts) return;
    const width = $('chart-tick').clientWidth || 400;
    for (const c of Object.values(charts)) c.setSize({ width, height: CHART_HEIGHT });
  }

  // ----- polling -----
  async function refreshList() {
    try {
      servers = await getJson('/api/servers');
      $('list-error').textContent = '';
      if (!selected && servers.length > 0) {
        selected = servers[0].serverId;
        location.hash = encodeURIComponent(selected);
        refreshHistory();
      }
      renderList();
      const s = servers.find((x) => x.serverId === selected);
      if (s) renderDetail(s); else renderNoServer();
    } catch (e) {
      $('list-error').textContent = 'Monitoring server unreachable: ' + e.message;
      renderNoServer();
    }
  }

  async function refreshHistory() {
    if (!selected) return;
    try {
      const history = await getJson('/api/servers/' + encodeURIComponent(selected) + '/metrics?minutes=' + HISTORY_MINUTES);
      renderHistory(history.samples);
    } catch (e) {
      // the list poll shows the error; keep the last chart
    }
  }

  window.addEventListener('resize', resizeCharts);
  window.addEventListener('hashchange', () => {
    const id = decodeURIComponent(location.hash.slice(1));
    if (id && id !== selected) select(id);
  });
  refreshList();
  refreshHistory();
  setInterval(refreshList, LIST_POLL_MS);
  setInterval(refreshHistory, HISTORY_POLL_MS);
})();
```

- [ ] **Step 6: 테스트와 수동 확인(§68)**

Run: `dotnet test Server/tests/ProjectH.Monitoring.Tests/ProjectH.Monitoring.Tests.csproj`
Expected: 모두 PASS(정적 파일 테스트 포함; `wwwroot`가 테스트 출력에 복사되지 않아 404면 Monitoring csproj의 `<Content Update="wwwroot\**" CopyToOutputDirectory="PreserveNewest" />`가 ProjectReference로 전파되는지 확인하고, 안 되면 테스트 csproj에 `<None Include="..\..\src\ProjectH.Monitoring\wwwroot\**" Link="wwwroot\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />`를 더한다).

수동 확인(브라우저; 자동화하지 않는다, D16). Monitoring Server + Game Server(`Monitoring__Enabled=true`)를 띄우고 `http://127.0.0.1:5080`을 연다:

| # | 확인 | 기대 |
|---|---|---|
| 1 | 서버 목록 | `dev-server-01` 한 줄, 초록 점, `0 players` |
| 2 | 상단 | `dev-server-01` + `ONLINE` 초록 pill |
| 3 | Players 카드 | 봇/Client를 붙이면 수가 오른다(`dotnet run --project Server/src/ProjectH.Bots -- --count 3` 등 기존 방법) |
| 4 | Tick 카드 | P95가 ms로, 샘플 수 ≈ 150(30 Hz × 5 s) |
| 5 | CPU 카드 | `%`와 "(process, all cores = 100 %)" 문구 |
| 6 | Memory 카드 | Managed와 Working Set이 MB로 따로 |
| 7 | Network 카드 | Send/Receive KB/s |
| 8 | 그래프 4개 | 5초마다 점이 늘어난다; 각 Y축 단위가 다르다 |
| 9 | Game Server 종료 | 15 s 안에 pill이 `OFFLINE`(빨강), 배너 "OFFLINE · last seen …", 카드가 흐려진다; 목록 점 빨강 |
| 10 | Game Server 재시작 | `ONLINE`으로 돌아오고 Monitoring 로그에 "online again" |
| 11 | 창 폭 900 px | 목록이 위로, 카드 2열, 그래프 1열, 가로 스크롤 없음 |

결과를 `Docs/Monitoring.md` "Web UI" 절의 확인 표에 그대로 적는다(한 항목이라도 확인하지 못했으면 "미확인"으로 적는다).

- [ ] **Step 7: M3 Commit**

```bash
git add Server/src/ProjectH.Monitoring/wwwroot Server/src/ProjectH.Monitoring/ProjectH.Monitoring.csproj Server/tests/ProjectH.Monitoring.Tests
git commit -m "feat(monitoring): add the web dashboard (status, players, tick, cpu, memory, network, errors) (M3)"
```

---

### Task 12: 문서 — `Docs/Monitoring.md`, 포인터

**Files:**
- Create: `Docs/Monitoring.md`
- Modify: `Docs/Server.md`("관측" 절 끝에 한 단락)
- Modify: `README.md`("Phase별 행동과 판단" 끝에 한 절)

- [ ] **Step 1: Docs/Monitoring.md**

아래 본문을 그대로 쓴다. "측정" 절은 Task 13이 채운다(그 전에는 절 제목 없이 둔다 — 측정 전 수치를 쓰지 않는다).

````markdown
# Monitoring

Game Server를 실시간으로 보는 가벼운 내부 도구다. 요청서 `Docs/requests/2026-10-08-monitoring-request.md`, 설계 `Docs/specs/2026-10-08-monitoring-design.md`(D1–D18).

## Architecture

```text
Game Server (ProjectH.Server)                       Monitoring Server (ProjectH.Monitoring)
┌─────────────────────────────────────────┐          ┌──────────────────────────────────────┐
│ Game Loop thread                        │          │ Kestrel 127.0.0.1:5080               │
│  every tick: RecordTick(ms)             │  HTTP    │  POST /api/ingest/metrics            │
│  every 5 s : Publish → MonitoringSlot ──┼─POST────▶│   token → JSON → SnapshotValidator   │
│ MonitoringSender (BackgroundService)    │  2 s     │   → MetricStore (lock, ring/server)  │
│  Take() → HttpClient, timeout, best effort│ timeout │  GET /api/servers, /{id}, /{id}/metrics│
└─────────────────────────────────────────┘          │  OfflineSweeper (log only)           │
                                                     │  wwwroot (uPlot, polling 2 s / 5 s)  │
                                                     └──────────────────────────────────────┘
```

- 둘은 별도 프로세스다. `ProjectH.Monitoring.Contracts`(Snapshot DTO와 상수)만 공유하고, Monitoring Server는 `ProjectH.Server`·`ProjectH.Shared`를 참조하지 않는다.
- Monitoring 장애는 Game Server에 닿지 않는다: Game Loop는 HTTP를 모르고(슬롯에 쓰기만), Sender는 실패를 버리고 다음 주기에 새 Snapshot을 보낸다. 슬롯은 용량 1이라 Monitoring Server가 느려도 Game Server 메모리는 Snapshot 하나다.
- Game Server가 멈추면 Snapshot이 안 나오고(Sender는 빈 슬롯이면 보내지 않는다) Monitoring Server는 `OfflineThresholdSeconds` 뒤 Offline으로 본다. Heartbeat는 Snapshot 자체다.

## How to Start

```bash
dotnet run --project Server/src/ProjectH.Monitoring                      # http://127.0.0.1:5080
Monitoring__Enabled=true dotnet run --project Server/src/ProjectH.Server   # PowerShell: $env:Monitoring__Enabled='true'
```

브라우저에서 `http://127.0.0.1:5080`. 둘의 시작 순서는 상관없다. 시작할 때 `Monitoring__Endpoint`·`Monitoring__ServerId`를 바꿀 수 있다.

## Game Server Configuration (`Monitoring` 섹션)

| 키 | 기본 | 범위 | 뜻 |
|---|---|---|---|
| `Enabled` | `false` | | 꺼져 있으면 슬롯·수집기·Sender·HttpClient를 만들지 않는다(Tick당 null 검사 1회) |
| `Endpoint` | `http://127.0.0.1:5080` | 절대 http/https URL | Monitoring Server 주소. `/api/ingest/metrics`를 붙여 보낸다 |
| `ServerId` | `dev-server-01` | `[A-Za-z0-9._-]{1,64}` | 이 서버의 이름. 서버마다 다르게 |
| `IntervalSeconds` | `5` | 1–60 | Snapshot 주기 |
| `TimeoutSeconds` | `2` | 1–30, ≤ Interval | 요청 하나의 상한 |
| `Token` | 없음 | | `X-Monitoring-Token` 값. **환경변수 `Monitoring__Token`으로만**(appsettings에 쓰지 않는다) |

틀린 값은 `PersistenceOptions`처럼 시작을 막는다. 환경변수는 `Monitoring__키`.

## Monitoring Server Configuration (`MonitoringServer` 섹션)

| 키 | 기본 | 범위 | 뜻 |
|---|---|---|---|
| `Urls`(최상위) | `http://127.0.0.1:5080` | | Bind. 외부 공개는 기본이 아니다 |
| `HistoryMinutes` | `10` | 1–120 | 서버별 메모리 History 길이 |
| `ExpectedIntervalSeconds` | `5` | 1–60 | Game Server의 Interval. 링 용량 = HistoryMinutes × 60 / 이 값(기본 120개) |
| `OfflineThresholdSeconds` | `15` | ≥ 2 × ExpectedInterval, ≤ 3600 | 마지막 수신 뒤 이 초를 넘으면 Offline. 15 s = 주기 3배: 전송 한 번 실패로는 Offline이 되지 않는다 |
| `SweepIntervalSeconds` | `5` | 1–60 | "offline" 로그를 찾는 주기(상태 자체는 읽을 때 계산) |
| `MaxServers` | `32` | 1–1000 | 그 이상의 새 ServerId는 429 |
| `MaxClockSkewSeconds` | `300` | 1–86400 | `observedAt`이 이보다 어긋나면 400 |
| `TickP95WarningMs` | `16.7` | ≥ 0 | 30 Hz Tick 예산 33.3 ms의 절반 |
| `MemoryWarningBytes` | `0`(꺼짐) | ≥ 0 | Working Set 경고 기준. 측정 전이라 꺼져 있다("측정" 절) |
| `IngestToken` | 없음 | | 설정되면 Token 없는 Ingest는 401. **환경변수 `MonitoringServer__IngestToken`으로만** |

## Metrics (Snapshot 필드)

모든 값은 Game Server가 이미 가진 값이다. 추정하지 않는다.

| 필드 | 단위 | 기준 | 출처 |
|---|---|---|---|
| `serverId`, `version`, `protocolVersion` | | | 설정, 어셈블리 InformationalVersion, `ProtocolConstants.ProtocolVersion` |
| `startedAt`, `observedAt` | UTC | Snapshot을 만든 Game Server 시계 | Monitoring Server는 `receivedAt`을 따로 기록한다 |
| `uptimeSeconds`, `windowSeconds` | s | 창 = 직전 Snapshot부터 실제 경과 | |
| `connectedPeers`, `players`, `graced` | | 지금 값. `players`는 유예(재접속 대기) 포함 | `HealthCounters` 게이지와 같은 값 |
| `matchState`, `round` | | 경기 하나 | `MatchFlowState` 이름 |
| `tickSamples`, `tickP50Ms`, `tickP95Ms`, `tickP99Ms`, `tickMaxMs` | ms | 이 창의 Tick(nearest-rank 백분위, Stats 로그와 같은 `TickMetrics`) | 자기 링(SimHz × Interval) |
| `cpuPercent` | % | **프로세스** CPU 시간 ÷ (창 × 논리 코어 수) × 100. 모든 코어를 다 쓰면 100. Stats 로그 `cpu%`와 같은 식 | `Environment.CpuUsage` |
| `managedMemoryBytes`, `workingSetBytes` | B | 지금 값 | `GC.GetTotalMemory(false)`, `Environment.WorkingSet` |
| `gcGen0/1/2` | | 시작부터 누적 | `GC.CollectionCount` |
| `packetsIn/OutPerSecond`, `bytesIn/OutPerSecond` | /s | 이 창. LiteNetLib 페이로드 기준(IP/UDP 헤더 제외) | `ServerStats` 누적의 차이 |
| `exceptions` | | 누적 = tickFailures + loopFailures + playerFailures + callbackErrors | `HealthCounters` |
| `invalidPackets` | | 누적 = badPackets 전체 | `HealthCounters.BadPacketsTotal` |
| `disconnects` | | 누적 = timeout + other | `HealthCounters` |
| `dbQueueCount`, `dbQueueCapacity` | | 경기 기록 큐(`MatchHistoryQueue`)의 지금 수와 용량 | |

Monitoring Server는 저장할 때 `receivedAt`(자기 시계)을 붙인다. UI의 Offline 판단·그래프 X축은 `receivedAt`이다.

## API

| 경로 | 응답 |
|---|---|
| `POST /api/ingest/metrics` | 202. 401 Token 틀림, 400 JSON·값 오류(`{"error": "..."}`), 413 16 KB 초과, 429 MaxServers 초과 |
| `GET /health` | `{"status":"ok","servers":n}` |
| `GET /api/servers` | `[{serverId, status(online/warning/offline), lastReceivedAt, lastObservedAt, warnings[], latest{Snapshot}}]` 이름순 |
| `GET /api/servers/{id}` | 위 + `thresholds{tickP95WarningMs, memoryWarningBytes, offlineThresholdSeconds, historyMinutes}`. 404 모름 |
| `GET /api/servers/{id}/metrics?minutes=5` | `{serverId, minutes, samples[{receivedAt, snapshot}]}` 오래된 순. `minutes`는 1–HistoryMinutes로 Clamp(기본 5) |

예:

```bash
curl -s -X POST http://127.0.0.1:5080/api/ingest/metrics -H 'Content-Type: application/json' -H 'X-Monitoring-Token: secret' \
  -d '{"serverId":"dev-server-01","version":"1.0.0","protocolVersion":18,"startedAt":"2026-10-08T12:00:00Z","observedAt":"2026-10-08T12:05:00Z","uptimeSeconds":300,"windowSeconds":5,"matchState":"Playing","players":2,"tickP95Ms":0.4,"dbQueueCapacity":16}'
curl -s http://127.0.0.1:5080/api/servers
```

(`observedAt`은 지금 시각 ±300 s 안이어야 한다.)

## Web UI

`wwwroot/index.html` + `app.js` + `app.css`, 빌드 없음, Dark. 왼쪽 서버 목록(상태 점·Players), 오른쪽 선택한 서버(URL `#serverId`). 카드: Status(Uptime·버전), Players(Peers·Graced), Match, Tick P95(P50·P99·Max·샘플), CPU(process, all cores = 100 %), Memory(Managed·Working Set·GC), Network(Send·Receive KB/s, pkt/s), Errors(Exceptions·Invalid·Disconnects), DB Queue. 그래프 4개(Tick P50/P95/P99, CPU, Memory, Network, 각자 Y축), 최근 5분. 목록·카드 2 s, 그래프 5 s Polling. Offline이면 빨간 배너 "OFFLINE · last seen …"와 값 흐리게. 경고는 카드 테두리 amber + 문장.

차트 라이브러리 **uPlot 1.6.31**(MIT, `wwwroot/lib/`, 의존성 없음, 약 45 KB): 캔버스 기반이라 선 그래프 4개를 2–5 s마다 다시 그려도 가볍고, 빌드 체인이 필요 없다. 라이선스는 `wwwroot/lib/uPlot.LICENSE`.

수동 확인(§68, Task 11 표): 구현 때 확인한 결과를 여기 적는다.

## Security

- 기본 Bind 127.0.0.1. 외부 공개·TLS·Reverse Proxy·Web UI 인증은 이 버전에 없다(별도 Phase).
- Ingest Token은 옵션(`MonitoringServer__IngestToken` ↔ `Monitoring__Token`). 비교는 고정 시간. Secret은 Repository에 없다.
- Ingest는 Body 16 KB 상한, JSON·값 검증(ServerId 규칙, 시각, 음수, NaN/Infinity, 문자열 길이), MaxServers로 메모리 상한. 잘못된 요청 로그는 분당 1줄.

## Adding a New Metric

1. `ServerMonitoringSnapshot`에 속성 하나(`Contracts`).
2. `MonitoringCollector.Publish`에 값 한 줄(Game Loop 스레드가 이미 가진 값만; 비유한값은 `Finite`).
3. 범위 검사가 필요하면 `SnapshotValidator`에 한 줄(double은 `Check`, 정수는 음수 검사 줄).
4. `app.js`의 카드/그래프.

Store·API는 Snapshot을 그대로 통과시키므로 고치지 않는다. 경고 규칙은 `ServerStatus.Of`, 임계값은 `MonitoringServerOptions`.

## Known Limitations

- History는 메모리뿐이다(기본 10분, 재시작하면 비어 있다). 장기 저장·Downsampling 없음.
- Realtime 아님(Polling 2 s / 5 s). SignalR 없음.
- 알림 없음(Discord/Slack/Email). UI 경고뿐.
- Web UI 인증·TLS 없음: 내부 네트워크 전용.
- 한 서버 = 경기 하나. `activeMatches`는 없다.
- Sender는 실패한 Snapshot을 버린다(재시도 큐 없음). Monitoring Server가 내려가 있던 동안의 값은 사라진다.
- Network는 LiteNetLib 페이로드 기준이라 OS 수준 트래픽보다 작다.
- 브라우저 표시는 자동 테스트가 없다(수동 확인 표).
````

- [ ] **Step 2: 포인터**

`Docs/Server.md` "관측" 절 맨 끝에:

```markdown
**Monitoring Server(2026-10-08):** 같은 값(Tick 백분위·CPU·메모리·네트워크·Health 카운터)을 5 s마다 별도 프로세스 `ProjectH.Monitoring`으로 보내 웹에서 본다. Game Loop는 슬롯에 Snapshot을 넣기만 하고 전송은 `MonitoringSender`가 한다. 기본은 꺼져 있다(`Monitoring:Enabled`). `Monitoring.md`.
```

`README.md` "### QA 도구 (QA-1–5, 스트레스)" 절 뒤(같은 형식: **행동:** / **판단:** 두 줄):

```markdown
### Monitoring Server

- **행동:** Game Server가 5초마다 Snapshot(Players·Tick P50/P95/P99/Max·CPU·Memory·Network·Error)을 별도 프로세스 Monitoring Server로 보내고, 정적 웹 대시보드에서 본다.
- **판단:** Game Loop는 용량 1 슬롯에 쓰기만 하고 전송은 BackgroundService가 2초 Timeout으로 한다 — Monitoring Server가 죽거나 느려도 Game Server는 Snapshot 하나만 들고 있고 로그는 상태가 바뀔 때 한 줄이다. 둘은 DTO 프로젝트 하나만 공유한다. DB·알림·인증은 넣지 않았다([Docs/Monitoring.md](Docs/Monitoring.md)).
```

- [ ] **Step 3: 확인**

Run: `dotnet test Server/ProjectH.Server.slnx`
Expected: 모두 PASS(문서만 바뀜). Commit은 Task 13 끝에서 M4로 묶는다.

---

### Task 13: 실제 프로세스 검증(§67·§91), 성능 비교(§90), M4 Commit, 최종 보고

**Files:**
- Modify: `Docs/Monitoring.md`("측정" 절 추가, Web UI 수동 확인 표 채움)
- Modify: `Docs/specs/2026-10-08-monitoring-design.md`(바뀐 결정이 있으면 D 표에 되돌려 적음)

- [ ] **Step 1: Release 빌드**

```bash
dotnet build Server/ProjectH.Server.slnx -c Release
```
Expected: 0 Error.

- [ ] **Step 2: §67 정상 수신 + §91 장애 (a)–(e)**

PowerShell 창 M(Monitoring): `dotnet run --project Server/src/ProjectH.Monitoring -c Release`
PowerShell 창 G(Game): `$env:Monitoring__Enabled='true'; dotnet run --project Server/src/ProjectH.Server -c Release`

| # | 상황 | 한다 | 기대(Game Server 창) | 기대(Monitoring) |
|---|---|---|---|---|
| a | 정상 | 20 s 기다린 뒤 `curl -s http://127.0.0.1:5080/api/servers` | `Stats`·`Health` 줄이 10 s마다 계속 | `first seen` 1줄, API에 `online`·`tickSamples` ≈ 150 |
| b | Monitoring 종료 | 창 M Ctrl+C, 30 s 기다림 | `Monitoring connection lost (...)` Warning **1줄**, 이후 Warning 없음, Stats 줄 계속 | — |
| c | 재시작 | 창 M 다시 실행, 15 s | `Monitoring connection restored after N failed posts` Information 1줄 | `first seen`(History는 비어 있었음), API `online` |
| d | 잘못된 Endpoint | 창 G 종료 후 `$env:Monitoring__Endpoint='http://nonexistent.invalid:1'`로 다시 실행, 30 s | `lost` 1줄(DNS), Stats 줄 계속, 종료 Ctrl+C가 바로 된다 | — |
| e | Connection Refused | `$env:Monitoring__Endpoint='http://127.0.0.1:1'`로 실행, 30 s | `lost` 1줄, Stats 줄 계속 | — |

(f) Timeout·느린 응답은 Task 9의 `ASlowServer_…`·`Stop_CancelsAnInFlightPost_…`가 실제 loopback HTTP로 검증한다(D16). 각 상황의 로그 줄 수를 세어 "측정" 절에 적는다. 어느 상황에서든 Game Server 창의 Stats 줄 간격이 10 s를 벗어나거나 `tickFailures`·`loopFailures`가 0이 아니면 **구현 결함**이다 — 보고하고 고친다.

추가로 QA smoke: 창 M·G를 모두 닫고 `dotnet run --project Server/src/ProjectH.QA -c Release -- run QA/Suites/smoke.json`(QA 도구가 서버를 띄운다, Monitoring 꺼짐). Expected: PASS(회귀 없음).

- [ ] **Step 3: §90 성능 비교(50명, OFF·ON 각 2회)**

QA Stress는 `server.options`로 서버를 띄우며 CLI에 서버 옵션 플래그가 없다. 환경변수는 자식 프로세스에 상속되므로 QA 도구를 띄우는 셸의 환경변수로 켠다.

```powershell
# OFF (2회)
Remove-Item Env:Monitoring__Enabled -ErrorAction SilentlyContinue
dotnet run --project Server/src/ProjectH.QA -c Release -- run QA/Scenarios/Stress/baseline.json --parameter-set 3 --set steadySeconds=60
dotnet run --project Server/src/ProjectH.QA -c Release -- run QA/Scenarios/Stress/baseline.json --parameter-set 3 --set steadySeconds=60
# ON (2회): Monitoring Server를 다른 창에서 띄운 채
$env:Monitoring__Enabled='true'
dotnet run --project Server/src/ProjectH.QA -c Release -- run QA/Scenarios/Stress/baseline.json --parameter-set 3 --set steadySeconds=60
dotnet run --project Server/src/ProjectH.QA -c Release -- run QA/Scenarios/Stress/baseline.json --parameter-set 3 --set steadySeconds=60
```

`--parameter-set 3` = 50명(1부터: 10/25/50/75/100). 각 Report(`QA/Reports/`)의 `steady` 구간 `tickP50Ms`·`tickP95Ms`·`tickP99Ms`·`workingSetMB`와, 서버 로그의 steady 구간 `Stats … cpu%=` 값(QA Report가 저장한 서버 로그 또는 창 출력)을 적는다. ON 실행 중 `curl -s http://127.0.0.1:5080/api/servers`로 `players`가 50인지도 확인한다(실제 수신 증거).

`Docs/Monitoring.md` 끝에 "## 측정" 절:

```markdown
## 측정 (2026-10-08, <머신: CPU 모델·코어 수>, Release)

### 성능 비교 (§90) — baseline 50명, steady 60 s

| 실행 | Tick P50 | Tick P95 | Tick P99 | CPU % (Stats) | Working Set MB |
|---|---|---|---|---|---|
| Monitoring OFF #1 | | | | | |
| Monitoring OFF #2 | | | | | |
| Monitoring ON #1 | | | | | |
| Monitoring ON #2 | | | | | |

판단: (ON이 OFF보다 P95가 더 나쁜지, 두 OFF 실행 사이의 차이보다 큰지 — 실측으로만)

### 네트워크 (§92)
- Interval: 5 s(설정). Payload: <Task 9 `Sends_…` 테스트의 LastBodyBytes 값 또는 Monitoring 로그> bytes. Timeout: 2 s(설정).

### 장애 검증 (§91)
| 상황 | Game Server 로그 | Stats 줄 계속 | 비고 |
|---|---|---|---|
| 정상 | | | |
| Monitoring 종료 | lost N줄 | | |
| 재시작 | restored N줄 | | |
| 잘못된 Endpoint | | | |
| Connection Refused | | | |
| Timeout·느린 응답 | 자동 테스트(MonitoringSenderTests) | | |

### MemoryWarningBytes
OFF/ON Working Set 실측 최대 <X> MB → `MemoryWarningBytes`를 <2X MB를 바이트로>로 둔다(근거: 측정 최대의 2배). appsettings.json도 같이 바꾼다.
```

표의 빈칸은 전부 실측값으로 채운다. 측정하지 못한 칸은 "미측정"과 이유를 적는다. `MemoryWarningBytes`를 정했으면 `Server/src/ProjectH.Monitoring/appsettings.json`과 `Docs/Monitoring.md` 설정 표의 기본값 설명도 맞춘다(`MonitoringServerOptions`의 코드 기본값은 0으로 둔다: 측정이 없는 환경의 기본).

- [ ] **Step 4: 전체 테스트 두 번, M4 Commit**

```bash
dotnet test Server/ProjectH.Server.slnx
dotnet test Server/ProjectH.Server.slnx
git add Docs/Monitoring.md Docs/Server.md README.md Docs/specs/2026-10-08-monitoring-design.md Docs/plans/2026-10-08-monitoring.md Server/src/ProjectH.Monitoring/appsettings.json
git commit -m "docs(monitoring): document the monitoring server and record the measurements (M4)"
```

- [ ] **Step 5: 최종 보고(§92 형식)**

채팅에 다음 절을 모두 쓴다: Architecture, Projects, Game Server, Monitoring Server, Web UI, Network(Interval/Payload/Timeout 실측), Performance(OFF/ON 표 실측), Tests(실행한 명령과 수), Failure Test, Run, Configuration, Known Limitations, Future. 그리고 **§89 완료 조건 23개**를 체크 표로 하나씩 확인한다(확인하지 못한 항목은 빈 칸으로 둔다). 하네스 확인 항목(스펙 "하네스 확인이 필요한 것")을 마지막에 적는다. Push는 하지 않는다.

---

## Self-Review (작성 시점)

- **Spec coverage:** D1 Task 1·2 / D2 Task 1 / D3 Task 8 / D4 Task 7 / D5 Task 9·10 / D6 Task 6 / D7 Task 2·5 / D8 Task 3·5 / D9 Task 4 / D10 Task 4·5 / D11 Task 2 / D12 Task 5 / D13 Task 11 / D14 Task 5·6(Token 키 없음 테스트) / D15 Task 1–10 / D16 Task 13 / D17 Task 12 / D18 범위 밖. §89 완료 조건은 Task 13 Step 5가 하나씩 확인한다.
- **Type consistency:** `MonitoringSource`의 위치 인자 순서(ConnectedPeers, Players, Graced, MatchState, Round, PacketsIn, PacketsOut, BytesIn, BytesOut, Health)는 Task 8의 테스트·Collector·GameLoop 호출에서 같다. `ServerSummary`·`MetricSample`·`IngestOutcome`·`ServerState`는 Task 4 정의를 Task 5가 그대로 쓴다. `MonitoringSender` 생성자 `(options, slot, logger, interval?, timeout?)`는 Task 9 테스트와 Task 10 `MonitoringSetup`에서 같다.
- **Review Focus:** 1 → Task 9 `AnEmptySlot_SendsNothing`, 2 → Task 9 `ASlowServer_…`, 3 → Task 8 `ZeroWindow_…`(−1 큐), 4 → Task 8 `ZeroWindow_…` + Task 9의 모든 예외 포착, 5 → Task 5 `UnknownServer_Is404_AndMinutesAreClamped`.
- **Placeholder:** 측정값 칸은 실측 전에 채울 수 없어 비워 두고 "실측값만" 규칙으로 묶었다. 그 밖의 TBD·"적절히" 없음.
