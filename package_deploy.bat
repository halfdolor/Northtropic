@echo off
chcp 65001 >nul
title Northtropic 部署包生成器
cd /d "%~dp0"

echo ========================================================
echo       Northtropic 智能学习系统 - 部署包一键打包生成
echo ========================================================
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0package_deploy.ps1"

echo.
pause
