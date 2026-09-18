namespace Northtropic.Services;

/// <summary>
/// 应用系统版本服务接口，提供每次发布自动更新的版本号、Git 提交哈希与构建时间元数据
/// </summary>
public interface IAppVersionService
{
    /// <summary>
    /// 标准语义化版本号，例如 "1.0.12"
    /// </summary>
    string Version { get; }

    /// <summary>
    /// 界面顶栏展示用的版本号文本，例如 "v1.0.12"
    /// </summary>
    string DisplayVersion { get; }

    /// <summary>
    /// Git 提交哈希 (短哈希，例如 "e8dcc36")
    /// </summary>
    string ShortCommitHash { get; }

    /// <summary>
    /// 构建/编译时间字符串，例如 "2026-09-18 08:20"
    /// </summary>
    string BuildTimestamp { get; }

    /// <summary>
    /// 完整版本悬浮提示信息，例如 "Northtropic v1.0.12 (构建: 2026-09-18 08:20, Commit: e8dcc36)"
    /// </summary>
    string FullVersionInfo { get; }
}
