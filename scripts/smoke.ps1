# =============================================================================
# 端到端冒烟验证（本机）
#
# 目的：用真实 HTTP 走一遍「注册 / 登录 → 打开每个功能页」，确认页面在
# 预渲染阶段不抛异常、关键内容真的出现在返回的 HTML 里。
#
# 前置：
#   1) 应用已经跑在 http://localhost:5195，并且 Identity__RequireConfirmedAccount=false
#      （否则新注册的账号登不进去，我们没法用 HTTP 拿到登录态）；
#   2) 已执行 scripts/smoke-data.sql 造好测试数据；
#   3) 结束之后执行 scripts/smoke-cleanup.sql 清干净。
#
# 为什么用 curl 而不是 Invoke-WebRequest：
#   Blazor 静态 SSR 的表单必须带 _handler 字段（表单名），
#   Invoke-WebRequest 拿到的响应对象在跳转后的行为不好控制，curl + cookie jar 更直白。
# =============================================================================
$ErrorActionPreference = 'Stop'

$base = 'http://localhost:5195'
$email = 'e2e-verify@example.com'
$password = 'E2eVerify!2026'

$work = Join-Path $env:TEMP 'kidplan-e2e'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$jar = Join-Path $work 'cookies.txt'
Remove-Item $jar -ErrorAction SilentlyContinue
$curl = 'curl.exe'

# 造测试数据的 SQL 需要直接连本机 MySQL。打包的 MySQL 路径是开发机专用的，
# 所以允许用 KIDPLAN_MYSQL 覆盖，找不到就退回 PATH 里的 mysql。
$mysql = $env:KIDPLAN_MYSQL
if (-not $mysql) {
    $bundled = 'C:\Code\SDemo\_tools\mysql\server\bin\mysql.exe'
    $mysql = if (Test-Path $bundled) { $bundled } else { 'mysql' }
}

$script:failures = 0
$childId = $null

function Get-Page([string]$path) {
    $file = Join-Path $work 'page.html'
    $code = & $curl -s -o $file -w '%{http_code}' -b $jar -c $jar "$base$path"
    $raw = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($file))

    # 必须解码实体再比对：Razor 里静态写的中文原样输出，
    # 但 @变量 出来的中文会被默认 HtmlEncoder 转成 &#x8170;&#x679C; 这种数字实体。
    # 只搜原始 HTML 的话，「腰果」搜不到、而帮助文案里恰好也写到的「牛奶」能搜到，
    # 就会出现一半通过一半失败的假象。
    $text = [System.Net.WebUtility]::HtmlDecode($raw)

    return @{ Code = $code; Body = $text; Raw = $raw }
}

# 一条硬断言。原来是写成 if(...){写一行 ✓} 的，没匹配上就什么都不打印，
# 看上去像「检查过了」——实际上等于没检查。少一条就计一次失败。
function Assert-Contains([string]$body, [string]$needle, [string]$label) {
    if ($body -match $needle) {
        Write-Output "         ✓ $label"
    } else {
        $script:failures++
        Write-Output "         ✗ $label（页面里没匹配到 /$needle/）"
    }
}

function Assert-NotContains([string]$body, [string]$needle, [string]$label) {
    if ($body -notmatch $needle) {
        Write-Output "         ✓ $label"
    } else {
        $script:failures++
        Write-Output "         ✗ $label（页面里不该出现 /$needle/，但匹配到了）"
    }
}

function Assert-Page([string]$path, [string]$label, [string[]]$expect) {
    $page = Get-Page $path
    $ok = $page.Code -eq '200'
    $missing = @()

    foreach ($needle in $expect) {
        if (-not $page.Body.Contains($needle)) { $missing += $needle }
    }

    # 进度行必须走 Write-Host：Write-Output 会被调用方的赋值语句吃掉
    # （`$null = Assert-Page ...` 会把函数的所有输出都收走）。
    if ($ok -and $missing.Count -eq 0) {
        Write-Host ("  [ OK ] {0,-46} {1}  {2,6} KB" -f $label, $page.Code, [Math]::Round($page.Body.Length / 1024, 1))
    } else {
        $script:failures++
        Write-Host ("  [FAIL] {0,-46} {1}" -f $label, $page.Code)
        if (-not $ok) { Write-Host '         状态码不是 200' }
        if ($missing.Count -gt 0) { Write-Host "         HTML 里缺：$($missing -join ' / ')" }
    }

    return $page
}

