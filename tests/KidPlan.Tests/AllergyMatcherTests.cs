using KidPlan.Web.Models;
using KidPlan.Web.Services;

namespace KidPlan.Tests;

public class AllergyMatcherTests
{
    /// <summary>造一条食物过敏原。等级默认「中等」，需要验证分级的地方再单独指定。</summary>
    private static ChildAllergen Food(string name, AllergySeverity severity = AllergySeverity.Moderate)
        => new() { Name = name, Kind = AllergenKind.Food, Severity = severity };

    private static ChildAllergen Other(string name, AllergySeverity severity = AllergySeverity.Moderate)
        => new() { Name = name, Kind = AllergenKind.Other, Severity = severity };

    [Fact]
    public void SplitTokens_SplitsOnCommonSeparators()
    {
        var tokens = AllergyMatcher.SplitTokens("牛奶、鸡蛋, 花生;芒果+菠萝/大豆");

        Assert.Equal(new[] { "牛奶", "鸡蛋", "花生", "芒果", "菠萝", "大豆" }, tokens);
    }

    [Fact]
    public void SplitTokens_DeduplicatesAndTrims()
    {
        var tokens = AllergyMatcher.SplitTokens("  牛奶 ，牛奶, 牛奶  ");

        Assert.Equal(new[] { "牛奶" }, tokens);
    }

    [Fact]
    public void SplitTokens_KeepsWordsThatContainAndJiYu()
    {
        // 和 / 及 / 与 刻意不作为分隔符，否则「和牛」「及第粥」会被切坏。
        Assert.Equal(new[] { "和牛" }, AllergyMatcher.SplitTokens("和牛"));
        Assert.Equal(new[] { "及第粥" }, AllergyMatcher.SplitTokens("及第粥"));
        Assert.Equal(new[] { "牛奶和鸡蛋" }, AllergyMatcher.SplitTokens("牛奶和鸡蛋"));
    }

    [Fact]
    public void SplitTokens_HandlesEmptyInput()
    {
        Assert.Empty(AllergyMatcher.SplitTokens(null));
        Assert.Empty(AllergyMatcher.SplitTokens("   "));
    }

    // ------------------------------------------------------------------
    // 关键词匹配（沿用旧行为，只是入参换成了带等级的记录）
    // ------------------------------------------------------------------

    [Fact]
    public void FindMatches_ExpandsMilkSynonyms()
    {
        var matches = AllergyMatcher.FindMatches([Food("牛奶")], "酸奶拌燕麦");

        Assert.Single(matches);
        Assert.Equal("牛奶", matches[0].Allergen);
        // 关键词按同义词表顺序匹配，「奶」排在「酸奶」之前，所以先命中「奶」。
        Assert.Equal("奶", matches[0].MatchedKeyword);
    }

    [Fact]
    public void FindMatches_MatchesDairyProductKeyword()
        => Assert.Equal("乳制品", AllergyMatcher.FindMatches([Food("牛奶")], "含乳制品的蛋糕")[0].MatchedKeyword);

    [Fact]
    public void FindMatches_MatchesSteamedEggForEggAllergy()
    {
        var matches = AllergyMatcher.FindMatches([Food("鸡蛋")], "小米粥 + 蒸蛋 + 西兰花");

        Assert.Single(matches);
        Assert.Equal("鸡蛋", matches[0].Allergen);
        Assert.Equal("蛋", matches[0].MatchedKeyword);
    }

    [Fact]
    public void FindMatches_ReturnsEmptyForSafeMenu()
        => Assert.Empty(AllergyMatcher.FindMatches(
            [Food("牛奶"), Food("鸡蛋"), Food("花生")], "小米粥", "西兰花"));

    [Fact]
    public void FindMatches_MatchesAllergenWithNoSynonymGroupByName()
    {
        var match = AllergyMatcher.FindMatches([Food("南瓜")], "南瓜小米粥")[0];

        Assert.Equal("南瓜", match.Allergen);
        Assert.Equal("南瓜", match.MatchedKeyword);
    }

    [Fact]
    public void FindMatches_DeduplicatesWhenOneAllergenHitsManyKeywords()
        => Assert.Single(AllergyMatcher.FindMatches([Food("牛奶")], "牛奶加奶酪再加黄油"));

