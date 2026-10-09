using System.ComponentModel.DataAnnotations;

namespace KidPlan.Web.Models;

/// <summary>
/// 一次身体情况记录。可以记录“当前”（单次，<see cref="RecordKind.Point"/>），
/// 也可以记录“指定时间段”（<see cref="RecordKind.Period"/>，用 PeriodStart/PeriodEnd 表示）。
/// 身高 / 体重这些数值会被成长曲线取用。
/// </summary>
public class GrowthRecord
{
    public int Id { get; set; }

    public int ChildId { get; set; }

    public Child? Child { get; set; }

    public RecordKind Kind { get; set; } = RecordKind.Point;

    /// <summary>记录时间。曲线的 X 轴按它排序。</summary>
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>时段记录的起始日期（Kind = Period 时有意义）。</summary>
    public DateOnly? PeriodStart { get; set; }

    /// <summary>时段记录的结束日期（Kind = Period 时有意义）。</summary>
    public DateOnly? PeriodEnd { get; set; }

    /// <summary>身高（厘米）。</summary>
    [Range(0, 300, ErrorMessage = "身高看起来不太对，请填 0~300 厘米")]
    public decimal? HeightCm { get; set; }

    /// <summary>体重（公斤）。</summary>
    [Range(0, 300, ErrorMessage = "体重看起来不太对，请填 0~300 公斤")]
    public decimal? WeightKg { get; set; }

    /// <summary>体温（摄氏度）。</summary>
    [Range(30, 45, ErrorMessage = "体温看起来不太对，请填 30~45 摄氏度")]
    public decimal? TemperatureC { get; set; }

    /// <summary>睡眠时长（小时）。时段记录时表示平均每天睡眠。</summary>
    [Range(0, 24, ErrorMessage = "睡眠时长请填 0~24 小时")]
    public decimal? SleepHours { get; set; }

    /// <summary>运动时长（分钟）。</summary>
    [Range(0, 1440, ErrorMessage = "运动时长请填 0~1440 分钟")]
    public int? ExerciseMinutes { get; set; }

    public AppetiteLevel Appetite { get; set; } = AppetiteLevel.Unknown;

    public MoodLevel Mood { get; set; } = MoodLevel.Unknown;

    /// <summary>症状 / 不适：咳嗽、流鼻涕、腹泻等。</summary>
    [MaxLength(500)]
    public string? Symptoms { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    /// <summary>记录人（AspNetUsers.Id）。家庭里可能有多个账号分别记录。</summary>
    [MaxLength(450)]
    public string? RecordedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// BMI 由身高体重实时算出，不落库，避免身高体重改了小数值不同步。
    /// </summary>
    public decimal? Bmi => HeightCm is > 0 && WeightKg is > 0
        ? Math.Round(WeightKg.Value / (HeightCm.Value / 100m) / (HeightCm.Value / 100m), 1)
        : null;
}
