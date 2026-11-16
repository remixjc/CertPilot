# CertPilot · Windows Server 证书自动化

CertPilot 是一个面向 Windows Server 的自托管 ACME 证书服务：一个 Windows Service 同时运行续期 Worker、HTTPS 管理端和版本化 Web API。任务、配置和挑战清理记录持久化到 SQLite；浏览器关闭、远程桌面注销或 API 请求断开都不会停止后台任务。

当前仓库已经包含可运行的 .NET 10 服务、Vue 3 管理端、Cloudflare DNS-01、HTTP-01、PEM/PFX 输出、ARI 续期调度、服务 Token、角色权限、网站 TLS 检测、Windows 安装脚本和 GitHub Actions。默认服务只监听 `127.0.0.1:8443`，开发时可用 loopback HTTP；远程访问必须配置明确的 HTTPS 管理证书和 Host 白名单。

## 快速开始

```bash
npm ci --prefix web
npm run build --prefix web
dotnet restore CertPilot.sln --locked-mode
dotnet test CertPilot.sln
dotnet run --project src/CertPilot.Service -- --CertPilot:DevelopmentHttp=true --CertPilot:Port=8080
```

首次运行会在 `DataDirectory` 写入随机管理员临时密码 `bootstrap-admin.txt`。浏览器打开服务地址，登录后必须设置新密码；密码更新后该文件自动删除。生产环境请使用 HTTPS 管理证书，不要把测试 HTTP 暴露到网络。

Windows 自包含发布和安装：

```powershell
dotnet publish src/CertPilot.Service/CertPilot.Service.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/CertPilot-win-x64
powershell -ExecutionPolicy Bypass -File scripts/install-service.ps1 -Binary .\artifacts\CertPilot-win-x64\CertPilot.Service.exe
```

卸载服务不会删除数据库、配置或已输出的证书文件：`scripts/uninstall-service.ps1`。

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

- `ci.yml`：Windows .NET 构建、测试、自包含发布和 Web 前端构建。
- `release.yml`：推送 `v*.*.*` 标签后上传 `win-x64` ZIP Release。
- `pages.yml`：将项目站点和交互原型发布到 GitHub Pages。首次启用时在仓库 Settings → Pages 选择 GitHub Actions。

站点源码位于 [site/index.html](site/index.html)，可点击界面原型位于 [prototype/index.html](prototype/index.html)。详细技术方案和验收记录见 [docs/implementation-plan.md](docs/implementation-plan.md)、[docs/web-api-design.md](docs/web-api-design.md) 和 [docs/prototype-validation.md](docs/prototype-validation.md)。

## 安全边界

首次部署请先使用 Staging 和本机 loopback 验证；生产环境必须配置可信管理证书、最小目录根和最小 DNS Token 权限。开放远程端口、防火墙规则、IIS/Nginx 证书绑定和域名 DNS 解析属于部署者明确配置的基础设施事项。
