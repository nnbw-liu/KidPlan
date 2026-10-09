# =============================================================================
# 无头 Edge + CDP 的公共部分
#
# 被 scripts/cdp.ps1（交互行为验证）和 scripts/relay-e2e.ps1（中继端到端）共用，
# 所以单独抽出来。dot-source 使用：
#
#   . "$PSScriptRoot\cdp-lib.ps1"
#   $cookie = Get-AuthCookie -Jar $jar
#   Start-CdpBrowser -Work $work
#   ...
#
# 状态放在 $global:KidPlanCdp 里：函数定义在被 dot-source 的文件中，
# 而 $script: 在跨文件调用时指向哪个作用域容易记错，用一个明确命名的全局变量最省心。
# =============================================================================

$global:KidPlanCdp = @{
    Ws      = $null
    MsgId   = 0
    Edge    = $null
    Work    = $null
}

function Get-AuthCookie {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Jar)

    foreach ($line in Get-Content $Jar) {
        # curl 把 HttpOnly 的 Cookie 写成 "#HttpOnly_localhost\t..."，
        # 直接按 '#' 开头跳过会把它一起丢掉——而 KidPlan.Auth 恰恰就是 HttpOnly。
        $line = $line -replace '^#HttpOnly_', ''
        if ($line.StartsWith('#') -or -not $line.Trim()) { continue }

        $parts = $line -split "`t"
        if ($parts.Count -ge 7 -and $parts[5] -eq 'KidPlan.Auth') {
            return @{ Name = $parts[5]; Value = $parts[6]; Domain = $parts[0] }
        }
    }

    throw "$Jar 里没有 KidPlan.Auth，先确认登录成功了。"
}

function Find-Edge {
    $candidates = @(
        'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
        'C:\Program Files\Microsoft\Edge\Application\msedge.exe'
    )
    # 注意最外层的 @()：只匹配到一个路径时，Where-Object 返回的是字符串而不是数组，
    # 于是 $edges[0] 会取到第一个字符 'C'，Start-Process 报「找不到文件 'C'」。
    $found = @($candidates | Where-Object { Test-Path $_ })
    if (-not $found) { throw '找不到 msedge.exe。' }
    return $found[0]
}

function Send-Cdp {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Method,
        $Params = @{}
    )

    $global:KidPlanCdp.MsgId++
    $id = $global:KidPlanCdp.MsgId
    $ws = $global:KidPlanCdp.Ws

    $payload = @{ id = $id; method = $Method; params = $Params } | ConvertTo-Json -Depth 12 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $null = $ws.SendAsync(
        [ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None
    ).GetAwaiter().GetResult()

    # 事件通知直接丢掉，只等自己那条命令的响应。
    while ($true) {
        $buffer = New-Object byte[] 1048576
        $stream = New-Object System.IO.MemoryStream
        do {
            $result = $ws.ReceiveAsync(
                [ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None
            ).GetAwaiter().GetResult()
            if ($result.Count -gt 0) { $stream.Write($buffer, 0, $result.Count) }
        } while (-not $result.EndOfMessage)

        $text = [System.Text.Encoding]::UTF8.GetString($stream.ToArray())
        $obj = $text | ConvertFrom-Json
        if ($obj.id -eq $id) { return $obj }
    }
}

function Invoke-Js {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Expression)

    $r = Send-Cdp -Method 'Runtime.evaluate' -Params @{
        expression    = $Expression
        returnByValue = $true
        awaitPromise  = $true
    }
    if ($r.result.exceptionDetails) {
        throw "JS 执行出错：$($r.result.exceptionDetails.text)"
    }
    return $r.result.result.value
}

function Save-Shot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $r = Send-Cdp -Method 'Page.captureScreenshot' -Params @{ format = 'png'; captureBeyondViewport = $false }
    $path = Join-Path $global:KidPlanCdp.Work "$Name.png"
    [System.IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($r.result.data))
    return $path
}

