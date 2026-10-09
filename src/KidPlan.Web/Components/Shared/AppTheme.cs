using MudBlazor;

namespace KidPlan.Web.Components.Shared;

/// <summary>
/// 应用主题。配色偏柔和，适合记录小孩成长的家庭应用。
/// </summary>
public static class AppTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#4C7DF0",
            Secondary = "#FF8A65",
            Info = "#42A5F5",
            Success = "#3FB984",
            Warning = "#F5A623",
            Error = "#E5484D",
            AppbarBackground = "#4C7DF0",
            AppbarText = "#FFFFFF",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#3A4256",
            Background = "#F5F7FB",
            Surface = "#FFFFFF",
            TextPrimary = "#2B3245",
            TextSecondary = "#6B7385",
            ActionDefault = "#6B7385",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#7FA1FF",
            Secondary = "#FFA98C",
            Success = "#4CCB96",
            Warning = "#FFB84D",
            Error = "#FF6B6F",
            Background = "#161A23",
            Surface = "#1E232E",
            AppbarBackground = "#1E232E",
            DrawerBackground = "#1E232E",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "12px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily =
                [
                    "-apple-system", "BlinkMacSystemFont", "Segoe UI", "PingFang SC",
                    "Hiragino Sans GB", "Microsoft YaHei", "Helvetica Neue", "Arial", "sans-serif",
                ],
            },
        },
    };
}
