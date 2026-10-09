#!/usr/bin/env bash
# =============================================================================
# KidPlan 一键部署 / 运维脚本
#
# 适用环境：阿里云 Linux（Ubuntu / Debian / Alibaba Cloud Linux / CentOS）
#           + 宝塔面板 + Docker（含 compose v2 插件）
#
# 用法：bash deploy/deploy.sh <子命令>
#   install      构建镜像并启动整套服务（默认命令，可重复执行）
#   start        启动已存在的容器
#   stop         停止容器（保留数据）
#   restart      重启容器
#   status       查看容器状态与健康检查结果
#   logs [服务]  跟踪日志，默认跟踪 app（例：logs mysql）
#   update       拉取代码 → 重新构建 → 重建容器 → 清理旧镜像
#   backup       备份数据库到 ./backups/kidplan-时间戳.sql.gz
#   restore <f>  从备份文件恢复数据库（支持 .sql / .sql.gz）
#   migrate      重启应用，让应用启动时自动执行 EF Core 迁移
#   uninstall    停止并删除容器（删除数据卷前会二次确认）
#   help         显示帮助
# =============================================================================

set -euo pipefail
IFS=$'\n\t'

# ---------------------------------------------------------------------------
# 定位脚本自身目录 → 切换到仓库根目录，保证从任意路径执行都能工作
# ---------------------------------------------------------------------------
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
cd -- "${REPO_ROOT}"

readonly SCRIPT_DIR REPO_ROOT
readonly ENV_FILE="${REPO_ROOT}/.env"
readonly ENV_EXAMPLE="${REPO_ROOT}/.env.example"
readonly COMPOSE_FILE="${REPO_ROOT}/docker-compose.yml"
readonly CSPROJ_PATH="${REPO_ROOT}/src/KidPlan.Web/KidPlan.Web.csproj"
readonly BACKUP_DIR="${REPO_ROOT}/backups"
readonly APP_SERVICE="app"
readonly DB_SERVICE="mysql"
readonly DB_NAME_DEFAULT="kidplan"
readonly BACKUP_KEEP_DAYS=14

# ---------------------------------------------------------------------------
# 彩色中文日志
# ---------------------------------------------------------------------------
if [[ -t 1 ]] && [[ "${NO_COLOR:-}" == "" ]]; then
    C_RED=$'\033[0;31m'; C_GREEN=$'\033[0;32m'; C_YELLOW=$'\033[0;33m'
    C_BLUE=$'\033[0;36m'; C_BOLD=$'\033[1m'; C_OFF=$'\033[0m'
else
    C_RED=''; C_GREEN=''; C_YELLOW=''; C_BLUE=''; C_BOLD=''; C_OFF=''
fi

info()  { printf '%s[信息]%s %s\n' "${C_BLUE}"   "${C_OFF}" "$*"; }
ok()    { printf '%s[完成]%s %s\n' "${C_GREEN}"  "${C_OFF}" "$*"; }
warn()  { printf '%s[警告]%s %s\n' "${C_YELLOW}" "${C_OFF}" "$*" >&2; }
error() { printf '%s[错误]%s %s\n' "${C_RED}"    "${C_OFF}" "$*" >&2; }
step()  { printf '\n%s==> %s%s\n' "${C_BOLD}${C_BLUE}" "$*" "${C_OFF}"; }

die() { error "$*"; exit 1; }

# ---------------------------------------------------------------------------
# 基础工具
# ---------------------------------------------------------------------------
DOCKER=()
COMPOSE=()

