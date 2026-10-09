namespace KidPlan.Web.Services;

/// <summary>
/// 根据出生年月算月龄和年龄文案。纯函数，方便测试。
/// </summary>
public static class AgeCalculator
{
    /// <summary>完整月龄（不足一个月算 0）。</summary>
    public static int MonthsBetween(DateOnly birthDate, DateOnly today)
    {
        var months = ((today.Year - birthDate.Year) * 12) + today.Month - birthDate.Month;
        if (today.Day < birthDate.Day)
        {
            months--;
        }

        return Math.Max(0, months);
    }

    /// <summary>总天数（不足一天算 0）。</summary>
    public static int DaysBetween(DateOnly birthDate, DateOnly today)
        => Math.Max(0, today.DayNumber - birthDate.DayNumber);

    /// <summary>
    /// 中文年龄文案：不满一个月显示天数，不满一岁显示月龄，一岁以上显示“x岁y个月”。
    /// </summary>
    public static string Describe(DateOnly birthDate, DateOnly today)
    {
        var days = DaysBetween(birthDate, today);
        if (days < 31)
        {
            return $"{days} 天";
        }

        var months = MonthsBetween(birthDate, today);
        if (months < 12)
        {
            return $"{months} 个月";
        }

        var years = months / 12;
        var rest = months % 12;
        return rest == 0 ? $"{years} 岁" : $"{years} 岁 {rest} 个月";
    }

    /// <summary>
    /// 生日是否合法（不能是未来，也不能早得离谱）。
    /// </summary>
    public static bool IsPlausibleBirthDate(DateOnly birthDate, DateOnly today)
        => birthDate <= today && birthDate >= today.AddYears(-25);
}
