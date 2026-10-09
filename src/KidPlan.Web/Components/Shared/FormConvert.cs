namespace KidPlan.Web.Components.Shared;

/// <summary>
/// MudBlazor 的日期/时间选择器用的是 <see cref="DateTime"/> / <see cref="TimeSpan"/>，
/// 而领域模型里用的是更精确的 <see cref="DateOnly"/> / <see cref="TimeOnly"/>。
/// 这一层做双向转换，并处理越界值，避免把不合法的时间写进数据库。
/// </summary>
public static class FormConvert
{
    /// <summary>DateTime? → DateOnly?（只取日期部分）。</summary>
    public static DateOnly? ToDateOnly(this DateTime? value)
        => value is { } d ? DateOnly.FromDateTime(d) : null;

    /// <summary>DateTime → DateOnly。</summary>
    public static DateOnly ToDateOnly(this DateTime value) => DateOnly.FromDateTime(value);

    /// <summary>DateOnly? → DateTime?（时间部分为 00:00）。</summary>
    public static DateTime? ToDateTimeValue(this DateOnly? value)
        => value is { } d ? d.ToDateTime(TimeOnly.MinValue) : null;

    /// <summary>DateOnly → DateTime（时间部分为 00:00）。</summary>
    public static DateTime ToDateTimeValue(this DateOnly value) => value.ToDateTime(TimeOnly.MinValue);

    /// <summary>TimeSpan? → TimeOnly?，越界时返回 null 而不是抛异常。</summary>
    public static TimeOnly? ToTimeOnly(this TimeSpan? value)
    {
        if (value is not { } span)
        {
            return null;
        }

        if (span < TimeSpan.Zero || span >= TimeSpan.FromDays(1))
        {
            return null;
        }

        return TimeOnly.FromTimeSpan(span);
    }

    /// <summary>TimeOnly? → TimeSpan?。</summary>
    public static TimeSpan? ToTimeSpan(this TimeOnly? value)
        => value is { } t ? t.ToTimeSpan() : null;

    /// <summary>TimeOnly → TimeSpan。</summary>
    public static TimeSpan ToTimeSpan(this TimeOnly value) => value.ToTimeSpan();
}
