# =============================================================================
# 交互路径验证（无头 Edge + CDP）
#
# 为什么需要这个：Blazor 交互式渲染的页面上有一部分行为**没有 URL 参数**，
# 只靠 HTTP GET 验证不到。最典型的就是「餐饮记录」的按周 / 按月 / 全部那一组按钮：
# 之前那次「进餐饮记录就卡死闪退」正是点了「全部」触发的
# （1900-01-01 一路展开成四万多天，再为每个空白天渲染一个 MudChip）。
#
# 所以这里真的开一个浏览器、真的点一下，并采集：
#   · 点击前后 innerText 的变化（确认内容真的换了，不是点了没反应）
#   · 点击耗时（确认没有卡住）
#   · 页面上的「漏记」chip 数量（确认有上限，不是几万个）
#   · 截图（人眼可以复核）
#
# 前置：
#   1) 应用跑在 http://localhost:5195；
#   2) 先跑过 scripts/smoke.ps1（它留下登录 Cookie 与测试数据）。
#
# CDP 那套管道（起浏览器、发命令、跑 JS、截图）在 scripts/cdp-lib.ps1 里，
# 和 scripts/relay-e2e.ps1 共用一份。
# =============================================================================
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\cdp-lib.ps1"

$base = 'http://localhost:5195'
$work = Join-Path $env:TEMP 'kidplan-e2e'
$jar = Join-Path $work 'cookies.txt'

if (-not (Test-Path $jar)) { throw "找不到 $jar，请先跑 scripts/smoke.ps1。" }

$script:failures = 0

$cookie = Get-AuthCookie -Jar $jar
Start-CdpBrowser -Work $work -Cookie $cookie

