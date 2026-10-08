using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 16 D2 regression: Loot Containers and Supply Drops roll from their own seed streams, so as long as no container is
// opened the floor loot is exactly what the floor tables roll. The hashes pin every item's id, kind, def id, rarity, amount
// and position bits.
// Phase 17 D13 (an intended change, re-taken on purpose): the floor tables now name their ammo types (Light, Medium, Heavy,
// Shells: four instead of three) and roll grenades (new weights 35/30/13/13/9, 50/17/13/13/7, 60/10/13/12/5), so every
// floor roll after the first ammo or consumable differs from Phase 16. The hashes below were taken from the Phase 17 code
// with the test loot data (TestGameData.LootJson, which mirrors the shipped loot.json without its weapon lists). The
// Phase 16 hashes (2b8eee1) were E73967B4..., 733EADC2... and B7AE93C1....
public class FloorLootRegressionTests
{
    private const string DevSandboxHash = "F1382CCA2F3F4D01298C5723A5517775962DA024C59322C5E67D6305C95FB214:50";
    private const string Round1Hash = "0B9F214613768BC408038465388A81BDF6821E81DFF1192E285B4F756E0EE0A3:50";
    private const string Round2Hash = "B3B935CA3AC7A965E9883163FA187A0FC03C49436DB6C7BE8AB5CF5799589F19:50";

    [Fact]
    public void DevSandboxLoot_IsPinned()
    {
        var h = new SandboxHarness(options: new ServerOptions { MaxPlayers = 8, DevRespawn = true, LootSeed = 7 });
        Assert.Equal(DevSandboxHash, Hash(h.Match.WorldItems));
    }

    [Fact]
    public void MatchLoot_Rounds1And2_ArePinned()
    {
        var h = new RoyaleHarness();
        h.Join(1);
        h.Join(2);
        h.RunToMatch();
        Assert.Equal(Round1Hash, Hash(h.Match.WorldItems));

        h.Match.Flow.Eliminate();
        h.Match.Flow.Finish(h.Match.ServerTick);
        h.TickUntil(() => h.Match.Flow.Round == 2, RoyaleHarness.ResultTicks + 5);
        h.RunToMatch();
        Assert.Equal(Round2Hash, Hash(h.Match.WorldItems));
    }

    // 기능: 월드 아이템 목록을 저장 순서대로 문자열로 만들고 SHA-256으로 줄인다(위치는 float 비트 그대로).
    // 입력: items - 경기의 월드 아이템.
    // 출력: 16진수 해시 문자열.
    private static string Hash(WorldItems items)
    {
        var text = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            var d = items[i].Data;
            text.Append(d.ItemId).Append(',').Append((int)d.Kind).Append(',').Append(d.DefId).Append(',').Append(d.Rarity).Append(',')
                .Append(d.Amount).Append(',').Append(BitConverter.SingleToInt32Bits(d.Position.X)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(d.Position.Y)).Append(',').Append(BitConverter.SingleToInt32Bits(d.Position.Z))
                .Append(',').Append(items[i].SpawnPoint).Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(text.ToString()))) + ":" + items.Count;
    }
}