detect_docker() {
    if ! command -v docker >/dev/null 2>&1; then
        error "未找到 docker 命令，请先安装 Docker。"
        error "官方一键安装脚本： curl -fsSL https://get.docker.com | bash -s docker"
        error "国内加速：       curl -fsSL https://get.docker.com | bash -s docker --mirror Aliyun"
        return 1
    fi

    # 优先使用 compose v2 插件（docker compose）
    if docker compose version >/dev/null 2>&1; then
        DOCKER=(docker)
        COMPOSE=(docker compose)
        return 0
    fi

    # 某些环境必须 sudo 才能访问 docker 守护进程
    if command -v sudo >/dev/null 2>&1 && sudo -n docker compose version >/dev/null 2>&1; then
        DOCKER=(sudo docker)
        COMPOSE=(sudo docker compose)
        warn "当前用户无 docker 权限，已自动改用 sudo docker。"
        return 0
    fi

    if command -v docker-compose >/dev/null 2>&1; then
        error "只检测到旧版独立命令 docker-compose，本项目要求 Docker Compose v2 插件（命令为 'docker compose'）。"
        error "请安装 compose v2 插件后重试，例如："
        error "  Ubuntu/Debian: sudo apt-get update && sudo apt-get install -y docker-compose-plugin"
        error "  Alibaba Cloud Linux / CentOS: sudo yum install -y docker-compose-plugin"
        error "  通用方式:      sudo mkdir -p /usr/local/lib/docker/cli-plugins && \\"
        error "                 sudo curl -SL https://github.com/docker/compose/releases/latest/download/docker-compose-linux-x86_64 -o /usr/local/lib/docker/cli-plugins/docker-compose && sudo chmod +x /usr/local/lib/docker/cli-plugins/docker-compose"
        return 1
    fi

    error "未检测到可用的 'docker compose' 命令，请安装 Docker Compose v2 插件。"
    return 1
}

compose() { "${COMPOSE[@]}" "$@"; }

# 数据库名：优先取 .env，其次取运行中的 MySQL 容器环境变量，最后用默认值
get_db_name() {
    local name=""
    if [[ -f "${ENV_FILE}" ]]; then
        name="$(grep -E '^[[:space:]]*MYSQL_DATABASE=' "${ENV_FILE}" | tail -n1 | cut -d= -f2- | tr -d '"' | tr -d "'" | tr -d '[:space:]' || true)"
    fi
    if [[ -z "${name}" ]] && [[ "${#DOCKER[@]}" -gt 0 ]]; then
        name="$("${DOCKER[@]}" ps --filter "label=com.docker.compose.service=${DB_SERVICE}" --format '{{.ID}}' 2>/dev/null | head -n1 || true)"
        if [[ -n "${name}" ]]; then
            name="$("${DOCKER[@]}" inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "${name}" 2>/dev/null \
                | grep -E '^MYSQL_DATABASE=' | head -n1 | cut -d= -f2- || true)"
        else
            name=""
        fi
    fi
    [[ -n "${name}" ]] || name="${DB_NAME_DEFAULT}"
    printf '%s' "${name}"
}

# ---------------------------------------------------------------------------
# 预检
# ---------------------------------------------------------------------------
preflight() {
    local problems=0

    if [[ "$(uname -s)" != "Linux" ]]; then
        error "本脚本只能在 Linux 服务器上运行（当前系统：$(uname -s)）。"
        error "请在阿里云 Linux 服务器上执行，Windows 本机请用 VS Code 远程 / Git Bash + ssh。"
        problems=$((problems + 1))
    fi

    detect_docker || problems=$((problems + 1))

    if [[ ! -f "${CSPROJ_PATH}" ]]; then
        error "未找到项目文件：src/KidPlan.Web/KidPlan.Web.csproj"
        error "请确认当前目录是仓库根目录，且代码已完整上传。"
        problems=$((problems + 1))
    fi

    if [[ ! -f "${COMPOSE_FILE}" ]]; then
        error "未找到 docker-compose.yml，代码可能上传不完整。"
        problems=$((problems + 1))
    fi

    if [[ ! -f "${ENV_EXAMPLE}" ]]; then
        error "未找到 .env.example，代码可能上传不完整。"
        problems=$((problems + 1))
    fi

    # 磁盘空间：低于 5GB 给出警告（镜像 + 构建缓存 + 数据库都很占空间）
    if command -v df >/dev/null 2>&1; then
        local avail_kb avail_gb
        avail_kb="$(df -Pk / 2>/dev/null | awk 'NR==2 {print $4}' || echo "")"
        if [[ -n "${avail_kb}" ]]; then
            avail_gb=$((avail_kb / 1024 / 1024))
            if (( avail_gb < 5 )); then
                warn "根分区剩余磁盘空间仅约 ${avail_gb}GB，建议至少保留 5GB（镜像 + 构建缓存 + 数据库）。"
                warn "可执行： docker system prune -af  清理无用镜像与构建缓存。"
            else
                info "磁盘剩余空间约 ${avail_gb}GB，充足。"
            fi
        fi
    fi

    # 内存：低于 2GB 建议加 swap
    if [[ -r /proc/meminfo ]]; then
        local mem_mb
        mem_mb="$(awk '/^MemTotal:/ {printf "%d", $2/1024}' /proc/meminfo)"
        if [[ -n "${mem_mb}" ]] && (( mem_mb < 2000 )); then
            warn "服务器物理内存仅 ${mem_mb}MB，构建 .NET 镜像和运行 MySQL 都可能失败。"
            warn "强烈建议先加 2GB swap（执行一次即可，重启后仍有效）："
            warn "  sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile && sudo swapon /swapfile"
            warn "  echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab"
        else
            info "物理内存约 ${mem_mb}MB。"
        fi
    fi

    if (( problems > 0 )); then
        die "环境预检未通过（共 ${problems} 项问题），请修复后重试。"
    fi
    ok "环境预检通过。"
}

