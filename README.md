# CertPilot · Windows Server 证书自动化

CertPilot 提供 **原生 WPF 桌面管理端 + Windows 后台服务 + Web 管理端 / API**。桌面入口为根目录的 **`CertPilot.exe`**。

CertPilot 是一个面向 Windows Server 的自托管 ACME 证书服务：一个 Windows Service 同时运行续期 Worker、HTTPS 管理端和版本化 Web API。任务、配置和挑战清理记录持久化到 SQLite；浏览器关闭、远程桌面注销或 API 请求断开都不会停止后台任务。

当前仓库包含 .NET 10 WPF 管理端、后台服务、Vue 3 管理端、Cloudflare DNS-01、HTTP-01、PEM/PFX 输出、ARI 续期调度、服务 Token、角色权限、网站 TLS 检测、Windows 安装脚本和 GitHub Actions。默认服务只监听 `127.0.0.1:8443`，开发时可用 loopback HTTP；远程访问必须配置明确的 HTTPS 管理证书和 Host 白名单。

## Windows 下载与使用

下载 [最新 Release](https://github.com/remixjc/CertPilot/releases/latest) 的 Windows x64 ZIP，**完整解压**后双击 **`CertPilot.exe`**。首次使用在 WPF 登录页点击“安装 / 更新本机服务”，安装完成后点击“读取初始账号”再登录；首次登录必须修改随机临时密码。

桌面端是 `net10.0-windows` / `UseWPF` / `WinExe` 的原生 XAML 程序，无需浏览器或 WebView2。它通过认证 API 管理配置与任务，提供证书列表、原生编辑表单、文件夹选择、DNS 凭据、申请 / 续期 / 网站检测、任务详情和本机服务维护。关闭 WPF 不会停止系统服务。桌面端用于本机管理，需要管理员权限；普通人员可以使用 Web 端的角色账号。

```text
CertPilot.exe                       ← 双击此文件打开 WPF 图形界面
service/
  CertPilot.Service.exe             后台服务宿主，由 Windows SCM 启动
  appsettings.json
  appsettings.Local.example.json
  wwwroot/                          随包分发的 Web 静态资源
scripts/
  install-service.ps1
  uninstall-service.ps1
安装服务.cmd
先读我-使用说明.txt
README.md
```

**`CertPilot.Service.exe` 直接启动会出现控制台，它不是桌面入口。** v0.1.0 只交付了服务发布物，没有 WPF 与安装脚本；从 v0.1.1 起使用完整桌面发布包。安装脚本会复制到 `%ProgramFiles%\CertPilot`，注册自动启动服务，配置异常恢复，并创建桌面 / 开始菜单快捷方式。不要只移动单个 EXE；`service`、`wwwroot` 和 `scripts` 必须一起保留。

初始数据默认位于 `%ProgramData%\CertPilot`，初始凭据文件为 `bootstrap-admin.txt`。Windows 数据目录仅 SYSTEM 与 Administrators 可访问。默认 WPF 地址为 `https://localhost:8443`；本机自签名管理证书通过数据目录中的 `management.cer` 精确固定匹配，远程连接使用系统信任链。没有将证书全局加入系统信任库；浏览器使用前应由管理员配置可信管理证书。

升级：退出旧 WPF，完整解压新包，从新包打开 WPF 点击“安装 / 更新本机服务”。配置、数据库与已有证书保留，服务更新期间会短暂停止。卸载：提升权限运行 `scripts/uninstall-service.ps1`，仅移除服务及快捷方式，保留程序、数据和证书。

## 从源码构建

需要 .NET 10 SDK、Node.js 22+。WPF 可交叉编译，运行和 Windows 安装验收必须在 Windows 执行。支持具有桌面体验的 Windows Server；Server Core 不运行 WPF，仍可单独部署服务并使用 Web。

```bash
npm ci --prefix web
npm run build --prefix web
dotnet restore CertPilot.sln --locked-mode
dotnet build CertPilot.sln -c Release --no-restore
dotnet test tests/CertPilot.Tests/CertPilot.Tests.csproj -c Release
```

在 Windows PowerShell 中生成完整自包含 x64 包：

```powershell
./scripts/publish-windows.ps1 -Version 0.1.1
# 本机开发打开 WPF
 dotnet run --project src/CertPilot.Desktop
# 仅运行前台服务宿主（开发调试）
 dotnet run --project src/CertPilot.Service -- --CertPilot:DevelopmentHttp=true --CertPilot:Port=8080
```

打包脚本检查两个 EXE、Web 页面、API 文档、设置和安装脚本均存在。WPF 默认 HTTPS；本机 HTTP 开发需在登录页填写 `http://localhost:8080`。

## 运行配置

服务读取 `appsettings.json`、可选的 `appsettings.Local.json`、环境变量和命令行。配置节为 `CertPilot`：

```json
{
  "CertPilot": {
    "ListenAddress": "127.0.0.1",
    "Port": 8443,
    "DataDirectory": "C:\\ProgramData\\CertPilot",
    "ManagementCertificatePath": "C:\\ProgramData\\CertPilot\\management.pfx",
    "ManagementCertificatePasswordFile": "C:\\ProgramData\\CertPilot\\management-password.txt",
    "AllowedHosts": ["localhost", "127.0.0.1", "certpilot.example.com"],
    "OutputRoots": ["D:\\Certificates"],
    "WebRoots": ["D:\\Sites"],
    "CorsOrigins": [],
    "ScanSeconds": 30,
    "DnsPropagationSeconds": 60,
    "AcmeTimeoutSeconds": 600
  }
}
```

`OutputRoots` 和 `WebRoots` 是服务器本地允许目录，配置保存时严格校验绝对路径、文件名、Windows 保留名称和路径越界。DNS Token 使用 Windows DPAPI LocalMachine 加密；非 Windows 开发环境使用本地 AES key，仅用于本机开发。

## 证书流程

1. 在 Web 管理端添加 Cloudflare Zone DNS Token，或登记 HTTP-01 Webroot。
2. 创建证书配置，选择 Staging 进行首次验证；通配符只允许 DNS-01。
3. 保存后发起申请，API 返回 `202 Accepted` 和持久化 `jobId`。
4. Worker 完成授权、签发、SAN/私钥匹配校验，以新版本目录写入文件并原子切换 `.current.json` 指针。
5. ARI 可用时按 CA 建议窗口安排续期；不可用时按实际证书寿命计算计划时间。失败任务保留订单、密钥和 DNS 清理记录，按退避重试。
6. IIS、Nginx 或其他网站加载证书属于独立部署步骤。网站 TLS 检测从服务器发起固定 443 连接，显示实际证书指纹是否匹配托管版本。

服务不会通过 API 返回 DNS 密钥、私钥、PFX 密码或任意文件。管理员可在服务器本机提升终端运行：

```powershell
CertPilot.Service.exe --pfx-password <certificate-id>
```

## API

在线文档：`/api-docs.html`；认证后可读取 `/openapi/v1.json`。集成后端使用管理员创建的限定范围 Bearer Token；浏览器 Cookie 写请求需 CSRF token。配置更新使用 `If-Match: "revision-N"`，创建和任务请求使用 `Idempotency-Key`。

主要接口：`GET/POST /api/v1/certificates`、`PUT/DELETE /api/v1/certificates/{id}`、`POST /api/v1/certificates/{id}/issuances|renewals|inspections`、`GET /api/v1/jobs/{id}`、`GET/POST/PUT /api/v1/credentials`、管理员的 `/api-tokens` 和 `/users`。

## GitHub Actions 与 Pages

- `ci.yml`：Windows .NET / WPF 构建、单元测试、完整包发布、真实 SCM 服务安装、原生 WPF 启动 / 渲染和 HTTPS API 冒烟验收，上传截图证据。
- `release.yml`：推送 `v*.*.*` 标签，生成完整 `win-x64` ZIP 与 SHA-256，安装及 WPF 冒烟测试通过后才上传 Release。
- `pages.yml`：将项目站点和交互原型发布到 GitHub Pages。首次启用时在仓库 Settings → Pages 选择 GitHub Actions。

站点源码位于 [site/index.html](site/index.html)，可点击界面原型位于 [prototype/index.html](prototype/index.html)。详细技术方案和验收记录见 [docs/implementation-plan.md](docs/implementation-plan.md)、[docs/web-api-design.md](docs/web-api-design.md) 和 [docs/prototype-validation.md](docs/prototype-validation.md)。

## 安全边界

首次部署请先使用 Staging 和本机 loopback 验证；生产环境必须配置可信管理证书、最小目录根和最小 DNS Token 权限。开放远程端口、防火墙规则、IIS/Nginx 证书绑定和域名 DNS 解析属于部署者明确配置的基础设施事项。

## 验证范围与限制

Windows 包冒烟验收包含：原生 XAML 窗口与编辑对话框创建 / 渲染、服务注册和开机启动配置、从其他工作目录启动、精确固定的本机 HTTPS、初始登录 / 强制改密、DNS 凭据和证书配置 CRUD、过期修订拒绝、WPF 退出后服务持续运行、Web 页面随包可访问。测试不发起真实 CA 请求，不代表实际域名签发或 IIS / Nginx 部署已经验收。桌面实现通过 Web API 连接服务；原方案中独立 Named Pipe 修复通道、WPF 内账号 / Token 管理和 IIS / Nginx 自动部署尚未实现，账号 / Token 管理可使用随附 Web 界面。
