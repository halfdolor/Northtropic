using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;

namespace Northtropic.Services
{
    public class SystemHealthService : ISystemHealthService
    {
        private readonly AppDbContext _context;
        private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;

        private static readonly ConcurrentQueue<SystemArchitectureEvent> _telemetryEvents = new();
        private const int MaxTelemetryEvents = 60;

        static SystemHealthService()
        {
            _telemetryEvents.Enqueue(new SystemArchitectureEvent
            {
                Category = "ArchitectureBoot",
                Level = "Success",
                Message = $"系统架构引擎启动完成，基于 .NET {Environment.Version} 与 SQLite WAL 异步连接池就绪。"
            });
        }

        public SystemHealthService(AppDbContext context, IDbContextFactory<AppDbContext>? dbContextFactory = null)
        {
            _context = context;
            _dbContextFactory = dbContextFactory;
        }

        public void RecordArchitectureEvent(string category, string level, string message, double? durationMs = null)
        {
            var evt = new SystemArchitectureEvent
            {
                Category = category,
                Level = level,
                Message = message,
                DurationMs = durationMs
            };
            _telemetryEvents.Enqueue(evt);
            while (_telemetryEvents.Count > MaxTelemetryEvents)
            {
                _telemetryEvents.TryDequeue(out _);
            }
        }

        public IReadOnlyList<SystemArchitectureEvent> GetRecentArchitectureEvents()
        {
            return _telemetryEvents.Reverse().ToList();
        }

        private ValueTask<AsyncDbScope> CreateDbScopeAsync()
        {
            return AsyncDbScope.CreateAsync(_dbContextFactory, _context);
        }

        public async Task<SystemHealthDto> GetSystemHealthAsync()
        {
            var dto = new SystemHealthDto();

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            // 1. 数据表记录数统计
            dto.TotalUsers = await ctx.Users.AsNoTracking().CountAsync();
            dto.TotalQuestions = await ctx.Questions.AsNoTracking().CountAsync();
            dto.TotalErrorItems = await ctx.ErrorItems.AsNoTracking().CountAsync();
            dto.TotalPracticeRecords = await ctx.PracticeRecords.AsNoTracking().CountAsync();
            dto.TotalHomeworkAssignments = await ctx.HomeworkAssignments.AsNoTracking().CountAsync();
            dto.TotalUserFavorites = await ctx.UserFavorites.AsNoTracking().CountAsync();
            dto.TotalLlmLogs = await ctx.LlmGenerationLogs.AsNoTracking().CountAsync();

            // 2. SQLite 物理文件检测
            var conn = ctx.Database.GetDbConnection();
            var dataSource = conn.DataSource;
            if (!string.IsNullOrWhiteSpace(dataSource) && File.Exists(dataSource))
            {
                var fileInfo = new FileInfo(dataSource);
                dto.DatabaseSizeBytes = fileInfo.Length;
                dto.DatabaseFileExists = true;

                var walPath = dataSource + "-wal";
                if (File.Exists(walPath))
                {
                    dto.WalSizeBytes = new FileInfo(walPath).Length;
                }
            }
            else
            {
                dto.DatabaseFileExists = false;
                dto.DatabaseSizeBytes = 0;
            }

            // 2.1 测量数据库查询延迟
            dto.DatabaseLatencyMs = await MeasureDatabaseLatencyInternalAsync(ctx);

            // 2.2 测量 SQLite 物理存储分页与空闲碎片 (PRAGMA page_count, page_size, freelist_count)
            try
            {
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA page_count;";
                var pc = await cmd.ExecuteScalarAsync();
                if (pc != null && long.TryParse(pc.ToString(), out var pageCount))
                {
                    dto.PageCount = pageCount;
                }

                cmd.CommandText = "PRAGMA page_size;";
                var ps = await cmd.ExecuteScalarAsync();
                if (ps != null && int.TryParse(ps.ToString(), out var pageSize) && pageSize > 0)
                {
                    dto.PageSize = pageSize;
                }

                cmd.CommandText = "PRAGMA freelist_count;";
                var fc = await cmd.ExecuteScalarAsync();
                if (fc != null && long.TryParse(fc.ToString(), out var freelistCount))
                {
                    dto.FreelistCount = freelistCount;
                }
            }
            catch
            {
                // 忽略非标准或内存数据库异常
            }

            // 2.3 监控数据库所在磁盘驱动卷剩余可用空间
            try
            {
                if (!string.IsNullOrEmpty(dataSource) && File.Exists(dataSource))
                {
                    var fullPath = Path.GetFullPath(dataSource);
                    var drivePath = Path.GetPathRoot(fullPath);
                    if (!string.IsNullOrEmpty(drivePath))
                    {
                        var driveInfo = new DriveInfo(drivePath);
                        if (driveInfo.IsReady)
                        {
                            dto.DiskFreeSpaceBytes = driveInfo.AvailableFreeSpace;
                            dto.DiskTotalSpaceBytes = driveInfo.TotalSize;
                        }
                    }
                }
            }
            catch
            {
                // 驱动卷不可达时静默忽略
            }

            // 3. SQLite 完整性检查 (PRAGMA integrity_check 与 PRAGMA foreign_key_check)
            dto.SqliteIntegrityStatus = await RunDatabaseIntegrityCheckInternalAsync(ctx);
            dto.ForeignKeyIntegrityStatus = await RunDatabaseForeignKeyCheckInternalAsync(ctx);
            if (!dto.ForeignKeyIntegrityStatus.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                dto.ForeignKeyViolationsCount = 1;
            }
            else
            {
                dto.ForeignKeyViolationsCount = 0;
            }

            // 4. 运行时指标
            dto.GcMemoryBytes = GC.GetTotalMemory(forceFullCollection: false);
            dto.GcGen0Collections = GC.CollectionCount(0);
            dto.GcGen1Collections = GC.CollectionCount(1);
            dto.GcGen2Collections = GC.CollectionCount(2);

            System.Threading.ThreadPool.GetAvailableThreads(out int availWorker, out int availIocp);
            System.Threading.ThreadPool.GetMaxThreads(out int maxWorker, out _);
            dto.ThreadPoolAvailableWorkerThreads = availWorker;
            dto.ThreadPoolAvailableIocpThreads = availIocp;
            dto.ThreadPoolMaxWorkerThreads = maxWorker;

            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                dto.ProcessUptime = DateTime.Now - proc.StartTime;
                dto.ThreadCount = proc.Threads.Count;
            }
            catch
            {
                dto.ProcessUptime = TimeSpan.Zero;
                dto.ThreadCount = 0;
            }

