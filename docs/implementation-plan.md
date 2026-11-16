# CertPilot 实施方案

方案日期：2026-11-16。状态：产品与工程设计，可用于拆分开发任务；不代表已完成可安装软件。

## 1. 产品目标与边界

构建一款在 Windows Server 上运行的证书管理工具。安装包注册自动启动的系统服务；服务同时运行证书任务、内置 Web 管理站点和 Web API。桌面管理端、浏览器和第三方前端共用服务中的配置、任务和权限规则。

本版已纳入新增需求：**服务启动即启动 Web 支持；浏览器可查看状态与执行检测；提供独立、版本化 REST API 供自定义前端集成**。详细约定见 [Web 与 API 设计](web-api-design.md)，包括认证、接口、示例和新增验收项。

| 用户需求 | 具体实现 | 交付标准 |
| --- | --- | --- |
| 安装即注册服务，自动启动 | x64 安装包注册 `CertPilot.Service`，设置 Automatic、启动并检查健康状态 | 无人登录时重启服务器，服务仍自动运行 |
| 现代管理界面，多证书管理 | WPF 桌面端；证书列表、详情、配置、DNS 凭据、任务记录、服务设置 | 每张证书独立域名、名称、邮箱、目录、基础文件名与续期状态 |
| Let's Encrypt 免费证书与自动续期 | ACME v2；DNS-01 / HTTP-01；ARI 优先、实际证书寿命兜底 | 可完成真实域名签发，并在无人操作时续期 |
| 退出软件后继续运行 | 管理端与服务为独立进程，通过本机命名管道通信 | 关闭界面、注销 RDP 后，任务不中断；重新打开即可修改 |
| 浏览器查看与检测 | 服务内托管 ASP.NET Core / Kestrel 和 Web 静态资源 | 无人登录时 Web 仍可访问；可看有效期、续期记录和检测结果 |
| 自定义前端集成 | `/api/v1` REST API、OpenAPI、异步任务、可撤销 Token | 第三方前端通过自身后端或获批的用户认证集成，无需访问数据库 |

第一版支持 Windows Server 2019 / 2022 / 2025 x64，桌面端需要桌面体验。Server 2016 可作为追加兼容目标，须单独验收；Server Core 的纯服务 + Web 安装模式需另做兼容验收，不要求安装 WPF。使用公开注册域名；私有后缀域名不属于本期范围。

第一版完成“签发、保存、自动续期”；**更新文件并不等于网站已加载新证书**。IIS 自动绑定、Nginx/Apache reload 作为第二阶段部署模块，需单独授权、权限和验收。正式投产前应确定网站如何加载新证书。

## 2. 关键前提

### 2.1 验证域名控制权

名称、域名、邮箱和保存目录不足以完成签发，还必须有一种可自动执行的域名验证方式。[S1]

| 场景 | 采用方式 | 首次需要配置 | 持续运行条件 |
| --- | --- | --- | --- |
| 普通域名，站点公开且 80 端口可达 | HTTP-01，写入既有站点的验证目录 | 站点物理根目录与验证路径映射 | 公网 80 端口持续可访问，所有前端均能返回验证内容 |
| 通配符证书，或应用不公开 | DNS-01，调用 DNS 服务商 API 添加 TXT | 服务商、最小权限 API 凭据、相关 Zone | DNS API 可访问，凭据有效，有公开权威 DNS 可查的验证记录 |

DNS-01 是默认推荐路径；自动添加/检查/清理 TXT，才能无人值守。手动粘贴 TXT 只适合调试，不计入自动续期支持。通配符 `*.example.com` 不包含根域名 `example.com`，需要时将二者一起填写。

第一版 DNS 适配目标：Cloudflare、阿里云 DNS、腾讯云 DNSPod。按实际 DNS 托管商选择，不按域名注册商推断。先实现一个适配器打通完整流程，再增加其他两家。正式版字段按各提供商的鉴权方式显示，不统一假定为单个 Token。

HTTP-01 默认使用既有站点目录，不抢占 IIS 已使用的 80 端口。测试写入权限、验证 URL 路由、A/AAAA 指向、多节点一致性；检测 CAA 是否允许签发。DNS-01 不要求业务服务器开放入站 80/443，但需出站 HTTPS 访问 ACME 和 DNS API，以及所需 DNS 查询网络条件。[S1]

### 2.2 续期不能写死为“90 天 / 提前 30 天”

