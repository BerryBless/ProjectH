using System.Diagnostics;
using ProjectH.QA.Faults;

namespace ProjectH.QA.Tests.Faults;

public sealed class DockerDbControllerTests
{
    // Records every call and answers from a script. Answers get the call's token so a test can model cancellation.
    private sealed class FakeDocker
    {
        public readonly List<(string[] Args, TimeSpan Timeout)> Calls = new();
        public Func<string[], CancellationToken, DockerCommandResult> Answer = (_, _) => Ok("");

        public DockerCommandRunner Runner => (args, timeout, ct) =>
        {
            var copy = args.ToArray();
            Calls.Add((copy, timeout));
            return Task.FromResult(Answer(copy, ct));
        };

        // 기능: 종료 코드 0으로 성공한 docker 명령 결과를 만든다.
        // 입력: stdout - 명령의 표준 출력.
        // 출력: 시간 초과·미설치가 아닌 성공 DockerCommandResult.
        public static DockerCommandResult Ok(string stdout) => new(0, stdout, "", false, false);

        // 기능: docker inspect가 "이름|값" 한 줄을 출력한 성공 결과를 만든다.
        // 입력: value - inspect 형식의 필드 값, name - 컨테이너 이름(기본 /projecth-mysql).
        // 출력: 표준 출력이 "name|value\n"인 성공 DockerCommandResult.
        // What `docker inspect --format "{{.Name}}|<field>"` prints for the container.
        public static DockerCommandResult Inspect(string value, string name = "/projecth-mysql") => Ok(name + "|" + value + "\n");

        // 기능: 실행 중이고 healthy한 projecth-mysql처럼 docker 명령에 답한다.
        // 입력: args - docker 명령 인자(첫 요소가 하위 명령).
        // 출력: inspect면 형식에 맞는 running/healthy/이름 결과, 그 외 명령은 빈 성공 결과.
        // A healthy, running projecth-mysql: answers every inspect the controller makes; other commands succeed.
        public static DockerCommandResult Running(string[] args) => args[0] switch
        {
            "inspect" when Format(args).EndsWith("{{.State.Running}}") => Inspect("true"),
            "inspect" when Format(args).EndsWith(DockerDbController.HealthFormat) => Inspect("healthy"),
            "inspect" => Inspect("/projecth-mysql"),
            _ => Ok(""),
        };
    }

    private static readonly DockerCommandResult TimedOut = new(-1, "", "", true, false);
    private static readonly DockerCommandResult NotFound = new(-1, "", "no docker", false, true);

    // 기능: docker 명령 인자에서 --format 다음 값을 꺼낸다.
    // 입력: args - docker 명령 인자.
    // 출력: --format 인자의 값. --format이 없으면 args[0].
    private static string Format(string[] args) => args[Array.IndexOf(args, "--format") + 1];

    [Fact]
    public async Task StopRunningContainerRecordsItAndRestoreStartsIt()
    {
        var fake = new FakeDocker { Answer = (args, _) => FakeDocker.Running(args) };
        var db = new DockerDbController(runner: fake.Runner);

        var stop = await db.StopAsync();
        Assert.True(stop.Ok);
        Assert.True(db.StoppedByThisController);
        Assert.Equal(["inspect", "--type", "container", "--format", "{{.Name}}|{{.State.Running}}", "projecth-mysql"], fake.Calls[0].Args);
        Assert.Equal(["stop", "--time", "10", "projecth-mysql"], fake.Calls[1].Args);
        Assert.True(fake.Calls[1].Timeout > TimeSpan.FromSeconds(DockerDbController.StopGraceSeconds));

        var restore = await db.RestoreAsync(TimeSpan.FromSeconds(5));
        Assert.True(restore.Attempted);
        Assert.True(restore.Ok, restore.Detail);
        Assert.False(db.StoppedByThisController);
        Assert.Contains(fake.Calls, c => c.Args.SequenceEqual(["start", "projecth-mysql"]));
        AssertNoDestructiveCommands(fake);
    }

    [Fact]
    public async Task StopOfAStoppedContainerIsNotRecordedAndRestoreDoesNothing()
    {
        var fake = new FakeDocker { Answer = (_, _) => FakeDocker.Inspect("false") };
        var db = new DockerDbController(runner: fake.Runner);

        var stop = await db.StopAsync();
        Assert.True(stop.Ok);
        Assert.False(db.StoppedByThisController);
        Assert.Single(fake.Calls);   // inspect only, no stop

        var restore = await db.RestoreAsync(TimeSpan.FromSeconds(1));
        Assert.False(restore.Attempted);
        Assert.True(restore.Ok);
        Assert.Single(fake.Calls);   // no start
    }

