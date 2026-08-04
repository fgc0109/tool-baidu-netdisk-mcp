# 安装与首次调用

## 环境

官方发布包面向 Windows x64，并包含 .NET 8 运行时；目标电脑不需要另外安装 .NET SDK 或运行时。源码构建需要 .NET 8 SDK 或更新版本。

将发布压缩包解压到仅当前用户可写的固定目录，例如：

```powershell
Expand-Archive .\baidu-netdisk-mcp-0.1.0-win-x64.zip -DestinationPath "$env:LOCALAPPDATA\Programs\BaiduNetdiskMcp"
```

压缩包包含：

- `cli\baidu-netdisk.exe`：授权和人工诊断命令；
- `mcp\baidu-netdisk-mcp.exe`：供 MCP 客户端通过 stdio 启动的服务器；
- `manifest.json`：各文件的 SHA-256 和大小；
- 本安装文档及安全说明。

可以先确认程序能够启动：

```powershell
& "$env:LOCALAPPDATA\Programs\BaiduNetdiskMcp\cli\baidu-netdisk.exe" --help
```

## 百度应用与授权

在百度网盘开放平台创建应用并取得 API Key、Secret Key。打开新的 PowerShell，设置当前进程环境变量：

```powershell
$env:BAIDU_CLIENT_ID = "你的 API Key"
$env:BAIDU_CLIENT_SECRET = "你的 Secret Key"
$env:BAIDU_REDIRECT_URI = "oob"
$env:BAIDU_APP_ROOT = "/apps/你的应用名"
```

执行登录：

```powershell
& "$env:LOCALAPPDATA\Programs\BaiduNetdiskMcp\cli\baidu-netdisk.exe" login
```

浏览器完成授权后，把页面显示的授权码粘贴到终端。默认使用 `oob` 流程，本地程序不监听端口，因此不需要 FRP、公网域名或回调服务。完成后验证首次 API 调用：

```powershell
& "$env:LOCALAPPDATA\Programs\BaiduNetdiskMcp\cli\baidu-netdisk.exe" account
```

Token 默认保存在 `%LOCALAPPDATA%\BaiduNetdiskMcp\tokens.json`。Windows 默认以当前登录用户的 DPAPI 加密，首次读取旧版明文 Token 文件时会自动迁移。

## 配置 MCP 客户端

把以下内容加入客户端的 MCP Server 配置；可执行文件路径必须使用本机绝对路径：

```json
{
  "mcpServers": {
    "baidu-netdisk": {
      "command": "C:\\Users\\你的用户名\\AppData\\Local\\Programs\\BaiduNetdiskMcp\\mcp\\baidu-netdisk-mcp.exe",
      "args": [],
      "env": {
        "BAIDU_CLIENT_ID": "你的 API Key",
        "BAIDU_CLIENT_SECRET": "你的 Secret Key",
        "BAIDU_APP_ROOT": "/apps/你的应用名",
        "BAIDU_LOCAL_ROOTS": "D:\\Downloads;D:\\Uploads"
      }
    }
  }
}
```

重启 MCP 客户端后，先调用 `server_info`，再调用 `get_account`。`BAIDU_LOCAL_ROOTS` 是 MCP 上传和下载可访问的本地目录白名单；Windows 下多个目录使用分号分隔。网盘写操作还会被 `BAIDU_APP_ROOT` 限制。

## 从源码构建发布包

在仓库根目录执行：

```powershell
.\scripts\publish-win-x64.ps1 -Version 0.1.0
```

脚本会生成 `artifacts\releases\baidu-netdisk-mcp-0.1.0-win-x64.zip` 及对应 `.sha256` 文件。发布过程只复制明确列出的程序和文档，拒绝 Token、`.env`、私钥等文件，并检查当前环境中的 API Key/Secret Key 没有出现在产物中。

## 故障排查

- 提示缺少 `BAIDU_CLIENT_ID`：确认变量设置在实际启动 CLI 或 MCP 客户端的进程环境中；仅在另一个 PowerShell 窗口设置不会生效。
- 浏览器授权后换取 Token 失败：检查 API Key、Secret Key、授权码是否匹配同一个应用，并确保 `BAIDU_REDIRECT_URI` 与申请授权码时完全一致。
- 提示需要重新授权：Refresh Token 已撤销、过期或不属于当前应用，重新执行 `login`。
- 无法解密 Token：DPAPI 密文只能由保存它的 Windows 用户在其用户配置文件下解密；切换用户或复制到另一台电脑后需要重新登录。
- MCP 客户端显示服务器退出：直接在 PowerShell 运行 MCP 可执行文件查看标准错误；不要向其标准输入手工输入普通文本。
- 上传或下载路径被拒绝：把确切的本地父目录加入 `BAIDU_LOCAL_ROOTS`，并确认网盘写入路径位于 `BAIDU_APP_ROOT` 内。
- 客户端找不到工具：确认它支持 MCP stdio，并重启客户端以重新执行协议初始化和工具发现。
