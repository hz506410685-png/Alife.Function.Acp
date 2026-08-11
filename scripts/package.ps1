<#
.SYNOPSIS
把插件打包成 Alife 插件市场格式的 zip：Alife.Function.Acp-<Version>.zip
市场引用格式：raw/refs/heads/main/{PluginId}/{Version}.zip
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

# 用临时目录只打包插件内容（不含外层文件夹）
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "acp-pack-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try {
    Copy-Item -LiteralPath $pluginDir -Destination $tmp -Recurse
    $inner = Join-Path $tmp "Alife.Function.Acp"
    if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -LiteralPath $inner -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "✅ 已生成：$zipPath"
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