# Blazor 静态 SSR 的表单需要 _handler（表单名）与防伪令牌成对出现。
function Submit-Form([string]$path, [string]$handler, [hashtable]$fields) {
    $html = (& $curl -s -c $jar "$base$path") -join "`n"

    $token = [regex]::Match($html, '<input[^>]*name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    if (-not $token) { throw "$path 的页面里找不到防伪令牌" }

    $args = @('-s', '-o', (Join-Path $work 'post.html'), '-w', '%{http_code} %{redirect_url}',
              '-b', $jar, '-c', $jar, '-X', 'POST', "$base$path",
              '--data-urlencode', "_handler=$handler",
              '--data-urlencode', "__RequestVerificationToken=$token")

    foreach ($key in $fields.Keys) {
        $args += @('--data-urlencode', "$key=$($fields[$key])")
    }

    return (& $curl @args)
}

Write-Output '=== 1. 注册并自动登录 ==='
function Register-TestAccount {
    return Submit-Form '/Account/Register' 'register' @{
        'Input.DisplayName'     = 'E2E 验证'
        'Input.Email'           = $email
        'Input.Password'        = $password
        'Input.ConfirmPassword' = $password
    }
}

$result = Register-TestAccount
Write-Output "  $result"

if ($result -notmatch '^30') {
    # 这个脚本是设计成可以反复跑的。上一次跑到一半留下的账号会让注册失败，
    # 所以这里先把它删掉（子表级联）再重试一次，而不是直接让人去手工清库。
    Write-Output '  这个账号还在（上次跑完没清理），先删掉再重试一次。'
    $cleanup = Join-Path $PSScriptRoot 'smoke-cleanup.sql'
    $null = & $mysql -h 127.0.0.1 -P 3306 -u root kidplan --default-character-set=utf8mb4 -e "source $cleanup" 2>&1

    Remove-Item $jar -ErrorAction SilentlyContinue
    $result = Register-TestAccount
    Write-Output "  $result"
}

if ($result -notmatch '^30') {
    Write-Output '  ✗ 注册没有跳转，后面都会失败。'
    exit 1
}

Write-Output ''
Write-Output '=== 2. 造测试数据（过敏原 / 成长记录 / 三餐 + 加餐） ==='
$sqlPath = Join-Path $PSScriptRoot 'smoke-data.sql'
$sqlOut = & $mysql -h 127.0.0.1 -P 3306 -u root kidplan --default-character-set=utf8mb4 -e "source $sqlPath" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Output "  ✗ 执行 $sqlPath 失败："
    $sqlOut | ForEach-Object { Write-Output "    $_" }
    exit 1
}

# 从 /children 页面取 childId：比在脚本里再连一次库拿 Id 更省事，
# 而且顺带验证了「新建的小孩确实出现在列表页上」。
$listPage = Get-Page '/children'
$m = [regex]::Match($listPage.Body, '/children/(\d+)/body')
if (-not $m.Success) {
    Write-Output '  ✗ 小孩列表里没有找到任何小孩，后面的页面验证没有意义。'
    exit 1
}

$childId = $m.Groups[1].Value
Write-Output "  childId = $childId"

Write-Output ''
Write-Output '=== 3. 登录态确认 ==='
# 注意变量名不要用 $home：它是 PowerShell 的只读自动变量。
$null = Assert-Page '/' '首页（登录后）' @('退出登录', '冒烟娃')

Write-Output ''
Write-Output '=== 4. 身体管理（独立一页：记录表单 + 最新数值 + 全部记录） ==='
# 身体管理和成长曲线是两个页面。这一页负责「记数据」，所以表单入口、摘要卡片、
# 记录表格都必须在；曲线页只留筛选器和图。
#
# 注意：记录表单是**点开才有**的（_showForm），预渲染的 HTML 里没有表单字段，
# 所以这里不查「记录方式 / 身高 (cm)」这些——真的点开它、逐项查字段由 scripts/cdp.ps1 负责。
$body = Assert-Page "/children/$childId/body" '身体管理' `
    @('记录身体情况', '编辑小孩资料', '成长曲线', '全部记录', '记录时间', '较上次',
      '身高', '体重', '体温', 'BMI')
Assert-Contains $body.Body '共 ' '页头显示了记录条数'
Assert-Contains $body.Body '时段记录' '记录列表里真的列出了记录（含那条时段记录）'

Write-Output ''
Write-Output '=== 4b. 成长曲线：可选择的指标 / 分图 / 叠加 / 归一化 / 记录方式 / 时间范围 ==='
# 指标的勾选项和画出来的图是两回事：勾选项由 query 决定（所以能用 URL 验证），
# 而图表本身在预渲染里也会出现。这里两种都查。
$null = Assert-Page "/children/$childId/growth" '默认（身高+体重+BMI 分图）' `
    @('选择要画的指标', '身高', '体重', 'BMI', '分图', '叠加', '记录方式', '近 6 个月')
