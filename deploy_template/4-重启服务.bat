@echo off
chcp 65001 >nul
title 重启 Northtropic 服务
cd /d "%~dp0"

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [提示] 正在请求管理员权限...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~dpnx0\"\"' -Verb RunAs"
    exit /b
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\service_ctl.ps1" -Action restart
pause
