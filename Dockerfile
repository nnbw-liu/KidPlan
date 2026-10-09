# =============================================================================
# KidPlan.Web 生产镜像（多阶段构建）
# 目标运行环境：阿里云 Linux + 宝塔面板 + Docker
# 容器内只跑明文 HTTP 8080，TLS 由宝塔 Nginx 反向代理终结。
# =============================================================================

# -----------------------------------------------------------------------------
# 阶段 1：构建 / 发布
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只拷贝 csproj 并 restore：只要依赖没变，改动业务代码时这一层会命中缓存，
# 重建镜像时无需重新下载 NuGet 包（宝塔服务器网络一般较慢，这一步很关键）。
COPY src/KidPlan.Web/KidPlan.Web.csproj src/KidPlan.Web/

# --no-restore 不是必须的，但显式 restore 可以吃到上面的层缓存。
RUN dotnet restore src/KidPlan.Web/KidPlan.Web.csproj

# 再拷贝其余源码。这里刻意保持 /src 下的目录结构与仓库一致，
# 因为 csproj 内的相对引用（Components / Data / Migrations）依赖这个结构。
COPY . .

# UseAppHost=false：不生成 Linux 原生可执行文件（apphost），
# 镜像里统一用 `dotnet KidPlan.Web.dll` 启动，镜像更小、跨架构更省心。
RUN dotnet publish src/KidPlan.Web/KidPlan.Web.csproj \
        -c Release \
        -o /app/publish \
        /p:UseAppHost=false

# -----------------------------------------------------------------------------
# 阶段 2：运行时
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl：Docker HEALTHCHECK 要用；tzdata：让 TZ=Asia/Shanghai 生效
# （基础镜像默认只有 UTC，缺 tzdata 时时区设置会被静默忽略，
#   会导致“饭点状态”等按本地时间判断的逻辑出错）。
# 基础镜像是 Debian 系，用 apt-get；装完立刻清理索引以减小镜像体积。
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl tzdata \
    && rm -rf /var/lib/apt/lists/*

ENV TZ=Asia/Shanghai
# 容器内固定监听 8080（明文 HTTP）；不要改成 https。
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
# 官方约定的容器内标记，部分 ASP.NET Core 组件据此调整行为。
ENV DOTNET_RUNNING_IN_CONTAINER=true
# Blazor Server 依赖 SignalR/WebSocket，宝塔 Nginx 反代必须放行 Upgrade 头。
EXPOSE 8080

WORKDIR /app

# 以非 root 用户运行：固定 uid/gid（这里 64198），
# 避免不同服务器上自动分配导致挂载卷权限漂移。
RUN groupadd --gid 64198 kidplan \
    && useradd --uid 64198 --gid 64198 --create-home --shell /usr/sbin/nologin kidplan

COPY --from=build --chown=kidplan:kidplan /app/publish .

USER kidplan

# 容器自检：/healthz 由应用暴露，返回 200 表示应用与其数据库都可用。
# start-period 放宽到 60s，因为应用启动时会自带重试地执行 EF Core 迁移。
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "KidPlan.Web.dll"]
