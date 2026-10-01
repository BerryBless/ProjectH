using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using Xunit;

namespace ProjectH.Server.Tests.ClientUi;

// Phase 12 D10, D14: the client's new UI strings (fall as a cause, the hints, the F1 movement and route lines), compiled
// here through the UiText source link.
public class UiTextMovementTests
{
    [Fact]
    public void AFall_IsNamedInTheKillFeed_AndTheResult()
    {
        Assert.Equal("낙하 ▸ bob", UiText.KillLine(null!, "bob", DeathCause.Fall));
        Assert.Equal("자기장 ▸ bob", UiText.KillLine(null!, "bob", DeathCause.Zone));
        Assert.Equal("alice ▸ bob", UiText.KillLine("alice", "bob", DeathCause.Zone));
        Assert.Equal("탈락 원인: 낙하", UiText.KilledBy(true, true, DeathCause.Fall, null!));
        Assert.Equal("탈락 원인: 자기장", UiText.KilledBy(true, true, DeathCause.Zone, null!));
        Assert.Equal("나를 처치한 플레이어: bob", UiText.KilledBy(true, false, DeathCause.Zone, "bob"));
        Assert.Equal(string.Empty, UiText.KilledBy(false, true, DeathCause.Fall, null!));
    }

    [Fact]
    public void TheHints_AreKorean()
    {
        Assert.Equal("[Space] 뛰어내리기", UiText.HintJump);
        Assert.Equal("[Space] 글라이더 펼치기", UiText.HintGlide);
        Assert.Equal("[E] 문 열기", UiText.HintDoorOpen);
        Assert.Equal("[E] 문 닫기", UiText.HintDoorClose);
    }

    [Fact]
    public void TheMovementLine_ShowsTenthsAndHundredths()
    {
        Assert.Equal("이동 Glide   수평 12.3 m/s   수직 -5.0 m/s   기력 87   보정 0.04 m", UiText.MovementLine("Glide", 123, -50, 87, 4));
        Assert.Equal("이동 Ground   수평 0.0 m/s   수직 0.0 m/s   기력 100   보정 1.25 m", UiText.MovementLine("Ground", 0, 0, 100, 125));
        // Final review C15: a negative value keeps its sign once, in front.
        Assert.Equal("이동 Ground   수평 0.0 m/s   수직 0.0 m/s   기력 100   보정 -1.05 m", UiText.MovementLine("Ground", 0, 0, 100, -105));
    }

    [Fact]
    public void TheRouteLine_ShowsWholeMetres()
    {
        Assert.Equal("수송기 (-100, 4) → (100, -4)", UiText.RouteLine(-100f, 3.6f, 99.8f, -4.4f));
    }
}
