# ==============================================================================
# Northtropic 智能学习系统 - 一键打包生成 Windows Server 部署包
# ==============================================================================

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$ProjectRoot = $PSScriptRoot
$PackageDir = Join-Path $ProjectRoot "Northtropic_DeployPackage"
$AppDir = Join-Path $PackageDir "app"
$TemplateDir = Join-Path $ProjectRoot "deploy_template"
$ZipFilePath = Join-Path $ProjectRoot "Northtropic_Windows_Server_Deploy.zip"

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "    Northtropic 智能学习系统 - 正在构建 Windows Server 部署包" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

# 1. 清理历史打包产物
Write-Host "[1/5] 清理历史打包输出..." -ForegroundColor Cyan
if (Test-Path $PackageDir) {
    Remove-Item -Path $PackageDir -Recurse -Force | Out-Null
}
if (Test-Path $ZipFilePath) {
    Remove-Item -Path $ZipFilePath -Force | Out-Null
}
New-Item -ItemType Directory -Path $AppDir -Force | Out-Null

# 2. 执行自包含 win-x64 编译发布
Write-Host "[2/5] 正在执行 Release 自包含编译发布 (win-x64，目标机免装.NET运行时)..." -ForegroundColor Cyan
$publishCmd = "dotnet publish `"$ProjectRoot\Northtropic.csproj`" -c Release -r win-x64 --self-contained true -o `"$AppDir`""
Invoke-Expression $publishCmd

if (-not (Test-Path (Join-Path $AppDir "Northtropic.exe"))) {
    Write-Host "[错误] 编译发布失败，未找到 Northtropic.exe！" -ForegroundColor Red
    exit 1
}
Write-Host "[√] .NET 8 自包含程序编译发布成功！" -ForegroundColor Green

# 3. 复制数据库与数据保护密钥模板
Write-Host "[3/5] 正在打包初始数据库与运行时数据..." -ForegroundColor Cyan
$sourceDb = Join-Path $ProjectRoot "northtropic_study.db"
$destDb = Join-Path $AppDir "northtropic_study.db"
if (Test-Path $sourceDb) {
    Copy-Item -Path $sourceDb -Destination $destDb -Force
    Write-Host "[√] 已打包现有 SQLite 业务数据库 (northtropic_study.db)" -ForegroundColor Green
} else {
    Write-Host "[提示] 未检测到根目录数据库，系统首次运行将自动初始化创建。" -ForegroundColor Yellow
}

$tempKeysDir = Join-Path $AppDir "temp-keys"
if (-not (Test-Path $tempKeysDir)) {
    New-Item -ItemType Directory -Path $tempKeysDir -Force | Out-Null
}

# 4. 复制一键安装、服务控制及说明文件
Write-Host "[4/5] 正在装配一键安装与运维脚本..." -ForegroundColor Cyan
if (Test-Path $TemplateDir) {
    Copy-Item -Path "$TemplateDir\*" -Destination $PackageDir -Recurse -Force
    Write-Host "[√] 一键安装与服务管理脚本装配完毕！" -ForegroundColor Green
} else {
    Write-Host "[错误] 未找到部署模板目录 $TemplateDir" -ForegroundColor Red
}

# 5. 压缩为一键部署 ZIP 压缩包
Write-Host "[5/5] 正在生成单一分发压缩包 (Northtropic_Windows_Server_Deploy.zip)..." -ForegroundColor Cyan
try {
    Compress-Archive -Path "$PackageDir\*" -DestinationPath $ZipFilePath -Force
    Write-Host "[√] ZIP 分发压缩包生成完成！" -ForegroundColor Green
} catch {
    Write-Host "[提示] 压缩为 ZIP 时出现非致命提示: $_" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host "                 部署包打包生成全部完成！" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "【打包产物路径】" -ForegroundColor Cyan
Write-Host "  1. 解压就绪目录:" -ForegroundColor White
Write-Host "     $PackageDir" -ForegroundColor Yellow
Write-Host ""
Write-Host "  2. 单文件 ZIP 压缩包 (推荐直接复制此文件到服务器):" -ForegroundColor White
Write-Host "     $ZipFilePath" -ForegroundColor Yellow
Write-Host ""
Write-Host "【服务器安装使用方法】" -ForegroundColor Cyan
Write-Host "  1. 将 ZIP 压缩包或 Northtropic_DeployPackage 文件夹复制到目标 Windows Server。" -ForegroundColor White
Write-Host "  2. 解压后，右键点击【1-安装服务(以管理员身份运行).bat】运行安装即可。" -ForegroundColor White
Write-Host ""