$null = Assert-Page "/children/$childId/growth?metrics=height,weight,bmi,temp,sleep,exercise,appetite,mood&view=overlay" `
    '全部 8 个指标 + 叠加' @('体温', '睡眠', '运动', '食欲', '精神状态', '叠加')
$null = Assert-Page "/children/$childId/growth?metrics=height,weight&view=overlay&normalize=true" `
    '叠加 + 归一化' @('归一化', '0~100')
$null = Assert-Page "/children/$childId/growth?kind=period" '只看时段记录' @('点/时段分开看')
$null = Assert-Page "/children/$childId/growth?range=all&metrics=temperature" '时间范围：全部 + 只画体温' @('全部', '体温')

Write-Output ''
Write-Output '=== 4c. 两个页面互不串味 ==='
# 拆页的回归点：曲线页不该再出现记录表单和记录表格，
# 身体管理页不该出现只有曲线页才有的指标筛选。
$growthPage = Get-Page "/children/$childId/growth"
Assert-Contains $growthPage.Body '去记录身体情况' '曲线页给了回身体管理的按钮'
Assert-Contains $growthPage.Body '/body' '曲线页链接到身体管理'
Assert-NotContains $growthPage.Body '新增一条记录' '曲线页不再有记录表单'
Assert-NotContains $growthPage.Body '保存记录' '曲线页不再有保存按钮'
Assert-NotContains $body.Body '选择要画的指标' '身体管理页不再有指标筛选'

Write-Output ''
Write-Output '=== 5. 过敏原管理（分等级） ==='
$allergens = Assert-Page "/children/$childId/allergens" '过敏原管理' `
    @('牛奶', '芒果', '鸡蛋', '花生', '腰果', '花粉', '不耐受', '轻微', '中等', '强过敏', '过敏性休克', '待复核', '非食物')
Assert-Contains $allergens.Body '已登记（6 条）' '6 条过敏原都在（含从旧文本迁移的那条）'

Write-Output ''
Write-Output '=== 6. 饮食安排（固定三餐 + 不定时加餐 + 过敏分级拦截） ==='
$meals = Assert-Page "/children/$childId/meals" '饮食安排（今天）' `
    @('固定三餐', '点心 / 零食', '不定时，想吃就记', '记一顿点心', '记一顿零食',
      '早餐', '午餐', '晚餐', '只算定了饭点的那几顿', 'AI 饮食推荐',
      '花生（强过敏）', '腰果（过敏性休克）', '牛奶（不耐受）')
Assert-Contains $meals.Body '安排时还没有确认过风险，请核实' '未确认风险的命中给了红字警告'

Write-Output ''
Write-Output '=== 7. 餐饮记录 ==='
# 注意：按周 / 按月 / 全部 是组件内部的交互状态，**没有** query 参数，
# 所以这里只用 URL 验证默认视图；「全部」那一档靠 scripts/cdp.ps1 真的点一下。
$history = Assert-Page "/children/$childId/meal-history" '默认（本周）' `
    @('按周', '按月', '全部', '本周汇总', '记录天数', '固定三餐', '点心零食', '涉及过敏食物')
# 只断言「有这一栏」，不断言具体是几餐：这一档是「本周」，而本周装得下今天是周几
# 决定了昨天那条算不算进来——写死「1 餐」会在跨周 / 跨午夜之后随机变红。
Assert-Contains $history.Body '餐还没确认过风险' '过敏回顾把「安排后才登记过敏」的那几餐单独列了出来'

Write-Output ''
Write-Output '=== 8. 模型设置（普通账号看不到全局配置） ==='
# 刚注册的账号不是管理员，所以「全局配置」页签不该出现——
# 那一页改的是服务器上的 config/ai.json，会影响这个部署上的所有账号。
$ai = Assert-Page '/settings/ai' '模型设置（非管理员）' `
    @('我的账号覆盖', '你当前不是管理员', '只给本账号加一个端点', '端点覆盖', '保存我的覆盖',
      '单独设置推荐参数', '清除覆盖')

Assert-Contains $ai.Body '模型名可以手工输入' '说明了模型名可以手工输入'
Assert-Contains $ai.Body 'OpenAI' '说明了接的是标准 OpenAI 接口'

