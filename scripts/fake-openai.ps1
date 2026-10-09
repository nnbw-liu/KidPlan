# =============================================================================
# 假的 OpenAI 兼容模型服务（只为本机验证用）
#
# 两个用途：
#   1) 中继链路（scripts/relay-e2e.ps1）：证明「请求确实从家里那台机器发出去的」；
#   2) AI 饮食推荐的界面链路（scripts/ai-e2e.ps1）：真的返回一份菜单，
#      让「生成 → 本地过敏复核 → 一键写入饮食安排」这条路能整条走一遍。
#
# 所以它按请求内容分两种回复：
#   · 提示词里有 "meals"（就是推荐请求）→ 返回一份结构化的菜单 JSON；
#   · 其他（「测试连接」发的是「请只回复两个字：可以」）→ 返回「可以」。
#
# 服务只实现两个接口：
#   GET  /v1/models            → 一个模型名的列表
#   POST /v1/chat/completions  → 上面两种回复之一
#
# 每个请求都会追加写进日志文件，用来证明「请求确实到过这里」。
#
# 用法：
#   pwsh -File scripts\fake-openai.ps1 -Port 1234 -LogFile "$env:TEMP\kidplan-e2e\fake-openai.log"
# =============================================================================
param(
    [int]$Port = 1234,
    [string]$LogFile = (Join-Path $env:TEMP 'kidplan-e2e\fake-openai.log'),
    [string]$ModelName = 'fake-local-qwen',
    [string]$Reply = '可以'
)

$ErrorActionPreference = 'Stop'

# 只留一个回复，省得日志越滚越大。
$logDir = Split-Path -Parent $LogFile
if ($logDir) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }
Set-Content -Path $LogFile -Value "fake-openai 启动，端口 $Port" -Encoding UTF8

function Write-Log([string]$line) {
    $stamp = (Get-Date).ToString('HH:mm:ss.fff')
    Add-Content -Path $LogFile -Value "$stamp $line" -Encoding UTF8
    Write-Host "$stamp $line"
}

# 一份「看起来像真模型给的」菜单。
#
# 刻意混进一条含**腰果**的菜：冒烟数据里腰果是「过敏性休克」级，
# 应用在拿到模型输出后还会用本地过敏原再查一遍，这条必须被拦下、不许写入。
# 这样一次就能验证两件事：安全的那几条真的能写进去，危险的真的写不进去。
#
# 不写 date 字段是有意的：应用在 date 缺失时会回落到「用户要求的那一天」，
# 免得假服务还得去解析提示词里的日期。
function Get-FakeMenu {
    $menu = @{
        summary = '以易消化、低致敏为主，搭配当季蔬菜；牛奶只在耐受范围内少量出现。'
        meals   = @(
            @{
                mealType = 'Breakfast'
                time     = '07:30'
                menu     = '小米南瓜粥 + 蒸蛋 + 清炒西兰花'
                foods    = '小米、南瓜、鸡蛋、西兰花'
                calories = 320
                reason   = '温和好消化，鸡蛋是已登记的强过敏原，这里只用水煮蛋黄少量尝试。'
            },
            @{
                mealType = 'Lunch'
                time     = '12:00'
                menu     = '软米饭 + 番茄炖牛肉 + 蒜蓉油麦菜'
                foods    = '大米、番茄、牛肉、油麦菜'
                calories = 480
                reason   = '补铁补蛋白，番茄的酸味能盖住牛肉的腥气。'
            },
            @{
                mealType = 'Dinner'
                time     = '18:00'
                menu     = '腰果炒虾仁 + 紫菜蛋花汤'
                foods    = '腰果、虾仁、紫菜'
                calories = 430
                reason   = '这条是故意放进来测试本地过敏复核的，应该被拦下。'
            },
            @{
                mealType = 'Snack'
                time     = '15:30'
                menu     = '蒸苹果'
                foods    = '苹果'
                calories = 90
                reason   = '下午加餐，补一点果胶。'
            }
        )
    }

    return ($menu | ConvertTo-Json -Depth 8 -Compress)
}

