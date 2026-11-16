# Web 管理与 Web API 设计

版本：设计草案 v0.2，2026-11-16。以下保留原设计约定；实际交付状态见 README。v0.1.1 已有原生 WPF、Windows 服务、真实 Web / API / Identity、任务与基础签发流程；Named Pipe 独立维护通道、完整配置管理和告警仍未实现。文中的“拟实现”不代表每个设计项均已交付。

## 1. 运行形态

安装后仍只有一个 Windows 系统服务 `CertPilot.Service`。服务开机自动运行：

- 续期 Worker：调度、签发、文件发布、重试与告警。
- Web 站点：内置浏览器管理页面，查看证书、任务、检测与服务状态；按角色提供操作功能。
- REST API：与内置 Web 分离的版本化数据/操作接口，可供其他前端项目集成。
- 本机控制管道：为 WPF 和维护助手提供初始化、诊断和恢复入口。

ASP.NET Core 可在 Windows 服务内使用 Kestrel 托管，无需额外安装 IIS。[W1] Web 前端使用 Vue 3 + TypeScript，打包为静态资源随安装程序交付；用户服务器无需 Node.js。WPF 保留为本机管理工具，不作为 Web 的运行前提。

所有入口调用同一个应用层，复用配置校验、任务队列、配置修订和数据库；不实现第二套续期逻辑。Web/API 的读请求不会触发签发。关闭 WPF、关闭浏览器、注销远程桌面均不停止服务。

### 生命周期与故障处理

根 Generic Host 持有调度器、数据库连接工厂、任务队列和应用层。`WebListenerHostedService` 在同一进程内控制独立 ASP.NET Core 子宿主的生命周期；子宿主引用共享应用层对象，不再次注册调度器，也不接管/重复释放根宿主的共享资源。

Web 绑定失败、TLS 配置错误由监听适配器捕获，标记 Web degraded，并按有上限的退避重试；不能让异常从根 HostedService 启动方法抛出而使 Worker 停止。不可恢复的进程错误仍交给 SCM 恢复。请求级异常经统一错误处理中间件返回失败，不转化为进程退出。

停止服务时：拒绝新写请求 → 停止接收新任务 → 给任务安全检查点/停机时间 → 停止 Web → 释放共享资源。web/worker 各自报告状态与最近心跳，不能仅凭 HTTP 返回 200 认定续期系统健康。

默认从服务启动即启用 Web；远程访问可选。首次安装须 Pipe、Worker、Web 健康检查都通过；已安装系统发生 Web 故障时保持续期并通过 Pipe/事件日志提示修复。

## 2. 地址、HTTPS 与初始化

建议默认入口（端口可改，以下为设计地址）：

```text
https://localhost:8443/                   管理站点
https://localhost:8443/api/v1/             REST API
https://localhost:8443/openapi/v1.json     已认证接口契约
https://localhost:8443/api-docs/           已认证交互式文档
```

默认仅绑定 IPv4/IPv6 loopback。启用内网访问时绑定显式配置的本机 IP，展示对应 DNS 名称访问入口，例如 `https://certpilot.example.com:8443`。用户填写的访问域名必须指向该服务器；远程可达还取决于网络路由与防火墙。Web 页展示“监听配置”和“检测到的可用状态”，不能以保存配置冒充已经能远程访问。

首次安装由提升权限的维护助手通过本机 Pipe 初始化管理员，无匿名远程注册入口、无固定默认密码。浏览器使用 HTTPS 登录；未初始化时只展示初始化说明，不能在网络上抢先创建管理员。

管理 HTTPS 与本工具待签发证书的引导分开：