if ($ai.Body.Contains('保存全局配置') -or $ai.Body.Contains('重新从文件加载')) {
    $script:failures++
    Write-Output '  ✗ 非管理员页面上出现了全局配置的按钮'
} else {
    Write-Output '  ✓ 页面上没有「保存全局配置 / 重新从文件加载」'
}

# 连服务器上的配置文件路径都不该漏给他。
if ($ai.Body -match '[A-Za-z]:\\[^\s<>"]*\.json') {
    $script:failures++
    Write-Output '  ✗ 非管理员页面上泄露了服务器配置文件路径'
} else {
    Write-Output '  ✓ 没有泄露服务器上的配置文件路径'
}

Write-Output ''
Write-Output '=== 8b. 把账号提成管理员之后（应该能看到全局配置） ==='
# 走「直接写库」这条路：不重新注册，只加一条角色关联，再重新登录一次拿新 Cookie。
# 角色是写进登录 Cookie 的，所以必须重新登录——这一步本身也值得验证。
$granted = & $mysql -h 127.0.0.1 -P 3306 -u root kidplan --default-character-set=utf8mb4 -N -B -e @"
INSERT INTO AspNetUserRoles (UserId, RoleId)
SELECT u.Id, r.Id FROM AspNetUsers u, AspNetRoles r
WHERE u.Email = '$email' AND r.NormalizedName = 'ADMIN'
  AND NOT EXISTS (SELECT 1 FROM AspNetUserRoles x WHERE x.UserId = u.Id AND x.RoleId = r.Id);
SELECT ROW_COUNT();
"@ 2>&1

if ("$granted".Trim() -eq '1') {
    Write-Output '  ✓ 已把测试账号加进 admin 角色'
} else {
    $script:failures++
    Write-Output "  ✗ 加 admin 角色失败（mysql 说：$granted）"
}

# 重新登录（旧 Cookie 里的角色声明还是旧的）
$loginPage = Join-Path $work 'login-page.html'
$null = & $curl -s -c $jar -o $loginPage "$base/account/login"
$loginHtml = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($loginPage))
$loginToken = [regex]::Match($loginHtml, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
$null = & $curl -s -c $jar -b $jar -o (Join-Path $work 'login-post.html') -X POST "$base/account/login" `
    --data-urlencode '_handler=login' `
    --data-urlencode "__RequestVerificationToken=$loginToken" `
    --data-urlencode "Input.Email=$email" `
    --data-urlencode "Input.Password=$password"

$aiAdmin = Assert-Page '/settings/ai' '模型设置（管理员）' @('全局配置', '我的账号覆盖', '重新从文件加载')
Assert-Contains $aiAdmin.Body '保存全局配置' '管理员能看到「保存全局配置」'
Assert-Contains $aiAdmin.Body '自动生效' '说明了改文件后自动生效'
Assert-Contains $aiAdmin.Body '30 天' '说明了前置周期最多 30 天'
Assert-Contains $aiAdmin.Body '本机中继' '提到了「本机中继」这种接入方式'

Write-Output ''
Write-Output '=== 9. 本机中继 ==='
$relay = Assert-Page '/settings/relay' '本机中继' @('中继')
Assert-Contains $relay.Body 'api/relay/connect' '提示了 /api/relay/connect'
Assert-Contains $relay.Body 'KidPlan\.Relay' '提示了 KidPlan.Relay'
Assert-Contains $relay.Body 'nginx' '有 Nginx 排错说明'

Write-Output ''
Write-Output '=== 10. 模型配置文件（默认落在内容根目录下的 config/ai.json） ==='
# 应用可以用 Ai__ConfigFile 把配置指到别处（scripts/relay-e2e.ps1 就是这么做的），
# 所以这里不做「必须等于某个固定路径」的硬断言，只要求页面上给出的路径文件真的存在。
# 注意要用管理员那一份页面：非管理员的页面上已经没有这个路径了。
$configPath = [regex]::Match($aiAdmin.Body, '([A-Za-z]:\\[^\s<>"]*\.json)').Groups[1].Value
if ($configPath -and (Test-Path $configPath)) {
    Write-Output "  ✓ 页面上给出的配置文件路径存在：$configPath"
} elseif ($configPath) {
    $script:failures++
    Write-Output "  ✗ 页面上写的是 $configPath，但这个文件不存在"
} else {
    $script:failures++
    Write-Output '  ✗ 管理员页面上没有给出配置文件路径'
}

Write-Output ''
if ($script:failures -eq 0) {
    Write-Output '全部通过。'
} else {
    Write-Output "有 $($script:failures) 项失败。"
    exit 1
}
