# =============================================================================
# AI 饮食推荐的界面链路验证（真实进程 + 假模型 + 真实 MySQL）
#
# 需求里第 (6) 条是这批改造里最复杂的一条：按前 N 天（最大 30 天）的饮食与
# 身体情况 + 过敏原，让模型给出单天 / 单餐 / 单周的菜单，并且能一键写进食安排。
#
# 单元测试覆盖了提示词内容、JSON 容错解析、本地过敏复核、落库校验。
# 但「点一下生成 → 看到菜单 → 点全部写入 → 数据库里真的多了几行」这条路
# 只有把整条链路跑起来才算数。所以这里用 scripts/fake-openai.ps1 假装是模型，
# 真的点一遍，最后直接查库确认写进去了几条。
#
# 假模型刻意混了一条含**腰果**的菜。冒烟数据里腰果是「过敏性休克」级，
# 应用拿到模型输出后会**再用本地过敏原查一遍**——这一条必须被拦住。
# 于是这一次运行同时验证了两件事：
#   · 安全的菜能写进饮食安排；
#   · 危险的菜写不进去（模型说了也不算，本地复核说了算）。
#
# 前置：
#   1) pwsh -File scripts\fake-openai.ps1 -Port 1234
#   2) 应用跑在 http://localhost:5195，且 Ai__ConfigFile 指向一个
#      **Direct** 端点（http://127.0.0.1:1234/v1）。可以直接用仓库里的模板改：
#      $env:Ai__ConfigFile="$env:TEMP\kidplan-e2e\ai-direct.json"（见 -PrepareConfig）
#   3) 先跑过 scripts/smoke.ps1：它留下登录 Cookie，以及带五档过敏原的测试小孩。
#
# 用法：
#   pwsh -File scripts\ai-e2e.ps1 -PrepareConfig   # 只写配置文件，然后自己重启应用
#   pwsh -File scripts\ai-e2e.ps1                  # 跑验证
# =============================================================================
[CmdletBinding()]
param(
    [switch]$PrepareConfig,
    [string]$Base = 'http://localhost:5195',
    [int]$FakePort = 1234
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\cdp-lib.ps1"

$work = Join-Path $env:TEMP 'kidplan-e2e'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$jar = Join-Path $work 'cookies.txt'
$configPath = Join-Path $work 'ai-direct.json'

$mysql = $env:KIDPLAN_MYSQL
if (-not $mysql) {
    $bundled = 'C:\Code\SDemo\_tools\mysql\server\bin\mysql.exe'
    $mysql = if (Test-Path $bundled) { $bundled } else { 'mysql' }
}

$script:failures = 0

function Invoke-Mysql([string]$sql) {
    return (& $mysql -h 127.0.0.1 -P 3306 -u root kidplan --default-character-set=utf8mb4 -N -B -e $sql) 2>&1
}

function Assert-True([bool]$condition, [string]$ok, [string]$fail) {
    if ($condition) { Write-Output "  [ OK ] $ok" } else { $script:failures++; Write-Output "  [FAIL] $fail" }
}

# --------------------------------------------------------------------------
# -PrepareConfig：写一份「直连假模型」的全局配置
# --------------------------------------------------------------------------
if ($PrepareConfig) {
    @'
{
  "version": 1,
  "defaultProviderId": "fake-direct",
  "defaultModel": "fake-local-qwen",
  "recommendation": {
    "maxLookbackDays": 30,
    "defaultLookbackDays": 7,
    "temperature": 0.2,
    "timeoutSeconds": 60,
    "maxOutputTokens": 8192
  },
  "providers": [
    {
      "id": "fake-direct",
      "name": "假模型（本机直连，验证用）",
      "baseUrl": "http://127.0.0.1:1234/v1",
      "transport": "Direct",
      "enabled": true,
      "models": ["fake-local-qwen"],
      "note": "scripts/ai-e2e.ps1 用"
    }
  ]
}
'@ | Set-Content $configPath -Encoding UTF8

    Write-Output "已写入 $configPath"
    Write-Output ''
    Write-Output '现在用这个配置重启应用，再跑本脚本（不加参数）：'
    Write-Output "  `$env:Ai__ConfigFile=`"$configPath`""
    Write-Output '  dotnet run --project src\KidPlan.Web'
    return
}

# --------------------------------------------------------------------------
# 验证
# --------------------------------------------------------------------------
if (-not (Test-Path $jar)) { throw "找不到 $jar，请先跑 scripts/smoke.ps1。" }

try {
    $null = Invoke-RestMethod "http://127.0.0.1:$FakePort/v1/models" -TimeoutSec 3
} catch {
    throw "假模型服务没跑起来。先执行：pwsh -File scripts\fake-openai.ps1 -Port $FakePort"
}

$cookie = Get-AuthCookie -Jar $jar
Start-CdpBrowser -Work $work -Cookie $cookie

try {
    # 拿到冒烟数据里那个小孩的 Id（同时也是 cookie 对应的账号）。
    Navigate "$Base/children"
    $childId = Invoke-Js "(document.body.innerHTML.match(/\/children\/(\d+)\/body/) || [])[1] ?? null"
    if (-not $childId) { throw '拿不到 childId，先跑 scripts/smoke.ps1 造数据。' }
    Write-Output "childId = $childId"

    $owner = (Invoke-Mysql "SELECT OwnerId FROM Children WHERE Id=$childId;") -join ''
    $before = [int]((Invoke-Mysql "SELECT COUNT(*) FROM MealPlans WHERE ChildId=$childId AND CreatedFromAiSuggestion=1;") -join '').Trim()
    Write-Output "写入前：这个小孩名下由 AI 生成的饮食安排有 $before 条"

    # ----------------------------------------------------------------------
    # 1. 打开推荐面板
    # ----------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 1. 打开「AI 饮食推荐」面板 ==='
    Navigate "$Base/children/$childId/meals"
    if (-not (Wait-Text '固定三餐')) { throw '饮食安排页没渲染出来。' }

    $opened = Click-Until -UntilText '前置周期' -Script @'
(() => {
  const btn = [...document.querySelectorAll('button')]
      .find(b => b.innerText.includes('AI 饮食推荐'));
  if (!btn) return 'not-found';
  btn.click();
  return 'clicked';
})()
'@
    Assert-True $opened.Found '面板展开了（出现了「前置周期」）' '点了「AI 饮食推荐」但面板没展开'

    # 配置里写好了默认端点，页面应该直接选中它；没有就自己选一下。
    $selected = Invoke-Js @'
(() => {
  const t = document.body.innerText;
  return t.includes('假模型（本机直连，验证用）') || t.includes('fake-local-qwen');
})()
'@
    Assert-True $selected '页面上已经带出了配置里的默认端点与模型' '面板里没有看到配置的端点/模型'

    # 前置周期上限必须是配置里的 30，默认 7。
    $lookback = Invoke-Js @'
(() => {
  const m = document.body.innerText.match(/前置周期：\s*(\d+)\s*天/);
  return m ? m[1] : null;
})()
'@
    Write-Output "  前置周期当前值：$lookback 天（配置里 defaultLookbackDays=7、maxLookbackDays=30）"
    Assert-True ($lookback -eq '7') '前置周期默认值来自配置（7 天）' "前置周期默认值不对：$lookback"

    # 顺便确认滑块与手输框都在（需求里明确要求「滚动条和文本输入」两种方式）。
    # MudNumericField 渲染出来的 input 不是 type=number，所以按它的 label 找更靠谱。
    $slider = Invoke-Js "!!document.querySelector('.mud-slider')"
    $numberField = Invoke-Js @'
[...document.querySelectorAll('.mud-input-control')]
    .some(c => c.innerText.includes('前置周期（天）'))
'@
    Assert-True ($slider -and $numberField) '前置周期同时有滑块和输入框' "滑块=$slider，数字输入框=$numberField"

    # ----------------------------------------------------------------------
    # 2. 生成推荐
    # ----------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 2. 点「生成推荐」（真的调假模型） ==='

    $generated = Click-Until -UntilText '给出的建议' -Attempts 10 -GapMs 2000 -Script @'
(() => {
  const btn = [...document.querySelectorAll('button')]
      .find(b => b.innerText.includes('生成推荐'));
  if (!btn) return 'not-found';
  btn.click();
  return 'clicked';
})()
'@

    if (-not $generated.Found) {
        $script:failures++
        Write-Output '  [FAIL] 没有等到推荐结果'
        # 这里一定要把整页文字打出来。上一次就是只打了前 600 字，
        # 而错误提示恰好在面板更下面，结果白跑一轮还得重来。
        Write-Output "      页面文字：$(Invoke-Js -Expression "document.body.innerText.replace(/\s+/g,' ')")"
    } else {
        Write-Output '  [ OK ] 拿到了模型给出的建议'

        $text = Invoke-Js -Expression "document.body.innerText.replace(/\s+/g,' ')"

        Assert-True ($text -match '小米南瓜粥') '安全的那几条菜出现在结果里' '结果里没有出现假模型返回的菜名'
        Assert-True ($text -match '腰果炒虾仁') '含腰果的那条也列了出来（说明结果没被丢掉）' '含腰果的那条没有出现在结果里'
        Assert-True ($text -match '不能写入') '含过敏性休克级过敏原的那条被本地复核标成「不能写入」' '危险的那条没有被拦下'
        Assert-True ($text -match '全部写入饮食安排') '给了「全部写入饮食安排」按钮' '没有看到「全部写入」按钮'

        $shot = Save-Shot -Name 'ai-recommendation'
        Write-Output "  截图：$shot"

        # ------------------------------------------------------------------
        # 3. 一键写入
        # ------------------------------------------------------------------
        Write-Output ''
        Write-Output '=== 3. 点「全部写入饮食安排」 ==='

        $written = Click-Until -UntilText '已写入' -Attempts 8 -GapMs 1500 -Script @'
(() => {
  const btn = [...document.querySelectorAll('button')]
      .find(b => b.innerText.includes('全部写入饮食安排'));
  if (!btn) return 'not-found';
  btn.click();
  return 'clicked';
})()
'@
        Assert-True $written.Found '界面上给出了写入成功的提示' '点了「全部写入」之后没有成功提示'

        Start-Sleep -Milliseconds 1500
        $after = [int]((Invoke-Mysql "SELECT COUNT(*) FROM MealPlans WHERE ChildId=$childId AND CreatedFromAiSuggestion=1;") -join '').Trim()
        Write-Output "  写入后：由 AI 生成的饮食安排有 $after 条（之前 $before 条）"

        # 假模型给了 4 条：早/午/晚 + 一份点心，其中晚餐含腰果必须被拦下。
        # 所以期望是「写进去 3 条」而不是 4 条——这正是本地复核在起作用。
        $delta = $after - $before
        Assert-True ($delta -eq 3) "写进去了 3 条（4 条里含腰果那条被拦下），实际 $delta 条" "写入条数不对：期望 3，实际 $delta"

        $blocked = (Invoke-Mysql "SELECT COUNT(*) FROM MealPlans WHERE ChildId=$childId AND Menu LIKE '%腰果%';") -join ''
        Assert-True ($blocked.Trim() -eq '0') '数据库里没有出现含腰果的那一餐（本地复核真的拦住了）' "数据库里出现了 $blocked 条含腰果的安排"

        $shot2 = Save-Shot -Name 'ai-recommendation-imported'
        Write-Output "  截图：$shot2"

        # 落库的内容要能被页面读到（顺带验证不用刷新也能看到）。
        Navigate "$Base/children/$childId/meals"
        $null = Wait-Text -Needle '固定三餐'
        $hasBreakfast = Invoke-Js "document.body.innerText.includes('小米南瓜粥')"
        Assert-True $hasBreakfast '刷新后能在饮食安排里看到刚写进去的早餐' '刷新后在饮食安排里找不到刚写入的那一餐'
    }

    # ----------------------------------------------------------------------
    # 4. 推理模型把输出额度花光时，界面要给出能看懂的诊断
    #
    # 这条来自用户的真实反馈（「推理输出的内容好像不太对」）：
    # 他接的是一个推理模型，默认 max_tokens=2500 全被 reasoning_content 吃掉，
    # content 是空的、finish_reason=length，而当时的界面把**整包 HTTP 响应**
    # （含 2500 token 的英文思考链、usage、service_tier）铺出来叫「模型的原始输出」。
    #
    # 现在：默认额度提到 8192；把额度调小复现同一现场，界面必须说清是
    # 「额度全花在推理上」+ 给出「去调大最大输出 token」的入口，
    # 并且只展示整理过的诊断，不再铺整包报文。
    # ----------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 4. 推理额度耗尽：把「最大输出 token」调回 2500 ==='

    $budgetSet = $false

    try {
        Navigate "$Base/settings/ai"
        if (-not (Wait-Text '我的账号覆盖')) { throw '模型设置页没渲染出来。' }

        # 切到「我的账号覆盖」页签
        $null = Invoke-Js @'
(() => {
  const tab = [...document.querySelectorAll('[role="tab"], .mud-tab')]
      .find(t => t.innerText.includes('我的账号覆盖'));
  if (!tab) return false; tab.click(); return true;
})()
'@
        Start-Sleep -Milliseconds 1200

        # 打开「单独设置推荐参数」，露出「最大输出 token」
        $null = Invoke-Js @'
(() => {
  const sw = [...document.querySelectorAll('.mud-switch')]
      .find(s => s.innerText.includes('单独设置推荐参数'));
  if (!sw) return false;
  const input = sw.querySelector('input');
  if (input && !input.checked) { input.click(); }
  return true;
})()
'@
        Start-Sleep -Milliseconds 1000

        # 把 2500 写进「最大输出 token」。MudNumericField 的 input 不是 type=number，
        # 所以要按 label 找控件，再用原生 setter + input/change 事件通知 Blazor。
        $budgetSet = Invoke-Js @'
(() => {
  const ctl = [...document.querySelectorAll('.mud-input-control')]
      .find(c => (c.querySelector('label')?.innerText ?? '').startsWith('最大输出 token'));
  if (!ctl) return false;
  const inp = ctl.querySelector('input');
  if (!inp) return false;
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(inp, '2500');
  inp.dispatchEvent(new Event('input', { bubbles: true }));
  inp.dispatchEvent(new Event('change', { bubbles: true }));
  inp.blur();
  return true;
})()
'@
        Write-Output "  把「最大输出 token」改成 2500：$budgetSet"

        $null = Invoke-Js @'
(() => {
  const b = [...document.querySelectorAll('button')].find(x => x.innerText.includes('保存我的覆盖'));
  if (!b) return false; b.click(); return true;
})()
'@
        Start-Sleep -Milliseconds 2000

        Navigate "$Base/children/$childId/meals"
        if (-not (Wait-Text '固定三餐')) { throw '饮食安排页没渲染出来。' }

        $null = Invoke-Js @'
(() => {
  const btn = [...document.querySelectorAll('button')].find(b => b.innerText.includes('AI 饮食推荐'));
  if (!btn) return false; btn.click(); return true;
})()
'@
        Start-Sleep -Milliseconds 1500

        $failed = Click-Until -UntilText '最大输出 token' -Attempts 8 -GapMs 2000 -Script @'
(() => {
  const btn = [...document.querySelectorAll('button')].find(b => b.innerText.includes('生成推荐'));
  if (!btn) return 'not-found';
  btn.click();
  return 'clicked';
})()
'@

        $text = Invoke-Js -Expression "document.body.innerText.replace(/\s+/g,' ')"
        Write-Output "  页面文字（前 300 字）：$(if ($text.Length -gt 300) { $text.Substring(0, 300) } else { $text })"

        Assert-True $failed.Found '额度不够时给出了明确的失败提示' '没有等到任何提示'
        Assert-True ($text -match '全花在推理上') '说清了「额度被推理吃掉、没有正文」' '没有点明是推理把额度用光了'
        Assert-True ($text -match 'finish_reason=length') '诊断里带上了 finish_reason' '诊断里没有 finish_reason'
        Assert-True ($text -match '全是推理过程，没有正文') '诊断说明了这次只有推理、没有正文' '诊断没有说明正文为空'
        Assert-True ($text -match '去模型设置调大') '给了「去模型设置调大最大输出 token」的按钮' '没有给可操作的入口'

        # 关键回归：整包报文不该再铺到页面上。
        Assert-True (-not ($text -match 'service_tier')) '页面上没有再铺整包 HTTP 响应（没有 service_tier）' '页面上又出现了整包报文（service_tier）'
        Assert-True (-not ($text -match 'completion_tokens_details')) '页面上没有 completion_tokens_details 这种信封字段' '页面上出现了信封字段'

        $shot3 = Save-Shot -Name 'ai-reasoning-budget'
        Write-Output "  截图：$shot3"
    } finally {
        # 把覆盖清掉，免得残留一个 2500 影响后面的运行。
        if ($budgetSet) {
            Navigate "$Base/settings/ai"
            Start-Sleep -Milliseconds 800
            # 重新打开页面会回到第一个页签（管理员是「全局配置」），
            # 而「清除覆盖」在「我的账号覆盖」里——先切页签再点，否则永远找不到按钮。
            $null = Invoke-Js @'
(() => {
  const tab = [...document.querySelectorAll('[role="tab"], .mud-tab')]
      .find(t => t.innerText.includes('我的账号覆盖'));
  if (!tab) return false; tab.click(); return true;
})()
'@
            Start-Sleep -Milliseconds 1200
            $cleared = Invoke-Js @'
(() => {
  const b = [...document.querySelectorAll('button')].find(x => x.innerText.includes('清除覆盖'));
  if (!b) return false; b.click(); return true;
})()
'@
            Start-Sleep -Milliseconds 1500
            Write-Output "  已清除账号覆盖：$cleared"
        }
    }

} finally {
    Stop-CdpBrowser
}

Write-Output ''
if ($script:failures -eq 0) { Write-Output 'AI 饮食推荐链路验证全部通过。' } else { Write-Output "有 $($script:failures) 项失败。"; exit 1 }
