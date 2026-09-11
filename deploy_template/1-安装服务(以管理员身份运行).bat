@echo off
chcp 65001 >nul
title Northtropic 一键服务安装程序
cd /d "%~dp0"

echo ========================================================
echo       Northtropic 智能学习系统 - Windows Server 一键安装
echo ========================================================
echo.

:: 检查管理员权限
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [提示] 正在请求管理员权限，请在弹出的UAC窗口中点击“是”...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~dpnx0\"\"' -Verb RunAs"
    exit /b
)

:: 执行 PowerShell 安装脚本
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1"

echo.
pause
