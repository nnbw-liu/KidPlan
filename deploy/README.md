# deploy/ 目录文件索引

本目录存放 KidPlan 的 Docker 部署与运维产物。**完整的中文部署指南、架构说明与故障排查请阅读
[`docs/DEPLOY.md`](../docs/DEPLOY.md)**；本文件只是一份快速索引。

| 文件 | 作用 |
| --- | --- |
| [`deploy.sh`](deploy.sh) | 一键部署 / 运维脚本（install、start、stop、restart、status、logs、update、backup、restore、migrate、uninstall、help）。自动做环境预检、自动生成带随机强密码的 `.env`、准备 `config/` 目录（按模板生成 `ai.json` 并把属主改成容器内的应用用户）、等待健康检查、清理旧备份。 |
| [`nginx-kidplan.conf`](nginx-kidplan.conf) | 宝塔站点反向代理配置模板。已包含 Blazor Server / SignalR 与本机中继（`/api/relay/connect`）所需的 WebSocket 升级头与超时设置，可直接粘贴到宝塔站点的“配置文件”里。 |

## 仓库根目录下与部署相关的文件

| 文件 | 作用 |
| --- | --- |
| `../Dockerfile` | 多阶段构建镜像：`dotnet/sdk:10.0` 编译发布，`dotnet/aspnet:10.0` 运行；非 root 用户、`TZ=Asia/Shanghai`、内置 `/healthz` 健康检查。 |
| `../.dockerignore` | 构建上下文排除清单（bin/obj、.git、IDE 目录、本地数据库、文档、含密钥的 `config/` 等）。 |
| `../docker-compose.yml` | 编排 MySQL 8 与应用容器；应用端口只绑定 `127.0.0.1`，交由宝塔 Nginx 反代。另有两个挂载：`kidplan-dataprotection-keys` 卷 → `/home/kidplan/.aspnet`（持久化 DataProtection 密钥，删掉它不会丢数据，但会导致所有人被登出），以及 `./config` → `/app/config`（全局模型配置，改完不用重启）。 |
| `../.env.example` | 环境变量模板。复制为 `.env` 并修改密码后使用；**`.env` 绝不能提交到 Git**。 |
| `../config/ai.example.json` | 模型（AI）全局配置的带注释模板。真正的 `config/ai.json` 里会有 API 密钥，已被 `.gitignore` 排除；应用首次启动也会自己生成，不复制模板也能跑。详见 [docs/DEPLOY.md 第十一节](../docs/DEPLOY.md#十一模型ai配置)。 |
| `../src/KidPlan.Relay/` | 本机中继（可选）。想用家里电脑上的本地模型时，把它拷到那台电脑上运行。见 [它的 README](../src/KidPlan.Relay/README.md)。 |

## 最短上手路径

```bash
# 1) 在服务器上进入仓库根目录
cd /www/wwwroot/KidPlan

# 2) 一键部署（会自动生成 .env 与随机密码）
bash deploy/deploy.sh install

# 3) 按屏幕提示，在宝塔面板配置站点 / 反向代理 / SSL / WebSocket
#    反向代理配置内容见 deploy/nginx-kidplan.conf
```

> ⚠️ **首次部署会被 SMTP 配置拦住一次**，这是有意的。
> 邮箱确认是强制开启的（`Identity__RequireConfirmedAccount=true`），没有可用的 SMTP
> 就没有任何人能注册成功，所以脚本宁可停下也不上线一个坏掉的注册流程。
> 按提示编辑 `.env`，填好这两项后再执行一次 `install`：
>
> ```ini
> SMTP_USERNAME=你的邮箱@qq.com
> SMTP_PASSWORD=邮箱的 SMTP 授权码      # 不是邮箱登录密码！
> ```
>
> 建议顺便把 `PUBLIC_BASE_URL=https://你的域名` 也填上，
> 否则邮件里的确认/重置链接可能指向容器内网地址（见 docs/DEPLOY.md 排错第 7 条）。

> 注意：所有 `.sh` 文件必须是 **LF** 换行。用 Windows 上传后如报
> `bash: $'\r': command not found`，请执行：
> `sed -i 's/\r$//' deploy/deploy.sh`（或 `dos2unix deploy/*.sh`）
