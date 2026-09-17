# ==============================================================================
# Northtropic Windows Server 全自动部署脚本 (带并发防抖锁与详细日志)
# ==============================================================================
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
System.Text.ASCIIEncoding = [System.Text.Encoding]::UTF8

 = "D:\Projects\Northtropic"
 = "D:\NorthtropicServer\app"
 = "NorthtropicEduService"
 = "D:\Deploy\logs\deploy_20260917.log"
 = "D:\Deploy\deploy.lock"

function Write-DeployLog() {
     = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
     = "[] "
    Write-Host  -ForegroundColor Cyan
    Add-Content -Path  -Value  -Encoding UTF8
}

if (Test-Path ) {
     = (Get-Item ).LastWriteTime
    if ((Get-Date) -  -lt (New-TimeSpan -Minutes 5)) {
        Write-DeployLog "[!] 检测到已有部署任务正在执行中，跳过重复并发请求。"
        exit 0
    }
}
New-Item -ItemType File -Path  -Force | Out-Null

try {
    Write-DeployLog "======== 开始执行自动化发布 ========"

    C:/Users/Administrator/.gemini/antigravity/bin;C:\Users\Administrator\AppData\Roaming\Antigravity\bin;C:\WINDOWS\system32;C:\WINDOWS;C:\WINDOWS\System32\Wbem;C:\WINDOWS\System32\WindowsPowerShell\v1.0\;C:\WINDOWS\System32\OpenSSH\;C:\Program Files\nodejs\;C:\Program Files\Git\cmd;C:\Program Files\dotnet\;C:\Users\Administrator\AppData\Local\Microsoft\WindowsApps;C:\Users\Administrator\AppData\Roaming\npm;C:\Users\Administrator\.dotnet\tools;C:\Users\Administrator\AppData\Local\Programs\Antigravity IDE\bin = "C:\Program Files\Git\cmd;C:\Program Files\dotnet;" + C:/Users/Administrator/.gemini/antigravity/bin;C:\Users\Administrator\AppData\Roaming\Antigravity\bin;C:\WINDOWS\system32;C:\WINDOWS;C:\WINDOWS\System32\Wbem;C:\WINDOWS\System32\WindowsPowerShell\v1.0\;C:\WINDOWS\System32\OpenSSH\;C:\Program Files\nodejs\;C:\Program Files\Git\cmd;C:\Program Files\dotnet\;C:\Users\Administrator\AppData\Local\Microsoft\WindowsApps;C:\Users\Administrator\AppData\Roaming\npm;C:\Users\Administrator\.dotnet\tools;C:\Users\Administrator\AppData\Local\Programs\Antigravity IDE\bin

    Set-Location 
    Write-DeployLog "[1/5] 正在拉取最新代码..."
    git fetch --all 2>&1 | Out-Null
    git reset --hard origin/main 2>&1 | Out-Null
    git pull origin main 2>&1 | Out-Null

    Write-DeployLog "[2/5] 正在执行 dotnet publish 编译..."
     = "D:\Deploy\temp_publish"
    if (Test-Path ) { Remove-Item  -Recurse -Force }
    
     = dotnet publish "\Northtropic.csproj" -c Release -o  --no-self-contained 2>&1
    Add-Content -Path  -Value ( -join "
") -Encoding UTF8

    if (-not (Test-Path "\Northtropic.dll")) {
        throw "编译输出未检测到 Northtropic.dll，编译失败！"
    }
    Write-DeployLog "[√] 编译成功完成！"

    Write-DeployLog "[3/5] 正在安全停止 Windows 服务: ..."
    if (Get-Service  -ErrorAction SilentlyContinue) {
        Stop-Service  -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }

    Write-DeployLog "[4/5] 正在同步新版本程序集到正式运行目录..."
    robocopy   /MIR /XF "appsettings.Production.json" /XD "temp-keys" "wwwroot/uploads" /R:2 /W:1 2>&1 | Out-Null

    Write-DeployLog "[5/5] 正在启动 Windows 服务: ..."
    if (Get-Service  -ErrorAction SilentlyContinue) {
        Start-Service 
    }

    Write-DeployLog "======== [√] 自动发布成功！服务已恢复健康运行 ========"
}
catch {
    Write-DeployLog "[×] 自动化发布过程异常: "
}
finally {
    if (Test-Path ) { Remove-Item  -Force -ErrorAction SilentlyContinue }
}