按核验时的官方安排，默认新签证书将于 **2027-02-10 改为 64 天**，后续还将缩短。项目必须从实际 X.509 `NotBefore/NotAfter` 和 ARI 建议计算时间，不能靠固定天数。[S2][S3]

### 2.3 联系邮箱不等于自动到期通知

邮箱用作本地联系人和应用告警收件人；如 ACME 服务支持账户联系字段，可按其规则更新。不得承诺 CA 会发到期邮件：Let's Encrypt 已于 **2025-06-04** 结束到期提醒邮件服务。本项目自行提供 SMTP 告警，首次配置后必须发送测试通知。[S4]

## 3. 确定的技术选型

| 层 | 选型 | 理由与约束 |
| --- | --- | --- |
| 运行时 | C# / .NET 10 LTS | Windows 服务与桌面端共用语言；使用受支持系统并跟进安全补丁 [S5] |
| 桌面管理 | WPF + MVVM，自定义 Fluent 风格主题 | 适配 Windows Server；与原型保持布局、字段、状态一致；不依赖浏览器运行时 |
| 后台服务 | .NET Generic Host + `AddWindowsService`，托管续期 Worker 与可恢复 Web 子宿主 | 一个 SCM 服务；Web 监听故障隔离，续期不中断 [S6][S13] |
| 本机通信 | Windows Named Pipes + 版本化 JSON 请求 | 明确 DACL，验证调用方 Windows 身份，拒绝远程客户端 [S7] |
| Web 宿主与 API | ASP.NET Core 10 / Kestrel / `/api/v1` | 与 Worker 共进程，控制器只调用共享应用层，不在请求内直接签发 [S13] |
| Web 管理端 | Vue 3 + TypeScript，构建后静态资源随安装包交付 | 生产环境无需 Node.js；复用本项目 API，也允许替换成自定义前端 |
| 接口文档 | OpenAPI JSON + 内置交互式文档页 | 文档生成与 UI 为不同组件；正式部署均需认证 [S14] |
| Web 身份 | ASP.NET Core Identity Cookie + CSRF；集成客户端使用服务 Token | 用户查看/操作权限和程序调用范围分离 [S15][S16] |
| ACME | Certes 稳定版作为候选，外层 `IAcmeClient` 抽象 | 已有 ACME v2、挑战和 PEM/PFX 能力；验证、调度由应用承担。ARI 需要专项验证，不默认已有支持 [S8] |
| 数据 | SQLite + 显式迁移 | 服务是唯一数据库写入者；小规模多证书管理无需外部数据库 |
| 调度 | `BackgroundService` + `PeriodicTimer` + 持久化任务表 | 一个调度源，重启可恢复；不再叠加计划任务重复执行 |
| 敏感信息 | DPAPI LocalMachine + 严格 NTFS ACL | 只由服务处理明文；机器范围加密不能代替文件访问控制 [S9] |
| 日志 | 结构化滚动文件 + Windows 事件日志 | 界面关闭仍可排障，日志统一脱敏 |
| 安装 | Inno Setup 6，x64，自包含发布 | 生成 `CertPilot-Setup-x64.exe`；安装管理员提权、检查子进程退出码 [S10] |

第三方版本在开工时以稳定发布版本锁定，提交依赖锁与许可证清单；不依赖浮动 latest。当前阶段没有锁定或安装任何生产依赖。

### 3.1 进程结构

```text
本机 WPF ──命名管道──┐
                    │    CertPilot.Service.exe / Windows SCM 自动启动
浏览器 ──HTTPS───────┼──► Web 静态站点 + REST API ──┐
                    │                             ├─ 共享应用层 / 鉴权 / 校验
第三方前端 ──自身后端┘    本机 Pipe 控制端 ──────────┘
      或授权用户会话       ├─ 配置 / SQLite / 密钥库
                         ├─ 持久化任务队列 / 唯一续期调度器
                         ├─ ACME / DNS / HTTP 验证 / TLS 检测
                         └─ 文件发布 / 重试 / 告警 / 审计
                                           ↓
                                 Let's Encrypt / DNS API
```

管理端关闭只终止自身。服务不依赖当前登录用户、托盘、浏览器或管理端的定时器。WPF 管理端向本机管理员开放；Web 按应用账号授权，默认仅监听本机 HTTPS，显式配置后可监听指定内网 IP。公网访问通过额外批准的网络入口部署，不默认暴露全部网卡。