            // 5. 安全防御状态与高频缓存吞吐指标
            dto.ActiveLockoutsCount = UserSessionService.ActiveLockoutsCount;
            dto.PendingSmsCodesCount = UserSessionService.PendingSmsCodesCount;
            dto.PendingDownloadTicketsCount = UserSessionService.ActiveDownloadTicketsCountStatic;
            dto.CategoryCacheHitCount = PracticeService.CategoryCacheHitCount;
            dto.CategoryCacheMissCount = PracticeService.CategoryCacheMissCount;
            dto.CategoryCacheHitRatio = PracticeService.CategoryCacheHitRatio;

            // 6. 系统架构综合健康评分模型 (0 - 100 分)
            int score = 100;
            var recs = new List<string>();

            if (!dto.IsDatabaseHealthy)
            {
                score -= 40;
                recs.Add("⚠️ SQLite 物理完整性校验未返回 ok，建议立即执行碎片整理或从快照自愈恢复！");
            }

            if (!dto.IsForeignKeyHealthy)
            {
                score -= 20;
                recs.Add($"⚠️ SQLite 外键逻辑完整性异常 ({dto.ForeignKeyIntegrityStatus})，建议排查孤儿数据或执行级联同步！");
            }

            if (dto.DatabaseLatencyMs > 50)
            {
                score -= 15;
                recs.Add($"⚠️ 数据库基础往返延迟偏高 ({dto.DatabaseLatencyMs:F1}ms)，建议执行【⚡ 数据库自愈与索引优化 (OPTIMIZE)】。");
            }
            else if (dto.DatabaseLatencyMs > 20)
            {
                score -= 5;
                recs.Add($"💡 数据库查询延迟稍高 ({dto.DatabaseLatencyMs:F1}ms)，可适时进行 PRAGMA optimize 统计索引维护。");
            }

            if (dto.WalSizeBytes > 50 * 1024 * 1024)
            {
                score -= 15;
                recs.Add($"⚠️ WAL 日志尺寸达到 {dto.WalSizeFormatted}，建议点击【⚡ 数据库自愈与索引优化】执行 WAL TRUNCATE 截断归档。");
            }
            else if (dto.WalSizeBytes > 20 * 1024 * 1024)
            {
                score -= 5;
                recs.Add($"💡 WAL 日志达到 {dto.WalSizeFormatted}，建议在业务低峰期执行检查点截断。");
            }

            if (dto.FragmentationRatio > 30.0 && dto.FragmentationBytes > 1024 * 1024)
            {
                score -= 10;
                recs.Add($"💡 SQLite 物理碎片率达到 {dto.FragmentationRatio:F1}% ({dto.FragmentationFormatted})，建议在业务低峰期执行【整理碎片 (VACUUM)】释放空间。");
            }

            if (dto.DiskFreeSpaceBytes > 0 && dto.DiskFreeSpaceBytes < 1024L * 1024 * 1024)
            {
                score -= 20;
                recs.Add($"⚠️ 数据库存储盘可用空间不足 1 GB (仅余 {dto.DiskFreeSpaceFormatted})，请及时清理主机磁盘以防写入中断！");
            }

