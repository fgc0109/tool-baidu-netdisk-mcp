# 百度网盘 C# 工具

当前完成 OAuth 2.0 授权码流程以及账号、文件查询、下载和上传。项目使用 .NET 8，不依赖第三方 NuGet 包。

已支持：

- 生成授权地址，并使用随机 `state` 防止回调流程中的 CSRF；
- 使用授权码换取 Access Token / Refresh Token；
- 刷新 Access Token；
- 原子写入本地 Token 文件，控制台仅显示脱敏 Token；
- 解析授权码或完整回调 URL，并处理百度 OAuth 错误响应；
- 查询授权用户信息和网盘容量；
- 浏览、搜索和下载文件；
- 在应用目录内完成流式分片上传、秒传及失败重试。

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
$env:BAIDU_APP_ROOT = "/apps/你的应用名"
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

## 4. 查询账号状态

完成一次登录授权后，查询账号资料：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- account
```

查询网盘总容量、已用容量和剩余容量：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- quota
```

这两个命令从本地 Token 文件读取 Access Token，不需要再次传入 API Key 或 Secret Key。

## 5. 浏览与检索文件

列出根目录：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- ls
```

分页列出指定目录，并按修改时间倒序：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- ls --dir "/资料" --start 0 --limit 100 --order time --desc
```

递归搜索文件名：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- search --key "报告" --dir "/"
```

根据列表或搜索结果中的 `fs_id` 查询详细元数据：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- meta --fs-id "123456789,987654321"
```

`ls`、`search` 和 `meta` 是只读命令，可以读取用户已授权的网盘路径。后续上传能力会按照百度开放平台要求限制到 `/apps/{应用名}`，不会把当前的读取范围误用为写入范围。

## 6. 下载文件

先通过 `ls` 或 `search` 取得文件的 `fs_id`，再下载到明确的本地文件路径：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- download --fs-id "123456789" --output "D:\Downloads\example.zip"
```

下载采用流式传输，不会把整个文件载入内存。程序会先写入目标目录中的随机 `.partial` 临时文件，完成大小和 MD5 校验后才移动到最终路径；失败或按 Ctrl+C 取消时会清理临时文件。

默认禁止覆盖已有文件。如确实需要替换，必须显式传入：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- download --fs-id "123456789" --output "D:\Downloads\example.zip" --overwrite
```

目标目录必须已经存在。Access Token 只会被附加到百度可信 HTTPS 下载域名，不会发送给任意第三方地址。

## 7. 上传文件

`BAIDU_APP_ROOT` 必须与百度开放平台分配给应用的目录一致。远程目标必须是该目录下的绝对文件路径：

```powershell
$env:BAIDU_APP_ROOT = "/apps/你的应用名"
dotnet run --project src/BaiduNetdisk.Cli -- upload `
  --local "D:\Uploads\example.zip" `
  --remote "/apps/你的应用名/example.zip"
```

上传使用固定 4 MiB 分片，依次执行预创建、上传服务定位、缺失分片传输和最终创建。文件按流读取，不会整体载入内存；临时网络或服务端错误会自动重试，按 Ctrl+C 可以取消。

同名文件默认自动改名，避免覆盖已有内容。可通过 `--on-conflict` 显式选择策略：

- `rename`：重名时自动改名，也是默认值；
- `rename-if-different`：分片列表不同时自动改名；
- `overwrite`：覆盖同名文件，只有明确传入时启用。

上传服务只接受 `/apps/{应用名}/` 下的目标，并只信任百度 HTTPS 上传域名。配置缺失、越界路径及不可信上传地址都会在传输前或最终创建前被拒绝。

## 8. 管理文件

所有写操作都受 `BAIDU_APP_ROOT` 限制。创建目录：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- mkdir --path "/apps/你的应用名/archive"
```

复制或移动文件；重复传入 `--source` 可以执行最多 100 项的批量操作：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- copy `
  --source "/apps/你的应用名/a.txt" `
  --source "/apps/你的应用名/b.txt" `
  --dest "/apps/你的应用名/archive"

dotnet run --project src/BaiduNetdisk.Cli -- move `
  --source "/apps/你的应用名/draft.txt" `
  --dest "/apps/你的应用名/archive" `
  --new-name "final.txt"
```

默认同名策略是 `fail`，不会隐式覆盖或改名。需要自动改名或覆盖时，分别显式传入 `--on-conflict rename` 或 `--on-conflict overwrite`。批量操作会逐项输出结果，部分失败时命令返回非零退出码。

重命名文件：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- rename `
  --path "/apps/你的应用名/old.txt" `
  --name "new.txt"
```

删除必须显式传入 `--confirm`，并且支持重复的 `--path`：

```powershell
dotnet run --project src/BaiduNetdisk.Cli -- delete `
  --path "/apps/你的应用名/obsolete.txt" `
  --confirm
```

删除会进入百度网盘的服务端删除流程；工具不会在缺少 `--confirm` 时发送请求。应用目录本身及其外部路径不能被这些命令修改。

## 开发计划

完整的功能边界、验收条件和提交拆分见 [需求文档](docs/requirements.md)。

## 官方资料

- [百度 OAuth 接入指南](https://openauth.baidu.com/doc/doc.html)
- [百度网盘开放平台](https://pan.baidu.com/union/)