根宿主持有唯一调度器、任务队列和应用层；`WebListenerHostedService` 管理同进程内独立启动/停止的 ASP.NET Core 子宿主，仅复用这些服务，不重复注册 Worker。端口冲突或管理 TLS 配置错误时记录 Web 为 degraded，后台续期继续运行，并由本机 Pipe 提供修复入口。首次安装仍须 Web 与 Worker 双健康检查通过才视为安装成功。完整生命周期规则见 Web 设计文档。

服务默认虚拟账号 `NT SERVICE\CertPilot.Service`，仅获得配置目录和授权输出目录所需权限；网络访问及企业代理策略在目标环境验证。[S11] 安装/升级助手以提升后的管理员身份处理目录授权，日常服务不拥有给任意系统目录修改 ACL 的权限。界面保存新目录时，服务先试写；无权限时给出“授权此目录”的明确操作，经安装助手处理后重试，不假定管理员的权限等于服务权限。

第一版不向远程 UNC 共享路径输出，避免服务身份、网络凭据和非原子文件操作扩大范围。

## 4. 管理界面与交互

设计：低饱和中性色、蓝色主操作、左侧固定导航、清晰表格，成功/待处理/失败均同时显示文字。原型中的域名、证书、服务状态均为模拟。

| 页面 | 内容 | 主要交互 |
| --- | --- | --- |
| 证书管理 | 名称、域名、证书状态、续期状态、有效期、格式 | 新建、进入详情 |
| 新建 / 编辑 | 名称、域名列表、邮箱、验证方式、凭据/站点目录、保存目录、基础文件名、格式、算法、自动续期 | 仅保存；保存并签发；校验字段与输出文件冲突 |
| 证书详情 | 所有域名、实际有效期、下次计划、输出文件、失败原因 | 编辑；立即签发/续期；暂停/恢复；移除托管 |
| DNS 凭据 | 凭据名称、提供商、引用状态 | 新增；替换；验证；被引用时禁止直接删除 |
| 任务记录 | 阶段、结果、错误、重试时间、关联证书 | 查看原因；定位配置；重试 |
| 服务设置 | 运行状态、启动方式、数据位置、调度与告警 | 测试通知；诊断服务连接 |
| Web 与 API | Web/Worker 独立状态、访问地址、监听模式、接口示例 | 本机/内网访问配置；浏览器监控预览；模拟 API 查询 |

浏览器端复用证书列表、详情、任务记录和检测页面。Viewer 只读，Operator 可配置与执行任务，Administrator 可管理 Web 接入和账号。桌面端与 Web 同时修改时以修订号防止覆盖。

第一版完整产品还应有首次运行引导：确认协议、选择测试/正式环境、配置 DNS 凭据、创建第一张证书、执行测试签发。原型重点实现证书核心流程；通知、协议引导、真实服务启停及文件夹选择器不在本次模拟范围。

### 4.1 表单规则

- 域名：逐行或逗号分隔、去重、IDNA/Punycode 规范化；不接受 URL、路径或端口。原型要求国际化域名手动转 Punycode，正式版自动转换。
- 一张证书可含多个域名；不同证书可设置不同名称、目录和文件名。提供商与 CA 上限由能力配置约束，界面不得静默丢弃域名。
- HTTP-01 不能配通配符；DNS-01 必须选择有效凭据。
- 路径由服务校验：本地绝对路径、非系统受保护位置、无越界与危险重解析点，检查运行身份下的实际创建/替换权限。
- 基础文件名禁止路径符、保留设备名称、非法字符、尾随点/空格；相同输出文件不允许被两张托管配置占用。
- 域名/算法等变更将产生新的配置修订，标记“待重新签发”；当前实际证书保留独立展示，不能把旧证书标成已覆盖新增域名。
- 修改输出目录：新位置预检成功后写入当前有效版本；失败保留旧位置并报告错误，直到发布成功才切换生效位置。
- 保存配置与签发是独立动作。“仅保存”不偷偷发起订单；“保存并签发”返回任务 ID，并显示进度。
- 第一版仅自动生成强随机 PFX 密码并加密保存；通过管理员授权操作一次性显示或导出。普通配置读取不返回密码。
- 移除托管不删除用户证书文件、不吊销证书；显式吊销属于后续独立高风险操作。

### 4.2 文件输出约定

例如保存路径 `D:\Certificates\website`，基础文件名 `website`：

```text
website.crt             叶证书 PEM
website.fullchain.pem   叶证书 + 中间证书链 PEM
website.key             对应私钥 PEM
website.pfx             证书 + 私钥 + 中间链，使用强密码
```

