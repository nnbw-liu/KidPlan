# KidPlan.Relay —— 本机中继

让**部署在云端**的 KidPlan 用上**跑在你家里电脑上**的模型。

---

## 1. 为什么需要它

云端服务器永远连不到你家的 `127.0.0.1:1234`。这不是配置问题，是网络拓扑决定的：
家里的电脑通常没有公网 IP，躲在路由器 NAT 后面，运营商还可能封了入站端口。
在路由器上做端口映射既有安全风险（把本地模型服务直接暴露到公网），
在手机流量 / 公司网络 / 换宽带之后也会失效。

所以把连接方向反过来：

```
手机 / 平板 / 任何浏览器
        │  ①访问网页（走公网，一直都能用）
        ▼
   云端服务器（KidPlan.Web）
        │  ②把模型请求顺着长连接发下来
        ▼
 你家里的电脑（KidPlan.Relay）  ──③在本机发真实 HTTP 请求──▶  本地模型（LM Studio / Ollama / vLLM…）
```

第 ② 步那条长连接是**你这台电脑主动连出去**的（出站 WebSocket，走 443），
所以不需要公网 IP、不需要改路由器、不需要开防火墙入站端口。

顺带也解决了「在外面用手机怎么办」：手机访问的仍然是云端页面，
模型请求的路径是 `云端 → 你家里的电脑 → 本地模型`，手机本身不用装任何东西。

---

## 2. 三步跑起来

### 第 1 步：在网页上生成令牌

登录 KidPlan → 左侧「模型」→ **本机中继** → 起个名字（例如「书房的台式机」）
→ 创建 → **把弹出的令牌复制下来**。

> 令牌只在创建时显示这一次，之后库里只存它的 SHA-256。丢了就删掉重建一个。

### 第 2 步：在本机运行中继

```powershell
# 下载/发布出来的 KidPlan.Relay.exe 所在目录
.\KidPlan.Relay.exe --server https://kidplan.example.com --token kpr_在这里粘贴令牌
```

`--server` 写你平时访问网页的地址即可（`https://…` 会自动换成 `wss://`，
只写域名则默认按 `wss://`）。命令行里给过的地址和令牌会被记进配置文件，
下次直接双击 exe 就能跑。

看到下面这样就说明连上了：

```
2026-10-05 09:12:03 [INFO] KidPlan.Relay 1.0.0 启动。
2026-10-05 09:12:03 [INFO] 配置文件：C:\Tools\KidPlan.Relay\relay.json
2026-10-05 09:12:03 [INFO] 服务器：wss://kidplan.example.com/api/relay/connect
2026-10-05 09:12:03 [INFO] 正在连接 wss://kidplan.example.com/api/relay/connect …
2026-10-05 09:12:04 [INFO] 已连接（本机 DESKTOP-ABC，中继版本 1.0.0）。
2026-10-05 09:12:04 [INFO] 服务器已确认节点「书房的台式机」（Id 1）。
```

同时网页「本机中继」页上的节点会变成**在线**。

### 第 3 步：把模型端点指向中继

「模型设置」→ 新增端点：

| 字段 | 填什么 |
|---|---|
| 名称 | 家里电脑上的 Qwen（随便写） |
| BaseUrl | `http://127.0.0.1:1234/v1` —— **要写本机视角的地址** |
| 接入方式 | **本机中继** |
| 中继节点 | 选刚创建的那台机器（留空 = 该账号下最近活跃的那台） |
| 模型名 | 手工填，例如 `qwen2.5-7b-instruct`，不要求能从接口拉到 |

> ⚠️ **BaseUrl 要写「本机视角」的地址**，不是服务器视角的。
> 中继是在你家里的电脑上发起真实请求的，所以那里 `127.0.0.1:1234` 就是
> 那台电脑自己。这是最容易填错的一格。
>
> 如果模型跑在同一局域网的**另一台**机器上（例如 NAS），
> 这里就写 `http://192.168.1.50:1234/v1`。