# ---------------------------------------------------------------------------
# 模型（AI）配置目录
#
# docker-compose 把宿主机的 ./config 挂进容器 /app/config，全局模型配置
# config/ai.json 就放在那里（可以在宝塔文件管理器里直接改，改完不用重启）。
#
# 这里要做两件事：
#   1) 目录不存在就建出来，并放一份带注释的模板，省得用户对着一片空白发呆；
#   2) 把属主改成容器内的运行用户（uid 64198），否则「保存配置」会失败，
#      页面上只会显示一句「配置文件不可写」，不说原因的话很难查到权限上。
# ---------------------------------------------------------------------------
readonly CONTAINER_UID=64198
readonly CONTAINER_GID=64198

prepare_config_dir() {
    local config_dir="${REPO_ROOT}/config"
    local config_file="${config_dir}/ai.json"
    local template="${config_dir}/ai.example.json"

    mkdir -p "${config_dir}" || die "无法创建 ${config_dir}。"

    if [[ ! -f "${config_file}" && -f "${template}" ]]; then
        cp "${template}" "${config_file}"
        ok "已根据模板生成 ${config_file}（还没配置模型，到「模型设置」页里加即可）。"
    fi

    # 容器内的应用以 uid 64198 运行，绑定挂载的目录属主必须跟着改。
    local owner=""
    owner="$(stat -c '%u:%g' "${config_dir}" 2>/dev/null || true)"
    if [[ "${owner}" != "${CONTAINER_UID}:${CONTAINER_GID}" ]]; then
        local sudo_cmd=""
        if [[ "$(id -u)" -ne 0 ]]; then
            if command -v sudo >/dev/null 2>&1; then
                sudo_cmd="sudo"
            else
                warn "${config_dir} 的属主是 ${owner:-未知}，需要 root 才能改成 ${CONTAINER_UID}:${CONTAINER_GID}。"
                warn "请手动执行： sudo chown -R ${CONTAINER_UID}:${CONTAINER_GID} ${config_dir}"
                return 0
            fi
        fi

        if ${sudo_cmd} chown -R "${CONTAINER_UID}:${CONTAINER_GID}" "${config_dir}" 2>/dev/null; then
            ok "已把 ${config_dir} 的属主设置为 ${CONTAINER_UID}:${CONTAINER_GID}（容器内应用用户）。"
        else
            warn "设置 ${config_dir} 属主失败；应用仍然能读配置，但页面上将无法保存。"
            warn "请手动执行： sudo chown -R ${CONTAINER_UID}:${CONTAINER_GID} ${config_dir}"
        fi
    fi
}

# ---------------------------------------------------------------------------
# .env 处理
# ---------------------------------------------------------------------------
gen_password() {
    local pwd=""
    if command -v openssl >/dev/null 2>&1; then
        pwd="$(openssl rand -base64 24 | tr -d '/+=' | cut -c1-24 || true)"
    fi
    if [[ -z "${pwd}" ]]; then
        pwd="$(head -c 32 /dev/urandom | base64 | tr -d '/+=' | cut -c1-24 || true)"
    fi
    # 兜底：极端情况下再退一步
    if [[ -z "${pwd}" ]]; then
        pwd="KidPlan$(date +%Y%m%d%H%M%S)"
    fi
    printf '%s' "${pwd}"
}

