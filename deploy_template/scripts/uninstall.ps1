# ==============================================================================
# Northtropic 智能学习系统 - Windows Server 服务卸载脚本
# ==============================================================================

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$ServiceName = "NorthtropicEduService"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$DeployRootDir = Split-Path -Parent $ScriptDir
$AppDir = Join-Path $DeployRootDir "app"
$AppSettingsPath = Join-Path $AppDir "appsettings.json"

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "       Northtropic 智能学习系统 - 服务卸载程序" -ForegroundColor Yellow
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

$confirm = Read-Host "确定要停止并卸载 Northtropic 系统服务吗？(Y/N，输入 Y 继续)"
if ($confirm -notmatch "^[Yy]") {
    Write-Host "[取消] 卸载操作已取消。" -ForegroundColor Gray
    exit 0
}

# 1. 停止服务
Write-Host "[1/3] 正在停止系统后台服务..." -ForegroundColor Cyan
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq "Running") {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    Write-Host "[√] 服务已成功停止。" -ForegroundColor Green
} else {
    Write-Host "[信息] 未检测到正在运行的 $ServiceName 服务。" -ForegroundColor Gray
}

# 2. 删除服务
Write-Host "[2/3] 正在从系统注销服务..." -ForegroundColor Cyan
& sc.exe delete $ServiceName | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "[√] Windows 服务已成功删除注销。" -ForegroundColor Green
} else {
    Write-Host "[提示] 服务已不存在或已被清理。" -ForegroundColor Gray
}

# 3. 清理防火墙规则
Write-Host "[3/3] 正在清理防火墙规则..." -ForegroundColor Cyan
try {
    if (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue) {
        $rules = Get-NetFirewallRule -Name "Northtropic_*" -ErrorAction SilentlyContinue
        if ($rules) {
            $rules | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        }
    }
} catch {
    # Ignore
}
Write-Host "[√] 防火墙临时规则已清理。" -ForegroundColor Green

Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host "                  Northtropic 服务已成功卸载！" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "【重要数据安全提示】" -ForegroundColor Yellow
Write-Host "  * 系统的业务数据 (SQLite 数据库文件: app\northtropic_study.db) 及用户文件已完整保留，未被删除。" -ForegroundColor White
Write-Host "  * 如需彻底删除所有文件，直接手动删除整个解压文件夹即可。" -ForegroundColor White
Write-Host "  * 如需重新安装，再次运行“1-安装服务(以管理员身份运行).bat”即可恢复。" -ForegroundColor White
Write-Host ""