    // Docker may still stop the container after the CLI timed out, so cleanup must start it again.
    [Fact]
    public async Task StopTimeoutIsRecordedSoRestoreStartsIt()
    {
        var fake = new FakeDocker { Answer = (args, _) => args[0] == "stop" ? TimedOut : FakeDocker.Running(args) };
        var db = new DockerDbController(runner: fake.Runner);

        var stop = await db.StopAsync();
        Assert.False(stop.Ok);
        Assert.True(stop.TimedOut);
        Assert.True(db.StoppedByThisController);

        var restore = await db.RestoreAsync(TimeSpan.FromSeconds(5));
        Assert.True(restore.Attempted);
        Assert.True(restore.Ok, restore.Detail);
        Assert.Contains(fake.Calls, c => c.Args.SequenceEqual(["start", "projecth-mysql"]));
    }

    [Fact]
    public async Task CancelDuringStopIsRecordedSoRestoreStartsIt()
    {
        using var scenario = new CancellationTokenSource();
        var fake = new FakeDocker();
        fake.Answer = (args, ct) =>
        {
            if (args[0] != "stop") return FakeDocker.Running(args);
            // The real runner kills its child and rethrows when the token is cancelled mid-call.
            scenario.Cancel();
            ct.ThrowIfCancellationRequested();
            return FakeDocker.Ok("");
        };
        var db = new DockerDbController(runner: fake.Runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.StopAsync(scenario.Token));
        Assert.True(db.StoppedByThisController);

        var restore = await db.RestoreAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(restore.Ok, restore.Detail);
        Assert.False(db.StoppedByThisController);
        Assert.Contains(fake.Calls, c => c.Args.SequenceEqual(["start", "projecth-mysql"]));
    }

    [Fact]
    public async Task UnavailableWhenDockerMissingOrContainerAbsent()
    {
        var missing = new DockerDbController(runner: new FakeDocker { Answer = (_, _) => NotFound }.Runner);
        Assert.False(await missing.IsAvailableAsync());

        var absent = new DockerDbController(runner: new FakeDocker { Answer = (_, _) => new(1, "", "No such container", false, false) }.Runner);
        Assert.False(await absent.IsAvailableAsync());

        var fake = new FakeDocker { Answer = (_, _) => FakeDocker.Inspect("/my-db", "/my-db") };
        var present = new DockerDbController("my-db", fake.Runner);
        Assert.True(await present.IsAvailableAsync());
        Assert.Equal(["inspect", "--type", "container", "--format", "{{.Name}}|{{.Name}}", "my-db"], fake.Calls[0].Args);
    }

    // `docker inspect abc123` also matches a container whose ID starts with abc123. Such a match must be refused and
    // must never lead to stop or start.
    [Fact]
    public async Task IdPrefixMatchOfAnotherContainerIsRefused()
    {
        var fake = new FakeDocker
        {
            Answer = (args, _) => args[0] == "inspect" ? FakeDocker.Inspect("true", "/other-db") : FakeDocker.Ok(""),
        };
        var db = new DockerDbController("abc123", fake.Runner);

        Assert.False(await db.IsAvailableAsync());

        var stop = await db.StopAsync();
        Assert.False(stop.Ok);
        Assert.Contains("another container", stop.StdErr);
        Assert.False(db.StoppedByThisController);

        var start = await db.StartAsync();
        Assert.False(start.Ok);

        var health = await db.WaitHealthyAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(health.Healthy);

        Assert.All(fake.Calls, c => Assert.Equal("inspect", c.Args[0]));
    }

    [Fact]
    public async Task WaitHealthyPollsThroughStartingAndUnhealthy()
    {
        var statuses = new Queue<string>(["starting", "unhealthy", "healthy"]);
        var fake = new FakeDocker { Answer = (_, _) => FakeDocker.Inspect(statuses.Dequeue()) };
        var db = new DockerDbController(runner: fake.Runner);

        var health = await db.WaitHealthyAsync(TimeSpan.FromSeconds(10));
        Assert.True(health.Healthy);
        Assert.Equal("healthy", health.LastStatus);
        Assert.Equal(3, fake.Calls.Count);
        Assert.Equal("{{.Name}}|" + DockerDbController.HealthFormat, Format(fake.Calls[0].Args));
    }

