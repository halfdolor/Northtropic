param (
    [Parameter(Mandatory=$true)]
    [ValidateSet("start", "stop", "restart", "status")]
    [string]$Action
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$ServiceName = "NorthtropicEduService"

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "       Northtropic 服务运维管理器" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if (-not $svc) {
    Write-Host "[错误] 系统中未安装 $ServiceName 服务！" -ForegroundColor Red
    Write-Host "请先运行“1-安装服务(以管理员身份运行).bat”进行安装。" -ForegroundColor Yellow
    exit 1
}

switch ($Action.ToLower()) {
    "start" {
        if ($svc.Status -eq "Running") {
            Write-Host "[提示] 服务当前已处于运行状态 (Running)。" -ForegroundColor Green
        } else {
            Write-Host "[操作] 正在启动服务..." -ForegroundColor Cyan
            Start-Service -Name $ServiceName
            Start-Sleep -Seconds 2
            $svc.Refresh()
            Write-Host "[结果] 服务当前状态: $($svc.Status)" -ForegroundColor Green
        }
    }
    "stop" {
        if ($svc.Status -eq "Stopped") {
            Write-Host "[提示] 服务当前已处于停止状态 (Stopped)。" -ForegroundColor Yellow
        } else {
            Write-Host "[操作] 正在停止服务..." -ForegroundColor Cyan
            Stop-Service -Name $ServiceName -Force
            Start-Sleep -Seconds 2
            $svc.Refresh()
            Write-Host "[结果] 服务当前状态: $($svc.Status)" -ForegroundColor Green
        }
    }
    "restart" {
        Write-Host "[操作] 正在重启服务..." -ForegroundColor Cyan
        Restart-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
        $svc.Refresh()
        Write-Host "[结果] 服务当前状态: $($svc.Status)" -ForegroundColor Green
    }
    "status" {
        Write-Host "服务名称: $($svc.Name)"
        Write-Host "显示名称: $($svc.DisplayName)"
        Write-Host "服务状态: $($svc.Status)" -ForegroundColor ($svc.Status -eq "Running" ? "Green" : "Red")
        Write-Host "启动类型: $($svc.StartType)"
    }
}
Write-Host ""
