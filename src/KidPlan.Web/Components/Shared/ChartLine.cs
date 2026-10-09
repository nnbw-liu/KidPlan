namespace KidPlan.Web.Components.Shared;

/// <summary>
/// 图表上的一条线。
///
/// 刻意不用 MudBlazor 的 <c>ChartSeries&lt;double&gt;</c> 当组件参数：
/// 那样页面代码就和具体图表库绑死了。README 里承诺过「换图表库只改 MetricChart 一个文件」，
/// 这个类型就是兑现那句话的一部分。
/// </summary>
/// <param name="Name">图例上的名字。</param>
/// <param name="Values">数值序列，长度必须和 X 轴标签一致。</param>
public sealed record ChartLine(string Name, IReadOnlyList<double> Values);
