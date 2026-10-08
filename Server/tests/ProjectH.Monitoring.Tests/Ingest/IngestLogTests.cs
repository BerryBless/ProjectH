using Microsoft.Extensions.Logging;
using ProjectH.Monitoring.Ingest;

namespace ProjectH.Monitoring.Tests.Ingest;

public class IngestLogTests
{
    // Keeps every formatted line; IngestLog writes from request threads, so the list is locked.
    private sealed class ListLogger : ILogger<IngestLog>
    {
        public readonly List<string> Lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        // 기능: 로그 한 줄을 문자열로 만들어 보관한다.
        // 입력: logLevel·eventId·state·exception - 로그 항목, formatter - 문자열 변환.
        // 출력: 반환값 없음. Lines에 한 줄이 더해진다.
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void Invalid_LogsOncePerMinute_AndCountsTheSuppressedOnes()
    {
        var time = new ManualTime();   // timestamps start at 0: the first line must still be logged and start a window
        var logger = new ListLogger();
        var log = new IngestLog(logger, time);

        log.Invalid("a", "s1");
        log.Invalid("b", "s1");
        log.Invalid("c", null);
        Assert.Single(logger.Lines);

        time.Advance(TimeSpan.FromSeconds(59));
        log.Invalid("d", "s1");
        Assert.Single(logger.Lines);

        time.Advance(TimeSpan.FromSeconds(1));
        log.Invalid("e", "s2");
        Assert.Equal(2, logger.Lines.Count);
        Assert.Contains("(e)", logger.Lines[1]);
        Assert.Contains("3 more", logger.Lines[1]);
    }
}
