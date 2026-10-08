using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectH.Monitoring.Contracts;
using ProjectH.Monitoring.Ingest;
using ProjectH.Monitoring.Storage;

namespace ProjectH.Monitoring.Tests.Ingest;

// The 413 decision without a socket: over loopback Kestrel may reset the connection before the client reads the 413
// (MonitoringAppTests.OversizedBody_IsRefused), so the status code itself is pinned here.
public class IngestEndpointTests
{
    [Fact]
    public async Task DeclaredLengthOverTheLimit_Is413_BeforeTheTokenCheck_AndStoresNothing()
    {
        var time = new ManualTime();
        var store = new MetricStore(new MonitoringServerOptions());
        var context = new DefaultHttpContext();
        context.Request.ContentLength = MonitoringContract.MaxBodyBytes + 1;   // no token header either: size comes first (D8)

        IResult result = await IngestEndpoint.HandleAsync(context, new MonitoringServerOptions { IngestToken = "secret" }, store, time,
            new IngestLog(NullLogger<IngestLog>.Instance, time), NullLogger<MetricStore>.Instance, CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, store.Count);
    }
}