填完点「测试连接」验证。之后饮食推荐里选中这个端点，推理就在你家电脑上跑了。

---

## 3. 让它开机自启

```powershell
.\KidPlan.Relay.exe --server https://kidplan.example.com --token kpr_xxx --install
```

注册的是一个名为 `KidPlan.Relay` 的 **Windows 计划任务**（登录时触发），
不是 Windows 服务。理由：服务需要管理员权限注册，而且在服务里跑的程序
看不到界面、出问题不好查；计划任务普通权限就够，你在任务管理器里也看得见、关得掉。

```powershell
.\KidPlan.Relay.exe --status       # 看当前配置 + 自启状态
schtasks /Run /TN KidPlan.Relay   # 立刻跑一次
.\KidPlan.Relay.exe --uninstall   # 取消自启（配置文件保留）
```

想要「不用登录也在跑」，在「任务计划程序」里把该任务的
*常规 → 不管用户是否登录都要运行* 勾上即可（会要求输入一次账户密码）。

---

## 4. 命令行参数

```
KidPlan.Relay.exe --server <地址> --token <令牌>   正常启动（并记住这两个参数）
KidPlan.Relay.exe --install ...                   注册开机自启
KidPlan.Relay.exe --uninstall                     取消开机自启
KidPlan.Relay.exe --status                        查看配置与自启状态
KidPlan.Relay.exe --help / --version

  --server <地址>     wss://kidplan.example.com / https://… / 只写域名都可以
  --token <令牌>      kpr_ 开头，在网页「本机中继」里生成
  --timeout <秒>      单次转发超时，默认 300（本地大模型冷启动可能很慢）
  --allow-public      允许转发到公网地址（默认只放行本机和内网）
```

配置文件（`relay.json`）优先放在 exe 旁边；如果放 exe 的目录写不进去
（例如装在 `C:\Program Files\` 下），会自动退到
`%APPDATA%\KidPlan.Relay\relay.json`。用 `--status` 可以看到实际用的是哪个。

内容长这样：

```json
{
  "server": "wss://kidplan.example.com",
  "token": "kpr_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "heartbeatSeconds": 20,
  "requestTimeoutSeconds": 300,
  "logFile": null,
  "allowPublicTargets": false
}
```

---

## 5. 自己发布一个 exe

```powershell
# 单文件、自带运行时、不需要目标机器装 .NET（win-x64）
dotnet publish src\KidPlan.Relay\KidPlan.Relay.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -o .\publish\relay-win-x64
```

产物是 `.\publish\relay-win-x64\KidPlan.Relay.exe`（约 15 MB）。
把这个 exe 拷到家里那台跑模型的电脑上就行。

其他架构把 `-r` 换成 `win-arm64`（部分轻薄本 / Surface）或 `linux-x64`
（想跑在 NAS 或路由器的 Docker 里）。不带 `-r` 的普通发布则需要目标机器上装 .NET 10 运行时。

---

## 6. 安全边界（值得读一遍）

中继的本质是「**服务器借你这台电脑去发 HTTP 请求**」。所以设计上做了几层收敛：

| 措施 | 说明 |
|---|---|
| 默认只放行内网目标 | 只允许 `127.0.0.1` / `localhost` / `10.x` / `172.16–31.x` / `192.168.x` / `169.254.x` / `fe80::` / `fc00::` / `.local` / `.lan`。转发到公网地址必须显式加 `--allow-public`。 |
| 只认 http/https | `file://` 之类一律拒绝，避免变成「让服务器读我硬盘上的文件」。 |
| 令牌不落明文 | 服务器库里只存 SHA-256，明文只在创建时显示一次。 |
| 令牌可随时停用 / 删除 | 「本机中继」页上停用会**立刻踢掉已建立的长连接**，不是只改一个字段。 |
| 连接方向是出站的 | 你家里不用开任何入站端口，本地模型服务也不会暴露到公网。 |

因此：**真要连公网上的模型服务（DeepSeek / OpenAI 等），请用「服务端直连」**，
不要用中继 —— 中继的存在意义就是「服务器连不到我的内网」。