try {
    # ----------------------------------------------------------------------
    # 1. 餐饮记录：点「全部」
    # ----------------------------------------------------------------------
    Write-Output '=== 1. 餐饮记录：点「全部」（曾经卡死闪退的那条路） ==='

    $childId = $null
    Navigate "$base/children"
    # 小孩列表页上的「身体管理」图标按钮指向 /children/{id}/body（成长曲线是另一个按钮）。
    $childId = Invoke-Js "(document.body.innerHTML.match(/\/children\/(\d+)\/body/) || [])[1] ?? null"
    if (-not $childId) { throw '拿不到 childId（列表页没有小孩）。' }
    Write-Output "  childId = $childId"

    Navigate "$base/children/$childId/meal-history"
    if (-not (Wait-Text '本周汇总')) { throw '餐饮记录页没渲染出来。' }
    $before = Invoke-Js 'document.body.innerText.length'
    Write-Output "  默认视图：$before 字符"

    # 真的点一下「全部」。用按钮文字定位，避免依赖 MudBlazor 的内部 class。
    $clicked = Invoke-Js @'
(() => {
  const btn = [...document.querySelectorAll('button')]
      .find(b => b.innerText.trim() === '全部');
  if (!btn) return false;
  btn.click();
  return true;
})()
'@
    if (-not $clicked) { throw '页面上找不到「全部」按钮。' }

    if (-not (Wait-Text '全部记录汇总')) {
        $script:failures++
        Write-Output '  [FAIL] 点了「全部」之后没有出现「全部记录汇总」'
    } else {
        $after = Invoke-Js 'document.body.innerText.length'
        $days = Invoke-Js "document.body.innerText.match(/记录天数\s*(\d+)\s*\/\s*(\d+)/)?.slice(1).join(' / ') ?? '?'"
        $chips = Invoke-Js "document.querySelectorAll('.mud-chip').length"
        Write-Output "  点「全部」之后：$after 字符，记录天数 $days，页面上的 chip 数 $chips"

        # 400 天上限 + 漏记 chip 最多 24 段：两者相加也远小于 1000。
        if ($chips -gt 1000) {
            $script:failures++
            Write-Output "  [FAIL] chip 数 $chips 太多了，上限没生效"
        } else {
            Write-Output "  [ OK ] chip 数 $chips，没有失控"
        }

        $page2 = Save-Shot 'meal-history-all'
        Write-Output "  截图：$page2"
    }

    # ------------------------------------------------------------------
    # 2. 餐饮记录：点「按月」（漏记 chip 的渲染路径）
    # ------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 2. 餐饮记录：点「按月」（这一段才有真正的「漏记」日子） ==='
    $null = Invoke-Js "[...document.querySelectorAll('button')].find(b => b.innerText.trim() === '按月')?.click() ?? null"
    Start-Sleep -Milliseconds 2500

    $missing = Invoke-Js "document.body.innerText.includes('没有固定三餐记录')"
    $chipCount = Invoke-Js "document.querySelectorAll('.mud-chip').length"
    if ($missing) {
        Write-Output "  [ OK ] 出现了「漏记」提示（chip 数 $chipCount）"
        $shot = Save-Shot 'meal-history-month'
        Write-Output "  截图：$shot"
    } else {
        $script:failures++
        Write-Output '  [FAIL] 按月视图里没有「没有固定三餐记录」'
    }

    # ------------------------------------------------------------------
    # 3. 成长曲线：勾指标 + 分图 / 叠加
    #
    # 指标是 MudChip（不是 checkbox），分图模式下每个指标一张 MudChart，
    # 所以「勾上体温 → 图从 3 张变 4 张」「切叠加 → 变回 1 张」是两个很硬的断言。
    # ------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 3. 成长曲线：勾上「体温」，再切「叠加」 ==='
    Navigate "$base/children/$childId/growth"
    if (-not (Wait-Text '选择要画的指标')) { throw '成长曲线页没渲染出来。' }

    # 注意：MudChart（9.11）一个图会渲染出**两个** .mud-chart 的 div，
    # 所以直接数 .mud-chart 得到的是「逻辑图数 × 2」（3 个指标 = 6）。
    # 这里改成数「包着一张图的 MudPaper」，正好等于 MetricChart 的个数。
    $charts = {
        Invoke-Js @'
[...document.querySelectorAll('.mud-paper')]
    .filter(p => p.querySelector('.mud-chart')).length
'@
    }
    $before = & $charts
    Write-Output "  默认（身高+体重+BMI）分图：$before 张图"

    # 点「体温」这个 chip。它的文字就是「体温」（没有数据时才会带「（无数据）」）。
    $toggled = Invoke-Js @'
(() => {
  const chip = [...document.querySelectorAll('.mud-chip')]
      .find(c => c.innerText.replace(/\s/g, '') === '体温');
  if (!chip) return 'not-found';
  chip.click();
  return 'clicked';
})()
'@
    Start-Sleep -Milliseconds 3000
    $after = & $charts
    Write-Output "  勾「体温」：$toggled，现在 $after 张图"

    if ($toggled -eq 'clicked' -and $after -eq $before + 1) {
        Write-Output "  [ OK ] 勾上体温之后多了一张图（$before → $after）"
    } else {
        $script:failures++
        Write-Output "  [FAIL] 指标选择没生效（$toggled，$before → $after）"
    }

    # 图表标题里应该有单位，用来确认多出来的确实是体温那张。
    $hasTempAxis = Invoke-Js "document.body.innerText.includes('℃')"
    if ($hasTempAxis) { Write-Output '  [ OK ] 页面上出现了 ℃ 轴' }

    # 切到叠加：多条线合成一张图。
    $null = Invoke-Js "[...document.querySelectorAll('button')].find(b => b.innerText.trim() === '叠加')?.click() ?? null"
    Start-Sleep -Milliseconds 3000
    $overlayCharts = & $charts
    $hasNormalize = Invoke-Js "document.body.innerText.includes('归一化')"
    Write-Output "  切「叠加」：$overlayCharts 张图，出现「归一化」开关 = $hasNormalize"

    if ($overlayCharts -eq 1 -and $hasNormalize) {
        Write-Output '  [ OK ] 叠加模式：4 个指标合成 1 张图，并给出了归一化开关'
    } else {
        $script:failures++
        Write-Output "  [FAIL] 叠加模式不对（$overlayCharts 张图，归一化=$hasNormalize）"
    }

    # 打开归一化，确认页面给出「0~100」的解释文案。
    $null = Invoke-Js @'
(() => {
  const sw = [...document.querySelectorAll('.mud-switch, label')]
      .find(e => e.innerText.includes('归一化'));
  if (!sw) return null;
  (sw.querySelector('input[type=checkbox]') || sw).click();
  return true;
})()
'@
    Start-Sleep -Milliseconds 2500
    $normalized = Invoke-Js "document.body.innerText.includes('0~100')"
    if ($normalized) { Write-Output '  [ OK ] 打开归一化后有 0~100 的说明' }
    else { Write-Output '  [ 注意 ] 没看到 0~100 的说明文案' }

    $shot3 = Save-Shot 'growth-overlay'
    Write-Output "  截图：$shot3"

    # ------------------------------------------------------------------
    # 3b. 成长曲线：切「只看时段记录」
    #
    # 这条是用户报的 bug：冒烟数据里那条时段记录只填了睡眠 / 运动 / 食欲 / 精神，
    # 而曲线默认勾的是身高 / 体重 / BMI，于是四条线全没数据、页面一片空白
    # （以前连提示都没有，看上去就是「曲线出不来」）。
    # 现在必须给出「勾选的指标没数据」+「这一档里有数据的是哪几个」+ 一键改勾。
    # ------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 3b. 成长曲线：「只看时段记录」且勾选指标没数据时 ==='
    Navigate "$base/children/$childId/growth?kind=period"
    if (-not (Wait-Text '选择要画的指标')) { throw '成长曲线页没渲染出来。' }

    $hint = Invoke-Js "document.body.innerText.includes('勾选的指标在这一档里没有数据')"
    $suggests = Invoke-Js "document.body.innerText.includes('这一档里有数据的指标是')"
    $upToDate = Invoke-Js "document.body.innerText.match(/这一档里有数据的指标是：([^。]+)/)?.[1] ?? ''"
    Write-Output "  提示=$hint，列出有数据的指标=$suggests（$upToDate）"

    if ($hint -and $suggests -and $upToDate.Contains('睡眠')) {
        Write-Output '  [ OK ] 明确说了「勾选的指标没数据」，并指出有数据的是哪几个'
    } else {
        $script:failures++
        Write-Output "  [FAIL] 时段记录档位没有给出可读的提示（$hint / $suggests / $upToDate）"
    }

    $shot3b = Save-Shot 'growth-period-hint'
    Write-Output "  截图：$shot3b"

    # 一键改勾之后：勾选应该换成「有数据的那些」，身高/体重/BMI 反过来变成（无数据）。
    #
    # 注意这里**不能数图**：这一档只有 1 个记录点，而 MetricChart 对单点会渲染成
    # 「目前只有 1 个数据点，再记录一次就能画出趋势。」的占位说明，压根没有 .mud-chart。
    # 所以断言「勾选真的换了」+「提示消失」+「出现了这几张图的标题」。
    $null = Invoke-Js @'
(() => {
  const b = [...document.querySelectorAll('button')]
      .find(x => x.innerText.includes('改勾这几个指标'));
  if (!b) return null;
  b.click();
  return true;
})()
'@
    Start-Sleep -Milliseconds 3000
    $stillHint = Invoke-Js "document.body.innerText.includes('勾选的指标在这一档里没有数据')"
    # 必须先把空白折叠：MudChip 的文字和它的 label 之间在 innerText 里是换行，不是空格。
    $nowEmpty = Invoke-Js "document.body.innerText.replace(/\s+/g,' ').includes('身高 （无数据）')"
    $hasSleepTitle = Invoke-Js "document.body.innerText.replace(/\s+/g,' ').includes('睡眠 小时')"
    $hasMoodTitle = Invoke-Js "document.body.innerText.includes('精神状态')"
    Write-Output "  点「改勾这几个指标」之后：提示还在=$stillHint，身高变（无数据）=$nowEmpty，出现睡眠/精神两张图=$hasSleepTitle/$hasMoodTitle"

    if (-not $stillHint -and $nowEmpty -and $hasSleepTitle -and $hasMoodTitle) {
        Write-Output '  [ OK ] 一键改勾把勾选换成了有数据的指标，并画了出来'
    } else {
        $script:failures++
        Write-Output "  [FAIL] 一键改勾没生效（hint=$stillHint，身高无数据=$nowEmpty，睡眠=$hasSleepTitle，精神=$hasMoodTitle）"
    }

    $shot3c = Save-Shot 'growth-period-charts'
    Write-Output "  截图：$shot3c"

    # ------------------------------------------------------------------
    # 3c. 身体管理：独立一页，记录表单真的能打开
    #
    # 拆页之后最容易坏的就是这里：曲线页把表单搬走了，身体管理页得真的还有它，
    # 而且点「记录身体情况」之后字段要出来（不是只剩一个按钮）。
    # ------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 3c. 身体管理：独立一页 + 表单能展开 ==='
    Navigate "$base/children/$childId/body"
    if (-not (Wait-Text '记录身体情况')) { throw '身体管理页没渲染出来。' }

    $hasList = Invoke-Js "document.body.innerText.includes('全部记录')"
    $hasCards = Invoke-Js "document.body.innerText.includes('较上次')"
    # 入口用「页面上有没有这个按钮」来判断，不去拼 CSS 选择器：
    # 单引号 + 转义双引号塞进 PowerShell 字符串很容易把自己拼坏。
    # 身体管理页上这个入口叫「成长曲线」（曲线页回过来的那个按钮才叫「去记录身体情况」）。
    $toGrowth = Invoke-Js "document.body.innerText.includes('成长曲线')"
    Write-Output "  全部记录=$hasList，最新数值卡片=$hasCards，有去成长曲线的入口=$toGrowth"

    if ($hasList -and $hasCards -and $toGrowth) {
        Write-Output '  [ OK ] 记录列表、最新数值、成长曲线入口都在这一页'
    } else {
        $script:failures++
        Write-Output "  [FAIL] 身体管理页内容不全（列表=$hasList，卡片=$hasCards，入口=$toGrowth）"
    }

    $opened = Invoke-Js @'
(() => {
  const b = [...document.querySelectorAll('button')]
      .find(x => x.innerText.includes('记录身体情况'));
  if (!b) return false;
  b.click();
  return true;
})()
'@
    if (-not $opened) { throw '身体管理页上找不到「记录身体情况」按钮。' }

    Start-Sleep -Milliseconds 1500
    $formFields = Invoke-Js @'
(() => {
  const text = document.body.innerText;
  const labels = ['记录方式', '身高 (cm)', '体重 (kg)', '体温 (℃)', '睡眠 (小时)',
                  '运动 (分钟)', '食欲', '精神状态', '症状 / 不适', '备注'];
  return labels.filter(l => text.includes(l)).length;
})()
'@
    $saveButton = Invoke-Js "document.body.innerText.includes('保存记录')"
    Write-Output "  展开后表单字段命中 $formFields / 10，保存按钮=$saveButton"

    if ($formFields -ge 9 -and $saveButton) {
        Write-Output '  [ OK ] 记录表单能展开，字段和保存按钮都在'
    } else {
        $script:failures++
        Write-Output "  [FAIL] 记录表单展开后字段不全（$formFields / 10，保存按钮=$saveButton）"
    }

    $shot3d = Save-Shot 'body-record-form'
    Write-Output "  截图：$shot3d"

    # 打开表单后不要留下脏状态：点「收起记录表单」回去，顺便验证按钮文字会切。
    $null = Invoke-Js @'
(() => {
  const b = [...document.querySelectorAll('button')]
      .find(x => x.innerText.includes('收起记录表单'));
  if (!b) return null;
  b.click();
  return true;
})()
'@
    Start-Sleep -Milliseconds 800
    $collapsed = Invoke-Js "document.body.innerText.includes('记录身体情况') && !document.body.innerText.includes('保存记录')"
    if ($collapsed) {
        Write-Output '  [ OK ] 「收起记录表单」把表单收起来了'
    } else {
        Write-Output '  [ 提示 ] 收起按钮的状态没有按预期切回去（不影响功能）'
    }

    # ------------------------------------------------------------------
    # 4. 饮食安排：切到昨天，确认加餐与确认按钮都在
    # ------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 4. 饮食安排：昨天（含已吃完 / 跳过 / AI 推荐标记） ==='
    # 日期相对今天算：冒烟数据是 CURDATE() 写的，写死某一天会在跨过午夜之后失配。
    $yesterdayDate = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd')
    Navigate "$base/children/$childId/meals?date=$yesterdayDate"
    if (-not (Wait-Text '固定三餐')) { throw '饮食安排页没渲染出来。' }
    $yesterday = Invoke-Js "document.body.innerText.includes('在幼儿园吃过了')"
    $aiTag = Invoke-Js "document.body.innerText.includes('AI 推荐')"
    if ($yesterday) { Write-Output '  [ OK ] 昨天那一餐读出了备注' } else { $script:failures++; Write-Output '  [FAIL] 昨天没读出来' }
    if ($aiTag) { Write-Output '  [ OK ] AI 推荐写入的那条带 AI 标记' } else { Write-Output '  [ 注意 ] 没看到 AI 推荐标记' }
    $shot4 = Save-Shot 'meals-yesterday'
    Write-Output "  截图：$shot4"

    # ------------------------------------------------------------------
    # 5. 记一顿点心：点一下，确认内联表单开着「点心」并且饭点默认是空的
    #
    # 注意这里**不是**弹窗：QuickAddAsync 只是把页面上的内联表单展开
    # （_showForm = true）并预置餐次，所以查的是「新增饮食安排」这段，不是 .mud-dialog。
    # ------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 5. 点「记一顿点心」展开表单 ==='
    Navigate "$base/children/$childId/meals"
    if (-not (Wait-Text '固定三餐')) { throw '饮食安排页没渲染出来。' }

    $opened = 'not-found'
    $form = $false

    # 这里刻意「反复点」而不是点一次就等：
    # Wait-Text 判断的是页面文字，而**预渲染的 HTML 里就已经有「固定三餐」**，
    # 所以它可能在 Blazor 的 SignalR 连接建立之前就返回 true，
    # 这时候点按钮是没人接的（点了等于没点）。重试几次就自然绕过了这个竞态。
    foreach ($attempt in 1..12) {
        $opened = Invoke-Js @'
(() => {
  const btn = [...document.querySelectorAll('button')]
      .find(b => b.innerText.includes('记一顿点心'));
  if (!btn) return 'not-found';
  btn.click();
  return 'clicked';
})()
'@
        Start-Sleep -Milliseconds 600
        $form = Invoke-Js "document.body.innerText.includes('新增饮食安排')"
        if ($form) { break }
    }

    if ($form) {
        Write-Output "  [ OK ] 内联表单展开了（$opened）"
        # 加餐的饭点必须是可选的：文案里会写明「可以留空表示不定时」。
        $hint = Invoke-Js "document.body.innerText.includes('留空表示不定时')"
        if ($hint) { Write-Output '  [ OK ] 表单说明里写了饭点可以留空（不定时）' }
        else { $script:failures++; Write-Output '  [FAIL] 表单里没有「留空表示不定时」的说明' }

        # 餐次应该已经预置成「点心」，而且给了「清空饭点（不定时）」这个按钮。
        $preset = Invoke-Js "document.body.innerText.includes('🍪 点心') || document.body.innerText.includes('点心')"
        $clear = Invoke-Js "document.body.innerText.includes('清空饭点')"
        if ($preset -and $clear) { Write-Output '  [ OK ] 餐次预置成点心，并且给了「清空饭点（不定时）」' }
        else { Write-Output "  [ 注意 ] 预置点心=$preset，清空饭点按钮=$clear" }

        $shot5 = Save-Shot 'meal-adhoc-form'
        Write-Output "  截图：$shot5"
    } else {
        $script:failures++
        Write-Output "  [FAIL] 点了「记一顿点心」表单没展开（$opened）"
    }

    # ------------------------------------------------------------------
    # 6. 换餐次时默认饭点必须跟着换
    #
    # 这是用户报的「午餐写着 7:30、晚餐写着 12:00」：状态其实是对的，
    # 但火忘掉的异步赋值晚于渲染，界面上永远慢一拍（显示上一个餐次的饭点）。
    # 所以这里断言的是 **DOM 里那个输入框的值**，不是内部状态。
    # 冒烟数据里三餐的饭点是 07:30 / 12:00 / 18:00。
    # ------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 6. 换餐次：默认饭点要跟着换（曾经整体慢一拍） ==='

    Navigate "$base/children/$childId/meals"
    if (-not (Wait-Text '固定三餐')) { throw '饮食安排页没渲染出来。' }

    $readTime = @'
(() => {
  const ctl = [...document.querySelectorAll('.mud-input-control')]
      .find(c => (c.querySelector('label')?.innerText ?? '') === '计划饭点');
  if (!ctl) return '(找不到)';
  const inp = ctl.querySelector('input');
  return inp.value === '' ? '(空)' : inp.value;
})()
'@

    $openForm = @'
(() => {
  const b = [...document.querySelectorAll('button')].find(x => x.innerText.trim() === '安排一餐');
  if (!b) return false; b.click(); return true;
})()
'@

    # 展开表单（同样要重试：预渲染的 HTML 里已经有「固定三餐」）
    $formOpen = $false
    foreach ($attempt in 1..12) {
        $null = Invoke-Js $openForm
        Start-Sleep -Milliseconds 600
        $formOpen = Invoke-Js "document.body.innerText.includes('新增饮食安排')"
        if ($formOpen) { break }
    }

    if (-not $formOpen) {
        $script:failures++
        Write-Output '  [FAIL] 「安排一餐」表单没展开'
    } else {
        # 默认是早餐
        Start-Sleep -Milliseconds 800
        $breakfast = Invoke-Js $readTime
        Write-Output "  刚打开（早餐）：$breakfast"

        $results = @{}
        foreach ($meal in @(@('午餐', '12:00'), @('晚餐', '18:00'), @('早餐', '07:30'), @('点心', '(空)'))) {
            $label = $meal[0]
            $expect = $meal[1]

            # 打开餐次下拉 → 点那一项
            $null = Invoke-Js @'
(() => {
  const ctl = [...document.querySelectorAll('.mud-input-control')]
      .find(c => (c.querySelector('label')?.innerText ?? '') === '餐次');
  if (!ctl) return false;
  const target = ctl.classList.contains('mud-select') ? ctl : ctl.querySelector('.mud-select');
  target.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
  target.click();
  return true;
})()
'@
            Start-Sleep -Milliseconds 700
            $picked = Invoke-Js @"
(() => {
  const item = [...document.querySelectorAll('.mud-list-item')]
      .find(i => i.innerText.includes('$label'));
  if (!item) return false;
  item.click();
  return true;
})()
"@
            Start-Sleep -Milliseconds 1500
            $actual = Invoke-Js $readTime
            $results[$label] = $actual

            if ($picked -and $actual -eq $expect) {
                Write-Output "  [ OK ] 切到「$label」→ 计划饭点 $actual"
            } else {
                $script:failures++
                Write-Output "  [FAIL] 切到「$label」→ 计划饭点 $actual（期望 $expect，点中=$picked）"
            }

            # 截图挑「午餐」那一张：改动前这里显示的正是 07:30（上一个餐次的饭点），
            # 是最能说明问题的一屏。放在最后点心那一屏拍的话，和上面第 5 节那张一模一样。
            if ($label -eq '午餐') {
                $shot6 = Save-Shot 'meals-mealtype-times'
                Write-Output "  截图：$shot6"
            }
        }
    }

    # ------------------------------------------------------------------
    # 7. 模型设置：管理员加端点，表单里就有「保存这个端点」
    #
    # 用户报的「添加模型端点，没有保存按钮，没办法保存数据」：
    # 保存按钮原先只在页签最上面，端点表单里的按钮写的是「确定（还没写文件）」，
    # 点完什么都不落盘，人却在页面下面，很容易以为没法保存。
    #
    # 这一条会**真的写 src/KidPlan.Web/config/ai.json**，所以先备份、最后还原。
    # ------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 7. 模型设置：加端点并在表单里直接保存 ==='

    # 配置文件路径从页面上读，不写死：应用可以用 Ai__ConfigFile 指到别处。
    $probeId = 'e2e-probe-provider'
    $configBackup = $null
    $configFile = $null

    try {
        Navigate "$base/settings/ai"
        if (-not (Wait-Text '全局配置')) { throw '模型设置页没渲染出来（当前账号不是管理员？先跑 smoke.ps1）。' }

        $configFile = Invoke-Js @'
(() => {
  const m = document.body.innerText.match(/[A-Za-z]:\\[^\s<>"]*\.json/);
  return m ? m[0] : null;
})()
'@
        if (-not $configFile) {
            throw '页面上没给出配置文件路径，没法确认「保存」是不是真写进去了。'
        }
        Write-Output "  页面给出的配置文件：$configFile"

        if (Test-Path $configFile) {
            $configBackup = [System.IO.File]::ReadAllText($configFile)
        }

        # 「＋ 添加端点」也要重试：预渲染的 HTML 里已经有这段文字了。
        $formOpen = $false
        foreach ($attempt in 1..12) {
            $null = Invoke-Js @'
(() => {
  const b = [...document.querySelectorAll('button')].find(x => x.innerText.includes('添加端点'));
  if (!b) return null; b.click(); return true;
})()
'@
            Start-Sleep -Milliseconds 600
            $formOpen = Invoke-Js "document.body.innerText.includes('保存这个端点')"
            if ($formOpen) { break }
        }

        if (-not $formOpen) {
            $script:failures++
            Write-Output '  [FAIL] 「添加端点」之后表单里没有「保存这个端点」按钮'
        } else {
            Write-Output '  [ OK ] 端点表单里有「保存这个端点」按钮'

            # 填 Id / 名称 / 地址
            $filled = Invoke-Js @"
(() => {
  const set = (label, value) => {
    const ctl = [...document.querySelectorAll('.mud-input-control')]
        .find(c => (c.querySelector('label')?.innerText ?? '').startsWith(label));
    if (!ctl) return false;
    const inp = ctl.querySelector('input, textarea');
    if (!inp) return false;
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(inp, value);
    inp.dispatchEvent(new Event('input', { bubbles: true }));
    inp.dispatchEvent(new Event('change', { bubbles: true }));
    inp.blur();
    return true;
  };
  return set('标识 Id', '$probeId') && set('显示名称', 'E2E 探针端点')
      && set('接口根地址', 'http://127.0.0.1:59999/v1');
})()
"@
            Write-Output "  填表单：$filled"
            Start-Sleep -Milliseconds 800

            $saved = Invoke-Js @'
(() => {
  const b = [...document.querySelectorAll('button')].find(x => x.innerText.includes('保存这个端点'));
  if (!b) return false; b.click(); return true;
})()
'@
            Start-Sleep -Milliseconds 2500

            $toast = Invoke-Js "document.body.innerText.includes('已写入配置文件')"
            $onDisk = (Test-Path $configFile) -and
                ([System.IO.File]::ReadAllText($configFile).Contains($probeId))

            Write-Output "  点保存=$saved，出现「已写入配置文件」提示=$toast，文件里已经有这个端点=$onDisk"

            if ($saved -and $toast -and $onDisk) {
                Write-Output '  [ OK ] 表单里的保存按钮真的写进了配置文件'
            } else {
                $script:failures++
                Write-Output '  [FAIL] 表单里的保存没有把端点写进配置文件'
            }

            $shot7 = Save-Shot 'ai-add-provider'
            Write-Output "  截图：$shot7"
        }
    } finally {
        # 还原配置文件（有备份就写回去，没有备份说明原本就不存在，那就删掉）。
        if ($configFile -and $configBackup -ne $null) {
            [System.IO.File]::WriteAllText($configFile, $configBackup)
            Write-Output "  已还原 $configFile"
        } elseif ($configFile -and (Test-Path $configFile)) {
            Remove-Item $configFile -Force
            Write-Output "  已删除本次跑出来的 $configFile"
        }
    }

    # ------------------------------------------------------------------
    # 8. 过敏原列表：不能一行占掉半屏
    #
    # 用户报的「列与列、行与行的间隔很大」：原来每条过敏原是一张 pa-3 的纸，
    # 里面的下拉还是 Outlined + 浮动 label，一行实测 ~95px。
    # 现在改成「一组一张纸 + 细线分隔 + 紧凑行」。这里量的是真实像素高度，
    # 所以阈值调的是布局本身，不是某段文案。
    # ------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 8. 过敏原列表：行高（一行不能占掉半屏） ==='

    Navigate "$base/children/$childId/allergens"
    if (-not (Wait-Text '已登记')) { throw '过敏原页没渲染出来。' }
    Start-Sleep -Seconds 1

    $rows = Invoke-Js @'
(() => {
  // 只量「列表里的那一行」：它们带 px-2 py-1，里面有个等级下拉。
  // 不能按 .mud-paper 量——下面「新增过敏原」那张表单也是带下拉的纸，会把结果带到 376px。
  const rows = [...document.querySelectorAll('div.px-2.py-1')]
      .filter(d => d.querySelector('.mud-select'));
  const heights = rows.map(r => Math.round(r.getBoundingClientRect().height));
  return JSON.stringify({ rows: heights.length, perRow: heights });
})()
'@
    Write-Output "  每一行的实测高度：$rows"

    $parsed = $rows | ConvertFrom-Json
    $worst = ($parsed.perRow | Measure-Object -Maximum).Maximum

    # 95px 是改动前的实测值，60px 是 Outlined+Dense 的实测值，45px 才是现在这样。
    # 阈值放到 55px：既挡得住「又变回大间距」，也不会因为字体差异就误报。
    if ($worst -le 55 -and $parsed.rows -ge 5) {
        Write-Output "  [ OK ] 最高的一行 ${worst}px（阈值 55px），共量到 $($parsed.rows) 行"
    } else {
        $script:failures++
        Write-Output "  [FAIL] 行高 ${worst}px / $($parsed.rows) 行，间距又变大了"
    }

    $shot8 = Save-Shot 'allergens-compact'
    Write-Output "  截图：$shot8"
}
finally {
    Stop-CdpBrowser
}

Write-Output ''
if ($script:failures -eq 0) { Write-Output '交互验证全部通过。' } else { Write-Output "有 $($script:failures) 项失败。"; exit 1 }
