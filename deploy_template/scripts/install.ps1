# ==============================================================================
# Northtropic 智能学习系统 - Windows Server 一键安装及服务配置脚本
# ==============================================================================

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$ServiceName = "NorthtropicEduService"
$DisplayName = "Northtropic 智能学习与AI题库系统"
$Description = "Northtropic 智能学习、AI题库及OCR题目解析系统后台核心服务"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$DeployRootDir = Split-Path -Parent $ScriptDir
$AppDir = Join-Path $DeployRootDir "app"
$ExePath = Join-Path $AppDir "Northtropic.exe"
$AppSettingsPath = Join-Path $AppDir "appsettings.json"

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "       Northtropic 智能学习系统 - Windows Server 一键安装程序" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

# 1. 检查执行环境与关键文件
if (-not (Test-Path $ExePath)) {
    Write-Host "[错误] 未找到可执行文件: $ExePath" -ForegroundColor Red
    Write-Host "请确认安装包是否完整解压，且包含 app\Northtropic.exe 文件。" -ForegroundColor Yellow
    exit 1
}

# 2. 交互配置监听端口
$defaultPort = "5000"
Write-Host "[配置] 请输入系统 Web 访问端口 (默认: $defaultPort)" -ForegroundColor Yellow
$inputPort = Read-Host "按回车直接使用默认端口 [$defaultPort]，或输入自定义端口"
$port = $defaultPort
if (-not [string]::IsNullOrWhiteSpace($inputPort)) {
    if ($inputPort -match "^\d+$" -and [int]$inputPort -ge 80 -and [int]$inputPort -le 65535) {
        $port = $inputPort
    } else {
        Write-Host "[警告] 输入端口无效，将自动采用默认端口: $defaultPort" -ForegroundColor Yellow
        $port = $defaultPort
    }
}
Write-Host "-> 当前选定服务端口: $port" -ForegroundColor Green
Write-Host ""

# 3. 更新 appsettings.json 配置
try {
    if (Test-Path $AppSettingsPath) {
        $jsonContent = Get-Content $AppSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $targetUrl = "http://0.0.0.0:$port"
        $jsonContent | Add-Member -NotePropertyName "Urls" -NotePropertyValue $targetUrl -Force
        $updatedJson = $jsonContent | ConvertTo-Json -Depth 10
        Set-Content -Path $AppSettingsPath -Value $updatedJson -Encoding UTF8
        Write-Host "[√] 成功更新应用配置文件: appsettings.json (监听地址: $targetUrl)" -ForegroundColor Green
    }
} catch {
    Write-Host "[警告] 更新 appsettings.json 失败: $_" -ForegroundColor Yellow
}

# 4. 创建必要的本地数据与存储目录
$tempKeysDir = Join-Path $AppDir "temp-keys"
if (-not (Test-Path $tempKeysDir)) {
    New-Item -ItemType Directory -Path $tempKeysDir -Force | Out-Null
    Write-Host "[√] 创建临时密钥目录: temp-keys" -ForegroundColor Green
}

# 5. 检查并注册/更新 Windows 系统服务
Write-Host ""
Write-Host "[服务] 正在配置 Windows 服务 ($ServiceName)..." -ForegroundColor Cyan

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existingService) {
    Write-Host "-> 检测到已存在同名服务，正在停止旧服务..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    # 更新二进制路径及启动方式
    $binPathCmd = "`"$ExePath`""
    & sc.exe config $ServiceName binPath= $binPathCmd start= auto DisplayName= $DisplayName | Out-Null
    Write-Host "[√] 已更新现有服务配置为最新路径: $ExePath" -ForegroundColor Green
} else {
    $binPathCmd = "`"$ExePath`""
    & sc.exe create $ServiceName binPath= $binPathCmd start= auto DisplayName= $DisplayName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[错误] 注册 Windows 服务失败！错误码: $LASTEXITCODE" -ForegroundColor Red
        exit 1
    }
    Write-Host "[√] Windows 服务注册成功 (设为开机自动启动)" -ForegroundColor Green
}

# 设置服务描述
& sc.exe description $ServiceName $Description | Out-Null

# 配置服务崩溃自动恢复 (1分钟后自动重启，循环恢复)
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Write-Host "[√] 已配置服务故障自愈策略 (崩溃自动秒级重启)" -ForegroundColor Green

