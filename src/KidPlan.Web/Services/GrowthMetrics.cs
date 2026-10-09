using KidPlan.Web.Models;

namespace KidPlan.Web.Services;

/// <summary>
/// 成长曲线里可以画的指标。
/// 顺序就是界面上勾选项的顺序。
/// </summary>
public enum GrowthMetric
{
    Height = 0,
    Weight = 1,
    Bmi = 2,
    Temperature = 3,
    SleepHours = 4,
    ExerciseMinutes = 5,
    Appetite = 6,
    Mood = 7,
}

/// <summary>指标的展示信息与取值。</summary>
public static class GrowthMetrics
{
    public static readonly GrowthMetric[] All =
    [
        GrowthMetric.Height,
        GrowthMetric.Weight,
        GrowthMetric.Bmi,
        GrowthMetric.Temperature,
        GrowthMetric.SleepHours,
        GrowthMetric.ExerciseMinutes,
        GrowthMetric.Appetite,
        GrowthMetric.Mood,
    ];

    /// <summary>打开页面时默认勾选的指标——「一眼看长势」这三项最常用。</summary>
    public static readonly GrowthMetric[] DefaultSelection =
    [
        GrowthMetric.Height,
        GrowthMetric.Weight,
        GrowthMetric.Bmi,
    ];

    /// <summary>URL / 存储里用的短名，避免把中文或枚举数字写进地址栏。</summary>
    public static string Key(GrowthMetric metric) => metric switch
    {
        GrowthMetric.Height => "height",
        GrowthMetric.Weight => "weight",
        GrowthMetric.Bmi => "bmi",
        GrowthMetric.Temperature => "temp",
        GrowthMetric.SleepHours => "sleep",
        GrowthMetric.ExerciseMinutes => "exercise",
        GrowthMetric.Appetite => "appetite",
        GrowthMetric.Mood => "mood",
        _ => "height",
    };

    public static GrowthMetric? FromKey(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "height" or "h" => GrowthMetric.Height,
        "weight" or "w" => GrowthMetric.Weight,
        "bmi" => GrowthMetric.Bmi,
        "temp" or "temperature" => GrowthMetric.Temperature,
        "sleep" => GrowthMetric.SleepHours,
        "exercise" or "sport" => GrowthMetric.ExerciseMinutes,
        "appetite" => GrowthMetric.Appetite,
        "mood" => GrowthMetric.Mood,
        _ => null,
    };

    public static string Title(GrowthMetric metric) => metric switch
    {
        GrowthMetric.Height => "身高",
        GrowthMetric.Weight => "体重",
        GrowthMetric.Bmi => "BMI",
        GrowthMetric.Temperature => "体温",
        GrowthMetric.SleepHours => "睡眠",
        GrowthMetric.ExerciseMinutes => "运动",
        GrowthMetric.Appetite => "食欲",
        GrowthMetric.Mood => "精神状态",
        _ => "指标",
    };

    public static string Unit(GrowthMetric metric) => metric switch
    {
        GrowthMetric.Height => "cm",
        GrowthMetric.Weight => "kg",
        GrowthMetric.Bmi => string.Empty,
        GrowthMetric.Temperature => "℃",
        GrowthMetric.SleepHours => "小时",
        GrowthMetric.ExerciseMinutes => "分钟",
        _ => string.Empty,
    };

    /// <summary>
    /// 指标之间量纲差得远（身高 100 上下、体温 36 上下），
    /// 叠在一张图里会把小的压成一条直线。所以叠加模式默认先各自归一化，
    /// 这张表说明每个指标「1 个单位的意义」，用于给用户解释。
    /// </summary>
    public static bool IsNumeric(GrowthMetric metric) => metric is not (GrowthMetric.Appetite or GrowthMetric.Mood);

    /// <summary>取值。枚举类指标转成 1~4 的阶梯值（0 = 未记录，视为没有值）。</summary>
    public static double? ValueOf(GrowthPoint point, GrowthMetric metric) => metric switch
    {
        GrowthMetric.Height => point.HeightCm,
        GrowthMetric.Weight => point.WeightKg,
        GrowthMetric.Bmi => point.Bmi,
        GrowthMetric.Temperature => point.TemperatureC,
        GrowthMetric.SleepHours => point.SleepHours,
        GrowthMetric.ExerciseMinutes => point.ExerciseMinutes,
        GrowthMetric.Appetite => point.Appetite == AppetiteLevel.Unknown ? null : (double)(int)point.Appetite,
        GrowthMetric.Mood => point.Mood == MoodLevel.Unknown ? null : (double)(int)point.Mood,
        _ => null,
    };

    /// <summary>枚举类指标的 Y 轴刻度文案（阶梯线要看得出「几档」）。</summary>
    public static IReadOnlyList<string>? TickLabels(GrowthMetric metric) => metric switch
    {
        GrowthMetric.Appetite => ["", "很差", "一般", "不错", "很好"],
        GrowthMetric.Mood => ["", "低落", "平稳", "开心", "兴奋"],
        _ => null,
    };

    /// <summary>把某个值渲染成人看的文本（图例、明细都用它）。</summary>
    public static string Format(GrowthMetric metric, double value) => metric switch
    {
        GrowthMetric.Appetite => ((AppetiteLevel)(int)Math.Round(value)).ToChinese(),
        GrowthMetric.Mood => ((MoodLevel)(int)Math.Round(value)).ToChinese(),
        _ => value.ToString("0.##"),
    };
}
