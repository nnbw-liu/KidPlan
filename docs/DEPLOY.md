# KidPlan 部署指南（阿里云 Linux + 宝塔面板 + Docker）

本文面向把 KidPlan 部署到**阿里云 Linux 服务器**、并使用**宝塔面板（BT Panel）**管理站点的用户。
按顺序读完即可完成从零到可访问的部署。

---

## 一、架构说明

```
                    互联网用户
                        │  HTTPS 443 / HTTP 80
                        ▼
        ┌───────────────────────────────────┐
        │  宝塔面板 Nginx（宿主机）          │
        │  · TLS 证书终结（Let's Encrypt）   │
        │  · 反向代理 + WebSocket 升级       │
        │  · 站点日志 / 强制 HTTPS           │
        └───────────────┬───────────────────┘
                        │ proxy_pass http://127.0.0.1:8080
                        │ （只监听回环地址，公网访问不到）
                        ▼
        ┌───────────────────────────────────┐
        │  Docker Compose 网络                │
        │  ┌─────────────────────────────┐   │
        │  │ app 容器                     │   │
        │  │ KidPlan.Web (Blazor Server) │   │
        │  │ 明文 HTTP 8080，非 root 用户 │   │
        │  │ 启动时自动执行 EF 迁移       │   │
        │  └──────────┬──────────────────┘   │
        │             │ Server=mysql:3306    │
        │  ┌──────────▼──────────────────┐   │
        │  │ mysql 容器 (MySQL 8.0)       │   │
        │  │ 数据存于具名卷               │   │
        │  │ kidplan-mysql-data           │   │
        │  └─────────────────────────────┘   │
        └───────────────────────────────────┘
```

要点：

- **只有 80 / 443 需要暴露到公网**；8080 只绑定 `127.0.0.1`，3306 完全不映射到宿主机。
- 容器内跑的是**明文 HTTP**，HTTPS 由宝塔 Nginx 负责，证书续期也由宝塔自动完成。
- 应用启动时会**自动执行数据库迁移**（`Database__AutoMigrate=true`，自带重试），
  所以部署流程里**不需要**单独跑 `dotnet ef database update`。