PEM / PFX / 同时输出可选。原型显示的是拟生成文件，不存在真实文件。

## 5. 签发和自动续期实现

### 5.1 首次签发

1. 管理端提交配置；服务校验、保存修订、创建任务，返回任务 ID。
2. 记录用户接受的 CA 订阅协议版本、环境与时间；如未接受则等待明确接受，不静默同意。
3. 预检代理/出站 HTTPS、时钟、DNS、CAA、凭据或 HTTP 路由、目标目录权限。
4. 使用当前环境对应的持久化 ACME 账户密钥；账户以环境与内部账户 ID 标识，邮箱仅作为联系信息。
5. 创建订单；按每个域名/挑战生成响应。
6. DNS：添加 TXT，记录提供商返回的记录 ID；轮询相关权威 DNS 与传播情况，超时可重试。根域名和通配符可能共用同名 TXT，须保留多个值，不能互相覆盖。
7. HTTP：写入随机挑战文件；验证映射并等待 CA 检查，不能覆盖其他应用挑战文件。
8. 完成授权、生成 CSR、等待订单完成、下载证书及中间链。
9. 校验证书公私钥匹配、SAN 完整、有效期、链与期望环境；不通过不替换已用证书。
10. 导出本地临时版本、读回校验、进入文件发布事务，再更新数据库的当前版本、指纹、有效期和下次续期时间。
11. 精确清理本任务创建的 TXT/挑战文件；清理失败记录为可恢复任务，不能误删现存验证记录。
12. 写入审计和结果；必要时发应用通知。

测试与生产的 ACME 账户、订单、证书和目录分离，测试证书明显标记且禁止覆盖正式输出。开发与重试测试先走 Staging，避免生产限额。[S12]

### 5.2 调度与重试

建议参数属于本项目设计，后续可通过配置调整：

```text
服务启动：
  恢复数据库与未完成发布事务
  将过期任务租约标记为可恢复
  检查错过的续期窗口，适度错峰入队

每 15 分钟：
  扫描已启用配置，按 nextAttemptAt 入队
  不在每次扫描时向 CA 重复下单

每张证书每日至少两次查询 ARI（含抖动），并遵循响应的 Retry-After：
  可用 → 在 suggestedWindow 内选择时间并持久化
  不可用 → 使用实际寿命计算兜底时间

T = NotAfter - NotBefore
T >= 10 天 → fallbackAt = NotBefore + T × 2/3
T <  10 天 → fallbackAt = NotBefore + T × 1/2

renewAt = min(有效 ARI 选定时间, 保守兜底上限)
若 ARI 要求提前 → 提前执行
已到期/窗口已过 → 尽快入队并告警，失败仍遵循退避
暂停 → 不自动产生新的签发任务；保留已有证书和到期告警
```

按官方集成建议使用 ARI 与寿命比例作为基础。[S3] Certes 的稳定包是否完整支持 ARI 必须在第一阶段验证：读取 directory 的 `renewalInfo`、构造正确的证书标识、解析建议窗口与 `Retry-After`、支持续期订单的 `replaces` 关联。缺失时在适配层补齐并测试；不能把手工下单冒充支持 ARI。

每张证书最多一个活动签发任务；默认全局并发 2。同一 DNS Zone 的记录操作协调串行；对同名挑战支持多值。任务使用数据库租约和配置修订快照，保存新配置不改变正在执行的订单；旧修订的产物不能直接覆盖新修订的生效状态。

失败建议 1 分钟、10 分钟、100 分钟、之后每日重试，添加抖动；CA/DNS 给出的 `Retry-After` 优先。429、网络错误可重试；凭据拒绝、域名非法、CAA 禁止等进入待修复状态并告警。[S3][S12] 手动重试也受同证书互斥、最小间隔和提供商限流约束。

服务重启、重复点击、异常断电不能造成重复签发风暴。异常退出让 SCM 按恢复策略重启；正常升级停机不被当作故障。

### 5.3 文件发布与恢复

单个文件可以使用同卷临时文件替换；多文件整体不能假定一次性原子更新。本项目采用：

1. 在目标卷的受限工作目录准备完整新版本；服务拥有目录，普通用户不可篡改；私钥从创建时就应用 ACL。
2. 保存带文件哈希、旧/新版本、修订号和阶段的发布事务日志。
3. 保留上一有效完整版本；按顺序替换文件，失败恢复上一组，恢复失败标记“输出不一致”并阻止部署，持续告警。
4. 所有文件读回一致后，提交当前版本和数据库元数据；重启时按事务日志恢复未完成事务。
5. 第二阶段由部署模块在整组文件提交后再切换 IIS 绑定或 reload。任意第三方进程自行实时读文件时，无法保证其看不到文件替换的中间瞬间，部署协作必须单独设计。

