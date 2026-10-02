using ProjectH.Client.UI;
using ProjectH.Shared.Protocol;
using ProjectH.Shared.Simulation;
using Xunit;

namespace ProjectH.Server.Tests.ClientUi;

// Phase 13 D16: the client's building strings (HUD lines, refusals, the F1 line), compiled here through the UiText
// source link.
public class UiTextBuildTests
{
    [Fact]
    public void TheHudLines_NameThePieceTheMaterialAndTheResources()
    {
        Assert.Equal("나무 120   돌 0   금속 35", UiText.ResourcesLine(120, 0, 35));
        Assert.Equal("건축: 경사로 · 돌", UiText.BuildModeLine(BuildPieceType.Ramp, BuildMaterialType.Stone));
        Assert.Equal("건축: 지붕 · 금속", UiText.BuildModeLine(BuildPieceType.Roof, BuildMaterialType.Metal));
    }

    [Fact]
    public void EveryRefusal_HasAText_AndOkHasNone()
    {
        Assert.Null(UiText.BuildRefusal(BuildResultCode.Ok));
        Assert.Equal("자원이 부족합니다", UiText.BuildRefusal(BuildResultCode.NoResource));
        for (int code = 1; code <= (int)BuildResultCode.BudgetFull + 1; code++)
            Assert.False(string.IsNullOrEmpty(UiText.BuildRefusal((BuildResultCode)code)));
    }

    [Fact]
    public void TheDebugLine_ShowsTheCounts()
    {
        Assert.Equal("도구 건축   벽/나무   구조물 12 (표시 10, 무시 3)   요청 40 (6/s)   거절 2 Blocked",
            UiText.BuildDebugLine(ToolKind.Build, BuildPieceType.Wall, BuildMaterialType.Wood, 12, 10, 3, 40, 2, BuildResultCode.Blocked, 6));
    }
}