1. 安装可使用已有的管理证书，或生成实例专属的本机管理证书，SAN 仅为 localhost，私钥权限限定为服务。
2. 安装器明确提供本机信任选项；拒绝安装信任时，仍可用 WPF 完成配置，直到导入可信管理证书后使用浏览器，不要求绕过浏览器证书校验。
3. 远程开启前必须配置与访问域名匹配、客户端信任的证书，可来自内部 CA 或通过 DNS-01 取得的公网域名证书。本机自签名证书不能被当作远程自动信任。
4. 管理证书私钥放入机器证书存储并授权服务读取；不要使用开发证书运行 Windows 服务。[W1]
5. 管理证书可绑定到已托管证书版本，成功更新后按受控流程刷新 Web 端点；WPF/Pipe 保留修复入口，避免管理证书过期后完全无法维护。

管理端口独立于业务网站，默认不抢占 80/443。监听、证书、Host 白名单或代理设置变更先校验、保存为待生效配置；需要短暂重启 Web 子宿主时在界面说明，并在失败时恢复上一可用配置，续期 Worker 保持运行。

Windows 防火墙规则由安装/维护助手在显式启用远程时创建，限定端口与来源 CIDR；停用时撤销产品创建的规则。生产若使用反向代理，转发头仅信任已配置代理，不接受任意客户端伪造原始 IP/协议。

## 3. 身份与权限

### 人员使用

内置 Web 使用 ASP.NET Core Identity 管理本地账号，Cookie 会话；密码用框架密码哈希存储，禁用公开注册，提供登录限流、失败锁定、退出和管理员重置。Cookie 为 HttpOnly、Secure，合理配置 SameSite；有状态写请求，包括登录和退出，验证 CSRF token/来源。[W2][W3]

| 角色 | 能力 |
| --- | --- |
| Viewer | 查看证书摘要、任务、检测结果与脱敏服务状态 |
| Operator | Viewer + 新建/编辑证书、维护 DNS 凭据、签发/续期/检测、暂停续期 |
| Administrator | Operator + 账号角色、服务 Token、Web 配置、允许输出根目录和告警设置 |

权限在服务端检查；隐藏按钮只改善界面。默认首次创建 Administrator，再按需创建只读人员账号。第一版按单服务器、单管理域设计，不宣称多租户隔离。

远程页面不能选择或读取浏览器所在电脑的磁盘；配置的保存目录属于服务器。可选目录列表仅列出管理员批准的输出根目录。新的根目录授权由本机维护助手处理，Web Operator 不能借服务为任意系统目录提权。

### 程序调用

第一版提供随机、不透明的服务 Token，以 `Authorization: Bearer <token>` 携带：

- 管理员创建；仅创建时显示完整值，服务器仅保存哈希、标识、范围、到期与使用记录。
- 范围为 `certificates:read`、`certificates:write`、`jobs:execute`、`credentials:write`、`system:read`；本期服务 Token 不授予账号/Token/Web 配置管理权限。
- 支持到期、轮换、立即撤销、审计与限流；不通过 URL query 传递。
- 长期 Token 放在集成项目的后端/BFF 或服务端密钥库；不写入前端源码、构建变量或浏览器 localStorage。
- 外部前端经自己的后端使用 Token 时，后端必须验证原有用户权限并映射允许操作，不能无条件代理管理员能力。

第一版自定义前端推荐“浏览器 → 集成项目自己的后端 → CertPilot API”。也可将自定义前端部署到本服务同源位置，复用 Cookie + CSRF。需要完全独立、跨域的纯 SPA 时，增加标准 OIDC Authorization Code + PKCE 与现有身份系统对接作为后续模块；不临时自造 OAuth 服务或向浏览器发永久管理员 Token。Identity 内置的 Bearer 格式也不能被描述为标准 JWT/OIDC 提供方。[W2]

### CORS 与秘密输出边界

同源内置前端无需 CORS。跨域只允许明确登记的 HTTPS origin，逐项限制方法与请求头，并暴露 `ETag`、`Location`、`Retry-After`；默认关闭跨域 Cookie。CORS 不是身份认证，不能用 `*` 加凭据放开管理接口。[W4]