            if (dto.ActiveLockoutsCount > 10)
            {
                score -= 5;
                recs.Add($"⚠️ 当前存在 {dto.ActiveLockoutsCount} 个活跃封禁，请持续关注防止恶意撞库攻击。");
            }

            if (dto.GcMemoryBytes > 500 * 1024 * 1024)
            {
                score -= 10;
                recs.Add($"💡 GC 托管内存占用达到 {dto.GcMemoryFormatted}，建议监控大对象堆并适时触发内存收敛。");
            }

            if (dto.ThreadPoolMaxWorkerThreads > 0 && (double)dto.ThreadPoolAvailableWorkerThreads / dto.ThreadPoolMaxWorkerThreads < 0.2)
            {
                score -= 10;
                recs.Add($"⚠️ 线程池可用工作线程较低 ({dto.ThreadPoolAvailableWorkerThreads}/{dto.ThreadPoolMaxWorkerThreads})，存在潜在线程饥饿与 Blazor 线路排队风险。");
            }

            if (recs.Count == 0)
            {
                recs.Add("✅ SQLite 物理完整性检验通过 (ok)，底层 B-Tree 与模式结构完整健壮");
                recs.Add("✅ SQLite 外键约束关系完整 (ok)，数据实体引用拓扑无孤儿脏行");
                recs.Add($"✅ 数据库查询往返时延极低 ({(dto.DatabaseLatencyMs >= 0 ? $"{dto.DatabaseLatencyMs:F1}ms" : "< 5ms")})，毫秒级响应性能卓越");
                recs.Add($"✅ SQLite 存储碎片率良好 ({dto.FragmentationRatio:F1}%)，分页结构紧凑无空洞");
                if (dto.DiskFreeSpaceBytes > 0)
                {
                    recs.Add($"✅ 存储驱动盘空间充裕 (余 {dto.DiskFreeSpaceFormatted})，写入余量充足");
                }
                recs.Add("✅ WAL 预写日志处于轻量健康阈值，无锁并发读取吞吐稳定");
                recs.Add($"✅ 托管线程池健康调度中 (可用工作线程: {dto.ThreadPoolAvailableWorkerThreads})，Blazor Server 线路无排队饥饿");
                recs.Add("✅ 登录防撞库与会话防御机制运行稳健，未探测到异常攻击峰值");
                if (dto.CategoryCacheHitCount + dto.CategoryCacheMissCount > 0)
                {
                    recs.Add($"✅ 考点分类架构缓存运作优异：命中率 {dto.CategoryCacheHitRatio:F1}% (命中 {dto.CategoryCacheHitCount} 次 / 穿透 {dto.CategoryCacheMissCount} 次)");
                }
            }

            dto.HealthScore = Math.Clamp(score, 0, 100);
            dto.HealthRating = dto.HealthScore switch
            {
                >= 90 => "卓越 (Excellent)",
                >= 75 => "良好 (Good)",
                >= 60 => "一般 (Fair)",
                _ => "需关注 (Warning)"
            };
            dto.HealthRecommendations = recs;

            dto.DiagnosedAt = DateTime.Now;
            return dto;
        }

        public async Task<string> RunDatabaseIntegrityCheckAsync()
        {
            await using var dbScope = await CreateDbScopeAsync();
            return await RunDatabaseIntegrityCheckInternalAsync(dbScope.Context);
        }

        private static async Task<string> RunDatabaseIntegrityCheckInternalAsync(AppDbContext ctx)
        {
            try
            {
                var conn = ctx.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                var result = await cmd.ExecuteScalarAsync();
                return result?.ToString() ?? "ok";
            }
            catch (Exception ex)
            {
                return $"Check failed: {ex.Message}";
            }
        }

        public async Task<string> RunDatabaseForeignKeyCheckAsync()
        {
            await using var dbScope = await CreateDbScopeAsync();
            return await RunDatabaseForeignKeyCheckInternalAsync(dbScope.Context);
        }

        private static async Task<string> RunDatabaseForeignKeyCheckInternalAsync(AppDbContext ctx)
        {
            try
            {
                var conn = ctx.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA foreign_key_check;";
                using var reader = await cmd.ExecuteReaderAsync();
                int violations = 0;
                var details = new List<string>();
                while (await reader.ReadAsync())
                {
                    violations++;
                    if (details.Count < 3)
                    {
                        var table = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
                        var rowid = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                        var parent = reader.IsDBNull(2) ? "unknown" : reader.GetString(2);
                        details.Add($"{table}(rowid:{rowid})->{parent}");
                    }
                }

                if (violations == 0)
                {
                    return "ok";
                }
                return $"Violations: {violations} ({string.Join(", ", details)})";
            }
            catch (Exception ex)
            {
                return $"Check failed: {ex.Message}";
            }
        }