    [Fact]
    public void FindMatches_ReturnsEveryDistinctAllergen()
    {
        var matches = AllergyMatcher.FindMatches([Food("牛奶"), Food("鸡蛋")], "鸡蛋牛奶布丁");

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, m => m.Allergen == "牛奶");
        Assert.Contains(matches, m => m.Allergen == "鸡蛋");
    }

    [Fact]
    public void FindMatches_ReturnsEmptyWhenNoAllergyRecorded()
    {
        Assert.Empty(AllergyMatcher.FindMatches(null, "什么都有"));
        Assert.Empty(AllergyMatcher.FindMatches([], "什么都有"));
    }

    [Fact]
    public void FindMatches_ReturnsEmptyWhenNothingToCheckAgainst()
        => Assert.Empty(AllergyMatcher.FindMatches([Food("牛奶")], null, ""));

    [Fact]
    public void FindMatches_ChecksEveryProvidedText()
    {
        Assert.Empty(AllergyMatcher.FindMatches([Food("花生")], "小米粥", "西兰花"));
        Assert.Single(AllergyMatcher.FindMatches([Food("花生")], "小米粥", "花生米"));
    }

    [Theory]
    [InlineData("猕猴桃", "奇异果沙拉")]
    [InlineData("菠萝", "凤梨酥")]
    [InlineData("柑橘", "鲜榨橙子汁")]
    [InlineData("添加剂", "含防腐剂的零食")]
    [InlineData("小麦", "全麦面筋")]
    [InlineData("海鲜", "白灼基围虾")]
    [InlineData("坚果", "腰果炒芹菜")]
    public void FindMatches_MatchesSynonyms(string allergen, string menu)
        => Assert.Single(AllergyMatcher.FindMatches([Food(allergen)], menu));

    [Fact]
    public void FindMatches_PrefersOverReporting()
    {
        // 已知取舍：宁可多报让家长确认，也不要漏报。「豆奶」会被当作奶制品提示。
        Assert.Single(AllergyMatcher.FindMatches([Food("牛奶")], "豆奶"));
    }

    // ------------------------------------------------------------------
    // 分级相关
    // ------------------------------------------------------------------

    [Fact]
    public void FindMatches_IgnoresNonFoodAllergens()
    {
        // 花粉、青霉素这类不该拿菜单去比对——「花」字出现在「花卷」里就会变成天天误报。
        var matches = AllergyMatcher.FindMatches(
            [Other("花粉", AllergySeverity.Severe), Other("青霉素", AllergySeverity.Anaphylaxis)],
            "花卷 + 小米粥");

        Assert.Empty(matches);
    }

    [Fact]
    public void FindMatches_CarriesTheRecordedSeverity()
    {
        var matches = AllergyMatcher.FindMatches(
            [Food("牛奶", AllergySeverity.Anaphylaxis)],
            "牛奶布丁");

        Assert.Equal(AllergySeverity.Anaphylaxis, matches[0].Severity);
        Assert.Equal(AllergyRiskAction.DoubleConfirm, matches[0].RiskAction);
    }

    [Fact]
    public void FindMatches_SortsBySeverityDescending()
    {
        // 一带而过的那条先被写进记录，但重的那条必须排在最前面，
        // 否则界面上最先看到的反而是「不耐受」。
        var matches = AllergyMatcher.FindMatches(
            [Food("牛奶", AllergySeverity.Intolerance), Food("花生", AllergySeverity.Severe)],
            "花生牛奶");

        Assert.Equal("花生", matches[0].Allergen);
        Assert.Equal("牛奶", matches[1].Allergen);
    }

    [Fact]
    public void MaxSeverity_ReturnsNullWhenNoMatch()
        => Assert.Null(AllergyMatcher.MaxSeverity([]));

    [Fact]
    public void MaxSeverity_ReturnsTheWorstOne()
        => Assert.Equal(
            AllergySeverity.Anaphylaxis,
            AllergyMatcher.MaxSeverity(AllergyMatcher.FindMatches(
                [Food("牛奶", AllergySeverity.Mild), Food("花生", AllergySeverity.Anaphylaxis)],
                "花生牛奶")));

    [Theory]
    [InlineData(AllergySeverity.Intolerance, AllergyRiskAction.Notice)]
    [InlineData(AllergySeverity.Mild, AllergyRiskAction.Notice)]
    [InlineData(AllergySeverity.Moderate, AllergyRiskAction.Confirm)]
    [InlineData(AllergySeverity.Severe, AllergyRiskAction.DoubleConfirm)]
    [InlineData(AllergySeverity.Anaphylaxis, AllergyRiskAction.DoubleConfirm)]
    public void ToRiskAction_MapsSeverityToGuardStrength(AllergySeverity severity, AllergyRiskAction expected)
        => Assert.Equal(expected, severity.ToRiskAction());

    [Fact]
    public void SeverityOrder_IsClinicLikeNotAlphabetical()
    {
        // 顺序是刻意定死的：休克 > 强过敏 > 中等 > 轻微 > 不耐受。
        // 枚举值本身按这个顺序递增，比较大小才有意义。
        Assert.Equal(
            new[]
            {
                AllergySeverity.Intolerance,
                AllergySeverity.Mild,
                AllergySeverity.Moderate,
                AllergySeverity.Severe,
                AllergySeverity.Anaphylaxis,
            },
            EnumDisplay.AllergySeverityOrder);

        Assert.True(AllergySeverity.Anaphylaxis > AllergySeverity.Severe);
        Assert.True(AllergySeverity.Severe > AllergySeverity.Moderate);
        Assert.True(AllergySeverity.Moderate > AllergySeverity.Intolerance);
    }
}