# HttpListener 在前缀含通配符时需要 URL ACL；绑定到 127.0.0.1 这个具体地址
# 普通用户就能监听，所以中继那边也必须填 127.0.0.1（这正好就是它的典型用法）。
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-Log "已监听 http://127.0.0.1:$Port/"

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request = $context.Request
        $response = $context.Response

        $path = $request.Url.AbsolutePath
        $body = ''
        if ($request.HasEntityBody) {
            $reader = [System.IO.StreamReader]::new($request.InputStream, [System.Text.Encoding]::UTF8)
            $body = $reader.ReadToEnd()
            $reader.Dispose()
        }

        # 把请求体也记一行（截断），验证时能看出应用发的提示词有没有真的过来。
        Write-Log "$($request.HttpMethod) $path  ← $($request.RemoteEndPoint)  body=$([Math]::Min($body.Length, 200)) 字节"

        $json = $null
        $status = 200

        if ($path -eq '/v1/models' -and $request.HttpMethod -eq 'GET') {
            $json = @{
                object = 'list'
                data   = @(@{ id = $ModelName; object = 'model'; owned_by = 'kidplan-fake' })
            } | ConvertTo-Json -Depth 5 -Compress
        }
        elseif ($path -eq '/v1/chat/completions' -and $request.HttpMethod -eq 'POST') {
            # 怎么判断这是不是一个「推荐」请求？
            #
            # 推荐请求的提示词里带着输出格式，里面一定有 meals。但**不能**去匹配
            # "\"meals\""：System.Text.Json 默认会把引号转义成 \u0022（为了能安全
            # 嵌进 HTML），实际发过来的是 \u0022meals\u0022。这里只匹配单词本身，
            # 而「测试连接」发的「请只回复两个字：可以」里没有 meals，不会误判。
            $isRecommendation = $body -match 'meals'

            # ---- 复现「推理模型把额度全花在思考上」这件事 ----
            #
            # 这是用户真实遇到的现场：max_tokens=2500 时，推理模型把 2500 个 token
            # 全用在 reasoning_content 上，content 是空字符串，finish_reason=length，
            # 一次推荐什么都没生成。
            # 这里按请求里的 max_tokens 复现同一条路径：额度小（≤2500）就只回推理、
            # 不给正文。这样 scripts/ai-e2e.ps1 能用真实请求验证界面上的诊断文案。
            $maxTokens = 0
            if ($body -match '"max_tokens"\s*:\s*(\d+)') { $maxTokens = [int]$Matches[1] }

            if ($isRecommendation -and $maxTokens -gt 0 -and $maxTokens -le 2500) {
                Write-Log "  → 复现推理耗尽：max_tokens=$maxTokens，只回 reasoning_content、不给正文"

                $reasoning = @(
                    'Need to produce JSON only. Need plan for the child full day breakfast lunch dinner.'
                    'Child allergies: milk mild, egg intolerance. Rule 1: no forbidden food.'
                    'Need output meals array with date, mealType, time, menu, foods, calories, reason.'
                    'Need to avoid repeating recent dishes. Should use light, easy to digest food.'
                    'Let me think about the calories budget for a 3 year old: breakfast 300, lunch 450...'
                    'Wait, I still need to double check the allergen list before writing anything.'
                ) -join ' '

                $json = @{
                    id           = 'chatcmpl-fake-reasoning'
                    object       = 'chat.completion'
                    created      = [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                    model        = $ModelName
                    service_tier = 'default'
                    choices      = @(@{
                        index         = 0
                        message       = @{ role = 'assistant'; content = ''; reasoning_content = $reasoning }
                        finish_reason = 'length'
                    })
                    usage        = @{
                        prompt_tokens              = 957
                        completion_tokens          = $maxTokens
                        total_tokens               = 957 + $maxTokens
                        completion_tokens_details  = @{ reasoning_tokens = $maxTokens }
                    }
                } | ConvertTo-Json -Depth 6 -Compress

                $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
                $response.StatusCode = 200
                $response.ContentType = 'application/json; charset=utf-8'
                $response.ContentLength64 = $bytes.Length
                $response.OutputStream.Write($bytes, 0, $bytes.Length)
                $response.OutputStream.Close()
                continue
            }

            $content = if ($isRecommendation) { Get-FakeMenu } else { $Reply }

            Write-Log "  → 按「$(if ($isRecommendation) { '菜单' } else { '一句话' })」回复"

            $json = @{
                id      = 'chatcmpl-fake'
                object  = 'chat.completion'
                created = [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                model   = $ModelName
                choices = @(@{
                    index         = 0
                    message       = @{ role = 'assistant'; content = $content }
                    finish_reason = 'stop'
                })
                usage   = @{ prompt_tokens = 10; completion_tokens = 2; total_tokens = 12 }
            } | ConvertTo-Json -Depth 6 -Compress
        }
        else {
            $status = 404
            $json = @{ error = @{ message = "假服务没有实现 $path"; type = 'invalid_request_error' } } |
                ConvertTo-Json -Depth 4 -Compress
        }

        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        $response.StatusCode = $status
        $response.ContentType = 'application/json; charset=utf-8'
        $response.ContentLength64 = $bytes.Length
        $response.OutputStream.Write($bytes, 0, $bytes.Length)
        $response.OutputStream.Close()
    }
} finally {
    $listener.Stop()
    $listener.Close()
    Write-Log '已停止。'
}
