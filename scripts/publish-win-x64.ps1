[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\releases'))
$packageName = "baidu-netdisk-mcp-$Version-win-x64"
$packageDirectory = [IO.Path]::GetFullPath((Join-Path $releaseRoot $packageName))
$archivePath = [IO.Path]::GetFullPath((Join-Path $releaseRoot "$packageName.zip"))
$checksumPath = "$archivePath.sha256"

function Assert-DescendantPath {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Parent
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the expected directory: $resolvedPath"
    }
}

Assert-DescendantPath -Path $releaseRoot -Parent $repositoryRoot
Assert-DescendantPath -Path $packageDirectory -Parent $releaseRoot
Assert-DescendantPath -Path $archivePath -Parent $releaseRoot

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'cli') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'mcp') -Force | Out-Null

$publishProperties = @(
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:Version=$Version"
)

& dotnet publish (Join-Path $repositoryRoot 'src\BaiduNetdisk.Cli\BaiduNetdisk.Cli.csproj') `
    @publishProperties --output (Join-Path $packageDirectory 'cli')
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }

& dotnet publish (Join-Path $repositoryRoot 'src\BaiduNetdisk.Mcp\BaiduNetdisk.Mcp.csproj') `
    @publishProperties --output (Join-Path $packageDirectory 'mcp')
if ($LASTEXITCODE -ne 0) { throw 'MCP Server publish failed.' }

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\installation.md') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\security.md') -Destination $packageDirectory

$forbiddenNames = @('.env', 'tokens.json', 'appsettings.local.json')
$forbiddenFiles = Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object {
    $forbiddenNames -contains $_.Name.ToLowerInvariant() `
        -or $_.Name.EndsWith('.tokens.json', [StringComparison]::OrdinalIgnoreCase) `
        -or $_.Extension -in @('.pfx', '.p12', '.key')
}
if ($forbiddenFiles) {
    throw 'The release directory contains a credential or local configuration file.'
}

$credentialValues = @(
    [Environment]::GetEnvironmentVariable('BAIDU_CLIENT_ID'),
    [Environment]::GetEnvironmentVariable('BAIDU_CLIENT_SECRET')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_.Length -ge 6 }

foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -Recurse -File) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    foreach ($credentialValue in $credentialValues) {
        if ([Text.Encoding]::UTF8.GetString($bytes).IndexOf($credentialValue, [StringComparison]::Ordinal) -ge 0 `
            -or [Text.Encoding]::Unicode.GetString($bytes).IndexOf($credentialValue, [StringComparison]::Ordinal) -ge 0) {
            throw "Release file $($file.Name) contains a credential from the current environment."
        }
    }
}

$manifestFiles = Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | ForEach-Object {
    $relativePath = $_.FullName.Substring($packageDirectory.TrimEnd('\').Length + 1).Replace('\', '/')
    [ordered]@{
        path = $relativePath
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = $_.Length
    }
}
$manifest = [ordered]@{
    name = 'baidu-netdisk-mcp'
    version = $Version
    runtime = 'win-x64'
    selfContained = $true
    files = @($manifestFiles)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageDirectory 'manifest.json') -Encoding utf8

Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $checksumPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$archiveHash  $([IO.Path]::GetFileName($archivePath))" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host "Release directory: $packageDirectory"
Write-Host "Release archive: $archivePath"
Write-Host "SHA-256: $archiveHash"