普通 API 和 OpenAPI 示例均不含 DNS 密钥、私钥、PFX 密码。第一版远程 API 不提供私钥/PFX 下载与任意文件读取，相关导出保留本机提升权限操作；证书摘要与公开链可按需后续增加下载接口。应用账号与 Token 永不出现在日志正文中。

## 4. API v1 资源约定

以下为拟实现契约。首个可运行服务交付时提供完整 `openapi.v1.json`、内置文档与 TypeScript 客户端示例，并由 CI 对实现做契约验证；本轮没有生成可调用的接口。

ASP.NET Core 的 OpenAPI 文档生成与交互式 UI 是不同交付项。[W5] 文档、UI 依赖打包在安装包内；禁止暴露测试密钥。正式环境文档需认证，调试写操作同样受 CSRF/Token scope 和业务限制。

| 方法与路径 | 行为 | 最低权限 / Token scope |
| --- | --- | --- |
| `GET /health/live` | Web 进程存活，匿名仅返回最小状态 | 匿名，无证书/路径信息 |
| `GET /health/ready` | Web、Worker、数据库就绪；故障返回 503 | Viewer / `system:read` |
| `GET /api/v1/system/status` | 版本、Web/Worker 心跳、队列、最近错误 | Viewer / `system:read` |
| `GET /api/v1/certificates` | 分页列表，支持状态和名称过滤 | Viewer / `certificates:read` |
| `GET /api/v1/certificates/{id}` | 配置及当前实际证书摘要，返回 ETag | Viewer / `certificates:read` |
| `POST /api/v1/certificates` | 仅创建配置，201 + Location | Operator / `certificates:write` |
| `PUT /api/v1/certificates/{id}` | 完整更新可写配置，必须 If-Match | Operator / `certificates:write` |
| `DELETE /api/v1/certificates/{id}` | 停止托管，保留文件，不吊销；必须 If-Match | Operator / `certificates:write` |
| `POST /api/v1/certificates/{id}/issuances` | 首次或配置变更后签发，返回任务 | Operator / `jobs:execute` |
| `POST /api/v1/certificates/{id}/renewals` | 当前证书续期，返回任务 | Operator / `jobs:execute` |
| `POST /api/v1/certificates/{id}/inspections` | 已配置域名 DNS/TLS 检测，返回任务 | Operator / `jobs:execute` |
| `GET /api/v1/jobs/{jobId}` | 任务阶段、结果、错误、重试时间 | Viewer / `certificates:read` |
| `GET /api/v1/certificates/{id}/events` | 此证书脱敏历史，分页 | Viewer / `certificates:read` |
| `GET /api/v1/credentials` | 凭据元数据，不返回秘密 | Operator / `credentials:write` |
| `POST /api/v1/credentials` | 按提供商写入密钥，只返回元数据 | Operator / `credentials:write` |
| `PUT /api/v1/credentials/{id}` | 替换凭据；必须 If-Match | Operator / `credentials:write` |
| `GET /api/v1/auth/csrf` | Cookie 客户端获取 CSRF token，禁止缓存 | 匿名可取，关联受限 Cookie |
| `POST /api/v1/auth/login` | 限流登录，设置 Cookie；不接受服务 Token 代登录 | 公开入口，验证 CSRF |
| `POST /api/v1/auth/logout` | 撤销当前会话 | Cookie 用户 + CSRF |
| `GET /api/v1/auth/me` | 当前人员角色与可用操作 | Cookie 用户 |
| `POST /api/v1/api-tokens` | 创建限定范围/到期服务 Token，一次性返回 | Administrator Cookie + CSRF |
| `DELETE /api/v1/api-tokens/{id}` | 立即撤销服务 Token | Administrator Cookie + CSRF |

Web 的账号、Token 列表、Web 设置、允许输出根目录和通知管理也提供管理员接口，完整字段在开发阶段随 OpenAPI 细化；不得通过普通查询响应回传秘密。

自动续期开关作为配置的 `autoRenew` 字段更新，遵循同样修订规则。实际证书与配置是不同对象：域名变更后的配置不意味着现有证书已经包含新 SAN。