    [Fact]
    public async Task WaitHealthyTimesOutWithLastStatus()
    {
        var fake = new FakeDocker { Answer = (_, _) => FakeDocker.Inspect("starting") };
        var db = new DockerDbController(runner: fake.Runner);

        var clock = Stopwatch.StartNew();
        var health = await db.WaitHealthyAsync(TimeSpan.FromMilliseconds(1200));
        Assert.False(health.Healthy);
        Assert.Equal("starting", health.LastStatus);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        // Every poll's timeout is capped by the time left.
        Assert.All(fake.Calls, c => Assert.True(c.Timeout <= TimeSpan.FromMilliseconds(1200)));
    }

    [Fact]
    public async Task RestoreReportsFailedStartAndKeepsTheRecord()
    {
        var fake = new FakeDocker { Answer = (args, _) => args[0] == "start" ? TimedOut : FakeDocker.Running(args) };
        var db = new DockerDbController(runner: fake.Runner);
        await db.StopAsync();

        var restore = await db.RestoreAsync(TimeSpan.FromSeconds(1));
        Assert.True(restore.Attempted);
        Assert.False(restore.Ok);
        Assert.True(db.StoppedByThisController);   // a later cleanup can still retry
    }

    [Theory]
    [InlineData("")]
    [InlineData("-v")]
    [InlineData("--rm")]
    [InlineData("a b")]
    [InlineData("db;rm")]
    public void InvalidContainerNamesAreRejected(string name)
    {
        Assert.Throws<ArgumentException>(() => new DockerDbController(name, new FakeDocker().Runner));
    }

    [Fact]
    public async Task RealRunnerReportsMissingExecutable()
    {
        var result = await DockerDbController.RunProcessAsync("projecth-no-such-exe-" + Guid.NewGuid().ToString("N"), [], TimeSpan.FromSeconds(5), default);
        Assert.True(result.NotFound);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task RealRunnerKillsItsChildOnTimeout()
    {
        // A child that would run ~30 s; the runner must give up after the timeout and kill it.
        var (file, args) = OperatingSystem.IsWindows()
            ? ("ping", new[] { "-n", "30", "127.0.0.1" })
            : ("sleep", new[] { "30" });

        var clock = Stopwatch.StartNew();
        var result = await DockerDbController.RunProcessAsync(file, args, TimeSpan.FromMilliseconds(300), default);
        Assert.True(result.TimedOut);
        Assert.False(result.Ok);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task RealRunnerCapturesOutput()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "echo hello" })
            : ("echo", new[] { "hello" });

        var result = await DockerDbController.RunProcessAsync(file, args, TimeSpan.FromSeconds(10), default);
        Assert.True(result.Ok, result.ToString());
        Assert.Equal("hello", result.StdOut.Trim());
    }

    // 기능: 기록된 docker 호출에 컨테이너·볼륨을 지우는 인자(down, rm, -v, --volumes)가 없는지 검사한다.
    // 입력: fake - 호출을 기록한 가짜 docker.
    // 출력: 반환값 없음. 파괴적 인자가 있으면 Assert 실패.
    private static void AssertNoDestructiveCommands(FakeDocker fake)
    {
        foreach (var (args, _) in fake.Calls)
        {
            Assert.DoesNotContain("down", args);
            Assert.DoesNotContain("rm", args);
            Assert.DoesNotContain("-v", args);
            Assert.DoesNotContain("--volumes", args);
        }
    }

    // Real Docker: stops and restores the configured container. Opt-in only (QA_DOCKER_TESTS=1) because it takes the
    // developer's MySQL down for a few seconds.
    [DockerFact]
    public async Task IntegrationStopAndRestore()
    {
        var db = new DockerDbController();
        Assert.True(await db.IsAvailableAsync(), "container projecth-mysql not found");
        try
        {
            var stop = await db.StopAsync();
            Assert.True(stop.Ok, stop.ToString());
        }
        finally
        {
            var restore = await db.RestoreAsync(DockerDbController.DefaultHealthTimeout);
            Assert.True(restore.Ok, restore.Detail);
        }
    }
}

public sealed class DockerFactAttribute : FactAttribute
{
    // 기능: 환경 변수 QA_DOCKER_TESTS가 1이 아니면 테스트를 Skip으로 표시한다.
    // 입력: 없음.
    // 출력: Docker 통합 테스트를 옵트인으로 만드는 Fact Attribute.
    public DockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("QA_DOCKER_TESTS") != "1")
            Skip = "set QA_DOCKER_TESTS=1 to run Docker integration tests";
    }
}
