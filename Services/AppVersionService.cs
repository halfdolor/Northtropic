using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Northtropic.Services;

/// <summary>
/// 系统版本服务实现，自动解析程序集元数据、Git 提交哈希及构建时间戳
/// </summary>
public class AppVersionService : IAppVersionService
{
    public string Version { get; }
    public string DisplayVersion { get; }
    public string ShortCommitHash { get; }
    public string BuildTimestamp { get; }
    public string FullVersionInfo { get; }

    public AppVersionService(IConfiguration? configuration = null)
    {
        var assembly = typeof(AppVersionService).Assembly;

        // 1. 读取程序集 InformationalVersion 属性 (如: 1.0.12+e8dcc36...)
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string parsedVersion = "";
        string parsedCommit = "";

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plusIdx = informationalVersion.IndexOf('+');
            if (plusIdx >= 0)
            {
                parsedVersion = informationalVersion.Substring(0, plusIdx).Trim();
                parsedCommit = informationalVersion.Substring(plusIdx + 1).Trim();
            }
            else
            {
                parsedVersion = informationalVersion.Trim();
            }
        }

        // 2. 回退检查：Assembly.GetName().Version 或 Configuration
        if (string.IsNullOrWhiteSpace(parsedVersion))
        {
            var asmVer = assembly.GetName().Version;
            if (asmVer != null && (asmVer.Major > 0 || asmVer.Minor > 0 || asmVer.Build > 0))
            {
                parsedVersion = $"{asmVer.Major}.{asmVer.Minor}.{Math.Max(0, asmVer.Build)}";
            }
            else
            {
                parsedVersion = configuration?["AppVersion"] ?? "1.0.1";
            }
        }

        // 3. 处理短 Commit Hash
        if (!string.IsNullOrWhiteSpace(parsedCommit))
        {
            ShortCommitHash = parsedCommit.Length > 7 ? parsedCommit.Substring(0, 7) : parsedCommit;
        }
        else
        {
            ShortCommitHash = "";
        }

        // 4. 解析构建时间
        string buildTimeStr = "";
        try
        {
            var location = assembly.Location;
            if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
            {
                var lastWrite = File.GetLastWriteTime(location);
                buildTimeStr = lastWrite.ToString("yyyy-MM-dd HH:mm");
            }
        }
        catch
        {
            // 忽略读取文件时间异常
        }

        if (string.IsNullOrWhiteSpace(buildTimeStr))
        {
            buildTimeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }
        BuildTimestamp = buildTimeStr;

        Version = parsedVersion;
        DisplayVersion = $"v{Version}";

        var details = $"构建: {BuildTimestamp}";
        if (!string.IsNullOrEmpty(ShortCommitHash))
        {
            details += $", Commit: {ShortCommitHash}";
        }
        FullVersionInfo = $"Northtropic {DisplayVersion} ({details})";
    }
}