ensure_env_file() {
    if [[ -f "${ENV_FILE}" ]]; then
        ok "已存在 .env，沿用其中的配置。"
        # 提前发现会导致 Compose 解析失败的特殊字符（$ 会被当作变量插值）
        local conn_line=""
        conn_line="$(grep -E '^(MYSQL_PASSWORD|MYSQL_ROOT_PASSWORD)=' "${ENV_FILE}" || true)"
        if [[ "${conn_line}" == *'$'* ]]; then
            error ".env 中的 MySQL 密码包含 \$ 字符，Docker Compose 会把它当作变量插值导致解析失败。"
            error "请把密码改成不含 \$ \" ' \` \\ @ : ; # 和空格的字符串，例如："
            error "  openssl rand -base64 24 | tr -d '/+=' | cut -c1-24"
            die "请修改 .env 后重新执行。"
        fi
    else
        step "生成 .env（随机强密码）"
        local root_pwd app_pwd
        root_pwd="$(gen_password)"
        app_pwd="$(gen_password)"

        umask 077
        cat > "${ENV_FILE}" <<EOF
# 本文件由 deploy/deploy.sh 自动生成于 $(date '+%Y-%m-%d %H:%M:%S')
# 请妥善保管，切勿提交到 Git。修改密码后需重新执行： bash deploy/deploy.sh restart

# --- MySQL ---
MYSQL_ROOT_PASSWORD=${root_pwd}
MYSQL_DATABASE=${DB_NAME_DEFAULT}
MYSQL_USER=kidplan
MYSQL_PASSWORD=${app_pwd}

# --- 邮件（SMTP）：邮箱确认是强制开启的，不配好这里就没有人能注册成功 ---
# QQ 邮箱：smtp.qq.com / 465 / SslOnConnect
SMTP_HOST=smtp.qq.com
SMTP_PORT=465
SMTP_SECURITY=SslOnConnect
# 发件邮箱地址
SMTP_USERNAME=CHANGE_ME@qq.com
# ⚠️ SMTP 授权码，不是邮箱登录密码
# QQ 邮箱获取：设置 → 账户 → POP3/IMAP/SMTP 服务 → 开启并生成授权码
SMTP_PASSWORD=CHANGE_ME_SMTP_AUTH_CODE
# 发件人地址，留空表示用 SMTP_USERNAME
SMTP_FROM_ADDRESS=
# 邮件里确认/重置链接的对外地址，例如 https://kidplan.example.com
PUBLIC_BASE_URL=

# --- 应用 ---
APP_PORT=8080
TZ=Asia/Shanghai
EOF
        chmod 600 "${ENV_FILE}"
        ok "已生成 ${ENV_FILE}（权限 600，密码为随机值）。"
        info "查看数据库密码： cat .env"
    fi

    check_smtp_env
}

# 邮箱确认是强制开启的（Identity__RequireConfirmedAccount=true），
# 没有可用的 SMTP 就没有任何人能注册成功。
# docker-compose.yml 里对 SMTP_USERNAME / SMTP_PASSWORD 用了 :? ，
# 缺配置时 compose 会抛一段变量插值报错——这里提前拦住，给出能照着做的提示。
check_smtp_env() {
    local smtp_user="" smtp_pass=""
    smtp_user="$(grep -E '^SMTP_USERNAME=' "${ENV_FILE}" | head -n1 | cut -d= -f2- || true)"
    smtp_pass="$(grep -E '^SMTP_PASSWORD=' "${ENV_FILE}" | head -n1 | cut -d= -f2- || true)"

    if [[ -n "${smtp_user}" && -n "${smtp_pass}" \
        && "${smtp_user}" != *CHANGE_ME* && "${smtp_pass}" != *CHANGE_ME* ]]; then
        ok "SMTP 已配置（发件账号：${smtp_user}）。"
        return 0
    fi

    error "缺少 SMTP 配置：邮箱确认是强制开启的，不配好就没有人能注册成功。"
    error "请编辑 ${ENV_FILE} ，填上这两项后重新执行："
    error "  SMTP_USERNAME=你的邮箱@qq.com"
    error "  SMTP_PASSWORD=邮箱的 SMTP 授权码（不是邮箱登录密码）"
    error "QQ 邮箱授权码获取路径：设置 → 账户 → POP3/IMAP/SMTP 服务 → 开启并生成授权码"
    die "已停止部署，避免上线一个无法注册的系统。"
}