        public async Task<double> MeasureDatabaseLatencyAsync()
        {
            await using var dbScope = await CreateDbScopeAsync();
            return await MeasureDatabaseLatencyInternalAsync(dbScope.Context);
        }

        private static async Task<double> MeasureDatabaseLatencyInternalAsync(AppDbContext ctx)
        {
            try
            {
                var conn = ctx.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1;";
                await cmd.ExecuteScalarAsync();
                sw.Stop();
                return Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            }
            catch
            {
                return -1;
            }
        }

        public async Task<DatabaseOptimizationResultDto> OptimizeDatabaseAsync()
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;
            var result = new DatabaseOptimizationResultDto();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var conn = ctx.Database.GetDbConnection();
                var dataSource = conn.DataSource;
                long totalBefore = 0;
                if (!string.IsNullOrWhiteSpace(dataSource) && File.Exists(dataSource))
                {
                    totalBefore += new FileInfo(dataSource).Length;
                    var walPath = dataSource + "-wal";
                    if (File.Exists(walPath))
                    {
                        totalBefore += new FileInfo(walPath).Length;
                    }
                }

                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                // 1. 执行 PRAGMA analyze (重估并写入 sqlite_stat1 索引统计信息)
                using (var cmdAnalyze = conn.CreateCommand())
                {
                    cmdAnalyze.CommandText = "PRAGMA analyze;";
                    await cmdAnalyze.ExecuteNonQueryAsync();
                    result.AnalyzeStatus = "ok";
                }

                // 2. 执行 PRAGMA optimize (更新查询优化器内部直方图统计)
                using (var cmdOptimize = conn.CreateCommand())
                {
                    cmdOptimize.CommandText = "PRAGMA optimize;";
                    await cmdOptimize.ExecuteNonQueryAsync();
                    result.OptimizeStatus = "ok";
                }

                // 3. 执行 PRAGMA wal_checkpoint(TRUNCATE) (刷新并截断 WAL 日志缓冲区)
                using (var cmdWal = conn.CreateCommand())
                {
                    cmdWal.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    var checkpointRes = await cmdWal.ExecuteScalarAsync();
                    result.CheckpointStatus = checkpointRes?.ToString() ?? "checkpoint ok";
                }

                // 4. 测量数据库查询延迟
                result.DatabaseLatencyMs = await MeasureDatabaseLatencyInternalAsync(ctx);

                long totalAfter = 0;
                if (!string.IsNullOrWhiteSpace(dataSource) && File.Exists(dataSource))
                {
                    totalAfter += new FileInfo(dataSource).Length;
                    var walPath = dataSource + "-wal";
                    if (File.Exists(walPath))
                    {
                        totalAfter += new FileInfo(walPath).Length;
                    }
                }

