namespace KidPlan.Web.Components.Shared;

using KidPlan.Web.Models;

/// <summary>界面配色映射，集中放一处，避免每个页面各写一套。</summary>
public static class Ui
{
    public static MudBlazor.Color MealStateColor(MealTimeState state) => state switch
    {
        MealTimeState.Eaten => MudBlazor.Color.Success,
        MealTimeState.Serving => MudBlazor.Color.Primary,
        MealTimeState.Overdue => MudBlazor.Color.Warning,
        _ => MudBlazor.Color.Default,
    };

    public static MudBlazor.Variant MealStateVariant(MealTimeState state) => state switch
    {
        MealTimeState.Eaten => MudBlazor.Variant.Filled,
        MealTimeState.Serving => MudBlazor.Variant.Filled,
        MealTimeState.Overdue => MudBlazor.Variant.Filled,
        _ => MudBlazor.Variant.Outlined,
    };

    /// <summary>
    /// 过敏等级 → 颜色。刻意让「不耐受 / 轻微」用低饱和度的灰蓝，
    /// 只有中等以上才是警告色——否则一屏全是红字，真正危险的那条反而被淹没。
    /// </summary>
    public static MudBlazor.Color AllergyColor(AllergySeverity severity) => severity switch
    {
        AllergySeverity.Anaphylaxis => MudBlazor.Color.Error,
        AllergySeverity.Severe => MudBlazor.Color.Error,
        AllergySeverity.Moderate => MudBlazor.Color.Warning,
        AllergySeverity.Mild => MudBlazor.Color.Info,
        _ => MudBlazor.Color.Default,
    };

    public static MudBlazor.Variant AllergyVariant(AllergySeverity severity) => severity switch
    {
        AllergySeverity.Anaphylaxis or AllergySeverity.Severe => MudBlazor.Variant.Filled,
        AllergySeverity.Moderate => MudBlazor.Variant.Filled,
        _ => MudBlazor.Variant.Outlined,
    };

    /// <summary>过敏等级 → 一个能一眼分出来的图标。</summary>
    public static string AllergyIcon(AllergySeverity severity) => severity switch
    {
        AllergySeverity.Anaphylaxis => "🚨",
        AllergySeverity.Severe => "⛔",
        AllergySeverity.Moderate => "⚠️",
        AllergySeverity.Mild => "🔸",
        _ => "ℹ️",
    };

    /// <summary>过敏等级 → MudBlazor 的 Severity（提示框用）。</summary>
    public static MudBlazor.Severity AllergyAlertSeverity(AllergySeverity severity) => severity switch
    {
        AllergySeverity.Anaphylaxis or AllergySeverity.Severe => MudBlazor.Severity.Error,
        AllergySeverity.Moderate => MudBlazor.Severity.Warning,
        _ => MudBlazor.Severity.Info,
    };

    public static string GenderEmoji(Gender gender) => gender switch
    {
        Gender.Boy => "👦",
        Gender.Girl => "👧",
        _ => "🧒",
    };

    public static string MealTypeEmoji(MealType type) => type switch
    {
        MealType.Breakfast => "🌅",
        MealType.Lunch => "🍚",
        MealType.Dinner => "🌙",
        MealType.Snack => "🍪",
        MealType.Treat => "🍎",
        _ => "🍽️",
    };

    /// <summary>餐次的短标签，汇总行里用。</summary>
    public static string MealTypeBadge(MealType type) => type switch
    {
        MealType.Breakfast => "早餐",
        MealType.Lunch => "午餐",
        MealType.Dinner => "晚餐",
        MealType.Snack => "点心",
        MealType.Treat => "零食",
        _ => "用餐",
    };
}