如果你确实想通过中继访问公网服务（例如某个只在特定 IP 白名单下可用的中转站），
加 `--allow-public`；此时请把这个令牌当成一把可以发起任意 HTTP 请求的钥匙来保管。

---

## 7. 连不上时怎么查

按顺序排，通常在前两步就能定位：

**① 网页上看节点是否在线**
「本机中继」页 → 节点状态。离线说明 WebSocket 没连上：

- `--server` 写的地址在你**这台电脑**上能打开吗？（浏览器访问一下）
- 令牌是不是复制少了字符？（重新创建最省事）
- 是不是在服务器上配了 Nginx？那 `/api/relay/connect` 必须放行 WebSocket：
  需要 `proxy_http_version 1.1;`、`proxy_set_header Upgrade $http_upgrade;`、
  `proxy_set_header Connection "upgrade";`。
  参考 `deploy/nginx-kidplan.conf` 里那段带注释的配置。
- 公司网络 / 酒店 Wi-Fi 有时会拦 WebSocket，换手机热点试一下就能确认。

> **关于 Ollama**：它默认只监听 `127.0.0.1`，如果你把模型跑在同一台机器上就没问题；
> 跑在局域网另一台机器上时要设 `OLLAMA_HOST=0.0.0.0`，并且那台机器的防火墙要放行 11434。

**② 在线但「测试连接」失败**
看中继窗口里的日志，它会把原因和下一步直接写出来：

- `连不上 http://…：…`：模型服务没启动、端口不对，或者地址填错了。
  （LM Studio 默认 `1234`，Ollama 默认 `11434`。）
- `出于安全考虑，中继默认只转发到本机和内网地址，拒绝了 …`：
  BaseUrl 写成了公网域名。这种情况本来就该用「服务端直连」，
  确实要走中继就加 `--allow-public`。
- `只支持 http / https，收到的是 …`：BaseUrl 里混进了别的协议。

**③ 在线、能连上，但推理超时**
本地模型首次加载要几十秒，甚至几分钟（取决于模型大小和硬盘）。
日志里会是「等本地模型超过 N 秒还没回」。调大两处超时：

- 中继：`--timeout 900`
- 网页「模型设置 → 推荐参数 → 超时秒数」

**④ 状态栏显示在线 / 离线来回跳**
Nginx 的 `proxy_read_timeout` 太小，长连接被定期掐断。
按第 ① 步里的 Nginx 配置改成 `3600s`。

**⑤ 在网页上停用了节点，可它看起来还连着**
正常情况下**应该**立刻断开：停用会把那条 WebSocket 主动掐掉，中继这边会看到
连接中断、然后按退避重连（此时因为节点已停用，拿到 401 就停下了）。
如果你确实观察到「停用了还连着」，那是 bug，不是配置问题——
请把中继窗口的日志留着，对照 `scripts/relay-e2e.ps1` 的第 5 步复现。

---

## 8. 本机怎么自测这条链路

不需要公网、不需要真的模型：`scripts/fake-openai.ps1` 起一个假的 OpenAI 兼容服务，
`scripts/relay-e2e.ps1` 把服务端 + 中继 + 假模型三个真进程拉起来跑一遍，
并且会**先做反证**（中继没启动时「测试连接」必须失败），再验证正向链路。
具体步骤见仓库根目录 README 的「怎么自己再跑一遍」。

---

## 9. 端口与防火墙

- **出站**：只需要能出站访问服务器的 443（或你自定义的端口）。公司网络里如果
  只允许 80/443，那就必须让站点走 HTTPS（`wss://`）。
- **入站**：不需要任何入站规则。中继不监听任何端口。
- **Windows 防火墙**：首次运行如果弹出提示，**取消**即可 —— 它不需要入站权限
  （弹窗通常是因为发布目录带网络位置标记，可以忽略）。
- **本地模型服务**：如果模型跑在另一台机器上，那台机器要允许该端口入站，
  并且服务要监听 `0.0.0.0` 而不是 `127.0.0.1`。
