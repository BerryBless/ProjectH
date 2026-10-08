using System.Linq;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Review fix C1 (SEC-6, SEC-11): every random roll of a match (floor loot, containers, supply drops, zone circles, drop order,
// transport route) is seeded from a secret made at each match start, so a client that knows the code and the seed options
// cannot predict them. Server:DeterministicSeeds keeps the old seed + round for tests and QA reproduction.
public class MatchSeedTests
{
    // 기능: 월드 아이템을 위치 순으로 정렬해 id를 뺀 목록으로 만든다(두 서버 비교용).
    // 입력: h - 경기.
    // 출력: 비교할 아이템 배열.
    private static WorldItemData[] LootOf(RoyaleHarness h) =>
        Enumerable.Range(0, h.Match.WorldItems.Count).Select(i => h.Match.WorldItems[i].Data)
            .OrderBy(d => d.Position.X).ThenBy(d => d.Position.Z).ThenBy(d => d.Position.Y)
            .Select(d => d with { ItemId = 0 }).ToArray();

    // 기능: 두 사람을 넣고 경기를 시작한다.
    // 입력: h - 경기.
    // 출력: 반환값 없음.
    private static void Start(RoyaleHarness h)
    {
        h.Join(1);
        h.Join(2);
        h.RunToMatch();
    }

    // 기능: 지금 라운드를 끝내고 다음 라운드의 경기를 시작한다.
    // 입력: h - 경기.
    // 출력: 반환값 없음.
    private static void NextRound(RoyaleHarness h)
    {
        ushort round = h.Match.Flow.Round;
        h.Match.Flow.Eliminate();
        h.Match.Flow.Finish(h.Match.ServerTick);
        h.TickUntil(() => h.Match.Flow.Round == round + 1, RoyaleHarness.ResultTicks + 5);
        h.RunToMatch();
    }

    [Fact]
    public void MatchSecretSeeds_DifferPerRoundAndPerPurpose()
    {
        var first = new RoyaleHarness(deterministicSeeds: false);
        var second = new RoyaleHarness(deterministicSeeds: false);
        Start(first);
        Start(second);
        // Same options, same round: two servers roll differently (each has its own secret).
        Assert.NotEqual(LootOf(first), LootOf(second));
        Assert.NotEqual(first.Match.Zone.CenterX(1), second.Match.Zone.CenterX(1));
        // Within one round the purposes use different seeds.
        Assert.NotEqual(first.Match.RoundSeed(1, Match.LootSalt), first.Match.RoundSeed(1, Match.ZoneSalt));
        Assert.NotEqual(first.Match.RoundSeed(1, Match.SpawnSalt), first.Match.RoundSeed(1, Match.RouteSalt));

        int round1Loot = first.Match.RoundSeed(1, Match.LootSalt);
        NextRound(first);
        Assert.NotEqual(round1Loot, first.Match.RoundSeed(1, Match.LootSalt));   // a new secret every round
    }

    [Fact]
    public void DeterministicSeeds_ReproduceTheOldBehaviour()
    {
        var first = new RoyaleHarness(deterministicSeeds: true);
        var second = new RoyaleHarness(deterministicSeeds: true);
        Start(first);
        Start(second);
        Assert.Equal(LootOf(first), LootOf(second));
        Assert.Equal(first.Match.Zone.CenterX(1), second.Match.Zone.CenterX(1));
        foreach (int salt in new[] { Match.LootSalt, Match.ZoneSalt, Match.SpawnSalt, Match.RouteSalt })
            Assert.Equal(7 + first.Match.Flow.Round, first.Match.RoundSeed(7, salt));   // seed + round, whatever the purpose
        NextRound(first);
        Assert.Equal(7 + 2, first.Match.RoundSeed(7, Match.LootSalt));
    }

    [Fact]
    public void DeterministicSeeds_IsOffByDefault()
    {
        Assert.False(new ServerOptions().DeterministicSeeds);
    }
}
