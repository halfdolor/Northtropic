using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Northtropic.Services
{
    public class SystemHealthDto
    {
        // 核心业务数据表记录规模
        public int TotalUsers { get; set; }
        public int TotalQuestions { get; set; }
        public int TotalErrorItems { get; set; }
        public int TotalPracticeRecords { get; set; }
        public int TotalHomeworkAssignments { get; set; }
        public int TotalUserFavorites { get; set; }
        public int TotalLlmLogs { get; set; }

        // SQLite 物理存储健康指标
        public bool DatabaseFileExists { get; set; } = true;
        public long DatabaseSizeBytes { get; set; }
        public string DatabaseSizeFormatted => DatabaseSizeBytes switch
        {
            >= 1024 * 1024 => $"{DatabaseSizeBytes / (1024.0 * 1024.0):F2} MB",
            >= 1024 => $"{DatabaseSizeBytes / 1024.0:F1} KB",
            _ => $"{DatabaseSizeBytes} Bytes"
        };
        public long WalSizeBytes { get; set; }
        public string WalSizeFormatted => WalSizeBytes switch
        {
            >= 1024 * 1024 => $"{WalSizeBytes / (1024.0 * 1024.0):F2} MB",
            >= 1024 => $"{WalSizeBytes / 1024.0:F1} KB",
            _ => $"{WalSizeBytes} Bytes"
        };
        public double DatabaseLatencyMs { get; set; }
        public string SqliteIntegrityStatus { get; set; } = "Unknown";
        public bool IsDatabaseHealthy => SqliteIntegrityStatus.Equals("ok", StringComparison.OrdinalIgnoreCase);
        public string ForeignKeyIntegrityStatus { get; set; } = "Unknown";
        public int ForeignKeyViolationsCount { get; set; } = 0;
        public bool IsForeignKeyHealthy => ForeignKeyIntegrityStatus.Equals("ok", StringComparison.OrdinalIgnoreCase) || ForeignKeyViolationsCount == 0;

        // SQLite 存储分页与物理碎片指标
        public long PageCount { get; set; }
        public int PageSize { get; set; } = 4096;
        public long FreelistCount { get; set; }
        public double FragmentationRatio => PageCount > 0 ? Math.Round((double)FreelistCount / PageCount * 100.0, 1) : 0.0;
        public long FragmentationBytes => FreelistCount * PageSize;
        public string FragmentationFormatted => FragmentationBytes switch
        {
            >= 1024 * 1024 => $"{FragmentationBytes / (1024.0 * 1024.0):F2} MB",
            >= 1024 => $"{FragmentationBytes / 1024.0:F1} KB",
            _ => $"{FragmentationBytes} Bytes"
        };

        // 存储驱动卷可用空间健康监控
        public long DiskFreeSpaceBytes { get; set; }
        public long DiskTotalSpaceBytes { get; set; }
        public string DiskFreeSpaceFormatted => DiskFreeSpaceBytes switch
        {
            >= 1024L * 1024 * 1024 => $"{DiskFreeSpaceBytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
            >= 1024 * 1024 => $"{DiskFreeSpaceBytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{DiskFreeSpaceBytes / 1024.0:F1} KB"
        };

        // 系统架构综合健康评分模型 (0 - 100 分)
        public int HealthScore { get; set; } = 100;
        public string HealthRating { get; set; } = "卓越 (Excellent)";
        public List<string> HealthRecommendations { get; set; } = new();

        // 运行时与内存指标
        public long GcMemoryBytes { get; set; }
        public string GcMemoryFormatted => $"{GcMemoryBytes / (1024.0 * 1024.0):F2} MB";
        public string DotNetVersion { get; set; } = Environment.Version.ToString();
        public TimeSpan ProcessUptime { get; set; }
        public string ProcessUptimeFormatted => $"{(int)ProcessUptime.TotalHours}小时 {ProcessUptime.Minutes}分 {ProcessUptime.Seconds}秒";
        public int ThreadCount { get; set; }
        public int ThreadPoolAvailableWorkerThreads { get; set; }
        public int ThreadPoolAvailableIocpThreads { get; set; }
        public int ThreadPoolMaxWorkerThreads { get; set; }
        public int GcGen0Collections { get; set; }
        public int GcGen1Collections { get; set; }
        public int GcGen2Collections { get; set; }

        // 安全与防御限流状态
        public int ActiveLockoutsCount { get; set; }
        public int PendingSmsCodesCount { get; set; }
        public int PendingDownloadTicketsCount { get; set; }

        // 架构性能与高频缓存吞吐指标
        public long CategoryCacheHitCount { get; set; }
        public long CategoryCacheMissCount { get; set; }
        public double CategoryCacheHitRatio { get; set; } = 100.0;

        // 架构性能分级与线程池饱和度 (系统架构师指标)
        public string LatencyRating => DatabaseLatencyMs switch
        {
            <= 5.0 => "卓越极速 (Optimal)",
            <= 25.0 => "良好平稳 (Good)",
            <= 100.0 => "轻度延迟 (Elevated)",
            _ => "严重告警 (Critical)"
        };
        public double ThreadPoolSaturationRatio => ThreadPoolMaxWorkerThreads > 0
            ? Math.Round((double)(ThreadPoolMaxWorkerThreads - ThreadPoolAvailableWorkerThreads) / ThreadPoolMaxWorkerThreads * 100.0, 2)
            : 0.0;

        // 诊断生成时间戳
        public DateTime DiagnosedAt { get; set; } = DateTime.Now;
    }

    public class TableStorageMetricDto
    {
        public string TableName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public long RowCount { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    public class DatabaseOptimizationResultDto
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public long BytesReclaimed { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public string OptimizeStatus { get; set; } = string.Empty;
        public string AnalyzeStatus { get; set; } = string.Empty;
        public string CheckpointStatus { get; set; } = string.Empty;
        public double DatabaseLatencyMs { get; set; }
    }

    public class DatabaseBackupResultDto
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string BackupFileName { get; set; } = string.Empty;
        public string BackupFilePath { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public string FileSizeFormatted => FileSizeBytes switch
        {
            >= 1024 * 1024 => $"{FileSizeBytes / (1024.0 * 1024.0):F2} MB",
            >= 1024 => $"{FileSizeBytes / 1024.0:F1} KB",
            _ => $"{FileSizeBytes} Bytes"
        };
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public double ElapsedMilliseconds { get; set; }
        public bool IntegrityVerified { get; set; }
        public string? Sha256Hash { get; set; }
    }

    public class BackupVerificationResultDto
    {
        public string FileName { get; set; } = string.Empty;
        public bool IsHealthy { get; set; }
        public string SqliteIntegrityStatus { get; set; } = "Unknown";
        public int TableCount { get; set; }
        public bool CoreTablesPresent { get; set; }
        public long FileSizeBytes { get; set; }
        public string FileSizeFormatted => FileSizeBytes switch
        {
            >= 1024 * 1024 => $"{FileSizeBytes / (1024.0 * 1024.0):F2} MB",
            >= 1024 => $"{FileSizeBytes / 1024.0:F1} KB",
            _ => $"{FileSizeBytes} Bytes"
        };
        public string? Sha256Hash { get; set; }
        public double VerificationDurationMs { get; set; }
        public DateTime CheckedAt { get; set; } = DateTime.Now;
        public string? ErrorMessage { get; set; }
    }

    public class SystemArchitectureEvent
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Category { get; set; } = "General"; // "Backup", "SelfHealing", "Security", "SpacedRepetition", "Performance"
        public string Level { get; set; } = "Info"; // "Info", "Warning", "Success", "Error"
        public string Message { get; set; } = string.Empty;
        public double? DurationMs { get; set; }
    }

    public class ArchitecturalDiagnosticResultDto
    {
        public DateTime ExecutedAt { get; set; } = DateTime.Now;
        public bool IsPassed { get; set; } = true;
        public string OverallStatus { get; set; } = "Pass";
        public double LatencyMs { get; set; }
        public string SqliteIntegrity { get; set; } = "ok";
        public string ForeignKeyIntegrity { get; set; } = "ok";
        public int CoreTablesFound { get; set; }
        public long TotalQuestionsScanned { get; set; }
        public long GcMemoryBytes { get; set; }
        public string LatencyRating { get; set; } = "Optimal";
        public List<string> DiagnosticCheckpoints { get; set; } = new();
    }

    public interface ISystemHealthService
    {
        Task<SystemHealthDto> GetSystemHealthAsync();
        Task<bool> VacuumDatabaseAsync();
        Task<string> RunDatabaseIntegrityCheckAsync();
        Task<string> RunDatabaseForeignKeyCheckAsync();
        Task<DatabaseOptimizationResultDto> OptimizeDatabaseAsync();
        Task<double> MeasureDatabaseLatencyAsync();
        Task<DatabaseBackupResultDto> CreateDatabaseBackupAsync(string? targetDir = null);
        Task<List<DatabaseBackupResultDto>> GetBackupsListAsync(string? targetDir = null);
        Task<bool> DeleteBackupAsync(string fileName, string? targetDir = null);
        Task<(int PrunedCount, long ReclaimedBytes)> PruneBackupsAsync(int keepCount = 5, int maxAgeDays = 30, string? targetDir = null);
        Task<string?> CalculateBackupSha256Async(string fileName, string? targetDir = null);
        Task<BackupVerificationResultDto> VerifyBackupSnapshotAsync(string fileName, string? targetDir = null);
        Task<string> ExportArchitectureDiagnosticReportJsonAsync();
        Task<List<TableStorageMetricDto>> GetTableStorageMetricsAsync();
        Task<ArchitecturalDiagnosticResultDto> RunArchitecturalSelfDiagnosticAsync();
        IReadOnlyList<SystemArchitectureEvent> GetRecentArchitectureEvents();
        void RecordArchitectureEvent(string category, string level, string message, double? durationMs = null);
    }
}