load_env() {
    # 只读取 KEY=VALUE 形式，忽略注释与空行；不执行任何命令，避免 .env 注入。
    if [[ -f "${ENV_FILE}" ]]; then
        while IFS= read -r line || [[ -n "${line}" ]]; do
            [[ "${line}" =~ ^[[:space:]]*# ]] && continue
            [[ "${line}" =~ ^[[:space:]]*$ ]] && continue
            if [[ "${line}" =~ ^([A-Za-z_][A-Za-z0-9_]*)=(.*)$ ]]; then
                local key="${BASH_REMATCH[1]}" val="${BASH_REMATCH[2]}"
                val="${val%\"}"; val="${val#\"}"
                val="${val%\'}"; val="${val#\'}"
                export "${key}=${val}"
            fi
        done < "${ENV_FILE}"
    fi
    export APP_PORT="${APP_PORT:-8080}"
    export MYSQL_DATABASE="${MYSQL_DATABASE:-${DB_NAME_DEFAULT}}"
}

app_port() { printf '%s' "${APP_PORT:-8080}"; }

# ---------------------------------------------------------------------------
# 等待健康检查
# ---------------------------------------------------------------------------
wait_for_health() {
    local timeout="${1:-120}"
    local waited=0
    local app_cid=""

    app_cid="$(compose ps -q "${APP_SERVICE}" 2>/dev/null || true)"
    if [[ -z "${app_cid}" ]]; then
        error "未找到应用容器，请检查启动日志： bash deploy/deploy.sh logs"
        return 1
    fi

    printf '正在等待应用健康检查 /healthz 通过（最多 %s 秒）' "${timeout}"
    while (( waited < timeout )); do
        local status=""
        status="$("${DOCKER[@]}" inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "${app_cid}" 2>/dev/null || true)"
        if [[ "${status}" == "healthy" ]]; then
            printf '\n'
            ok "应用健康检查通过（health=healthy）。"
            return 0
        fi
        # 容器已退出就没必要再等了
        local running=""
        running="$("${DOCKER[@]}" inspect --format '{{.State.Running}}' "${app_cid}" 2>/dev/null || true)"
        if [[ "${running}" == "false" ]]; then
            printf '\n'
            error "应用容器已退出。最近日志："
            compose logs --tail 50 "${APP_SERVICE}" || true
            return 1
        fi
        sleep 3
        waited=$((waited + 3))
        printf '.'
    done
    printf '\n'
    warn "等待 ${timeout} 秒后仍未变为 healthy，请查看日志： bash deploy/deploy.sh logs"
    return 1
}

# ---------------------------------------------------------------------------
# 子命令
# ---------------------------------------------------------------------------
print_banner() {
    printf '%s\n' "${C_BOLD}${C_BLUE}"
    echo "  ============================================================"
    echo "   KidPlan （小孩计划）一键部署脚本"
    echo "   Docker + MySQL 8 + Blazor Server + 宝塔 Nginx 反向代理"
    echo "  ============================================================"
    printf '%s\n' "${C_OFF}"
}

print_next_steps() {
    local port
    port="$(app_port)"
    echo
    ok "KidPlan 已部署完成！"
    echo
    printf '%s---------------- 访问地址 ----------------%s\n' "${C_BOLD}" "${C_OFF}"
    echo "  服务器本机地址 : http://127.0.0.1:${port}"
    echo "  对外访问地址   : 请先在宝塔面板完成下面的“宝塔站点配置”"
    echo
    printf '%s---------------- 数据库信息 ----------------%s\n' "${C_BOLD}" "${C_OFF}"
    echo "  账号 / 密码 / 库名都保存在： ${REPO_ROOT}/.env （权限 600，请勿提交 Git）"
    echo "  查看命令： cat .env"
    echo "  MySQL 没有对公网开放 3306，只有在容器网络内才能访问。"
    echo
    printf '%s---------------- 宝塔面板后续配置（重要） ----------------%s\n' "${C_BOLD}" "${C_OFF}"
    echo "  1. 宝塔 → 网站 → 添加站点："
    echo "     - 域名填你自己的域名（例如 kidplan.example.com）"
    echo "     - 不要勾选“创建数据库 / FTP / PHP”，纯静态站点即可"
    echo "  2. 进入该站点 → 反向代理 → 添加反向代理："
    echo "     - 代理名称：kidplan"
    echo "     - 目标 URL ：http://127.0.0.1:${port}"
    echo "     - 发送域名 ：\$host"
    echo "     - 添加完成后，把 ${REPO_ROOT}/deploy/nginx-kidplan.conf 的内容"
    echo "       替换/粘贴到该站点的 Nginx 配置中（宝塔 → 网站 → 配置文件）"
    echo "  3. 该站点 → SSL → Let's Encrypt → 申请证书 → 开启“强制 HTTPS”"
    echo "  4. 【最容易漏的一步】开启 WebSocket 支持："
    echo "     - 反向代理设置里勾选“WebSocket 支持”，或确认 Nginx 配置包含："
    echo "         proxy_http_version 1.1;"
    echo "         proxy_set_header Upgrade \$http_upgrade;"
    echo "         proxy_set_header Connection \"upgrade\";"
    echo "     - Blazor Server 依赖 WebSocket，未开启会出现页面反复“正在重新连接”"
    echo "  5. 阿里云安全组只放行 80 / 443，不要放行 3306 / ${port}"
    echo
    printf '%s---------------- 常用运维命令 ----------------%s\n' "${C_BOLD}" "${C_OFF}"
    echo "  bash deploy/deploy.sh status     # 查看状态"
    echo "  bash deploy/deploy.sh logs       # 跟踪应用日志"
    echo "  bash deploy/deploy.sh update     # 更新到最新代码并重建"
    echo "  bash deploy/deploy.sh backup     # 备份数据库"
    echo "  bash deploy/deploy.sh help       # 全部命令"
    echo
}

cmd_install() {
    print_banner
    step "1/5 环境预检"
    preflight

    step "2/5 准备 .env 配置"
    ensure_env_file
    load_env

    step "3/5 拉取基础镜像并构建应用镜像"
    compose pull --quiet "${DB_SERVICE}" || warn "基础镜像拉取失败，将尝试使用本地已有镜像继续。"
    info "开始构建（首次构建需要下载 NuGet 包，可能耗时较久，请耐心等待）……"
    compose build

    step "4/5 启动容器"
    prepare_config_dir
    compose up -d --remove-orphans
    info "当前状态："
    compose ps || true

    step "5/5 等待应用就绪"
    local health_ok=0
    if wait_for_health 120; then
        health_ok=1
    fi

    if (( health_ok == 1 )); then
        print_next_steps
    else
        warn "服务已启动，但健康检查尚未通过。请先执行以下命令排查："
        warn "  bash deploy/deploy.sh logs app"
        warn "  bash deploy/deploy.sh logs mysql"
        info "应用启动时会自动执行数据库迁移，数据库刚初始化完成时可能需要多等一会儿。"
    fi
}

cmd_start() {
    step "启动容器"
    load_env
    compose up -d --remove-orphans
    compose ps || true
    ok "启动命令已执行。"
}

cmd_stop() {
    step "停止容器（保留数据）"
    compose stop
    ok "已停止。重新启动： bash deploy/deploy.sh start"
}

cmd_restart() {
    step "重启容器"
    load_env
    compose restart
    compose ps || true
    ok "已重启。"
}

cmd_status() {
    step "容器状态"
    load_env
    compose ps || true
    echo
    local port
    port="$(app_port)"
    info "应用健康检查："
    local body
    if body="$(curl -fsS --max-time 5 "http://127.0.0.1:${port}/healthz" 2>/dev/null)"; then
        ok "http://127.0.0.1:${port}/healthz -> ${body:-HTTP 200}"
    else
        warn "http://127.0.0.1:${port}/healthz 访问失败（应用可能未就绪，或宿主机没有 curl）。"
    fi
    echo
    info "端口监听情况（应只监听 127.0.0.1:${port}）："
    if command -v ss >/dev/null 2>&1; then
        ss -lntp 2>/dev/null | grep -E ":${port}\b" || info "未发现监听 ${port} 的进程。"
    else
        info "（系统无 ss 命令，跳过端口检查）"
    fi
}

cmd_logs() {
    load_env
    local service="${1:-${APP_SERVICE}}"
    info "跟踪 ${service} 日志（Ctrl+C 退出）……"
    compose logs -f --tail 100 "${service}"
}

cmd_update() {
    print_banner
    step "1/4 更新代码"
    if [[ -d "${REPO_ROOT}/.git" ]] && command -v git >/dev/null 2>&1; then
        info "检测到 Git 仓库，执行 git pull --ff-only ……"
        git -C "${REPO_ROOT}" pull --ff-only || warn "git pull 失败（可能有本地改动），将使用当前代码继续。"
    else
        info "不是 Git 仓库（或未安装 git），跳过代码拉取，直接使用当前目录的代码。"
        info "如果你是用宝塔文件管理器上传的，请先上传新代码再执行本命令。"
    fi

    step "2/4 重新构建镜像"
    load_env
    compose build --pull

    step "3/4 重建容器"
    # 更新时也走一遍：新版本可能新增了配置项，目录属主也可能被 git pull 改掉。
    prepare_config_dir
    compose up -d --remove-orphans
    wait_for_health 120 || warn "健康检查未通过，请查看日志： bash deploy/deploy.sh logs"

    step "4/4 清理悬空镜像"
    "${DOCKER[@]}" image prune -f || true
    ok "更新完成。"
}

cmd_backup() {
    load_env
    local db_name
    db_name="$(get_db_name)"
    mkdir -p "${BACKUP_DIR}"

    local stamp file tmp
    stamp="$(date '+%Y%m%d-%H%M%S')"
    file="${BACKUP_DIR}/kidplan-${stamp}.sql.gz"
    tmp="${file}.part"

    local root_pwd="${MYSQL_ROOT_PASSWORD:-}"
    if [[ -z "${root_pwd}" ]]; then
        die "无法从 .env 读取 MYSQL_ROOT_PASSWORD，无法备份。"
    fi

    step "备份数据库 ${db_name}"
    info "输出文件：${file}"
    # 单条事务保证 InnoDB 表之间数据一致
    if compose exec -T "${DB_SERVICE}" \
            mysqldump -uroot -p"${root_pwd}" \
            --single-transaction --quick --default-character-set=utf8mb4 \
            --databases "${db_name}" | gzip -9 > "${tmp}"; then
        mv -f "${tmp}" "${file}"
        ok "备份完成：${file} （$(du -h "${file}" | cut -f1)）"
    else
        rm -f "${tmp}"
        die "备份失败，请先确认 MySQL 容器正在运行： bash deploy/deploy.sh status"
    fi

    # 清理 14 天前的备份
    local removed
    removed="$(find "${BACKUP_DIR}" -maxdepth 1 -type f -name 'kidplan-*.sql.gz' -mtime "+${BACKUP_KEEP_DAYS}" -print -delete 2>/dev/null | wc -l | tr -d '[:space:]')"
    if [[ -n "${removed}" ]] && (( removed > 0 )); then
        info "已清理 ${removed} 个超过 ${BACKUP_KEEP_DAYS} 天的旧备份。"
    fi
    find "${BACKUP_DIR}" -maxdepth 1 -type f -name '*.part' -mtime +1 -delete 2>/dev/null || true
}

cmd_restore() {
    local input="${1:-}"
    if [[ -z "${input}" ]]; then
        error "用法： bash deploy/deploy.sh restore <备份文件>"
        info "可用备份："
        ls -lh "${BACKUP_DIR}" 2>/dev/null || info "（还没有任何备份）"
        return 1
    fi
    if [[ ! -f "${input}" ]]; then
        die "备份文件不存在：${input}"
    fi

    load_env
    local db_name
    db_name="$(get_db_name)"

    # 检测是否为 gzip 压缩
    local is_gz=0
    if [[ "${input}" == *.gz ]]; then
        is_gz=1
    elif command -v file >/dev/null 2>&1 && file -b "${input}" | grep -qi 'gzip'; then
        is_gz=1
    fi

    warn "即将把 ${input} 恢复到数据库 ${db_name}，该库中现有数据会被覆盖！"
    local reply=""
    read -r -p "确认继续？输入 yes 继续，其它任意键取消： " reply || true
    if [[ "${reply}" != "yes" ]]; then
        info "已取消。"
        return 0
    fi

    local root_pwd="${MYSQL_ROOT_PASSWORD:-}"
    if [[ -z "${root_pwd}" ]]; then
        die "无法从 .env 读取 MYSQL_ROOT_PASSWORD，无法恢复。"
    fi

    step "恢复数据库"
    if [[ "${is_gz}" -eq 1 ]]; then
        gunzip -c -- "${input}" | compose exec -T "${DB_SERVICE}" mysql -uroot -p"${root_pwd}"
    else
        compose exec -T "${DB_SERVICE}" mysql -uroot -p"${root_pwd}" < "${input}"
    fi
    ok "数据已恢复。"
    info "应用数据表结构可能已变化，正在重启应用以自动执行迁移……"
    compose restart "${APP_SERVICE}" >/dev/null
    wait_for_health 120 || warn "应用健康检查未通过，请查看日志： bash deploy/deploy.sh logs"
    ok "恢复流程结束。"
}

cmd_migrate() {
    load_env
    step "重启应用以触发启动时自动迁移（Database__AutoMigrate=true）"
    compose restart "${APP_SERVICE}"
    wait_for_health 120 || warn "健康检查未通过，请查看日志： bash deploy/deploy.sh logs"
    ok "迁移流程结束。若迁移失败，日志中会出现 EF Core 的详细错误。"
}

cmd_uninstall() {
    warn "本操作将停止并删除 KidPlan 的所有容器。"
    local reply=""
    read -r -p "确认卸载容器？输入 yes 继续： " reply || true
    if [[ "${reply}" != "yes" ]]; then
        info "已取消。"
        return 0
    fi

    step "移除容器与应用镜像"
    compose down --remove-orphans || true

    echo
    warn "${C_BOLD}数据库数据存放在 Docker 具名卷 kidplan-mysql-data 中。${C_OFF}"
    warn "删除该卷将【永久丢失所有账号与记录】，且无法恢复。"
    warn "建议先执行备份： bash deploy/deploy.sh backup"
    local purge=""
    read -r -p "是否同时删除数据库数据卷？输入 DELETE 才会删除，其它输入保留数据： " purge || true
    if [[ "${purge}" == "DELETE" ]]; then
        if compose down -v --remove-orphans; then
            ok "容器与数据卷均已删除。"
        else
            warn "数据卷删除失败，请手动检查： docker volume ls | grep kidplan"
        fi
    else
        info "已保留数据卷。下次 install 时会继续使用原有数据。"
    fi
    ok "卸载流程结束。"
}

cmd_help() {
    print_banner
    cat <<'EOF'
用法：
  bash deploy/deploy.sh [子命令] [参数]

子命令：
  install        构建镜像并启动整套服务（默认；可重复执行，幂等）
  start          启动已存在的容器
  stop           停止容器（保留数据）
  restart        重启容器
  status         查看容器状态 + 健康检查 + 端口监听
  logs [服务]    跟踪日志，默认 app；例：bash deploy/deploy.sh logs mysql
  update         拉取代码 → 重建镜像 → 重建容器 → 清理悬空镜像
  backup         备份数据库到 ./backups/kidplan-时间戳.sql.gz，并清理 14 天前的旧备份
  restore <文件> 从备份文件恢复（支持 .sql 与 .sql.gz）
  migrate        重启应用，触发启动时自动执行 EF Core 迁移
  uninstall      停止并删除容器（删除数据卷前会二次确认）
  help           显示本帮助

示例：
  bash deploy/deploy.sh                  # 等同于 install
  bash deploy/deploy.sh logs mysql       # 只看数据库日志
  bash deploy/deploy.sh restore backups/kidplan-20260101-030000.sql.gz

部署完成后请阅读： docs/DEPLOY.md （完整中文部署与排障指南）
EOF
}

# ---------------------------------------------------------------------------
# 入口
# ---------------------------------------------------------------------------
main() {
    local cmd="${1:-install}"
    if (( $# > 0 )); then
        shift || true
    fi

    case "${cmd}" in
        help|-h|--help)
            cmd_help
            return 0
            ;;
        install|start|stop|restart|status|logs|update|backup|restore|migrate|uninstall)
            ;;
        *)
            error "未知子命令：${cmd}"
            cmd_help
            return 1
            ;;
    esac

    preflight

    case "${cmd}" in
        install)   cmd_install "$@" ;;
        start)     cmd_start "$@" ;;
        stop)      cmd_stop "$@" ;;
        restart)   cmd_restart "$@" ;;
        status)    cmd_status "$@" ;;
        logs)      cmd_logs "$@" ;;
        update)    cmd_update "$@" ;;
        backup)    cmd_backup "$@" ;;
        restore)   cmd_restore "$@" ;;
        migrate)   cmd_migrate "$@" ;;
        uninstall) cmd_uninstall "$@" ;;
    esac
}

# 仅在被直接执行时运行入口逻辑；被 source（例如自测脚本）时只加载函数定义。
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    main "$@"
fi
