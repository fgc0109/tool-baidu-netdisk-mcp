# 安全说明

## 凭据与 Token

- `BAIDU_CLIENT_SECRET` 只从进程环境读取，不写入配置文件或发布包。
- Windows 默认的 `BAIDU_TOKEN_PROTECTION=auto` 使用 DPAPI `CurrentUser` 加密 Token；密文绑定当前 Windows 用户。旧版明文 JSON 会在首次成功读取后自动迁移。
- `BAIDU_TOKEN_PROTECTION=dpapi` 可强制要求 DPAPI；在非 Windows 平台会拒绝启动。
- `BAIDU_TOKEN_PROTECTION=plain` 是显式兼容选项。非 Windows 的 `auto` 也会使用普通文件存储；此时应通过操作系统权限、加密磁盘或外部密钥存储保护 Token 文件。
- MCP 客户端配置如果包含 Secret Key，应限制为当前用户可读，并排除在版本控制、同步盘和诊断包之外。

DPAPI 保护静态磁盘内容，但无法防止同一 Windows 用户下的恶意进程读取环境变量、调用程序或解密数据。不要在不可信机器或共享账户上保存授权。

## 日志与协议

MCP Server 使用 `Microsoft.Extensions.Logging`，最低级别为 `Warning`，所有诊断输出写到标准错误；标准输出只承载 MCP JSON-RPC 帧。CLI 和 MCP 错误边界会清理 `access_token`、`refresh_token`、`client_secret`、`session_secret` 及 Bearer Token，OAuth 服务返回的详细描述不会直接写入异常消息。

不要把命令行完整输出、MCP 客户端日志或 Token 文件上传到公开 Issue。报告问题时可保留错误代码、HTTP 状态码和百度 `request_id`，但应删除授权码、Token、Secret Key、用户名和本地绝对路径。

## 文件访问边界

- MCP 本地上传源与下载目标仅允许位于 `BAIDU_LOCAL_ROOTS` 中；未配置时不允许 MCP 访问本地文件。
- 网盘写操作仅允许位于 `BAIDU_APP_ROOT` 中。
- 下载默认不覆盖，复制/移动默认冲突失败，删除必须显式确认。
- Access Token 只会发送到代码中允许的百度 HTTPS API、上传和下载域名。

## 发布包

`scripts\publish-win-x64.ps1` 生成未裁剪的 Windows x64 单文件、自包含 CLI 和 MCP Server。MCP 工具发现依赖反射，因此发布时明确禁用 trimming。脚本从空的暂存目录开始，只复制两个发布输出及两份文档，并执行以下检查：

- 拒绝 `.env`、Token 文件、本地 appsettings、证书和私钥文件；
- 检查当前环境的 API Key 与 Secret Key 未嵌入任何产物；
- 为每个文件生成 SHA-256 清单，并为最终 ZIP 生成独立校验和。

发布前仍应在隔离构建环境中运行测试，并审查最终清单。仓库中的 `artifacts` 目录被 Git 忽略。