## 6. 数据、本机协议与 Web API

### 6.1 数据表

| 表 | 关键字段 |
| --- | --- |
| CertificateConfig | Id、Revision、DisplayName、ContactEmail、ValidationType、CredentialId、WebRoot、OutputDirectory、FileBaseName、Formats、KeyAlgorithm、AutoRenew、AcmeAccountId |
| CertificateDomain | CertificateId、NormalizedDomain、SortOrder |
| CertificateVersion | Id、CertificateId、ConfigRevision、SAN、Thumbprint、NotBeforeUtc、NotAfterUtc、KeySecretId、ExportGeneration、Environment、PublishStatus |
| Credential | Id、Provider、DisplayName、EncryptedPayload、Health、UpdatedAtUtc |
| AcmeAccount | Id、Environment、DirectoryUrl、AccountUrl、EncryptedKey、ContactMetadata、AcceptedTermsVersion |
| Job | Id、CertificateId、ConfigRevision、Type、Status、Stage、Attempt、LeaseUntilUtc、NextAttemptAtUtc、ErrorCode、SafeMessage |
| RenewalPlan | CertificateVersionId、AriWindowStartUtc、AriWindowEndUtc、AriRefreshAtUtc、RenewAtUtc |
| PublishTransaction | Id、VersionId、Manifest、Stage、RecoveryState |
| AuditEvent / Notification | OperatorSid、Action、EntityId、TimestampUtc；告警渠道、去重键、投递结果 |
| WebUser / WebRole / ApiToken | Identity 用户与角色；TokenId、TokenHash、Scopes、ExpiresAtUtc、RevokedAtUtc、LastUsedAtUtc；不存储明文服务 Token |
| InspectionResult / WebSettings | 证书引用、DNS/TLS 检测类型、执行位置、观测时间、结果；监听、可信 Host、CORS 和管理证书引用 |

证书有效状态、任务执行状态、自动续期开关和部署状态分开存储。续期失败时证书可能仍有效，不能只用一个状态字段覆盖所有事实。内部时间统一 UTC，界面按服务器时区显示。

### 6.2 命名管道协议

管道示例：`\\.\pipe\CertPilot.Control.v1`。使用显式 DACL：服务 SID、SYSTEM 和本机 Administrators；拒绝匿名、普通用户与远程客户端。设置首实例保护并验证服务端身份，避免伪造管道接收凭据；不要使用开放默认 ACL。[S7]

```text
Envelope: { protocolVersion, requestId, method, params }
Reply:    { requestId, success, data, error: { code, message } }

GetServiceStatus / ListCertificates / GetCertificate
SaveCertificate(expectedRevision) / RemoveCertificate
StartIssue / StartRenewal / SetAutoRenew
ListCredentials / SaveCredential / ValidateCredential
GetJob / ListEvents / ConfigureNotifications / TestNotification
```

服务限流、限制报文长度、连接超时与字段校验；读取配置不回传 API 密钥/私钥/PFX 密码。失败码区分 `DNS_AUTH_FAILED`、`DNS_PROPAGATION_TIMEOUT`、`CAA_FORBIDDEN`、`HTTP_UNREACHABLE`、`OUTPUT_ACCESS_DENIED`、`ACME_RATE_LIMITED` 等。

### 6.3 Web API

对外资源根路径 `/api/v1`，支持证书配置 CRUD、异步签发/续期/检测、任务结果、审计与脱敏状态查询。JSON 命名使用 camelCase，时间为 UTC ISO 8601，失败使用 Problem Details + 稳定错误码。

查询不触发签发，写入有权限与修订检查，长任务返回 `202 Accepted` 和任务地址。第三方前端可通过 OpenAPI 生成 TypeScript 客户端；长期服务 Token 放在集成方后端，浏览器使用用户会话。完整端点、权限、请求和响应见 [Web 与 API 设计](web-api-design.md)。

## 7. 安装、权限、升级和卸载

### 7.1 产物