function Start-CdpBrowser {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Work,
        [hashtable]$Cookie,
        [int]$Port = 9222
    )

    $global:KidPlanCdp.Work = $Work

    $profile = Join-Path $Work "edge-profile-$Port"
    Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue

    $edge = Find-Edge
    $global:KidPlanCdp.Edge = Start-Process -FilePath $edge -PassThru -ArgumentList @(
        '--headless=new',
        "--remote-debugging-port=$Port",
        "--user-data-dir=$profile",
        '--no-first-run',
        '--no-default-browser-check',
        '--disable-gpu',
        '--window-size=1500,1200',
        'about:blank'
    )

    $targets = $null
    foreach ($i in 1..40) {
        Start-Sleep -Milliseconds 500
        try {
            $targets = Invoke-RestMethod "http://127.0.0.1:$Port/json/list" -TimeoutSec 3
            if ($targets) { break }
        } catch { }
    }
    if (-not $targets) { throw "连不上 Edge 的调试端口 $Port。" }

    $page = $targets | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $null = $ws.ConnectAsync(
        [Uri]$page.webSocketDebuggerUrl, [Threading.CancellationToken]::None
    ).GetAwaiter().GetResult()
    $global:KidPlanCdp.Ws = $ws

    $null = Send-Cdp -Method 'Page.enable'
    $null = Send-Cdp -Method 'Runtime.enable'
    $null = Send-Cdp -Method 'Network.enable'

    # 把 curl 拿到的登录 Cookie 塞进浏览器，省掉用 JS 填表单登录这一步。
    if ($Cookie) {
        $null = Send-Cdp -Method 'Network.setCookie' -Params @{
            name   = $Cookie.Name
            value  = $Cookie.Value
            domain = 'localhost'
            path   = '/'
        }
    }
}

function Stop-CdpBrowser {
    if ($global:KidPlanCdp.Ws) { $global:KidPlanCdp.Ws.Dispose() }

    $work = $global:KidPlanCdp.Work
    $edge = $global:KidPlanCdp.Edge
    if ($edge -and -not $edge.HasExited) { Stop-Process -Id $edge.Id -Force -ErrorAction SilentlyContinue }

    # Edge 会 fork 出一堆子进程（渲染器 / GPU / utility / crashpad）。
    # 只杀 Start-Process 拿到的那个父进程，剩下的会变成孤儿一直留在后台——
    # 跑几次验证就能攒下几十个进程。这里按「命令行里带着本次的临时 profile 目录」
    # 精确地收尾，绝不去碰用户自己开的浏览器（它们的命令行里没有这个路径）。
    if ($work) {
        $stray = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine -like "*$work*" })

        foreach ($p in $stray) {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}

function Navigate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Url,
        [int]$SettleMs = 3500
    )
    $null = Send-Cdp -Method 'Page.navigate' -Params @{ url = $Url }
    Start-Sleep -Milliseconds $SettleMs
}

# 等页面出现某段文字。注意它可能在**预渲染**阶段就为真（那时交互还没接上），
# 所以「点了按钮要看效果」的地方要配合重试，不能只点一次就等。
function Wait-Text {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Needle,
        [int]$TimeoutMs = 15000
    )

    # 用 ConvertTo-Json 把一个字符串变成合法的 JS 字面量，省得自己转义引号。
    $literal = $Needle | ConvertTo-Json -Compress
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        if (Invoke-Js -Expression "document.body.innerText.includes($literal)") { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

# 反复点，直到页面出现某段文字或放弃。
# 处理的是这个竞态：预渲染的 HTML 里已经有页面文字，但 SignalR 还没连上，
# 此时点按钮没人接（点了等于没点）。
function Click-Until {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Script,
        [Parameter(Mandatory)][string]$UntilText,
        [int]$Attempts = 12,
        [int]$GapMs = 600
    )

    $clicked = 'not-found'
    foreach ($i in 1..$Attempts) {
        $clicked = Invoke-Js -Expression $Script
        Start-Sleep -Milliseconds $GapMs
        if (Wait-Text -Needle $UntilText -TimeoutMs 400) { return @{ Clicked = $clicked; Found = $true } }
    }
    return @{ Clicked = $clicked; Found = $false }
}