# 6. 配置 Windows 高级防火墙入站规则
Write-Host ""
Write-Host "[防火墙] 正在配置防火墙入站放行规则 (TCP 端口: $port)..." -ForegroundColor Cyan
$firewallRuleName = "Northtropic_Web_Port_$port"

try {
    # 尝试使用 PowerShell NetSecurity 模块
    if (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue) {
        Remove-NetFirewallRule -Name $firewallRuleName -ErrorAction SilentlyContinue
        New-NetFirewallRule -Name $firewallRuleName `
            -DisplayName "Northtropic 智能学习系统 (端口 $port)" `
            -Description "允许局域网/互联网客户端访问 Northtropic 系统" `
            -Direction Inbound `
            -LocalPort $port `
            -Protocol TCP `
            -Action Allow `
            -Enabled True | Out-Null
        Write-Host "[√] Windows 防火墙规则添加成功 (端口 $port 已放行)" -ForegroundColor Green
    } else {
        # Fallback to netsh
        & netsh advfirewall firewall delete rule name="$firewallRuleName" | Out-Null
        & netsh advfirewall firewall add rule name="$firewallRuleName" dir=in action=allow protocol=TCP localport=$port description="Northtropic Web Service" | Out-Null
        Write-Host "[√] netsh 防火墙规则添加成功 (端口 $port 已放行)" -ForegroundColor Green
    }
} catch {
    Write-Host "[警告] 防火墙规则配置异常: $_。如果无法从其他机器访问，请手动在防火墙中放行 TCP $port 端口。" -ForegroundColor Yellow
}

# 7. 启动服务并验证运行状态
Write-Host ""
Write-Host "[启动] 正在启动服务..." -ForegroundColor Cyan
Start-Service -Name $ServiceName -ErrorAction SilentlyContinue

$timeout = 15
$running = $false
for ($i = 0; $i -lt $timeout; $i++) {
    Start-Sleep -Seconds 1
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq "Running") {
        $running = $true
        break
    }
}

if ($running) {
    Write-Host "[√] Northtropic 系统服务已成功启动并在后台运行！" -ForegroundColor Green
} else {
    Write-Host "[警告] 服务启动状态检测超时，请运行“4-重启服务.bat”或使用“6-控制台调试运行.bat”查看具体报错信息。" -ForegroundColor Yellow
}

# 8. 获取本机网络 IP 地址并打印访问指引
Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host "                  Northtropic 系统安装部署完成！" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "【访问方式】" -ForegroundColor Cyan
Write-Host "  1. 本机/服务器访问地址:" -ForegroundColor White
Write-Host "     http://localhost:$port" -ForegroundColor Yellow
Write-Host "     http://127.0.0.1:$port" -ForegroundColor Yellow
Write-Host ""

$ipList = @()
try {
    $ipList = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | `
        Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" } | `
        Select-Object -ExpandProperty IPAddress
} catch {
    $ipList = @()
}

if ($ipList -and $ipList.Count -gt 0) {
    Write-Host "  2. 同局域网/办公电脑访问地址 (在其他电脑浏览器输入):" -ForegroundColor White
    foreach ($ip in $ipList) {
        Write-Host "     http://${ip}:$port" -ForegroundColor Cyan
    }
} else {
    Write-Host "  2. 局域网访问地址:" -ForegroundColor White
    Write-Host "     http://<服务器内网IP>:$port" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "【日常运维管理】" -ForegroundColor Cyan
Write-Host "  - 启动服务: 运行 2-启动服务.bat"
Write-Host "  - 停止服务: 运行 3-停止服务.bat"
Write-Host "  - 重启服务: 运行 4-重启服务.bat"
Write-Host "  - 卸载服务: 运行 5-卸载服务(以管理员身份运行).bat"
Write-Host "  - 数据备份: 数据库位于 app\northtropic_study.db (日常复制即可备份)"
Write-Host ""

# 询问是否立即打开浏览器
$openBrowser = Read-Host "是否立即在浏览器中打开系统？(Y/N，默认 Y)"
if ([string]::IsNullOrWhiteSpace($openBrowser) -or $openBrowser -match "^[Yy]") {
    Start-Process "http://localhost:$port"
}
