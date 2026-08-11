<#
.SYNOPSIS
把 Alife.Function.Acp 插件安装到 Alife 的插件目录。

.EXAMPLE
.\scripts\install.ps1                       # 使用默认 Alife 路径
.\scripts\install.ps1 -AlifeRoot D:\Alife   # 指定 Alife 安装目录
#>
param(
    [string]$AlifeRoot = "C:\Users\$env:USERNAME\Documents\Alife"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginDir = Join-Path $repoRoot "Alife.Function.Acp"
$targetDir = Join-Path $AlifeRoot "Storage\Plugins\Alife.Function.Acp"

if (-not (Test-Path $pluginDir)) { throw "找不到插件源码目录：$pluginDir" }
if (-not (Test-Path $AlifeRoot)) { throw "找不到 Alife 安装目录：$AlifeRoot" }

# 备份旧版（如有）
if (Test-Path $targetDir) {
    $bak = "$targetDir.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
    Write-Host "发现旧版本，备份到：$bak"
    Move-Item -LiteralPath $targetDir -Destination $bak
}

Write-Host "安装插件到：$targetDir"
Copy-Item -LiteralPath $pluginDir -Destination $targetDir -Recurse

# 强制重编译：删除旧的编译产物
$compiled = Join-Path $AlifeRoot "Runtime\PluginContext\CompiledPlugins"
if (Test-Path $compiled) {
    Get-ChildItem -LiteralPath $compiled -Filter "Alife.Function.Acp*" -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "删除旧编译产物：$($_.FullName)"; Remove-Item -LiteralPath $_.FullName -Force }
}

Write-Host ""
Write-Host "✅ 安装完成。请重启 Alife，然后检查："
Write-Host "  1) Storage\Configuration\Alife.Function.Acp.AcpService.json 配置 agent 与白名单"
Write-Host "  2) 对话里测试：<agent_list/> <agent_start agent=\"codex\"/> <派活>你好</派活>"
