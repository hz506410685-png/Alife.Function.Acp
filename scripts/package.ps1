<#
.SYNOPSIS
把插件打包成 Alife 插件市场格式的 zip：Alife.Function.Acp-<Version>.zip
市场引用格式：raw/refs/heads/main/{PluginId}/{Version}.zip
注意：打包内容位于 zip 根目录（与官方市场格式一致）。
#>
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginDir = Join-Path $repoRoot "Alife.Function.Acp"
$manifest = Get-Content (Join-Path $pluginDir "manifest.json") -Raw | ConvertFrom-Json
$version = $manifest.Version
$zipName = "Alife.Function.Acp-$version.zip"
$outDir = Join-Path $repoRoot "dist"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$zipPath = Join-Path $outDir $zipName

# 用临时目录把插件内容放到 zip 根目录（不包含外层 Alife.Function.Acp 文件夹）
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "acp-pack-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $pluginDir '*') -Destination $tmp -Recurse
    if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "✅ 已生成：$zipPath"
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
