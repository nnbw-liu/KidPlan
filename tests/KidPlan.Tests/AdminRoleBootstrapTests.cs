using KidPlan.Web.Data;

namespace KidPlan.Tests;

/// <summary>
/// 管理员的指派方式（Admin:Emails → admin 角色）。
///
/// 为什么值得有测试：这段解析出错的表现特别隐蔽——应用照常启动、日志里只有一行警告，
/// 结果就是「模型设置」里的全局配置页签谁都看不到，人只会以为是页面坏了。
/// 解析本身是纯函数，跟数据库无关，所以单独盯住它。
/// </summary>
public class AdminRoleBootstrapTests
{
    [Fact]
    public void ParseEmails_AcceptsCommaSemicolonAndWhitespace()
    {
        var emails = AdminRoleBootstrap.ParseEmails("a@example.com,b@example.com; c@example.com\td@example.com");

        Assert.Equal(
            ["a@example.com", "b@example.com", "c@example.com", "d@example.com"],
            emails);
    }

    [Fact]
    public void ParseEmails_DeduplicatesIgnoringCaseAndTrims()
    {
        // 环境变量里很容易写成 `a@x.com, A@X.com ,a@x.com`。
        var emails = AdminRoleBootstrap.ParseEmails("  A@X.com , a@x.com,,; b@x.com  ");

        Assert.Equal(["A@X.com", "b@x.com"], emails);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",; ,")]
    public void ParseEmails_ReturnsEmptyForNothingConfigured(string? raw)
    {
        Assert.Empty(AdminRoleBootstrap.ParseEmails(raw));
    }

    [Fact]
    public void AdminRoleName_IsLowerCaseAdmin()
    {
        // 角色名会被规范化成大写再比较，但配置文件里写的是小写，
        // 这里钉住常量，避免哪天改成 "Administrator" 而迁移脚本还在找 "admin"。
        Assert.Equal("admin", KidPlanRoles.Admin);
    }
}
