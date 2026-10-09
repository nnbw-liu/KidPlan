# =============================================================================
# 本机中继端到端验证（真实进程 + 真实 WebSocket + 真实 HTTP）
#
# 验的是需求里的第 (7) 条：应用部署在云端，模型跑在自己家里的电脑上，
# 云端**连不到**家里的 127.0.0.1。方案是让家里的电脑主动连出去挂长连接，
# 服务器把模型请求顺着这条连接发下来。
#
# 这里把两个进程真的跑起来：
#
#   浏览器 ──> KidPlan.Web（服务端，假装在云上）
#                   │  长连接（真实 WebSocket）
#                   ▼
#              KidPlan.Relay（假装在家里）
#                   │  真实 HTTP
#                   ▼
#              假模型服务（scripts/fake-openai.ps1，监听 127.0.0.1:1234）
#
# 关键设计是**先做一次反证**：中继没启动时，「测试连接」必须是失败的。
# 因为 127.0.0.1:1234 在这台机器上其实**直连也能通**，如果这时它还成功，
# 就说明请求根本没走中继、这个验证毫无意义。反过来它也证明了
# transport=LocalRelay 这个配置真的生效了（没有被 Direct 兜底）。
#
# 前置：
#   1) pwsh -File scripts\fake-openai.ps1 -Port 1234
#   2) 应用跑在 http://localhost:5195，且 Ai__ConfigFile 指向
#      %TEMP%\kidplan-e2e\ai-relay.json（-Prepare 会把这份配置写出来，
#      里面有一个 transport=LocalRelay 的端点）
#   3) 第一次先跑 -Prepare（造账号 / 令牌 / 节点 / 应用侧配置）
#
# 用法：
#   pwsh -File scripts\relay-e2e.ps1 -Prepare
#   pwsh -File scripts\relay-e2e.ps1
#   pwsh -File scripts\relay-e2e.ps1 -Cleanup
# =============================================================================
[CmdletBinding()]
param(
    [switch]$Prepare,
    [switch]$Cleanup,
    [string]$Base = 'http://localhost:5195',
    [string]$Root = 'C:\Code\SDemo\KidPlan'
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\cdp-lib.ps1"

$work = Join-Path $env:TEMP 'kidplan-e2e'
New-Item -ItemType Directory -Force -Path $work | Out-Null

$mysql = $env:KIDPLAN_MYSQL
if (-not $mysql) {
    $bundled = 'C:\Code\SDemo\_tools\mysql\server\bin\mysql.exe'
    $mysql = if (Test-Path $bundled) { $bundled } else { 'mysql' }
}

$email = 'relay-e2e@example.com'
$password = 'RelayE2e!2026'
$tokenFile = Join-Path $work 'relay-token.txt'
$ownerFile = Join-Path $work 'relay-owner.txt'
$relayLog = Join-Path $work 'relay.log'
$fakeLog = Join-Path $work 'fake-openai.log'
$jar = Join-Path $work 'relay-cookies.txt'

# 中继的配置文件。ResolveConfigPath() 优先用 exe 旁边的这一份，
# 所以把 server / token / logFile 都写进去，启动时就不用在命令行里带参数了
# （顺带也验证了「配置文件能被正确读取」这条路）。
$relayConfig = Join-Path $Root 'src\KidPlan.Relay\bin\Debug\net10.0\relay.json'

# 应用侧的全局配置（属于本脚本的前置条件，由 -Prepare 生成）。
# 跑之前要把应用启动成 Ai__ConfigFile 指向它。
$aiConfig = Join-Path $work 'ai-relay.json'

$script:failures = 0
$script:relayJob = $null

function Invoke-Mysql([string]$sql) {
    return (& $mysql -h 127.0.0.1 -P 3306 -u root kidplan --default-character-set=utf8mb4 -N -B -e $sql) 2>&1
}

function Get-RelayLog {
    # 中继把日志同时写到 logFile 和控制台。以日志文件为准，
    # 只有文件还没建起来（进程起不来 / 崩在前面）时才回落到读 job 输出，
    # 否则同一段日志会被拼两遍，看不出哪条是新的。
    if (Test-Path $relayLog) {
        $text = Get-Content $relayLog -Raw
        if ($text -and $text.Trim()) { return $text }
    }
    if ($script:relayJob) {
        return ((Receive-Job -Job $script:relayJob -Keep 2>&1) -join "`n")
    }
    return ''
}

function Assert-True([bool]$condition, [string]$ok, [string]$fail) {
    if ($condition) { Write-Output "  [ OK ] $ok" } else { $script:failures++; Write-Output "  [FAIL] $fail" }
}

# --------------------------------------------------------------------------
# -Prepare：注册账号、生成令牌、插入中继节点
# --------------------------------------------------------------------------
if ($Prepare) {
    Remove-Item $jar -ErrorAction SilentlyContinue

    $html = (& curl.exe -s -c $jar "$Base/Account/Register") -join "`n"
    $antiforgery = [regex]::Match($html, '<input[^>]*name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    $handler = [regex]::Match($html, '<input[^>]*name="_handler"[^>]*value="([^"]*)"').Groups[1].Value

    $result = & curl.exe -s -o (Join-Path $work 'register.html') -w '%{http_code}' -b $jar -c $jar `
        -X POST "$Base/Account/Register" `
        --data-urlencode "_handler=$handler" `
        --data-urlencode "__RequestVerificationToken=$antiforgery" `
        --data-urlencode 'Input.DisplayName=中继验证' `
        --data-urlencode "Input.Email=$email" `
        --data-urlencode "Input.Password=$password" `
        --data-urlencode "Input.ConfirmPassword=$password"

    Write-Output "注册：HTTP $result"
    if ($result -notmatch '^30') { throw '注册没有跳转，账号可能已存在（先跑 -Cleanup）。' }

    # 自己造一个令牌并直接插库：这条链路要验的是「连接 + 转发」；
    # 令牌的生成与界面呈现已经被单元测试和 scripts/cdp.ps1 覆盖了。
    $token = 'kpr_' + (([Convert]::ToBase64String(
        [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) -replace '\+', '-' -replace '/', '_' -replace '=', '')
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $hash = [Convert]::ToHexString(
        $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($token))).ToLowerInvariant()

    Set-Content $tokenFile -Value $token -Encoding UTF8

    $owner = (Invoke-Mysql "SELECT Id FROM AspNetUsers WHERE Email='$email';") -join ''
    if (-not $owner) { throw '库里找不到刚注册的账号。' }
    Set-Content $ownerFile -Value $owner -Encoding UTF8

    $prefix = $token.Substring(0, 12)
    $null = Invoke-Mysql @"
DELETE FROM RelayNodes WHERE OwnerId='$owner';
INSERT INTO RelayNodes (OwnerId, Name, TokenHash, TokenPrefix, Enabled, CreatedAt)
VALUES ('$owner', '家里的台式机', '$hash', '$prefix', 1, NOW(6));
"@

    Write-Output "ownerId = $owner"
    Write-Output "令牌前缀 = $prefix"

    # 应用侧的全局配置：必须**先**有一个 transport=LocalRelay 的端点，
    # 后面点「测试连接」才有东西可测。这份文件以前是手工准备的，
    # 结果从干净环境跑起来第一步就必然失败（页面上「还没有配置任何端点」），
    # 所以改成 -Prepare 直接生成——它本来就是这一步的前置条件。
    @{
        version        = 1
        defaultProviderId = 'local-relay'
        defaultModel   = 'fake-model'
        recommendation = @{
            maxLookbackDays    = 30
            defaultLookbackDays = 7
            temperature        = 0.4
            timeoutSeconds     = 180
            # 与新默认值一致：推理模型会把 2500 全花在思考过程上、正文为空。
            # 这里只做「测试连接 / 试调用」，但配置值保持和线上默认一致更好排查。
            maxOutputTokens    = 8192
        }
        providers      = @(
            @{
                id        = 'local-relay'
                name      = '家里的模型（走中继）'
                baseUrl   = 'http://127.0.0.1:1234/v1'
                apiKey    = ''
                transport = 'LocalRelay'   # 关键：不许直连兜底
                enabled   = $true
                models    = @('fake-model')
                note      = '中继验证用；127.0.0.1:1234 是假模型服务'
            }
        )
    } | ConvertTo-Json -Depth 6 | Set-Content $aiConfig -Encoding UTF8

    Write-Output "应用侧配置 = $aiConfig（含一个 LocalRelay 端点）"
    Write-Output '准备完成。'
    return
}

# --------------------------------------------------------------------------
# -Cleanup
# --------------------------------------------------------------------------
if ($Cleanup) {
    $owner = if (Test-Path $ownerFile) { (Get-Content $ownerFile -Raw).Trim() } else { '' }
    if ($owner) { $null = Invoke-Mysql "DELETE FROM RelayNodes WHERE OwnerId='$owner';" }
    $null = Invoke-Mysql "DELETE FROM AspNetUsers WHERE Email='$email';"

    # 中继启动时会把 server / token 记进 relay.json，里面有令牌，跑完要删掉。
    Remove-Item $relayConfig -ErrorAction SilentlyContinue
    Remove-Item $tokenFile, $ownerFile, $jar -ErrorAction SilentlyContinue

    Write-Output (Invoke-Mysql "SELECT (SELECT COUNT(*) FROM AspNetUsers WHERE Email='$email') AS 残留账号, (SELECT COUNT(*) FROM RelayNodes) AS 中继节点;" | Out-String)
    return
}

# --------------------------------------------------------------------------
# 验证
# --------------------------------------------------------------------------
foreach ($required in @($tokenFile, $ownerFile, $jar)) {
    if (-not (Test-Path $required)) {
        throw "缺少 $required，先跑：pwsh -File scripts\relay-e2e.ps1 -Prepare"
    }
}

try {
    $null = Invoke-RestMethod 'http://127.0.0.1:1234/v1/models' -TimeoutSec 3
} catch {
    throw '假模型服务没跑起来。先执行：pwsh -File scripts\fake-openai.ps1 -Port 1234'
}

$owner = (Get-Content $ownerFile -Raw).Trim()
$token = (Get-Content $tokenFile -Raw).Trim()

$cookie = Get-AuthCookie -Jar $jar
Start-CdpBrowser -Work $work -Cookie $cookie

try {
    Navigate "$Base/settings/ai"
    if (-not (Wait-Text -Needle '全局配置')) { throw '模型设置页没渲染出来。' }

    $testButton = @'
(() => {
  const btn = [...document.querySelectorAll('button')]
      .find(b => b.innerText.trim() === '测试连接');
  if (!btn) return 'not-found';
  btn.click();
  return 'clicked';
})()
'@

    # ----------------------------------------------------------------------
    # 1. 反证：中继没启动，必须失败
    # ----------------------------------------------------------------------
    Write-Output '=== 1. 反证：中继没启动时，「测试连接」必须失败 ==='
    Write-Output '    （127.0.0.1:1234 在这台机器上直连是通的——如果这里成功，说明根本没走中继）'

    $beforeModels = ([regex]::Matches(
        (Get-Content $fakeLog -Raw -ErrorAction SilentlyContinue), 'GET /v1/models')).Count

    $click = Click-Until -UntilText '没能拉到模型列表' -Script $testButton

    if ($click.Found) {
        $message = Invoke-Js -Expression @'
(() => {
  const t = document.body.innerText;
  const i = t.indexOf('没能拉到模型列表');
  return t.slice(i, i + 120).replace(/\s+/g, ' ');
})()
'@
        Write-Output "  提示：$message"
        Assert-True ($message -match '中继') `
            '确实是因为「没有中继在线」而失败 —— transport=LocalRelay 生效了' `
            '失败了，但原因不是「没有中继在线」'
    } else {
        $script:failures++
        Write-Output '  [FAIL] 点了「测试连接」没有等到任何结果提示'
    }

    $afterModels = ([regex]::Matches((Get-Content $fakeLog -Raw), 'GET /v1/models')).Count
    Assert-True ($afterModels -eq $beforeModels) `
        '假模型服务一次请求都没收到（请求确实没绕过中继直连）' `
        "假模型服务收到了 $($afterModels - $beforeModels) 次请求，说明请求没走中继"

    # ----------------------------------------------------------------------
    # 2. 启动中继，等它连上
    # ----------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 2. 启动 KidPlan.Relay（模拟家里那台电脑） ==='

    Remove-Item $relayLog, $relayConfig -ErrorAction SilentlyContinue
    @{
        server                = $Base
        token                 = $token
        heartbeatSeconds      = 5      # 心跳密一点，「被踢掉」能更快看到
        requestTimeoutSeconds = 60
        logFile               = $relayLog
        allowPublicTargets    = $false
    } | ConvertTo-Json | Set-Content $relayConfig -Encoding UTF8
    Write-Output "  写入中继配置：$relayConfig"

    # 先把上次跑留下的在线痕迹擦掉。否则「LastSeenAt 非空」这个判断
    # 在上一次的时间戳上直接为真，会误判成「已上线」，然后再去读一份空日志——
    # 表现出来就是莫名其妙的「中继日志里没有『已连接』」。
    $null = Invoke-Mysql "UPDATE RelayNodes SET LastSeenAt=NULL, MachineName=NULL, AgentVersion=NULL WHERE OwnerId='$owner';"

    # 不带任何命令行参数启动：server / token 全从配置文件读。
    $script:relayJob = Start-Job -ScriptBlock {
        param($root)
        Set-Location $root
        & dotnet run --project src\KidPlan.Relay\KidPlan.Relay.csproj --no-build 2>&1
    } -ArgumentList $Root

    $startedAt = Get-Date
    $online = $false
    foreach ($i in 1..40) {
        Start-Sleep -Milliseconds 1000
        $seen = ((Invoke-Mysql "SELECT LastSeenAt FROM RelayNodes WHERE OwnerId='$owner';") -join '').Trim()
        if ($seen -and $seen -ne 'NULL') {
            # 再确认一次这个时间戳是本次写进去的，不是残留。
            if ([datetime]$seen -ge $startedAt.AddSeconds(-5)) { $online = $true; break }
        }
    }

    if ($online) {
        $seen = ((Invoke-Mysql "SELECT LastSeenAt FROM RelayNodes WHERE OwnerId='$owner';") -join '').Trim()
        $machine = ((Invoke-Mysql "SELECT MachineName FROM RelayNodes WHERE OwnerId='$owner';") -join '').Trim()
        $version = ((Invoke-Mysql "SELECT AgentVersion FROM RelayNodes WHERE OwnerId='$owner';") -join '').Trim()
        Write-Output "  [ OK ] 节点已上线：LastSeenAt=$seen，机器=$machine，中继版本=$version（心跳已落库）"
    } else {
        $script:failures++
        Write-Output '  [FAIL] 节点一直没有上线。中继日志：'
        Get-RelayLog | Select-Object -Last 25 | ForEach-Object { Write-Output "      $_" }
        throw '中继没连上，后续验证没有意义。'
    }

    $log = Get-RelayLog
    Assert-True ($log -match '已连接') '中继日志里有「已连接」' '中继日志里没有「已连接」'
    Assert-True ($log -match '服务器已确认节点') '中继日志里有「服务器已确认节点」（握手完成）' '没有收到 server-hello'

    # ----------------------------------------------------------------------
    # 3. 正向：再测一次，必须成功，而且请求要真的到过假模型服务
    # ----------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 3. 再点一次「测试连接」（这次应该成功） ==='

    # 重新加载，把上一次的错误提示清掉，免得读到旧文本。
    Navigate "$Base/settings/ai"
    $null = Wait-Text -Needle '全局配置'

    $click = Click-Until -UntilText '试调用' -Script $testButton

    if ($click.Found) {
        $message = Invoke-Js -Expression @'
(() => {
  const t = document.body.innerText;
  const i = t.indexOf('拉到');
  return t.slice(Math.max(0, i - 10), i + 120).replace(/\s+/g, ' ');
})()
'@
        Write-Output "  提示：$message"
        Assert-True ($message -match 'fake-local-qwen' -and $message -match '可以') `
            '拉到了模型列表，并且试调用拿到了假模型的回复「可以」' `
            '提示里没有出现模型名或回复内容'

        $fakeLogText = Get-Content $fakeLog -Raw
        $chatCount = ([regex]::Matches($fakeLogText, 'POST /v1/chat/completions')).Count
        $modelCount = ([regex]::Matches($fakeLogText, 'GET /v1/models')).Count
        Write-Output "  假模型服务累计收到：GET /v1/models ×$modelCount，POST /v1/chat/completions ×$chatCount"
        Assert-True ($chatCount -ge 1 -and $modelCount -ge 2) `
            '请求真的落到了「家里」那台机器上的模型服务' `
            '假模型服务没有收到预期的请求'

        $shot = Save-Shot -Name 'relay-test-connection'
        Write-Output "  截图：$shot"
    } else {
        $script:failures++
        Write-Output '  [FAIL] 没有等到「试调用」成功的提示'
        Write-Output "      页面文字：$(Invoke-Js -Expression "document.body.innerText.replace(/\s+/g,' ').slice(0,400)")"
    }

    # ----------------------------------------------------------------------
    # 4. 中继自己的日志里应该能看到它去请求 127.0.0.1:1234
    # ----------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 4. 中继进程的转发日志 ==='
    Start-Sleep -Milliseconds 1500
    $log = Get-RelayLog

    $forwarded = $log -match 'GET http://127\.0\.0\.1:1234/v1/models' -or
                 $log -match 'POST http://127\.0\.0\.1:1234/v1/chat/completions'
    Assert-True $forwarded `
        '中继日志里能看到它自己去请求 127.0.0.1:1234（说明请求是在「家里」发出去的）' `
        '中继日志里没有转发记录'

    Write-Output '  ---- 中继日志（最后 12 行）----'
    Get-RelayLog | Select-Object -Last 12 | ForEach-Object { Write-Output "    $_" }

    # ----------------------------------------------------------------------
    # 5. 在界面上停用节点 → 长连接必须被立刻踢掉
    #
    # 这一步必须走界面（而不是 SQL UPDATE）：踢连接发生在
    # RelayNodeService.SetEnabledAsync 里，直接改库只是改了个字段，
    # 长连接会一直挂着，「禁用」就成了假的。
    # ----------------------------------------------------------------------
    Write-Output ''
    Write-Output '=== 5. 在界面上停用节点，长连接必须被立刻踢掉 ==='
    Navigate "$Base/settings/relay"
    $null = Wait-Text -Needle '家里的台式机'

    $toggled = Click-Until -UntilText '连接已经断开' -Attempts 8 -Script @'
(() => {
  const sw = [...document.querySelectorAll('.mud-switch')]
      .find(e => e.innerText.includes('启用'));
  if (!sw) return 'not-found';
  (sw.querySelector('input[type=checkbox]') || sw).click();
  return 'clicked';
})()
'@

    Assert-True $toggled.Found `
        '界面上给出了「正在挂着的连接已经断开」的提示' `
        '界面没有给出停用成功的提示'

    $kicked = $false
    foreach ($i in 1..15) {
        Start-Sleep -Milliseconds 1000
        if ((Get-RelayLog) -match '连接中断|服务器关闭了连接|已停止') { $kicked = $true; break }
    }

    Assert-True $kicked `
        '中继侧也观察到了连接被断开（不是只改了个字段）' `
        '15 秒内没在中继侧看到断开'

    # 收拾干净：把节点重新启用，免得下次跑的时候状态不对。
    $null = Invoke-Mysql "UPDATE RelayNodes SET Enabled=1 WHERE OwnerId='$owner';"

} finally {
    Stop-CdpBrowser
    if ($script:relayJob) {
        Stop-Job $script:relayJob -ErrorAction SilentlyContinue
        Remove-Job $script:relayJob -Force -ErrorAction SilentlyContinue
    }
}

Write-Output ''
if ($script:failures -eq 0) { Write-Output '中继端到端验证全部通过。' } else { Write-Output "有 $($script:failures) 项失败。"; exit 1 }