```text
CertPilot-Setup-x64.exe          签名的安装程序
release-manifest.json           版本、兼容性、哈希
SHA256SUMS.txt                  下载完整性校验

C:\Program Files\CertPilot\
  Manager\CertPilot.Manager.exe
  Service\CertPilot.Service.exe
  Service\wwwroot\               Web 管理端构建结果
  Service\api-docs\              随版本交付的 API 文档资源
  Maintenance\CertPilot.Maintenance.exe

C:\ProgramData\CertPilot\
  data\certpilot.db
  secrets\
  logs\
  backups\
```

自包含 `win-x64` 发布将运行时随程序交付，用户无需预装 .NET；运行时安全修复随产品升级交付。安装包签名证书和时间戳服务需提前准备，费用单独核算。

### 7.2 安装流程

1. 管理员提权，检查系统架构、系统版本、已有服务与安装版本。
2. 复制程序，创建 ProgramData 与日志目录，限制 ACL。
3. 注册 `CertPilot.Service`，配置虚拟账号、Automatic 和失败恢复。
4. 建立服务 SID 与目录权限；配置默认本机 HTTPS、实例专属管理证书和首次管理员初始化入口；启动服务并检查 Pipe、Worker 和 Web 三项健康，而非仅判断进程存在。
5. 失败则停止安装、展示错误、回滚本次新增注册和文件；保留可诊断日志。
6. 创建开始菜单/桌面快捷方式；管理端按本机管理员策略启动。

安装时不占用业务站点的 80/443，建议管理端口 8443（可更改）。本机首次初始化由提升权限的维护助手通过 Pipe 完成；管理证书、浏览器信任与远程访问前提见 Web 设计文档。启用内网时检查指定 IP、TLS 证书和防火墙来源范围，不自动开放所有来源。

Inno Setup 中提升权限使用 `PrivilegesRequired=admin`；通过维护助手执行服务注册与配置，必须等待并检查退出码，不能把 `[Run]` 成功启动命令当作安装成功。[S10]

下面仅为**服务实现并发布后**的管理员 PowerShell 验证示例，不是当前可运行成品的安装步骤：

```powershell
sc.exe create CertPilot.Service `
  binPath= '"C:\Program Files\CertPilot\Service\CertPilot.Service.exe"' `
  start= auto `
  obj= 'NT SERVICE\CertPilot.Service'
if ($LASTEXITCODE -ne 0) { throw "Create service failed" }

sc.exe failure CertPilot.Service reset= 86400 actions= restart/10000/restart/30000/restart/60000
if ($LASTEXITCODE -ne 0) { throw "Configure recovery failed" }

sc.exe start CertPilot.Service
if ($LASTEXITCODE -ne 0) { throw "Start service failed" }

Get-Service CertPilot.Service
```

目录 ACL、事件源注册、服务 SID 类型、升级回滚和健康检查由维护助手统一实现，以上命令不能替代完整安装器。

### 7.3 升级与卸载

- 升级前：暂停新任务，完成或安全中断当前任务、清理租约；使用 SQLite 备份 API 创建一致备份，不能在 WAL 活跃时只复制 `.db`。
- 备份程序、数据库和密钥密文；停服务，替换程序，执行带版本检查的事务迁移，启动并健康检查。
- 失败时恢复配套的程序和数据库；明确区分可逆迁移与不可逆迁移，不能让旧程序直接读取新 schema。
- 卸载先停服务、注销服务，再移除程序；默认保留配置和用户证书。“清除密钥与配置”需单独勾选确认。
- DPAPI LocalMachine 密文不是跨机器迁移备份。[S9] 第二阶段提供用用户口令/恢复密钥加密的迁移包，禁止导出明文密钥压缩包。

## 8. 日志、告警与安全落实

- DNS API 密钥、ACME 账户密钥、私钥和 PFX 密码由服务加密保存；数据库和密钥目录 ACL 限定服务、SYSTEM 与管理员。机器范围 DPAPI 不隔离本机其他账户，因此不能省略 ACL。
- 日志不记录原始授权头、Token、私钥或完整敏感 API 响应。导出诊断包前再做脱敏。
- 输出私钥仅给服务和指定使用者读取；目标目录不得为普通用户可写，避免替换与目录劫持。
- 保存密码/凭据走受控本机管道或有权限的 HTTPS API；所有查询端只显示名称、提供商、可用性和可替换入口，不回传秘密。
- Web 账号、服务 Token、CSRF、精确 CORS、登录限流和审计按 Web 设计文档实现；Web 目录只发布静态管理资源，不映射输出证书或 ProgramData。
- 第一版不提供任意 PowerShell 钩子；后续部署动作使用固定参数化适配器，防止高权限命令注入。
- 首次续期失败告警；持续失败与到期阈值分级告警并去重。短寿命证书阈值按实际寿命比例收缩，不能固定依赖 30 天提醒。
- SMTP 参数与密码加密保存；测试邮件失败应可见。应用中填邮箱而未配置 SMTP 时，明确提示仅有本地事件日志告警。
- 本机服务完全停止时无法自行发送邮件；通过 Windows 事件日志/外部主机监控检查服务心跳属于运维接入，生产部署时落实。
- 客户对 DNS/API 授权、出站代理和时间同步负责；产品预检与错误信息帮助定位，不承诺所有网络条件下都必然成功。

## 9. 项目拆分与执行计划

纳入 Web、认证、检测和 API 后，建议一名熟悉 C#/Windows 服务并能开发 Web 前端的工程师，配合 Windows 测试机与 DNS 测试区，约 **30–35 个工作日**完成第一版；相较原估算增加约 10 个工作日。这是估算，不是工期承诺，外部接口、网络与部署条件会影响进度。

```text
src/
  CertPilot.Manager/             WPF / MVVM
  CertPilot.Service/             SCM Root Host / Worker / Pipe / Web 生命周期
  CertPilot.WebApi/              ASP.NET Core 端点 / 认证 / OpenAPI
  CertPilot.Web/                 Vue 3 / TypeScript 管理前端
  CertPilot.Contracts/           DTO / 协议版本 / 错误码
  CertPilot.Core/                签发与续期用例 / 状态机
  CertPilot.Infrastructure/      SQLite / DPAPI / 文件发布
  CertPilot.Acme/                Certes 包装 / ARI
  CertPilot.Dns/                 提供商接口与适配器
  CertPilot.Maintenance/         服务安装 / ACL / 升级
