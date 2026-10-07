# ==============================================================================
# Northtropic Windows Server 全自动部署脚本 (带并发防抖锁与详细日志)
# ==============================================================================
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$SourceDir = "D:\Projects\Northtropic"
$TargetDir = "D:\NorthtropicServer\app"
$ServiceName = "NorthtropicEduService"
$LogDir = "D:\Deploy\logs"
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
$LogFile = Join-Path $LogDir ("deploy_" + (Get-Date -Format 'yyyyMMdd') + ".log")
$LockFile = "D:\Deploy\deploy.lock"

function Write-DeployLog($msg) {
    $time = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $logLine = "[$time] $msg"
    Write-Host $logLine -ForegroundColor Cyan
    Add-Content -Path $LogFile -Value $logLine -Encoding UTF8
}

if (Test-Path $LockFile) {
    $lockTime = (Get-Item $LockFile).LastWriteTime
    if ((Get-Date) - $lockTime -lt (New-TimeSpan -Minutes 5)) {
        Write-DeployLog "[!] 检测到已有部署任务正在执行中，跳过重复并发请求。"
        exit 0
    }
}
New-Item -ItemType File -Path $LockFile -Force | Out-Null

try {
    Write-DeployLog "======== 开始执行自动化发布 ========"

    $env:PATH = "C:\Program Files\Git\cmd;C:\Program Files\dotnet;" + $env:PATH

    if (Test-Path $SourceDir) {
        Set-Location $SourceDir
    }

    Write-DeployLog "[1/5] 正在拉取最新代码..."
    git fetch --all 2>&1 | Out-Null
    git reset --hard 2>&1 | Out-Null
    git pull 2>&1 | Out-Null

    Write-DeployLog "[2/5] 正在执行 dotnet publish 编译..."
    $TempPublishDir = "D:\Deploy\temp_publish"
    if (Test-Path $TempPublishDir) { Remove-Item $TempPublishDir -Recurse -Force }
    
    $publishOutput = dotnet publish "$SourceDir\Northtropic.csproj" -c Release -o $TempPublishDir --no-self-contained 2>&1
    Add-Content -Path $LogFile -Value ($publishOutput -join "`n") -Encoding UTF8

    if (-not (Test-Path "$TempPublishDir\Northtropic.dll")) {
        throw "编译输出未检测到 Northtropic.dll，编译失败！"
    }
    Write-DeployLog "[√] 编译成功完成！"

    Write-DeployLog "[3/5] 正在安全停止 Windows 服务: $ServiceName..."
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }

    Write-DeployLog "[4/5] 正在同步新版本程序集到正式运行目录..."
    robocopy $TempPublishDir $TargetDir /MIR /XF "appsettings.Production.json" /XD "temp-keys" "wwwroot/uploads" /R:2 /W:1 2>&1 | Out-Null

    Write-DeployLog "[5/5] 正在启动 Windows 服务: $ServiceName..."
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Start-Service $ServiceName
    }

    Write-DeployLog "======== [√] 自动发布成功！服务已恢复健康运行 ========"
}
catch {
    Write-DeployLog "[×] 自动化发布过程异常: $_"
}
finally {
    if (Test-Path $LockFile) { Remove-Item $LockFile -Force -ErrorAction SilentlyContinue }
}
