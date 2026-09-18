@echo off
chcp 65001 >nul
title Northtropic - 安全测试与部署发布门禁

echo ======================================================================
echo           Northtropic 智能学习系统 · 本地安全测试与发布门禁
echo ======================================================================
echo.

echo [1/3] 正在执行编译检查 (dotnet build)...
call dotnet build
if %errorlevel% neq 0 (
    echo.
    echo [错误] 编译未通过，存在语法或引用错误！已阻止发布。
    pause
    exit /b %errorlevel%
)
echo [√] 编译成功！
echo.

echo [2/3] 正在执行全套自动化回归测试 (dotnet test)...
call dotnet test Northtropic.Tests
if %errorlevel% neq 0 (
    echo.
    echo [错误] 单元测试未通过！已阻止发布以防影响生产环境。
    pause
    exit /b %errorlevel%
)
echo [√] 自动化测试全部通过！
echo.

echo ======================================================================
echo [提示] 本地代码已全部验证通过，尚未推送到 Git 远端。
echo [提示] 您可以先启动本地服务测试页面与功能无误后再决定。
echo ======================================================================
echo.
set /p confirm="[?] 确认本地测试通过，立即推送到 Git 触发云端自动部署？(Y/N): "

if /i "%confirm%"=="Y" (
    echo.
    echo 正在推送到 Git 远端仓库...
    git push
    if %errorlevel% equ 0 (
        echo.
        echo [√] 推送成功！云端 CI/CD 自动部署已触发。
    ) else (
        echo.
        echo [错误] 推送失败，请检查网络或 Git 配置。
    )
) else (
    echo.
    echo [已取消] 已中止推送，当前修改仍保存在本地，未同步到线上。
)

echo.
pause