- Blazor Server 依赖 **WebSocket**，反向代理必须正确透传 `Upgrade` 头，否则界面会反复“正在重新连接”。
- 宿主机上的 `./config/ai.json` 以读写方式挂进容器的 `/app/config`，用来放**全局模型配置**。
  改它不需要重启（应用自己监听文件变更）；目录属主必须是容器内的应用用户（uid 64198），
  详见 [第十一节](#十一模型ai配置)。

---

## 一之二、可选：接自己电脑上的本地模型

如果模型跑在你家里的电脑上（LM Studio / Ollama / vLLM…），云端服务器是**连不到**它的——
家里通常没有公网 IP，做端口映射既不安全、换个网络就失效。

解决办法是让**家里的电脑主动连出去**，用附带的 `KidPlan.Relay` 挂一条出站长连接：

```
手机 / 浏览器 ──①访问网页──> 云服务器 ──②顺着长连接派发──> 家里的 KidPlan.Relay ──③本机请求──> 本地模型
```

- 云服务器**不需要任何额外配置**，只要 `/api/relay/connect` 能过 Nginx（本仓库的
  `deploy/nginx-kidplan.conf` 里已经配好）。
- 家里的电脑**不需要开任何入站端口**，也**不需要公网 IP**。
- 手机在外面照样能用：手机访问的是云端页面，模型请求的路径是「云端 → 家里电脑 → 本地模型」。
- 注册令牌和查看在线状态都在网页的「模型设置 → 本机中继」里。

完整步骤（发布 exe、开自启、安全边界、排错）见
[src/KidPlan.Relay/README.md](../src/KidPlan.Relay/README.md)。

> ⚠️ 要在「模型设置」里把那个端点的**接入方式**选成「本机中继」，
> 并且 `BaseUrl` 填**本机视角**的地址（`http://127.0.0.1:1234/v1`），
> 因为真实请求是在家里那台电脑上发的。这是最容易填错的一格。

---

## 二、服务器要求

| 项目 | 最低 | 建议 |
| --- | --- | --- |
| CPU | 2 核 | 2 核 |
| 内存 | 2 GB | 2 GB + 2 GB swap |
| 磁盘 | 20 GB | 40 GB 以上（Docker 镜像与构建缓存较占空间） |
| 系统 | Ubuntu 22.04 / Debian 12 / Alibaba Cloud Linux 3 / CentOS 7+ | Ubuntu 22.04 LTS |

**关于 2GB 内存**：`dotnet publish` 编译过程本身就可能吃掉 1GB+ 内存，MySQL 默认又要占几百 MB。
内存不足时构建会被 OOM Killer 杀掉，表现为 `Killed` 或 `exit code 137`。

> 强烈建议 2C2G 机器先加一块 2GB swap（执行一次，重启后依然有效）：
>
> ```bash
> sudo fallocate -l 2G /swapfile
> sudo chmod 600 /swapfile
> sudo mkswap /swapfile
> sudo swapon /swapfile
> echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
> free -h   # 确认 Swap 一行有 2.0Gi
> ```

**阿里云安全组**（控制台 → 安全组 → 配置规则，入方向）：

| 端口 | 是否放行 | 说明 |
| --- | --- | --- |
| 22 | ✅ 放行 | SSH，建议限制来源 IP |
| 80 | ✅ 放行 | HTTP，证书签发与跳转需要 |
| 443 | ✅ 放行 | HTTPS |
| 8888（或自定义） | ⚠️ 谨慎 | 宝塔面板端口，建议限制来源 IP 或改用域名 + 面板 SSL |
| 3306 | ❌ **不要放行** | 数据库只在容器内网，公网开放极易被勒索/爆破 |
| 8080 | ❌ **不要放行** | 应用只给本机 Nginx 反代，公网开放会绕过 HTTPS |

---

## 三、前置准备

### 1. 安装 Docker（官方脚本一行命令）

```bash
# 通用（海外源）
curl -fsSL https://get.docker.com | bash -s docker

# 阿里云服务器推荐（使用阿里云镜像源，更快）
curl -fsSL https://get.docker.com | bash -s docker --mirror Aliyun

# 设置开机自启
sudo systemctl enable --now docker

# 验证：必须同时看到 Client 与 Server 两段信息
docker version
```

### 2. 安装 Docker Compose v2 插件

本项目的脚本**要求 `docker compose`（v2 插件）**，不支持旧的 `docker-compose` 独立命令。

```bash
docker compose version    # 能输出版本号（v2.x.x）即可
```

若提示找不到命令，按系统安装：

```bash
# Ubuntu / Debian
sudo apt-get update && sudo apt-get install -y docker-compose-plugin

# Alibaba Cloud Linux / CentOS
sudo yum install -y docker-compose-plugin

# 通用兜底（x86_64）
sudo mkdir -p /usr/local/lib/docker/cli-plugins
sudo curl -SL https://github.com/docker/compose/releases/latest/download/docker-compose-linux-x86_64 \
     -o /usr/local/lib/docker/cli-plugins/docker-compose
sudo chmod +x /usr/local/lib/docker/cli-plugins/docker-compose
```

> 国内服务器访问 GitHub 慢时，可把上面的下载地址换成镜像加速地址，
> 或先在本地下载后用宝塔文件管理器上传到 `/usr/local/lib/docker/cli-plugins/`。

### 3. 配置镜像加速（可选，但强烈建议）

宝塔面板 → **Docker → 配置**，或在 `/etc/docker/daemon.json` 中加入：

```json
{
  "registry-mirrors": ["https://docker.m.daocloud.io", "https://mirror.ccs.tencentyun.com"]
}
```

保存后 `sudo systemctl restart docker`。

### 4. 安装宝塔面板

```bash
# 官方安装脚本（Ubuntu/Debian）
url=https://download.bt.cn/install/install_lts.sh; if [ -f /usr/bin/curl ]; then curl -sSO "$url"; else wget -O install_lts.sh "$url"; fi; bash install_lts.sh ed8484bec
```

安装完成后记下面板地址、用户名与密码；登录面板后建议：

- 在 **面板设置** 里开启面板 SSL、绑定域名、改掉默认端口；
- 在 **软件商店** 确认已安装 **Nginx**（1.20 以上即可）。

> 宝塔与 Docker 安装没有先后要求，但请确保 Nginx 版本 ≥ 1.20，否则部分代理指令可能不支持。

---

## 四、上传代码

### 方式 A：Git 克隆（推荐，便于后续 `deploy.sh update`）

```bash
cd /www/wwwroot
sudo git clone <你的仓库地址> KidPlan
sudo chown -R "$USER":"$USER" /www/wwwroot/KidPlan
cd KidPlan
```

### 方式 B：宝塔文件管理器上传

1. 本地把仓库打包为 zip（**不要**把 `bin/`、`obj/` 打进去，没必要且体积大）。
2. 宝塔 → 文件 → 进入 `/www/wwwroot/` → 新建目录 `KidPlan` → 上传 zip → 解压。

### ⚠️ 关于 LF 换行（Windows 上传必看）

在 Windows 上编辑过的 `.sh` 文件通常是 **CRLF** 换行，在 Linux 上会报错：

```
bash: $'\r': command not found
```

上传后务必修正：

```bash
cd /www/wwwroot/KidPlan
sed -i 's/\r$//' deploy/deploy.sh
# 或（若已安装 dos2unix）
dos2unix deploy/deploy.sh
```

本地提交前可设置 Git 自动转换（仓库管理员一次性配置）：

```bash
git config --global core.autocrlf input
# 并在仓库根目录添加 .gitattributes：
#   *.sh text eol=lf
```

---

## 五、一键部署

```bash
cd /www/wwwroot/KidPlan
bash deploy/deploy.sh install
```

脚本会自动完成：

1. **环境预检**：Linux 系统、docker、`docker compose` v2、项目文件、磁盘空间（<5GB 警告）、内存（<2GB 提示加 swap）。
2. **生成 `.env`**：若不存在则用 `openssl rand` 生成随机强密码，写入 `.env` 并 `chmod 600`。
3. **构建镜像**：先 `compose pull mysql:8.0`，再 `compose build`（首次构建要下载 NuGet 包，可能较慢）。
4. **启动容器**：`compose up -d`，等待 MySQL 健康检查通过后应用才启动。
5. **健康检查**：轮询 `http://127.0.0.1:8080/healthz` 最多 120 秒，全部通过后打印后续步骤。

> **关于登录状态（DataProtection 密钥卷）**：应用用 ASP.NET Core DataProtection 加密 Identity
> 的认证 Cookie 与令牌，密钥需要跨容器重建持久化，因此 compose 已把具名卷
> `kidplan-dataprotection-keys` 挂到应用容器的 `/home/kidplan/.aspnet`（该路径对应镜像里
> 非 root 用户 `kidplan` 的家目录，必须以 Dockerfile 中的实际用户名为准）。
> **删除这个卷不会丢数据，但会导致所有人被登出**（Cookie 解不开，需要重新登录），
> 所以 `docker compose down -v` 或手动删卷前请知悉这一点。

看到类似输出即成功：

```
[完成] 应用健康检查通过（health=healthy）。
[完成] KidPlan 已部署完成！
  服务器本机地址 : http://127.0.0.1:8080
```

部署完成后可以立刻在本机验证：

```bash
curl -fsS http://127.0.0.1:8080/healthz && echo
```

> `install` 是**幂等**的：重复执行只会重建镜像并重启容器，不会丢数据、不会覆盖已有的 `.env`。

---

## 六、宝塔站点配置

### 步骤 1：新建站点

宝塔 → **网站** → **添加站点**：

| 字段 | 填写 |
| --- | --- |
| 域名 | 你自己的域名，例如 `kidplan.example.com`（需已解析到本服务器） |
| 根目录 | 任意（保持默认即可，本部署不使用静态目录） |
| FTP / 数据库 / PHP | 全部**不创建** |

### 步骤 2：添加反向代理

进入该站点 → **反向代理** → **添加反向代理**：

| 字段 | 填写 |
| --- | --- |
| 代理名称 | `kidplan` |
| 目标 URL | `http://127.0.0.1:8080` |
| 发送域名 | `$host` |

### 步骤 3：替换为完整配置（关键）

宝塔自动生成的代理配置**默认缺少 WebSocket 与长超时**，必须替换。
把 [`deploy/nginx-kidplan.conf`](nginx-kidplan.conf) 的内容粘贴到
**网站 → 该站点 → 配置文件**中的 `server { }` 内（覆盖原来的 `location / { ... }`）。

最少必须包含这几行，它们是 **WebSocket 关键行**：

```nginx
proxy_http_version 1.1;
proxy_set_header Upgrade    $http_upgrade;
proxy_set_header Connection "upgrade";
proxy_read_timeout 3600s;
proxy_send_timeout 3600s;
proxy_buffering off;
```

改完保存并重载：

```bash
nginx -t && nginx -s reload
# 宝塔环境通常为： /www/server/nginx/sbin/nginx -t
```

> **关于 `$connection_upgrade`**：该变量需要在 nginx 的 `http { }` 上下文用 `map` 定义，
> 而站点配置文件位于 `server` 层级，写进去会导致 nginx 启动失败。
> 因此模板中直接写 `Connection "upgrade"`，对普通 HTTP 请求无害。
> 如果你的宝塔已在全局定义（`grep connection_upgrade /www/server/nginx/conf/nginx.conf`），
> 也可以换成 `proxy_set_header Connection $connection_upgrade;`。

### 步骤 4：申请 SSL 证书 + 强制 HTTPS

站点 → **SSL**：

1. 选择 **Let's Encrypt** → 勾选域名 → **申请**（需要 80 端口可达）。
2. 申请成功后打开 **强制 HTTPS** 开关。
3. 建议同时开启 **TLS 1.2/1.3**、**HSTS**（确认全站 HTTPS 正常后再开 HSTS）。

> 不要在配置文件里手写 `listen 443 ssl;` 与证书路径，宝塔会自己插入，手写容易冲突。

### 步骤 5：开启 WebSocket 支持（最容易漏）

- 宝塔反向代理界面若有 **“WebSocket 支持”** 开关，请**打开**；
- 同时确认上一步粘贴的配置中包含 `Upgrade` / `Connection "upgrade"` 两行。

### 步骤 6：验证

浏览器打开 `https://kidplan.example.com`：

- 打开 F12 → Network → 筛选 `WS`，应能看到 `/ _blazor` 的 WebSocket 连接状态为 **101 Switching Protocols**；
- 页面左上角不应出现“正在重新连接”的提示。

---

## 七、首次使用

1. 浏览器访问站点，点击右上角 **Register（注册）**。
2. 注册后**先去邮箱点确认链接**才能登录（`Identity__RequireConfirmedAccount=true`）。没收到就点登录页上的「重新发送确认邮件」。
3. 登录后进入 **添加小孩**，填写昵称、出生日期等信息。
4. 到 **过敏原管理** 逐个确认等级。如果你是从旧版本升级上来的，原有的自由文本过敏原会被自动搬迁成分级记录，
   **默认等级一律是「强过敏」并标记为「待复核」**——这是刻意的保守选择（猜高了只是多确认一次，猜低了会漏掉真正的危险），
   但你需要按实际情况把等级调下来（不耐受 / 轻微 / 中等）。
5. （可选）配模型：**模型设置** → 新增一个 OpenAI 兼容端点 → 填 BaseUrl、密钥、模型名 → **测试连接**。
   想接家里的模型就先到 **本机中继** 创建节点拿令牌，见[第一之二节](#一之二可选接自己电脑上的本地模型)。

---

## 八、运维

所有命令都在仓库根目录执行（`cd /www/wwwroot/KidPlan`）。

### 查看日志

```bash
bash deploy/deploy.sh logs          # 跟踪应用日志（Ctrl+C 退出）
bash deploy/deploy.sh logs mysql    # 只看数据库日志
bash deploy/deploy.sh status        # 容器状态 + 健康检查 + 端口监听
```

### 更新到新版本

```bash
bash deploy/deploy.sh update
```

流程：`git pull --ff-only`（非 Git 目录会跳过，需自己上传新代码）→ `compose build --pull`
→ `compose up -d` → 等待健康检查 → `docker image prune -f` 清理悬空镜像。

> 若更新包含新的数据库迁移，应用启动时会自动执行，无需手工操作。

### 备份

```bash
bash deploy/deploy.sh backup
```

产出 `backups/kidplan-YYYYmmdd-HHMMSS.sql.gz`，并自动清理 **14 天前**的旧备份。
建议在宝塔 **计划任务** 里加一条 Shell 脚本任务，每天凌晨执行：

```bash
cd /www/wwwroot/KidPlan && bash deploy/deploy.sh backup >> /www/wwwlogs/kidplan-backup.log 2>&1
```

### 恢复

```bash
bash deploy/deploy.sh restore backups/kidplan-20260101-030000.sql.gz
```

会二次确认（需输入 `yes`），恢复后自动重启应用以执行迁移。

### 手动触发迁移

```bash
bash deploy/deploy.sh migrate     # 就是重启 app，让启动期自动迁移跑一遍
```

### 卸载

```bash
bash deploy/deploy.sh uninstall
```

先确认是否删除容器；随后**再单独确认**是否删除数据卷（需输入 `DELETE`）。
只要不输入 `DELETE`，数据库数据会保留，之后 `install` 仍可继续使用。

### 其他常用命令

```bash
bash deploy/deploy.sh start | stop | restart
docker compose ps
docker stats --no-stream        # 查看各容器内存占用
docker compose logs --tail 200 app
```

---

## 九、故障排查

### 1. 502 Bad Gateway

含义：Nginx 连不上后端 `127.0.0.1:8080`。按顺序排查：

```bash
bash deploy/deploy.sh status                      # 容器是否 Up / healthy
curl -v http://127.0.0.1:8080/healthz             # 宿主机能否直接访问
bash deploy/deploy.sh logs                        # 应用日志是否有异常
```

常见原因：

| 原因 | 处理 |
| --- | --- |
| 应用容器没起来（构建/迁移失败） | 看 `logs app`，修好后再 `restart` |
| 应用启动慢（首次迁移） | 等 1–2 分钟；`HEALTHCHECK` 的 `start_period` 已放宽到 90s |
| 反代目标端口写错 | 确认 `docker-compose.yml` 的 `APP_PORT`（默认 8080）与 Nginx 里一致 |
| SELinux 拦截（CentOS 系） | `setsebool -P httpd_can_network_connect 1` |

### 2. 页面反复提示“正在重新连接”（WebSocket 未生效）

这是宝塔 + Blazor Server 最常见的坑。检查：

1. F12 → Network → `WS`，看 `_blazor` 请求是否为 `101 Switching Protocols`；
   - 若返回 `200` 或 `400`：说明升级头没透传，配置里缺 `Upgrade` / `Connection "upgrade"`。
2. 确认 `proxy_http_version 1.1;`（写成 1.0 一定失败）。
3. 确认宝塔反向代理的 **WebSocket 支持** 已勾选。
4. 确认没有 CDN/云加速在中间掐断长连接（阿里云 CDN 默认不支持 WebSocket）。
5. 若连接能建立但很快断开：加大超时并关闭缓冲。

```nginx
proxy_read_timeout 3600s;
proxy_send_timeout 3600s;
proxy_buffering off;
```

6. 应用侧可临时提高日志级别定位（`ASPNETCORE_ENVIRONMENT=Development` 不推荐在生产长期使用）。

### 3. 数据库连接失败 / 认证插件问题

错误示例：`Unable to connect to any of the specified MySQL hosts`、
`Authentication method 'caching_sha2_password' failed`。

排查顺序：

```bash
bash deploy/deploy.sh logs mysql | tail -50
bash deploy/deploy.sh logs app   | grep -i -E 'mysql|connection' | tail -50

# 在容器内确认账号能登录（密码从 .env 读取）
source .env
docker compose exec mysql mysql -u"$MYSQL_USER" -p"$MYSQL_PASSWORD" -h 127.0.0.1 -e "select 1"
```

| 原因 | 处理 |
| --- | --- |
| MySQL 8 默认 `caching_sha2_password` | 连接串已带 `AllowPublicKeyRetrieval=True;SslMode=None`（MySqlConnector 语法），一般无需改 |
| 密码含特殊字符 | 连接串用 `;` 分隔，密码里的 `;` `=` `$` 会破坏解析。改成纯字母数字：`.env` 里换密码后 `bash deploy/deploy.sh restart` |
| `.env` 改过密码但数据卷已初始化 | MySQL 只在**首次创建数据卷**时读取 `MYSQL_PASSWORD`。改密码需进容器改，或备份后删卷重建：`bash deploy/deploy.sh backup && bash deploy/deploy.sh uninstall`（选删除数据卷）再 `install` |
| 应用启动时数据库还没就绪 | compose 已用 `depends_on: condition: service_healthy` + 应用侧重试，若仍失败可 `restart` 一次 |

### 4. 迁移失败

现象：应用反复重启，日志出现 `MySqlException` 或 `PendingModelChangesWarning`。

```bash
bash deploy/deploy.sh logs app | grep -A 30 -i 'migrat'
```

| 原因 | 处理 |
| --- | --- |
| 数据库权限不足 | 确认 `MYSQL_USER` 对目标库有全部权限（compose 首次初始化会自动授权） |
| 手改过表结构 | 用备份恢复到迁移状态，或手工补齐差异 |
| 迁移文件缺失 | 确认 `src/KidPlan.Web/Data/Migrations` 目录完整上传 |
| 磁盘满导致无法建表 | `df -h`，清理后再 `bash deploy/deploy.sh migrate` |

> 注意：**不要把 `Database__AutoMigrate` 设为 `false`** 除非你打算用 `dotnet ef` 手工迁移，
> 否则新版本上线后表结构不会更新，应用会直接报错。

### 5. 容器被 OOM Kill（内存不足）

现象：`docker compose ps` 里容器不断重启；`docker inspect` 显示
`OOMKilled: true`；`dmesg | grep -i 'killed process'` 能看到记录。

```bash
free -h
docker stats --no-stream
dmesg -T | grep -i -E 'oom|killed process' | tail -20
```

处理：

1. 加 swap（见第二节），并把 MySQL 的缓冲池调小：
   在 `docker-compose.yml` 的 mysql 服务里把 `--innodb-buffer-pool-size=256M` 改成 `128M`。
2. 构建阶段失败（`Killed`）时，先在本地构建好镜像推到镜像仓库，或在服务器上临时加 swap 再构建。
3. 关闭不必要服务（宝塔里的 PHP、Redis 等）释放内存。
4. 长期内存不够就升级服务器规格（2C2G → 2C4G）。

### 6. 时区不对，饭点状态判断错误

现象：明明中午却显示“未吃饭”；备份文件名的时间戳与本地时间差 8 小时。

排查：

```bash
docker compose exec app date                  # 应输出 CST（+0800）时间
docker compose exec app cat /etc/timezone     # 应为 Asia/Shanghai
docker compose exec mysql date
```

处理：

- 确认 `.env` 中 `TZ=Asia/Shanghai`，且 `docker-compose.yml` 已把 `TZ` 传给容器；
- 确认 Dockerfile 里安装了 `tzdata`（否则 `TZ` 会被静默忽略）；
- 改动后必须**重建/重建容器**：`bash deploy/deploy.sh restart`；
- 如果只是宿主机时间不对，在宝塔面板 → **系统** 里同步时间，或执行 `sudo timedatectl set-timezone Asia/Shanghai && sudo hwclock -w`。

### 7. 收不到确认邮件 / 重置邮件

邮箱确认是强制的，这一项不通就等于没人能注册。按顺序排查：

1. **看应用日志里那次发信的结果**：

   ```bash
   docker compose logs app | grep -E "已发送邮件|发送邮件.*失败|Smtp"
   ```

   - `已发送邮件「确认 KidPlan 邮箱 - xxx」到 xxx@xx` → 应用这边成功了，问题在对方邮箱（垃圾箱、被拦截、地址写错）。
   - `发送邮件「...」到 ... 失败（SMTP ...）` → 后面会跟完整的 SMTP 原始异常，照着看下面几条。

2. **`.env` 里填的是授权码还是登录密码**。这是最常见的错：QQ/163 都要用「SMTP 授权码」，不是邮箱密码。QQ 邮箱路径：设置 → 账户 → POP3/IMAP/SMTP 服务 → 开启并生成授权码。

3. **启动日志里有没有那条 Error**。如果日志出现「已开启邮箱确认，但 SMTP 配置不完整」，说明变量没传到容器：

   ```bash
   docker compose exec app printenv | grep Smtp
   ```

   应能看到 `Smtp__Host` / `Smtp__UserName` / `Smtp__Password` 等。

4. **端口和加密方式要配套**：`465` + `SslOnConnect`，或 `587` + `StartTls`。465 配成 `StartTls` 会一直卡住直到超时。

5. **云厂商封了 25 端口**：阿里云等默认封禁出站 25，用 465/587 不受影响（本项目默认 465）。

6. **邮件里的链接点开是内网地址**（`http://172.x.x.x:8080/...` 或 `http://localhost`）：说明 `Smtp__PublicBaseUrl` 没配，而 Nginx 又没把真实域名传给应用。两个办法，任选其一：

   - 在 `.env` 里设 `PUBLIC_BASE_URL=https://你的域名`（最简单，推荐）；
   - 或在宝塔站点的 Nginx 配置里补上转发头：

     ```nginx
     proxy_set_header Host              $host;
     proxy_set_header X-Forwarded-Host  $host;
     proxy_set_header X-Forwarded-Proto $scheme;
     proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
     ```

   改完 `.env` 后执行 `bash deploy/deploy.sh restart` 生效。

### 8. 模型设置相关

| 现象 | 原因与处理 |
| --- | --- |
| 「模型设置」页显示**配置文件不可写** | `./config` 的属主不是容器内的应用用户。执行 `sudo chown -R 64198:64198 ./config`，然后刷新页面。 |
| 改了 `config/ai.json` 但没生效 | 页面顶部会显示「上次读取时间」和「上次的错误」。最常见的是 JSON 写坏了（注释里写了 `//` 但括号没配对），出错时应用**不会**抛弃旧配置，所以表面上「像没生效」。点「重新从文件加载」可以看到确切错误。 |
| 保存后注释全没了 | 这是预期行为：网页保存会重写整个文件。带注释的版本请留在 `config/ai.example.json`。 |
| 「测试连接」返回 401 / 403 | API Key 不对，或该端点要求额外的请求头（有些中转站要 `HTTP-Referer`），填到端点的「额外请求头」里。 |
| 「测试连接」返回 404 | BaseUrl 写多了：应该写到版本号为止（`https://api.deepseek.com/v1`），**不要再带** `/chat/completions`。 |
| 本地模型请求超时 | 本地大模型首次加载很慢。调大网页上的「推荐参数 → 超时秒数」，同时把中继的 `--timeout` 一起调大。 |
| 推荐失败并提示「模型把输出额度全花在推理上了」 | 接的是**推理模型**（deepseek-r1 / v4 这类会先输出 `reasoning_content` 的模型），而 `maxOutputTokens` 太小：思考过程会把额度吃光，正文一个 token 都轮不到。默认已经是 8192，别调回 2500；真出问题就在「模型设置 → 全局配置」（普通账号在「我的账号覆盖 → 单独设置推荐参数」）里调到 8000 以上。反过来，**本地小模型**上下文只有 4096 时要把 `maxOutputTokens` 调回 2000 左右，否则接口直接返回 400。 |
| 推荐失败，日志里有「模型返回了空正文」 | 页面上只会显示整理过的诊断（`finish_reason` / token 用量 / 正文或推理开头），完整原始响应在容器日志里：`docker compose logs -f web \| grep 模型返回了空正文`。这样设计是因为推理模型的原始报文是一整包英文思考链，铺在页面上没法看。 |

### 9. 本机中继相关

| 现象 | 原因与处理 |
| --- | --- |
| 「本机中继」页上节点一直**离线** | 家里那台电脑上的 `KidPlan.Relay` 没连上。先看它的窗口日志；再确认 `/api/relay/connect` 没有被 Nginx 拦掉（需要 WebSocket 升级头）。 |
| 节点**在线 / 离线反复跳** | Nginx 的 `proxy_read_timeout` 太小，长连接被定期掐断。`/api/relay/connect` 要 `proxy_read_timeout 3600s`，见 `deploy/nginx-kidplan.conf`。 |
| 测试连接报「只转发到本机和内网地址」 | 端点的 BaseUrl 填成了公网域名。中继只用于「服务器连不到我的内网」，连公网服务应该用「服务端直连」。确实要走中继就加 `--allow-public`。 |
| 停用后还在用 | 正常情况下不会。停用会**立刻掐掉已建立的长连接**（服务端主动 Abort，中继侧会看到连接中断、重连时因为节点已停用而拿到 401），不是只改一个字段。如果症状仍在，检查是不是有两个节点（另一台机器上还在跑）。这条链路在本机用真进程验证过（`scripts/relay-e2e.ps1`），但**没有跨过公网**——上云之后建议手工复现一次。 |
| 中继连不上，但本机怎么测都是好的 | 公网这一段（`wss://` + Nginx 升级头 + 证书）是本机测不到的部分。先按上一行确认 `/api/relay/connect` 的 WebSocket 配置，再看家里那台机器的出站 443 有没有被公司网络拦掉（换手机热点一试就知道）。 |

### 10. 其他速查

```bash
# 磁盘被 Docker 占满
docker system df
docker system prune -af          # 慎用：会删除所有未使用镜像与构建缓存
docker builder prune -f

# 查看某容器详细信息 / 退出码
docker compose ps -a
docker inspect --format '{{.State.ExitCode}} {{.State.OOMKilled}}' $(docker compose ps -q app)

# 完全重来（保留数据）
bash deploy/deploy.sh stop && bash deploy/deploy.sh start
```

---

## 附录：容器环境变量一览

`docker-compose.yml` 中 `app` 服务注入的变量，与 ASP.NET Core 配置的对应关系如下
（双下划线 `__` 是配置节的层级分隔符）：

| 环境变量 | 对应配置 / 作用 | 默认值 |
| --- | --- | --- |
| `ConnectionStrings__DefaultConnection` | `ConnectionStrings:DefaultConnection`，数据库连接串（MySqlConnector 语法） | 由 `.env` 拼装 |
| `Database__MySqlVersion` | `Database:MySqlVersion`，固定 MySQL 版本以免启动时探测连接 | `8.0.36` |
| `Database__AutoMigrate` | `Database:AutoMigrate`，启动时是否自动执行 EF 迁移 | `true`（**不要关**） |
| `Database__RetryOnFailure` | `Database:RetryOnFailure`，EF 命令级重试 | `true` |
| `Database__MigrateMaxAttempts` | `Database:MigrateMaxAttempts`，启动迁移的最大重试次数 | `10` |
| `Identity__RequireConfirmedAccount` | `Identity:RequireConfirmedAccount`，注册后是否需要邮箱确认 | `true`（**别关**，关了就没有邮箱确认） |
| `Admin__Emails` | `Admin:Emails`，哪些账号是管理员（能看「模型设置 → 全局配置」）。逗号 / 分号 / 空格分隔，改完**要重新登录** | 空（没人能看到全局配置，启动日志会警告） |
| `Smtp__Host` | `Smtp:Host`，SMTP 服务器地址 | `smtp.qq.com` |
| `Smtp__Port` | `Smtp:Port`，SMTP 端口 | `465` |
| `Smtp__Security` | `Smtp:Security`，加密方式：`SslOnConnect`(465) / `StartTls`(587) / `None`(明文，仅本机测试) | `SslOnConnect` |
| `Smtp__UserName` | `Smtp:UserName`，发件邮箱账号 | 由 `.env` 的 `SMTP_USERNAME` 注入（**必填**） |
| `Smtp__Password` | `Smtp:Password`，**SMTP 授权码**（不是邮箱登录密码） | 由 `.env` 的 `SMTP_PASSWORD` 注入（**必填**） |
| `Smtp__FromAddress` | `Smtp:FromAddress`，发件人地址，留空则用 `UserName` | 空 |
| `Smtp__PublicBaseUrl` | `Smtp:PublicBaseUrl`，邮件里链接使用的对外地址 | 空（按当前请求推断，见下方排错第 7 条） |
| `ASPNETCORE_ENVIRONMENT` | 运行环境 | `Production` |
| `ASPNETCORE_URLS` | 监听地址 | `http://+:8080` |
| `TZ` | 容器时区 | `Asia/Shanghai` |
| `HttpsRedirection__Enabled` | `HttpsRedirection:Enabled`，是否强制跳转 HTTPS。TLS 由宝塔 Nginx 终结，容器内是明文 HTTP，所以必须保持 `false`，否则会重定向成 `http://` 造成循环 | `false`（**不要开**） |
| `Ai__ConfigFile` | `Ai:ConfigFile`，全局模型配置（`config/ai.json`）的绝对路径 | `/app/config/ai.json`（与内容根目录下的默认位置一致） |

> 应用另外会采纳 Nginx 传来的 `X-Forwarded-Proto` / `X-Forwarded-For`
> （已清除已知代理限制），因此反向代理配置里的 `proxy_set_header X-Forwarded-Proto $scheme;`
> **不能删**，否则 HTTPS 站点会被应用误判为 HTTP。

---

## 十一、模型（AI）配置

### 配置放在哪

全局模型配置是**容器外**的 `./config/ai.json`，通过绑定挂载进容器：

```
宿主机  /www/wwwroot/KidPlan/config/ai.json
   ↕  （compose: ./config:/app/config）
容器内  /app/config/ai.json
```

为什么用绑定挂载而不是具名卷：

- 可以直接在**宝塔文件管理器**里编辑，不必进容器；
- 重建镜像 / 重新部署**不会**把配置冲掉；
- 里面可能有真实 API 密钥，所以它**没有**进 Git（`.gitignore` / `.dockerignore` 都排除了），
  仓库里只留带注释的模板 `config/ai.example.json`。

### 权限（很容易踩）

容器内的应用以 **uid 64198（kidplan）** 运行，绑定挂载的目录属主必须跟着改，否则
「模型设置」页会显示**「配置文件不可写」**，而且只改文件内容不会生效（保存会失败）。

```bash
cd /www/wwwroot/KidPlan
sudo chown -R 64198:64198 ./config
```

`bash deploy/deploy.sh install` / `update` 会自动做这一步（`prepare_config_dir`），
手工部署时才需要自己执行。

### 谁能改全局配置（管理员）

「模型设置 → 全局配置」改的是**这个部署上所有人共用的默认值**（里面还有 API Key），
所以它只对 `admin` 角色开放；普通账号打开这一页只会看到「我的账号覆盖」。

指派管理员有两种方式：

```bash
# 方式一（推荐）：改 .env / docker-compose 里的 Admin__Emails，然后重启容器
Admin__Emails=you@example.com,ops@example.com

# 方式二：直接写库（不用重启，但角色写在登录 Cookie 里，改完要重新登录一次）
docker compose exec db mysql -uroot -p"$MYSQL_ROOT_PASSWORD" kidplan -e "
INSERT IGNORE INTO AspNetRoles (Id, Name, NormalizedName, ConcurrencyStamp)
VALUES (UUID(), 'admin', 'ADMIN', UUID());
INSERT IGNORE INTO AspNetUserRoles (UserId, RoleId)
SELECT u.Id, r.Id FROM AspNetUsers u, AspNetRoles r
WHERE u.Email='you@example.com' AND r.NormalizedName='ADMIN';"
```

几个容易踩的点：

- **角色是写进登录 Cookie 的**，改完库里的角色必须**退出再登录**才生效（页面不会自动变）。
- `Admin__Emails` 里配的邮箱如果**还没有注册**，启动日志会给一条 `以下邮箱还没注册` 的警告；
  配错到最后一个都不剩时，日志会明确写「当前没有任何管理员」，用于提醒你没人能改全局配置了。
- 反过来说，**别把 `Admin__Emails` 当成访问控制**：它只是「启动时把这几个人加进 admin 角色」，
  之后即使从变量里删掉，已经在角色里的人**依然是管理员**，要收回得从 `AspNetUserRoles` 里删。

### 改配置的两种方式

**方式一：网页上改（推荐）**

「模型设置 → 全局配置」里加端点、填模型名、点保存。保存后立刻生效，**不用重启容器**。
添加端点的表单里就有「保存这个端点」，填完直接存；只放进列表不保存的话，
页签顶部会一直挂着一条「有未保存的改动」的提示条。

**方式二：直接改文件（适合批量改 / 脚本化）**

```bash
vi /www/wwwroot/KidPlan/config/ai.json    # 或宝塔文件管理器
```

应用自己盯着这个文件（400ms 去抖），**保存即生效**。要点：

- JSON 写坏了**不会**把服务搞崩：它会继续用上一份能用的配置，并把错误显示在
  「模型设置」页顶部的提示条里（同时给出配置文件路径、上次读取时间、是否可写）。
- 万一页面上看不出所以然，点一下「重新从文件加载」。
- 但请注意：**在网页上保存会重写整个文件**，`ai.json` 里的注释会丢掉
  （带注释的版本请保留在 `config/ai.example.json` 或你自己的笔记里）。

### 账号级覆盖

除了全局配置，**每个账号还可以单独覆盖**（换默认模型、停用某个全局端点、加一个只有自己用的端点）。
覆盖内容存在数据库的 `AiUserSettings` 表里，**不写这个文件**，所以：

- 覆盖不会因为改 `ai.json` 而丢失；
- 备份数据库时它跟着一起备份（`deploy.sh backup` 已经包含）；
- 全局配置改了之后，没做覆盖的账号会自动跟着变。

> 为什么不用 `AddJsonFile(reloadOnChange: true)` 一步到位？
> 配置系统在文件变更回调里解析失败会直接抛异常，而那个异常跑在线程池线程上没人接得住——
> **在配置文件里打错一个逗号就能把整个应用搞崩**。所以这里自己拿 `FileSystemWatcher`，
> 失败只记日志、继续用上一份好配置。详见 README 第六节第 10 条。

### 接本地模型（本机中继）

见[第一之二节](#一之二可选接自己电脑上的本地模型)与
[src/KidPlan.Relay/README.md](../src/KidPlan.Relay/README.md)。
Nginx 侧唯一的要求是 `/api/relay/connect` 必须放行 WebSocket 且长连接超时够大
（`deploy/nginx-kidplan.conf` 里已单独写了这个 location）。

---

## 十、安全建议

1. **改掉所有默认凭据**
   - `.env` 里的 `MYSQL_ROOT_PASSWORD` / `MYSQL_PASSWORD` 必须是随机强密码（`install` 已自动生成）；
   - 宝塔面板默认端口 `8888` 与初始账号密码必须修改，并开启面板 SSL 与登录二次验证。
2. **`.env` 绝不能提交到 Git**
   里面是明文数据库密码。仓库根目录的 `.gitignore` 与 `.dockerignore` 都已排除 `.env`，
   无需另外创建；提交前请再确认一次 `git status` 里没有 `.env`。
3. **不要对公网开放 8080 / 3306**
   - compose 已把应用端口绑定在 `127.0.0.1`，请勿改成 `0.0.0.0`；
   - MySQL 服务没有 `ports` 映射，这是刻意设计，不要为了“方便调试”加回去。
4. **阿里云安全组最小化放行**，只留 22（限 IP）、80、443；宝塔端口建议限来源 IP。
5. **定期备份**
   - 用宝塔计划任务每天执行 `bash deploy/deploy.sh backup`；
   - 备份文件建议再同步一份到 OSS（宝塔 → 计划任务 → 备份到 OSS）。
6. **保持更新**
   定期 `bash deploy/deploy.sh update`，并及时更新 Docker 镜像基础版本（`docker compose build --pull`）。
7. **开启 HTTPS 与 HSTS**，避免密码在明文 HTTP 下传输。
8. **限制注册**：自用部署注册完成后，可考虑把站点访问限制为内网/指定 IP，
   或在应用层关闭公开注册，避免陌生人创建账号。
9. **看日志发现异常**：宝塔 → 网站 → 日志，关注大量 `POST /Account/Login`（爆破迹象），
   可在宝塔 **Nginx 防火墙** 中对该路径做频率限制。
10. **`config/ai.json` 里有 API 密钥**
    它不进 Git（已加 `.gitignore` / `.dockerignore`），但**备份和宝塔的文件备份会把它一起打包**。
    建议 `chmod 600 config/ai.json`，并且不要把备份目录放到公开可访问的路径下。
    另外：给模型端点用**单独的、可随时吊销的密钥**，比在多个地方复用同一把主密钥安全得多。
11. **中继令牌等于一把能发起 HTTP 请求的钥匙**
    中继默认只放行本机和内网地址，这是有意收紧的边界，不要为了图省事到处加 `--allow-public`。
    怀疑某台机器丢了就立刻在「本机中继」页**停用或删除**那个节点——旧令牌会立即失效、
    已建立的长连接也会被踢掉。