tests/
  CertPilot.Core.Tests/
  CertPilot.IntegrationTests/
  CertPilot.WebApi.Tests/        鉴权 / 契约 / 异步任务 / 并发更新
  CertPilot.Web.E2E/             浏览器登录 / 查看 / 检测 / 编辑
installer/
  CertPilot.iss
scripts/
  build.ps1
  package.ps1
```

| 阶段 | 预计工作日 | 可验收产物 |
| --- | --- | --- |
| 交互确认、工程骨架与风险验证 | 1–3 | 服务/Pipe/Web 最小闭环；候选 ACME 库、ARI 与目标 OS 验证 |
| 一个 DNS 提供商打通签发 | 4–7 | Staging 实际签发、账户持久化、PEM/PFX、目录权限预检 |
| 稳健自动续期与恢复 | 8–12 | ARI/兜底调度、幂等互斥、退避、断电恢复、文件发布事务 |
| 桌面管理与其余验证适配 | 13–17 | 配置 CRUD、日志、HTTP-01、另外两家 DNS、PFX 密码管理 |
| Web API 与账号权限 | 18–21 | REST、Identity、角色、Token、异步任务、OpenAPI 契约 |
| 浏览器管理与检测 | 22–26 | Vue 页面、DNS/TLS 检测、前端集成示例、并发与 CORS 验证 |
| 安装、升级与告警 | 27–30 | Web 资源打包、TLS 初始化、Setup.exe、回滚、SMTP |
| 系统矩阵与全链路验收 | 31–35 | Windows 重启后 Web/Worker 可用、集成演示、真实签发、签名发布 |

推荐先交付一个 DNS 适配器的纵向可用版本，再扩展其他提供商。第一版必须包含续期失败处理和重启恢复，不能只完成一次签发就视为上线。

构建与打包命令约定（实现对应项目和脚本后执行）：

```powershell
dotnet restore --locked-mode
dotnet test --configuration Release
dotnet publish src/CertPilot.Service -c Release -r win-x64 --self-contained true
dotnet publish src/CertPilot.Manager -c Release -r win-x64 --self-contained true
dotnet publish src/CertPilot.Maintenance -c Release -r win-x64 --self-contained true
.\scripts\package.ps1 -Configuration Release -Runtime win-x64
```

Windows CI 先构建 Web 静态资源并复制到 Service 的 `wwwroot`，再执行自包含发布；不要求用户服务器运行 Node.js。同时执行 WPF 编译、API 契约和权限测试、浏览器测试、签发集成测试和安装器编译。Mac 可看原型与编辑代码；不能在当前 Mac 环境宣称完成 Windows 服务安装、开机自启动或 IIS 验收。

## 10. 验收清单

### 用户可见行为

- 全新服务器双击安装，一次管理员提权，服务自动启动，界面能连接。
- 至少三张证书，各自不同目录/文件名，配置修改能持久化。
- 普通域名、根域名 + 通配符、多个 SAN 均实际签发；私钥匹配、链完整。
- PEM/PFX 使用实际应用或证书解析工具验证，PFX 密码可由授权管理员取得。
- 关闭界面、注销 RDP、重启服务器后分别验证任务执行和配置仍在。
- 测试时钟进入续期窗口，真实产生新证书，更新时间与下次计划正确。
- 暂停单张续期不影响其他配置，手动签发语义明确。
- 浏览器在关闭 WPF、注销 RDP、服务器重启后均可登录，看到同一批证书与任务。
- 自定义前端通过 API 查询、创建配置、发起任务并轮询结果；不同权限账号的行为符合授权。
- 检测区分“托管证书文件”和“站点实际呈现证书”，显示检测时间与服务端观测位置。

### 故障与安全

- 同一证书连点续期只产生一个活动任务；同一 DNS 名称多值 TXT 不相互覆盖。
- 错误 Token、DNS 传播慢、CAA 禁止、HTTP 不可达、IPv6 错配都显示对应原因。
- CA/DNS 429 遵守退避与 Retry-After；重启不清空限流记录。
- 域名变更时旧证书不能错误展示为覆盖新域名；运行中编辑使用配置修订隔离。
- 目录无权限、磁盘满、文件占用、多文件替换中断都保留/恢复旧版本并准确报告。
- 服务异常退出后 SCM 恢复；断电中断签发/发布后可恢复。
- 非管理员无法连接控制管道、读取密钥文件或借服务覆盖系统文件。
- SMTP 投递测试成功；未配置告警和邮件投递失败都有明确状态。
- 安装失败回滚；升级恢复程序与数据库；卸载默认保留证书。
- Web 端口冲突、TLS 失效时服务呈现 degraded，但 Worker 不停；恢复后 Web 可重新监听。
- 未登录返回 401、权限不足 403；Cookie 写请求验证 CSRF，未获批跨域请求不能读取响应。
- 并发配置修改返回 412；相同幂等请求不重复签发；调用者断连后任务仍持久执行。
- 浏览器不能取得集成 Token、私钥和 PFX 密码；检测不能用于访问任意内网目标。

### 交付物

签名安装包、源代码、Web 静态资源、OpenAPI 契约与生成客户端示例、锁定依赖与许可证清单、构建脚本、安装升级说明、故障排查手册、系统矩阵验收记录、文件哈希。

## 11. 官方依据

以下为 2026-11-16 核验的官方文档。文中 `[Sx]` 对应此处；项目的调度间隔、目录结构、界面与工期为设计建议。

```text
[S1] Let's Encrypt — Challenge Types
https://letsencrypt.org/docs/challenge-types/

