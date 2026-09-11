@echo off
chcp 65001 >nul
title Northtropic 控制台运行调试模式
cd /d "%~dp0app"

echo ========================================================
echo       Northtropic 智能学习系统 - 控制台调试模式
echo       (关闭此控制台窗口即停止运行，正式环境请使用 1-安装服务)
echo ========================================================
echo.

if not exist "Northtropic.exe" (
    echo [错误] 未在 app 目录下找到 Northtropic.exe，请检查安装包文件是否完整！
    pause
    exit /b 1
)

echo [信息] 正在启动 Northtropic 控制台进程...
echo [信息] 若要在浏览器中访问，请稍候并查看下方监听端口地址。
echo.

Northtropic.exe
pause
