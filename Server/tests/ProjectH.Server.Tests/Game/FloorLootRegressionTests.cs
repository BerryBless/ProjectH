using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ProjectH.Server.Game;
using ProjectH.Server.Game.Items;
using Xunit;

namespace ProjectH.Server.Tests.Game;

// Phase 16 D2 regression: Loot Containers and Supply Drops roll from their own seed streams, so as long as no container is
// opened the floor loot is exactly what it was before Phase 16. The hashes below were taken from the Phase 15 code
// (2b8eee1) before any Phase 16 change; they pin every item's id, kind, def id, rarity, amount and position bits.
public class FloorLootRegressionTests
{
    private const string DevSandboxHash = "E73967B46FCC971E1212F5334B17F091ADFED1CE0DD44D596D06581F3D7CF21B:50";
    private const string Round1Hash = "733EADC2F8D4A99E7B2DDA0C47C24EDA9CB6CEB94D02EB7EEBF3BA3841EC63F8:50";
    private const string Round2Hash = "B7AE93C157AECED31106BF9AA20542CC0105CF4CF6E1E26195925961AD54DA4D:50";

    [Fact]
    public void DevSandboxLoot_IsUnchangedByPhase16()
    {
        var h = new SandboxHarness(options: new ServerOptions { MaxPlayers = 8, DevRespawn = true, LootSeed = 7 });
        Assert.Equal(DevSandboxHash, Hash(h.Match.WorldItems));
    }

    [Fact]
    public void MatchLoot_Rounds1And2_AreUnchangedByPhase16()
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