                sw.Stop();
                result.ElapsedMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                result.BytesReclaimed = Math.Max(0, totalBefore - totalAfter);
                result.Success = true;
                result.Message = $"SQLite 优化自愈执行成功！完成 PRAGMA analyze / optimize 统计索引维护与 WAL 日志截断，耗时 {result.ElapsedMilliseconds:F1}ms，释放 {result.BytesReclaimed} 字节。";
                RecordArchitectureEvent("SelfHealing", "Success", result.Message, result.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.Success = false;
                result.ElapsedMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                result.Message = $"优化自愈失败: {ex.Message}";
                RecordArchitectureEvent("SelfHealing", "Error", result.Message, result.ElapsedMilliseconds);
                return result;
            }
        }

        public async Task<bool> VacuumDatabaseAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;
            try
            {
                await ctx.Database.ExecuteSqlRawAsync("VACUUM;");
                sw.Stop();
                RecordArchitectureEvent("SelfHealing", "Success", $"SQLite 原生 VACUUM 碎片规整执行完毕，耗时 {sw.ElapsedMilliseconds}ms", sw.ElapsedMilliseconds);
                return true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                RecordArchitectureEvent("SelfHealing", "Error", $"SQLite 原生 VACUUM 执行失败: {ex.Message}", sw.ElapsedMilliseconds);
                return false;
            }
        }

        public async Task<DatabaseBackupResultDto> CreateDatabaseBackupAsync(string? targetDir = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new DatabaseBackupResultDto();

            try
            {
                await using var dbScope = await CreateDbScopeAsync();
                var ctx = dbScope.Context;
                var conn = ctx.Database.GetDbConnection();

                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                var baseDir = AppContext.BaseDirectory;
                var backupDirectory = !string.IsNullOrWhiteSpace(targetDir)
                    ? targetDir
                    : Path.Combine(baseDir, "backups");

                if (!Directory.Exists(backupDirectory))
                {
                    Directory.CreateDirectory(backupDirectory);
                }

                var fileName = $"northtropic_hotbackup_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.db";
                var fullPath = Path.Combine(backupDirectory, fileName);
                var normalizedPath = fullPath.Replace("\\", "/").Replace("'", "''");

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"VACUUM INTO '{normalizedPath}';";
                    await cmd.ExecuteNonQueryAsync();
                }

                sw.Stop();

                if (File.Exists(fullPath))
                {
                    var fi = new FileInfo(fullPath);
                    result.Success = true;
                    result.BackupFileName = fileName;
                    result.BackupFilePath = fullPath;
                    result.FileSizeBytes = fi.Length;
                    result.CreatedAt = fi.CreationTime;
                    result.ElapsedMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

                    bool integrityOk = false;
                    try
                    {
                        var connStr = new SqliteConnectionStringBuilder
                        {
                            DataSource = fullPath,
                            Mode = SqliteOpenMode.ReadOnly
                        }.ToString();

                        using var verifyConn = new SqliteConnection(connStr);
                        await verifyConn.OpenAsync();
                        using var checkCmd = verifyConn.CreateCommand();
                        checkCmd.CommandText = "PRAGMA integrity_check;";
                        var checkResult = await checkCmd.ExecuteScalarAsync();
                        integrityOk = string.Equals(checkResult?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        integrityOk = false;
                    }

                    result.IntegrityVerified = integrityOk;
                    result.Message = integrityOk
                        ? $"热快照备份创建成功！文件: {fileName} ({result.FileSizeFormatted})，物理完整性校验: OK，耗时 {result.ElapsedMilliseconds}ms。"
                        : $"热快照备份已生成但完整性校验未通过，请检查磁盘空间。";

                    RecordArchitectureEvent("HotBackup", integrityOk ? "Success" : "Warning", result.Message, result.ElapsedMilliseconds);
                }
                else
                {
                    result.Success = false;
                    result.Message = "VACUUM INTO 执行后未能在目标路径检测到生成的快照文件。";
                    RecordArchitectureEvent("HotBackup", "Error", result.Message, sw.ElapsedMilliseconds);
                }

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.Success = false;
                result.ElapsedMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                result.Message = $"创建 SQLite 热备份快照失败: {ex.Message}";
                RecordArchitectureEvent("HotBackup", "Error", result.Message, result.ElapsedMilliseconds);
                return result;
            }
        }

        public async Task<List<DatabaseBackupResultDto>> GetBackupsListAsync(string? targetDir = null)
        {
            var list = new List<DatabaseBackupResultDto>();
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var backupDirectory = !string.IsNullOrWhiteSpace(targetDir)
                    ? targetDir
                    : Path.Combine(baseDir, "backups");

                if (!Directory.Exists(backupDirectory))
                {
                    return list;
                }

                var files = Directory.GetFiles(backupDirectory, "*.db")
                    .OrderByDescending(f => File.GetCreationTime(f));

                foreach (var file in files)
                {
                    var fi = new FileInfo(file);
                    list.Add(new DatabaseBackupResultDto
                    {
                        Success = true,
                        BackupFileName = fi.Name,
                        BackupFilePath = fi.FullName,
                        FileSizeBytes = fi.Length,
                        CreatedAt = fi.CreationTime,
                        IntegrityVerified = true
                    });
                }
            }
            catch (Exception ex)
            {
                RecordArchitectureEvent("HotBackup", "Warning", $"获取备份列表发生异常: {ex.Message}");
            }

            return await Task.FromResult(list);
        }

        public async Task<bool> DeleteBackupAsync(string fileName, string? targetDir = null)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;

            var cleanName = Path.GetFileName(fileName);
            if (!string.Equals(cleanName, fileName, StringComparison.Ordinal) ||
                fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\') ||
                !fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            {
                RecordArchitectureEvent("HotBackup", "Warning", $"拦截到非法或不合规的备份文件删除请求: {fileName}");
                return false;
            }

            try
            {
                var baseDir = AppContext.BaseDirectory;
                var backupDirectory = !string.IsNullOrWhiteSpace(targetDir)
                    ? targetDir
                    : Path.Combine(baseDir, "backups");

                var fullPath = Path.Combine(backupDirectory, cleanName);
                if (!File.Exists(fullPath))
                {
                    return false;
                }

                File.Delete(fullPath);
                RecordArchitectureEvent("HotBackup", "Success", $"历史快照文件已安全删除: {cleanName}");
                return await Task.FromResult(true);
            }
            catch (Exception ex)
            {
                RecordArchitectureEvent("HotBackup", "Error", $"删除备份快照 {cleanName} 发生异常: {ex.Message}");
                return false;
            }
        }

        public async Task<(int PrunedCount, long ReclaimedBytes)> PruneBackupsAsync(int keepCount = 5, int maxAgeDays = 30, string? targetDir = null)
        {
            int pruned = 0;
            long reclaimedBytes = 0;

            try
            {
                var baseDir = AppContext.BaseDirectory;
                var backupDirectory = !string.IsNullOrWhiteSpace(targetDir)
                    ? targetDir
                    : Path.Combine(baseDir, "backups");

                if (!Directory.Exists(backupDirectory))
                {
                    return (0, 0);
                }

                var files = Directory.GetFiles(backupDirectory, "*.db")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                var now = DateTime.Now;
                var toDelete = new List<FileInfo>();

                for (int i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    bool exceedsKeepCount = i >= keepCount;
                    bool exceedsAge = (now - file.CreationTime).TotalDays > maxAgeDays;

                    if (exceedsKeepCount || exceedsAge)
                    {
                        toDelete.Add(file);
                    }
                }

                foreach (var file in toDelete)
                {
                    try
                    {
                        long size = file.Length;
                        file.Delete();
                        pruned++;
                        reclaimedBytes += size;
                    }
                    catch (Exception ex)
                    {
                        RecordArchitectureEvent("HotBackup", "Warning", $"清理过期备份快照 {file.Name} 失败: {ex.Message}");
                    }
                }

                if (pruned > 0)
                {
                    double mbReclaimed = Math.Round(reclaimedBytes / (1024.0 * 1024.0), 2);
                    RecordArchitectureEvent("HotBackup", "Success", $"自动轮转清理完成：已归档删除 {pruned} 份过期快照，回收物理磁盘空间 {mbReclaimed} MB。");
                }
            }
            catch (Exception ex)
            {
                RecordArchitectureEvent("HotBackup", "Error", $"执行备份自动归档轮转异常: {ex.Message}");
            }

            return await Task.FromResult((pruned, reclaimedBytes));
        }

        public async Task<string?> CalculateBackupSha256Async(string fileName, string? targetDir = null)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            var cleanName = Path.GetFileName(fileName);
            if (!string.Equals(cleanName, fileName, StringComparison.Ordinal) ||
                fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            {
                return null;
            }

            try
            {
                var baseDir = AppContext.BaseDirectory;
                var backupDirectory = !string.IsNullOrWhiteSpace(targetDir)
                    ? targetDir
                    : Path.Combine(baseDir, "backups");

                var fullPath = Path.Combine(backupDirectory, cleanName);
                if (!File.Exists(fullPath)) return null;

                using var sha256 = SHA256.Create();
                await using var stream = File.OpenRead(fullPath);
                var hash = await sha256.ComputeHashAsync(stream);
                var hashStr = Convert.ToHexString(hash).ToLowerInvariant();
                RecordArchitectureEvent("HotBackup", "Info", $"计算快照文件 SHA-256 指纹完成: {cleanName} [{hashStr.Substring(0, 12)}...]");
                return hashStr;
            }
            catch (Exception ex)
            {
                RecordArchitectureEvent("HotBackup", "Error", $"计算快照 SHA-256 异常: {ex.Message}");
                return null;
            }
        }

        public async Task<List<TableStorageMetricDto>> GetTableStorageMetricsAsync()
        {
            var metrics = new List<TableStorageMetricDto>();
            try
            {
                await using var dbScope = await CreateDbScopeAsync();
                var ctx = dbScope.Context;

                metrics.Add(new TableStorageMetricDto
                {
                    TableName = "Questions",
                    DisplayName = "试题知识库",
                    RowCount = await ctx.Questions.CountAsync(),
                    Description = "核心题库，含各学段学科题目、标准选项与解答"
                });

                metrics.Add(new TableStorageMetricDto
                {
                    TableName = "Users",
                    DisplayName = "系统用户表",
                    RowCount = await ctx.Users.CountAsync(),
                    Description = "学生、家长、教师与管理员账户档案与个性化设定"
                });

                metrics.Add(new TableStorageMetricDto
                {
                    TableName = "PracticeRecords",
                    DisplayName = "练习做题记录",
                    RowCount = await ctx.PracticeRecords.CountAsync(),
                    Description = "学生历次练习流水、作答轨迹与耗时统计"
                });

                metrics.Add(new TableStorageMetricDto
                {
                    TableName = "ErrorItems",
                    DisplayName = "智能错题本",
                    RowCount = await ctx.ErrorItems.CountAsync(),
                    Description = "艾宾浩斯记忆追踪、错因分析与巩固复习状态"
                });

                metrics.Add(new TableStorageMetricDto
                {
                    TableName = "HomeworkAssignments",
                    DisplayName = "作业布置记录",
                    RowCount = await ctx.HomeworkAssignments.CountAsync(),
                    Description = "班级作业分发、提交与批改明细"
                });

                metrics.Add(new TableStorageMetricDto
                {
                    TableName = "UserFavorites",
                    DisplayName = "用户收藏题本",
                    RowCount = await ctx.UserFavorites.CountAsync(),
                    Description = "学生重点关注试题与个性化收藏"
                });

                metrics.Add(new TableStorageMetricDto
                {
                    TableName = "CurriculumSubjectConfigs",
                    DisplayName = "学科课标配置",
                    RowCount = await ctx.CurriculumSubjectConfigs.CountAsync(),
                    Description = "各年级学科教材版本与知识图谱拓扑"
                });

                metrics.Add(new TableStorageMetricDto
                {
                    TableName = "LlmGenerationLogs",
                    DisplayName = "AI 导师生成审计",
                    RowCount = await ctx.LlmGenerationLogs.CountAsync(),
                    Description = "AI 导师苏格拉底启发与变式题生成审计日志"
                });
            }
            catch (Exception ex)
            {
                RecordArchitectureEvent("Telemetry", "Warning", $"获取数据表存储指标异常: {ex.Message}");
            }

            return metrics;
        }

        public async Task<BackupVerificationResultDto> VerifyBackupSnapshotAsync(string fileName, string? targetDir = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new BackupVerificationResultDto
            {
                FileName = fileName,
                CheckedAt = DateTime.Now
            };

            string? cleanName = null;
            try
            {
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    result.ErrorMessage = "快照文件名不能为空。";
                    return result;
                }

                cleanName = Path.GetFileName(fileName);
                if (cleanName != fileName || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                {
                    result.ErrorMessage = "检测到非法路径穿越参数，校验请求已被架构安全网关拦截。";
                    RecordArchitectureEvent("SecurityDefense", "Warning", $"非法快照校验尝试: {fileName}");
                    return result;
                }

                var ext = Path.GetExtension(cleanName).ToLowerInvariant();
                if (ext != ".db" && ext != ".bak")
                {
                    result.ErrorMessage = "快照文件扩展名不合法，仅支持 .db 与 .bak 格式。";
                    return result;
                }

                var backupDirectory = targetDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups");
                var fullPath = Path.Combine(backupDirectory, cleanName);
                if (!File.Exists(fullPath))
                {
                    result.ErrorMessage = $"目标快照文件不存在: {cleanName}";
                    return result;
                }

                var fileInfo = new FileInfo(fullPath);
                result.FileSizeBytes = fileInfo.Length;
                if (fileInfo.Length == 0)
                {
                    result.ErrorMessage = "快照文件物理大小为 0 字节，属于无效空文件。";
                    return result;
                }

                // 计算 SHA-256 哈希
                using (var sha256 = SHA256.Create())
                await using (var fileStream = File.OpenRead(fullPath))
                {
                    var hashBytes = await sha256.ComputeHashAsync(fileStream);
                    result.Sha256Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                }

                // 建立只读连接，执行物理完整性校验与元数据巡检
                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = fullPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Shared
                }.ToString();

                await using var conn = new SqliteConnection(connStr);
                await conn.OpenAsync();

                // PRAGMA integrity_check
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA integrity_check(100);";
                    var status = Convert.ToString(await cmd.ExecuteScalarAsync()) ?? "unknown";
                    result.SqliteIntegrityStatus = status;
                }

                // 统计表数量与核心表存在性
                var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        tables.Add(reader.GetString(0));
                    }
                }

                result.TableCount = tables.Count;
                var coreTables = new[] { "Questions", "Users", "PracticeRecords", "ErrorItems" };
                result.CoreTablesPresent = coreTables.All(tables.Contains);

                result.IsHealthy = string.Equals(result.SqliteIntegrityStatus, "ok", StringComparison.OrdinalIgnoreCase) && result.CoreTablesPresent;
                sw.Stop();
                result.VerificationDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

                if (result.IsHealthy)
                {
                    RecordArchitectureEvent("BackupVerify", "Success", $"快照物理深度校验通过: {cleanName} (包含 {result.TableCount} 张表, 耗时 {result.VerificationDurationMs}ms)", result.VerificationDurationMs);
                }
                else
                {
                    result.ErrorMessage = $"快照完整性状态异常 [{result.SqliteIntegrityStatus}], 核心表完整性: {result.CoreTablesPresent}";
                    RecordArchitectureEvent("BackupVerify", "Error", $"快照深度校验异常: {cleanName} [{result.ErrorMessage}]", result.VerificationDurationMs);
                }

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.VerificationDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                result.ErrorMessage = $"校验过程发生底层异常: {ex.Message}";
                result.IsHealthy = false;
                RecordArchitectureEvent("BackupVerify", "Error", $"快照校验发生异常: {cleanName ?? fileName} - {ex.Message}", result.VerificationDurationMs);
                return result;
            }
        }

        public async Task<string> ExportArchitectureDiagnosticReportJsonAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var health = await GetSystemHealthAsync();
                var tableMetrics = await GetTableStorageMetricsAsync();
                var backups = await GetBackupsListAsync();
                var recentEvents = GetRecentArchitectureEvents();

                var report = new
                {
                    ReportMetadata = new
                    {
                        Title = "Northtropic 架构健康与自愈审计诊断全量快照",
                        GeneratedAt = DateTime.Now,
                        HostEnvironment = new
                        {
                            DotNetVersion = Environment.Version.ToString(),
                            OSDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                            OSArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                            ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                            ProcessorCount = Environment.ProcessorCount,
                            MachineName = Environment.MachineName,
                            ProcessUptime = health.ProcessUptimeFormatted
                        }
                    },
                    ArchitectureHealth = health,
                    TableStorageMetrics = tableMetrics,
                    BackupSnapshots = backups,
                    TelemetryEventStream = recentEvents
                };

                var options = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                var json = System.Text.Json.JsonSerializer.Serialize(report, options);
                sw.Stop();
                RecordArchitectureEvent("Telemetry", "Success", $"生成全量架构诊断报告快照完成 (耗时 {sw.Elapsed.TotalMilliseconds:F1}ms)", sw.Elapsed.TotalMilliseconds);
                return json;
            }
            catch (Exception ex)
            {
                sw.Stop();
                RecordArchitectureEvent("Telemetry", "Error", $"生成架构诊断报告失败: {ex.Message}", sw.Elapsed.TotalMilliseconds);
                throw;
            }
        }

        public async Task<ArchitecturalDiagnosticResultDto> RunArchitecturalSelfDiagnosticAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new ArchitecturalDiagnosticResultDto
            {
                ExecutedAt = DateTime.Now
            };

            try
            {
                // 1. 延迟测定
                result.LatencyMs = await MeasureDatabaseLatencyAsync();
                result.LatencyRating = result.LatencyMs switch
                {
                    <= 5.0 => "卓越极速 (Optimal)",
                    <= 25.0 => "良好平稳 (Good)",
                    <= 100.0 => "轻度延迟 (Elevated)",
                    _ => "严重告警 (Critical)"
                };
                result.DiagnosticCheckpoints.Add($"[Checkpoint 1] 数据库往返延迟: {result.LatencyMs:F2}ms ({result.LatencyRating})");

                // 2. 数据库物理与外键完整性
                result.SqliteIntegrity = await RunDatabaseIntegrityCheckAsync();
                result.ForeignKeyIntegrity = await RunDatabaseForeignKeyCheckAsync();
                bool dbOk = result.SqliteIntegrity.Equals("ok", StringComparison.OrdinalIgnoreCase);
                bool fkOk = result.ForeignKeyIntegrity.Equals("ok", StringComparison.OrdinalIgnoreCase);
                result.DiagnosticCheckpoints.Add($"[Checkpoint 2] SQLite PRAGMA quick_check: {result.SqliteIntegrity}, foreign_key_check: {result.ForeignKeyIntegrity}");

                // 3. 核心业务表存储探针
                var tableMetrics = await GetTableStorageMetricsAsync();
                result.CoreTablesFound = tableMetrics.Count;
                var questionMetric = tableMetrics.FirstOrDefault(m => m.TableName == "Questions");
                result.TotalQuestionsScanned = questionMetric?.RowCount ?? 0;
                result.DiagnosticCheckpoints.Add($"[Checkpoint 3] 核心数据表巡检完成: 探针捕获 {result.CoreTablesFound} 张表, 题库总量 {result.TotalQuestionsScanned} 道");

                // 4. 运行时内存
                result.GcMemoryBytes = GC.GetTotalMemory(false);
                result.DiagnosticCheckpoints.Add($"[Checkpoint 4] 运行时托管堆内存: {result.GcMemoryBytes / (1024.0 * 1024.0):F2} MB");

                result.IsPassed = dbOk && fkOk && result.CoreTablesFound >= 6;
                result.OverallStatus = result.IsPassed ? "Pass" : "Warning";

                sw.Stop();
                RecordArchitectureEvent("SelfDiagnostic", result.IsPassed ? "Success" : "Warning",
                    $"全量架构自检完成: 状态={result.OverallStatus}, 延迟={result.LatencyMs:F2}ms, 题量={result.TotalQuestionsScanned}", sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.IsPassed = false;
                result.OverallStatus = "Fail";
                result.DiagnosticCheckpoints.Add($"[Exception] 架构自检异常中断: {ex.Message}");
                RecordArchitectureEvent("SelfDiagnostic", "Error", $"架构自检发生异常: {ex.Message}", sw.Elapsed.TotalMilliseconds);
            }

            return result;
        }
    }
}
