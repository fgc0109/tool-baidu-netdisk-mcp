# 百度网盘 C# 工具

当前完成第一阶段：百度 OAuth 2.0 授权码流程。项目使用 .NET 8，不依赖第三方 NuGet 包。

已支持：

- 生成授权地址，并使用随机 `state` 防止回调流程中的 CSRF；
- 使用授权码换取 Access Token / Refresh Token；
- 刷新 Access Token；
- 原子写入本地 Token 文件，控制台仅显示脱敏 Token；
- 解析授权码或完整回调 URL，并处理百度 OAuth 错误响应。

## 1. 准备百度应用

先在[百度网盘开放平台](https://pan.baidu.com/union/)创建应用，取得 `API Key` 和 `Secret Key`。

默认使用 `oob` 回调：授权后由页面显示授权码，再粘贴回命令行。若开放平台要求配置回调地址，可设置 `BAIDU_REDIRECT_URI`；换取 Token 时必须使用与获取授权码时完全相同的地址。

> `Secret Key` 只应保存在可信后端或本机环境变量中，不应提交到仓库，也不应放进浏览器、桌面安装包或日志。

## 2. 配置

PowerShell：

```powershell
$env:BAIDU_CLIENT_ID = "你的 API Key"
$env:BAIDU_CLIENT_SECRET = "你的 Secret Key"
$env:BAIDU_REDIRECT_URI = "oob"
```

默认权限为 `basic netdisk`。如果你的网盘应用后台或文档要求逗号分隔，可以覆盖：

```powershell
$env:BAIDU_OAUTH_SCOPE = "basic,netdisk"
```

## 3. 登录授权

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- login
```

程序会打开百度授权页。授权后，将页面显示的授权码粘贴回终端即可。默认 Token 文件位于：

```text
%LOCALAPPDATA%\BaiduNetdiskMcp\tokens.json
```

也可以拆分执行：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- auth-url
dotnet run --project src/BaiduNetdisk.Cli -- exchange --code "授权码"
dotnet run --project src/BaiduNetdisk.Cli -- refresh
dotnet run --project src/BaiduNetdisk.Cli -- show
```

如果使用真实回调 URL，`exchange` 支持直接接收整个 URL；同时传入生成授权地址时记录的 `state`，程序会验证它：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- exchange --code "https://example.com/callback?code=...&state=..." --state "之前的state"
```

## 官方资料

- [百度 OAuth 接入指南](https://openauth.baidu.com/doc/doc.html)
- [百度网盘开放平台](https://pan.baidu.com/union/)