[S2] Let's Encrypt — 64-Day Certificate Lifetimes Coming Feb 2027
https://letsencrypt.org/2026/10/07/64-day-certs.html

[S3] Let's Encrypt — Integration Guide
https://letsencrypt.org/docs/integration-guide/

[S4] Let's Encrypt — Ending Support for Expiration Notification Emails
https://letsencrypt.org/2025/01/22/ending-expiration-emails.html

[S5] Microsoft / dotnet — .NET 10 Supported OS
https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md

[S6] Microsoft Learn — Create Windows Service using BackgroundService
https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service

[S7] Microsoft Learn — Named Pipe Security and Access Rights
https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights

[S8] Certes — Official Repository / README
https://github.com/fszlin/certes

[S9] Microsoft Learn — How to use data protection
https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection

[S10] Inno Setup — PrivilegesRequired / Exec
https://jrsoftware.org/is6help/topic_setup_privilegesrequired.htm
https://jrsoftware.org/ishelp/topic_isxfunc_exec.htm

[S11] Microsoft Learn — Service Accounts
https://learn.microsoft.com/windows-server/identity/ad-ds/manage/understand-service-accounts

[S12] Let's Encrypt — Rate Limits
https://letsencrypt.org/docs/rate-limits/

[S13] Microsoft Learn — Host ASP.NET Core in a Windows Service
https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0

[S14] Microsoft Learn — Generate OpenAPI documents
https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0

[S15] Microsoft Learn — Use Identity to secure a Web API backend for SPAs
https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0

[S16] Microsoft Learn — Prevent Cross-Site Request Forgery
https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0
```