### 通用规则

- UTF-8 JSON、camelCase，时间为 UTC ISO 8601；UI 显示服务器时区。服务端生成 ID，不以域名或本地路径作为资源 ID。
- 分页：`limit` 默认 20、上限 100，`cursor` 为不透明游标；返回 `items`、`nextCursor`。
- GET 不产生远程检测/签发副作用。检测与签发创建持久任务，不依赖请求连接存活。
- 更新必须 `If-Match: "revision-N"`；未提供返回 428，过期返回 412，防止桌面与 Web 互相覆盖。桌面协议的 expectedRevision 映射同一规则。
- 配置创建与任务 POST 支持并要求 `Idempotency-Key`：按调用主体 + 路由 + 请求哈希保存至少 24 小时；相同键/内容重放相同结果，不同内容返回 409。同证书另有活动任务时返回 409 及现有任务引用。
- 202 返回 `jobId`、`status`、`statusUrl`，以及 `Location` 和建议轮询的 `Retry-After`。不得提前返回“已签发成功”。
- 建议任务轮询 3–5 秒、概览 15–30 秒，页面隐藏时降低频率；429 按 Retry-After 退避。第一版使用轮询，SignalR/SSE 作为后续增强。
- 失败为 `application/problem+json`：`type`、`title`、`status`、`detail`、`code`、`traceId`、字段级 `errors`（适用时）；不回传堆栈或上游密钥。
- 401 无有效身份、403 无权限、404 无资源、409 业务冲突、412 修订冲突、422 配置校验失败、428 缺少前置条件、429 限流、503 相关服务不可用。API 失败返回 JSON，不能跳转 HTML 登录页。

## 5. 请求与响应示例

### 查询

集成项目后端发起：

```http
GET /api/v1/certificates?limit=20 HTTP/1.1
Authorization: Bearer <server-side-token>
Accept: application/json
```

```json
{
  "items": [
    {
      "id": "cert_01",
      "revision": 7,
      "name": "官方网站",
      "domains": ["example.com", "www.example.com"],
      "autoRenew": true,
      "certificate": {
        "status": "valid",
        "notAfterUtc": "2027-01-17T00:00:00Z",
        "domains": ["example.com", "www.example.com"]
      },
      "renewal": {
        "status": "scheduled",
        "nextAttemptAtUtc": "2026-12-18T00:00:00Z"
      },
      "inspection": {
        "status": "notChecked",
        "observedAtUtc": null
      }
    }
  ],
  "nextCursor": null
}
```

以上为演示日期，不是固定续期算法。配置域名与实际证书域名分别返回。详情响应可包含批准的输出目录，普通列表不必暴露服务器磁盘结构。

### 续期任务

```http
POST /api/v1/certificates/cert_01/renewals HTTP/1.1
Authorization: Bearer <server-side-token>
Idempotency-Key: integration-renew-cert01-20261116-001
Content-Type: application/json

{}
```

```http
HTTP/1.1 202 Accepted
Location: /api/v1/jobs/job_01
Retry-After: 3
Content-Type: application/json
```

```json
{
  "jobId": "job_01",
  "status": "queued",
  "statusUrl": "/api/v1/jobs/job_01"
}
```

后续 `GET /api/v1/jobs/job_01` 获得 `queued / running / succeeded / failed`，包含阶段、结果和下一次尝试时间。首次签发用 `/issuances`，不将无当前证书状态称为续期。返回成功前必须完成文件组校验与发布。

### Cookie 客户端

1. 从同源 `GET /api/v1/auth/csrf` 取得请求 token，与浏览器 HttpOnly Cookie 关联。
2. 登录 POST 带 `X-CSRF-TOKEN`，取得会话；成功登录后重新获取 token，后续写请求带该头。
3. GET 读取配置与任务；PUT 同时携带 `If-Match`。
4. 退出 POST 带 CSRF，服务器使会话失效；不靠仅清除前端变量退出。

