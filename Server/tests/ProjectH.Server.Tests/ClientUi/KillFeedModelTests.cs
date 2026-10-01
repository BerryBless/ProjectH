using ProjectH.Client.UI;
using Xunit;

namespace ProjectH.Server.Tests.ClientUi;

// Phase 11 D10 (spec §2): the kill feed keeps the newest 5 lines, each for 6 seconds.
public class KillFeedModelTests
{
    [Fact]
    public void TheSixthLine_PushesTheOldestOut()
    {
        var feed = new KillFeedModel();
        for (int i = 1; i <= 5; i++) feed.Add("line " + i, i);
        Assert.Equal(5, feed.Count);
        Assert.Equal("line 5", feed.Line(0));   // newest first
        Assert.Equal("line 1", feed.Line(4));

        feed.Add("line 6", 6f);
        Assert.Equal(KillFeedModel.Capacity, feed.Count);
        Assert.Equal("line 6", feed.Line(0));
        Assert.Equal("line 2", feed.Line(4));
    }

    [Fact]
    public void ALine_GoesAfterSixSeconds()
    {
        var feed = new KillFeedModel();
        feed.Add("a", 10f);
        feed.Add("b", 12f);
        int version = feed.Version;

        Assert.False(feed.Expire(15.9f));
        Assert.Equal(version, feed.Version);
        Assert.True(feed.Expire(16f));
        Assert.Equal(1, feed.Count);
        Assert.Equal("b", feed.Line(0));
        Assert.NotEqual(version, feed.Version);

        Assert.True(feed.Expire(18f));
        Assert.Equal(0, feed.Count);
        Assert.False(feed.Expire(100f));
    }

    [Fact]
    public void TheRing_WrapsAroundForLong()
    {
        var feed = new KillFeedModel();
        for (int i = 0; i < 23; i++)
        {
            feed.Add("k" + i, i);
            feed.Expire(i);
        }
        // At t = 22 the lines from t = 17..22 are younger than 6 s, but only 5 fit.
        Assert.Equal(5, feed.Count);
        Assert.Equal("k22", feed.Line(0));
        Assert.Equal("k18", feed.Line(4));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => feed.Line(5));
    }

    [Fact]
    public void Clear_EmptiesIt()
    {
        var feed = new KillFeedModel();
        feed.Clear();
        Assert.Equal(0, feed.Version);
        feed.Add("a", 0f);
        feed.Clear();
        Assert.Equal(0, feed.Count);
        Assert.Equal(2, feed.Version);
    }
}
