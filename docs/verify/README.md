# docs/verify —— 本机端到端验证的截图

这些图片由 [`scripts/cdp.ps1`](../../scripts/cdp.ps1) 用无头 Edge 自动生成，
不是手画的，也不是设计稿。留着它们的原因很实际：**「点了之后没卡死」这件事，
自动化断言只能证明一半，另一半得看一眼**。

| 文件 | 说明 |
| --- | --- |
| [`body-record-form.png`](body-record-form.png) | 身体管理（独立一页）：点开「记录身体情况」后的表单，10 个字段 + 保存按钮都在；上面是过敏原一览和最新数值卡片，下面是记录列表。 |
| [`growth-metrics-overlay.png`](growth-metrics-overlay.png) | 成长曲线：勾上「体温」后切到「叠加」，4 个指标合成 1 张图，并打开归一化。 |
| [`growth-period-hint.png`](growth-period-hint.png) | 成长曲线切到「只看时段记录」、而勾的身高 / 体重 / BMI 在这一档没有数据时：说明白是这一档没数据、列出有数据的指标（体温 / 睡眠 / 运动 / 食欲 / 精神），并给一个「改勾这几个指标」。 |
| [`growth-period-charts.png`](growth-period-charts.png) | 点了「改勾这几个指标」之后：身高 / 体重 / BMI 变成「（无数据）」，睡眠、精神等图出来了（只有 1 个点时显示「再记录一次就能画出趋势」）。 |
| [`meal-history-all.png`](meal-history-all.png) | 餐饮记录：点了「全部」。这一条是那次「进餐饮记录就卡死闪退」的回归——以前这里是四万多天的对象 + 一天一个 chip。 |
| [`meal-history-month-missing-days.png`](meal-history-month-missing-days.png) | 餐饮记录：按月，底部是「漏记」提示（连续的日子已经合并成段）。 |
| [`meals-yesterday.png`](meals-yesterday.png) | 饮食安排：昨天那一屏，固定三餐 + 加餐分段显示，含已吃完 / 已跳过 / AI 推荐标记。 |
| [`meals-adhoc-form.png`](meals-adhoc-form.png) | 点「＋ 记一顿点心」展开的内联表单：餐次预置为点心，饭点可以留空（不定时）。 |
| [`meals-mealtype-times.png`](meals-mealtype-times.png) | 在同一个表单里把餐次切到「午餐」：计划饭点跟着变成 12:00。改动前这里显示的是上一个餐次的 07:30（整体慢一拍）。 |
| [`ai-add-provider.png`](ai-add-provider.png) | 模型设置：添加端点的表单，里面就有「保存这个端点」——点完立刻写进 `config/ai.json`。 |
| [`allergens-compact.png`](allergens-compact.png) | 过敏原管理：按等级分组，一组一张卡片、组内细线分隔，一行里放得下名字 / 等级 / 检测结果 / 备注 / 操作（一行 45px，改动前 95px 上下）。 |
| [`relay-test-connection.png`](relay-test-connection.png) | 本机中继跑通之后，「测试连接」成功：拉到了「家里」那台机器上的模型列表，并试调用拿到了回复。 |
| [`ai-recommendation.png`](ai-recommendation.png) | AI 饮食推荐：假模型返回了 4 条菜，含腰果（过敏性休克级）的那条被本地复核标成「不能写入」。 |
| [`ai-recommendation-imported.png`](ai-recommendation-imported.png) | 点了「全部写入饮食安排」之后：4 条里写进去 3 条，危险的那条没有落库。 |
| [`ai-reasoning-budget.png`](ai-reasoning-budget.png) | 推理模型把 `max_tokens` 全花在思考过程上时（用户报的「推理输出不太对」）：页面上给出「额度全花在推理上了」+ `finish_reason=length` + token 用量，并给「去模型设置调大最大输出 token」的按钮；整包 HTTP 响应不再铺出来。 |

前十一张由 [`scripts/cdp.ps1`](../../scripts/cdp.ps1) 生成，中继那张由
[`scripts/relay-e2e.ps1`](../../scripts/relay-e2e.ps1) 生成，最后三张由
[`scripts/ai-e2e.ps1`](../../scripts/ai-e2e.ps1) 生成
（后两个都需要 [`scripts/fake-openai.ps1`](../../scripts/fake-openai.ps1) 先跑起来）。

重新生成：

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:ASPNETCORE_URLS='http://localhost:5195'
$env:Identity__RequireConfirmedAccount='false'
$env:Admin__Emails='e2e-verify@example.com'   # smoke.ps1 用的就是这个邮箱；点「保存这个端点」要管理员
dotnet run --project src\KidPlan.Web
# 另开一个窗口：
pwsh -File scripts\fake-openai.ps1 -Port 1234     # 常驻，下面中继那两条要用
pwsh -File scripts\smoke.ps1
pwsh -File scripts\cdp.ps1
pwsh -File scripts\relay-e2e.ps1 -Prepare         # 会写出 %TEMP%\kidplan-e2e\ai-relay.json
# 把应用改成 Ai__ConfigFile 指向它，重启，再跑：
pwsh -File scripts\relay-e2e.ps1
pwsh -File scripts\relay-e2e.ps1 -Cleanup
# AI 推荐那三张需要把应用的配置指到一个直连假模型的端点：
pwsh -File scripts\ai-e2e.ps1 -PrepareConfig      # 写配置，然后重启应用
pwsh -File scripts\ai-e2e.ps1
```

截图会落在 `%TEMP%\kidplan-e2e\`，需要的话手工拷回本目录
（文件名列在上面的表里，注意脚本里的名字和本目录里的名字略有出入）。
跑完记得清数据：`scripts/smoke-cleanup.sql`（中继那条会清自己的账号）。