正式版 Data Protection key ring 保存在服务专属目录并加密保护，升级/重启保持会话机制可用；撤销账号与会话有服务端状态检查。

## 6. “查看检测”的范围

Web 同时展示三种不同事实：

| 检测对象 | 展示内容 | 判断来源 |
| --- | --- | --- |
| 托管证书 | SAN、有效期、指纹、计划与文件发布状态 | 本服务保存的证书和任务 |
| 域名验证条件 | DNS 解析、CAA、凭据有效性或 HTTP 验证路由 | 服务端预检；密钥不输出 |
| 实际网站证书 | TLS 是否可连、SNI 下实际呈现指纹、链校验、域名匹配、有效期、是否与托管版本一致 | Windows 服务器发起的独立 TLS 连接 |

新建证书无需强制存在 HTTPS 站点；尚未部署时 TLS 检测可失败，但不能因此把 ACME 签发标成失败。TLS 检测不替换服务器、不开启端口、不绕过验证。

第一版仅允许 Operator 为已托管配置中的具体 FQDN 发起检测，固定 443 端口；通配符不能直接作为 TLS 连接目标，必须选择同一配置中已存在的具体域名，否则仅支持 DNS 预检。拒绝任意 URL、IP、跳转和自定义端口，避免成为网络探测代理。

解析后检查全部目标地址，默认拒绝 loopback、私网、链路本地、保留地址和云元数据目标；连接固定到已检查 IP，SNI 仍使用域名，重试/重新解析重新校验，防止 DNS 重绑定。多 A/AAAA 分别记录观测，不以单个成功结果代表所有节点。需要内网站点检测时，由本机管理员额外登记具体主机与 CIDR，不允许远程 Operator 自行放宽。

结果必须标明 `observedFrom: server`、`observedAtUtc`、目标域名/IP、握手/证书验证结果及与托管指纹匹配情况。服务器侧探测不等于所有公网或用户电脑都能访问；Web 展示检测时间，不能将历史成功显示成实时可达。

## 7. 新增任务与验收

在原方案上新增约 10 个工作日的估算，整体第一版约 30–35 个工作日。新增任务包含 Web 生命周期隔离、HTTPS 初始化、Identity/角色/Token、API 契约与前端、检测和权限测试；具体安排见实施方案。

验收重点：

- 服务随开机启动，WPF 全程关闭时仍能通过浏览器登录与查看任务。
- 浏览器、WPF 和集成 API 查询一致；双端编辑产生修订冲突，不静默覆盖。
- 内网授权后只在配置的地址/端口和来源范围可访问，未授权远程来源拒绝。
- Viewer 不能触发检测/签发、修改配置或读取凭据；撤销 Token 后立即拒绝请求。
- Cookie 写操作验证 CSRF，跨域来源按名单控制；日志和接口不泄露秘密。
- 任务请求断连仍执行，同幂等键不会重复签发；API 限流不会饿死续期 Worker。
- Web 端口冲突/TLS 证书失效不会停止 Worker；Pipe 可修复，恢复后 Web 重新可用。
- 网站仍提供旧证书时明确显示指纹不一致；DNS/超时/链错误与托管证书状态分别显示。
- OpenAPI 与实现契约一致，自定义前端集成示例能完成查询、发起任务和等待结果。

## 8. 官方依据

以下于 2026-11-16 核验。上面的地址、端口、接口、权限与生命周期隔离为本项目设计约定。

```text
[W1] Microsoft Learn — Host ASP.NET Core in a Windows Service
https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0

[W2] Microsoft Learn — Use Identity to secure a Web API backend for SPAs
https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0

[W3] Microsoft Learn — Prevent Cross-Site Request Forgery
https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0

[W4] Microsoft Learn — Enable Cross-Origin Requests (CORS)
https://learn.microsoft.com/en-us/aspnet/core/security/cors?view=aspnetcore-10.0

[W5] Microsoft Learn — Generate OpenAPI documents
https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0
```
