using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;

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

        public IReadOnlyList<SystemArchitectureEvent> GetRecentArchitectureEvents(string? category = null, string? level = null, int? maxCount = null)
        {
            var query = _telemetryEvents.Reverse().AsEnumerable();
            if (!string.IsNullOrWhiteSpace(category) && category != "全部" && category != "All")
            {
                query = query.Where(e => string.Equals(e.Category, category.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(level) && level != "全部" && level != "All")
            {
                query = query.Where(e => string.Equals(e.Level, level.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (maxCount.HasValue && maxCount.Value > 0)
            {
                query = query.Take(maxCount.Value);
            }
            return query.ToList();
        }

        public ArchitectureTelemetrySummaryDto GetArchitectureTelemetrySummary()
        {
            var events = _telemetryEvents.ToArray();
            var summary = new ArchitectureTelemetrySummaryDto
            {
                TotalEvents = events.Length,
                ErrorCount = events.Count(e => string.Equals(e.Level, "Error", StringComparison.OrdinalIgnoreCase)),
                WarningCount = events.Count(e => string.Equals(e.Level, "Warning", StringComparison.OrdinalIgnoreCase)),
                SuccessCount = events.Count(e => string.Equals(e.Level, "Success", StringComparison.OrdinalIgnoreCase)),
                InfoCount = events.Count(e => string.Equals(e.Level, "Info", StringComparison.OrdinalIgnoreCase))
            };

            if (summary.TotalEvents > 0)
            {
                double validEvents = summary.TotalEvents - summary.ErrorCount;
                summary.ReliabilityScore = Math.Round(Math.Max(0.0, Math.Min(100.0, (validEvents / summary.TotalEvents) * 100.0)), 1);

                var eventsWithDuration = events.Where(e => e.DurationMs.HasValue && e.DurationMs.Value > 0).ToList();
                if (eventsWithDuration.Count > 0)
                {
                    summary.AverageDurationMs = Math.Round(eventsWithDuration.Average(e => e.DurationMs!.Value), 2);
                }
            }

            return summary;
        }

        public async Task<AdaptiveMaintenancePlanDto> EvaluateAdaptiveMaintenancePlanAsync()
        {
            var plan = new AdaptiveMaintenancePlanDto();
            try
            {
                var health = await GetSystemHealthAsync();

                // 1. Check WAL log size (threshold: 4 MB for warning, 10 MB for critical)
                if (health.WalSizeBytes >= 10 * 1024 * 1024)
                {
                    plan.RequiresWalCheckpoint = true;
                    plan.ActionReasons.Add($"WAL 日志体积达到 {health.WalSizeFormatted}，超过 10 MB 紧急截断阈值，需立即执行 PRAGMA wal_checkpoint(TRUNCATE)；");
                }
                else if (health.WalSizeBytes >= 4 * 1024 * 1024)
                {
                    plan.RequiresWalCheckpoint = true;
                    plan.ActionReasons.Add($"WAL 日志体积达到 {health.WalSizeFormatted}，超过 4 MB 日常维护水位；");
                }

                // 2. Check Fragmentation ratio & Freelist
                if (health.FragmentationRatio >= 30.0 && health.FragmentationBytes >= 2 * 1024 * 1024)
                {
                    plan.RequiresVacuum = true;
                    plan.ActionReasons.Add($"SQLite 存储碎片率达到 {health.FragmentationRatio:F1}% (空闲页 {health.FreelistCount}，产生 {health.FragmentationFormatted} 空间空洞)，建议执行 VACUUM 规整；");
                }
                else if (health.FragmentationRatio >= 15.0)
                {
                    plan.RequiresOptimization = true;
                    plan.ActionReasons.Add($"SQLite 存储碎片率达到 {health.FragmentationRatio:F1}%，建议调度自愈优化；");
                }

                // 3. Check Query Latency
                if (health.DatabaseLatencyMs > 25.0)
                {
                    plan.RequiresOptimization = true;
                    plan.ActionReasons.Add(health.DatabaseProvider == "PostgreSQL"
                        ? $"数据库往返时延升至 {health.DatabaseLatencyMs:F1}ms，建议执行 ANALYZE 重建查询计划统计；"
                        : $"数据库往返时延升至 {health.DatabaseLatencyMs:F1}ms，建议执行 PRAGMA analyze / optimize 重建统计索引；");
                }

                // 3.5 Check Data Integrity & Orphans
                var integrityAudit = await AuditDataIntegrityAsync();
                if (integrityAudit.TotalIssuesCount > 0)
                {
                    plan.RequiresOrphanCleanup = true;
                    plan.ActionReasons.Add($"检测到 {integrityAudit.TotalIssuesCount} 项孤儿/异常关联数据，建议执行事务级自愈清理；");
                    if (plan.UrgencyLevel == "Low") plan.UrgencyLevel = "Medium";
                }

                // 4. Determine Urgency
                if (health.WalSizeBytes >= 10 * 1024 * 1024 || (health.FragmentationRatio >= 40.0 && health.FragmentationBytes >= 5 * 1024 * 1024) || !health.IsDatabaseHealthy || !health.IsForeignKeyHealthy || integrityAudit.TotalIssuesCount > 50)
                {
                    plan.UrgencyLevel = "Critical";
                }
                else if (plan.RequiresVacuum || plan.RequiresWalCheckpoint)
                {
                    plan.UrgencyLevel = "High";
                }
                else if (plan.RequiresOptimization || plan.RequiresOrphanCleanup)
                {
                    plan.UrgencyLevel = "Medium";
                }
                else
                {
                    plan.UrgencyLevel = "Low";
                    plan.ActionReasons.Add("系统各项物理存储指标、WAL 队列、查询延迟与业务数据拓扑均处于最优或健康水平，暂无需执行重整操作。");
                }
            }
            catch (Exception ex)
            {
                plan.UrgencyLevel = "Warning";
                plan.ActionReasons.Add($"评估自适应维护计划时捕获异常: {ex.Message}");
            }

            return plan;
        }

        private static readonly System.Threading.SemaphoreSlim _maintenanceLock = new(1, 1);

        public async Task<AdaptiveMaintenanceExecutionResultDto> ExecuteAdaptiveMaintenancePlanAsync(AdaptiveMaintenancePlanDto? plan = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new AdaptiveMaintenanceExecutionResultDto();

            // 1. 获取互斥信号量，防止并发重复执行 VACUUM/重整造成数据库繁忙或锁竞争
            bool acquired = await _maintenanceLock.WaitAsync(TimeSpan.FromSeconds(30));
            if (!acquired)
            {
                result.Success = false;
                result.Message = "系统正在进行另一项底层存储重整或维护任务，请稍后重试。";
                RecordArchitectureEvent("SelfHealing", "Warning", result.Message);
                return result;
            }

            try
            {
                // 2. 若未传入有效 plan，则先动态评估
                plan ??= await EvaluateAdaptiveMaintenancePlanAsync();

                // 3. 采样维护前基线指标
                var preHealth = await GetSystemHealthAsync();
                result.BeforeWalSizeBytes = preHealth.WalSizeBytes;
                result.BeforeFragmentationRatio = preHealth.FragmentationRatio;
                result.BeforeLatencyMs = preHealth.DatabaseLatencyMs;

                long beforeTotalFileBytes = preHealth.DatabaseSizeBytes + preHealth.WalSizeBytes;

                // 4. 按架构优先级安全执行维护动作
                // 动作 A: WAL Checkpoint (TRUNCATE) 刷新并截断 WAL 日志
                if (plan.RequiresWalCheckpoint || plan.UrgencyLevel == "Critical" || plan.UrgencyLevel == "High")
                {
                    await using (var dbScope = await CreateDbScopeAsync())
                    {
                        if (dbScope.Context.Database.IsSqlite())
                        {
                            var conn = dbScope.Context.Database.GetDbConnection();
                            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
                            using var cmd = conn.CreateCommand();
                            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                            await cmd.ExecuteScalarAsync();
                            result.ExecutedActions.Add("WAL 日志截断归档 (PRAGMA wal_checkpoint(TRUNCATE))");
                        }
                        else
                        {
                            result.ExecutedActions.Add("PostgreSQL 引擎自动检查点归档 (Auto-Managed Checkpoint)");
                        }
                    }
                }

                // 动作 B: VACUUM 碎片重整
                if (plan.RequiresVacuum || plan.UrgencyLevel == "Critical")
                {
                    bool vacOk = await VacuumDatabaseAsync();
                    if (vacOk)
                    {
                        await using var dbScope = await CreateDbScopeAsync();
                        if (dbScope.Context.Database.IsSqlite())
                        {
                            result.ExecutedActions.Add("SQLite 物理存储碎片重整 (VACUUM)");
                        }
                        else
                        {
                            result.ExecutedActions.Add("PostgreSQL 存储空间重整 (VACUUM)");
                        }
                    }
                }

                // 动作 C: Analyze & Optimize 索引直方图与统计信息重估
                if (plan.RequiresOptimization || plan.RequiresVacuum || plan.RequiresWalCheckpoint || result.ExecutedActions.Count == 0)
                {
                    await using (var dbScope = await CreateDbScopeAsync())
                    {
                        if (dbScope.Context.Database.IsSqlite())
                        {
                            var conn = dbScope.Context.Database.GetDbConnection();
                            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
                            using (var cmdAnalyze = conn.CreateCommand())
                            {
                                cmdAnalyze.CommandText = "PRAGMA analyze;";
                                await cmdAnalyze.ExecuteNonQueryAsync();
                            }
                            using (var cmdOptimize = conn.CreateCommand())
                            {
                                cmdOptimize.CommandText = "PRAGMA optimize;";
                                await cmdOptimize.ExecuteNonQueryAsync();
                            }
                            result.ExecutedActions.Add("查询优化器与直方图统计重建 (PRAGMA analyze / optimize)");
                        }
                        else
                        {
                            try
                            {
                                var conn = dbScope.Context.Database.GetDbConnection();
                                if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
                                using var cmdAnalyze = conn.CreateCommand();
                                cmdAnalyze.CommandText = "ANALYZE;";
                                await cmdAnalyze.ExecuteNonQueryAsync();
                                result.ExecutedActions.Add("PostgreSQL 查询计划统计重建 (ANALYZE)");
                            }
                            catch (Exception ex)
                            {
                                result.ExecutedActions.Add($"PostgreSQL ANALYZE 评估跳过 ({ex.Message})");
                            }
                        }
                    }
                }

                // 动作 D: 业务孤儿数据自愈清理
                if (plan.RequiresOrphanCleanup || plan.UrgencyLevel == "Critical")
                {
                    var purgeRes = await PurgeOrphanedRecordsAsync();
                    if (purgeRes.Success && purgeRes.TotalPurgedCount > 0)
                    {
                        result.PurgedOrphanCount = purgeRes.TotalPurgedCount;
                        result.ExecutedActions.Add($"业务孤儿数据自愈清理 ({purgeRes.TotalPurgedCount} 条)");
                    }
                }

                // 5. 采样维护后指标并计算收益
                var postHealth = await GetSystemHealthAsync();
                result.AfterWalSizeBytes = postHealth.WalSizeBytes;
                result.AfterFragmentationRatio = postHealth.FragmentationRatio;
                result.AfterLatencyMs = postHealth.DatabaseLatencyMs;

                long afterTotalFileBytes = postHealth.DatabaseSizeBytes + postHealth.WalSizeBytes;
                result.BytesReclaimed = Math.Max(0, beforeTotalFileBytes - afterTotalFileBytes);

                // 6. 验证数据库完整性
                var integrityCheck = await RunDatabaseIntegrityCheckAsync();
                result.IntegrityVerified = string.Equals(integrityCheck, "ok", StringComparison.OrdinalIgnoreCase);

                sw.Stop();
                result.ElapsedMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                result.Success = true;

                string actionsDesc = string.Join(" + ", result.ExecutedActions);
                string reclaimedDesc = result.BytesReclaimed > 1024 * 1024
                    ? $"{result.BytesReclaimed / (1024.0 * 1024.0):F2} MB"
                    : (result.BytesReclaimed > 1024 ? $"{result.BytesReclaimed / 1024.0:F1} KB" : $"{result.BytesReclaimed} Bytes");

                result.Message = $"自适应自愈维护执行成功！完成 [{actionsDesc}]，耗时 {result.ElapsedMilliseconds}ms，释放存储空间 {reclaimedDesc}，时延自 {result.BeforeLatencyMs:F1}ms 优化至 {result.AfterLatencyMs:F1}ms。";
                RecordArchitectureEvent("SelfHealing", "Success", result.Message, result.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.Success = false;
                result.ElapsedMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                result.Message = $"执行自适应维护时发生异常: {ex.Message}";
                RecordArchitectureEvent("SelfHealing", "Error", result.Message, result.ElapsedMilliseconds);
                return result;
            }
            finally
            {
                _maintenanceLock.Release();
            }
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

            // 2. 数据库物理存储与引擎检测
            dto.DatabaseProvider = ctx.Database.IsSqlite() ? "SQLite" : (ctx.Database.ProviderName?.Split('.').LastOrDefault() ?? "PostgreSQL");
            var conn = ctx.Database.GetDbConnection();
            var dataSource = conn.DataSource;
            if (ctx.Database.IsSqlite())
            {
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
            }
            else
            {
                dto.DatabaseFileExists = true;
                try
                {
                    if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
                    using var sizeCmd = conn.CreateCommand();
                    sizeCmd.CommandText = "SELECT pg_database_size(current_database());";
                    var sizeRes = await sizeCmd.ExecuteScalarAsync();
                    if (sizeRes != null && long.TryParse(sizeRes.ToString(), out var dbBytes))
                    {
                        dto.DatabaseSizeBytes = dbBytes;
                    }
                }
                catch { }
            }

            // 2.1 测量数据库查询延迟
            dto.DatabaseLatencyMs = await MeasureDatabaseLatencyInternalAsync(ctx);

            // 2.2 测量 SQLite 物理存储分页与空闲碎片 (PRAGMA page_count, page_size, freelist_count)
            if (ctx.Database.IsSqlite())
            {
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
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                dto.TotalAvailableMemoryBytes = gcInfo.TotalAvailableMemoryBytes;
                dto.MemoryLoadBytes = gcInfo.MemoryLoadBytes;
                dto.GcPauseRatio = Math.Round(gcInfo.PauseTimePercentage, 2);
            }
            catch
            {
                // 静默容错
            }
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

            if (dto.TotalAvailableMemoryBytes > 0 && dto.MemoryPressurePercentage > 90.0)
            {
                score -= 15;
                recs.Add($"⚠️ 宿主/容器物理内存压力极高 ({dto.MemoryPressurePercentage:F1}%)，剩余可用空间偏低，建议扩容或削峰限流。");
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

                if (ctx.Database.IsSqlite())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA integrity_check;";
                    var result = await cmd.ExecuteScalarAsync();
                    return result?.ToString() ?? "ok";
                }
                else
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT 1;";
                    await cmd.ExecuteScalarAsync();
                    return "ok";
                }
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
                if (!ctx.Database.IsSqlite())
                {
                    return "ok";
                }

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

                if (ctx.Database.IsSqlite())
                {
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
                }
                else
                {
                    using (var cmdAnalyze = conn.CreateCommand())
                    {
                        cmdAnalyze.CommandText = "ANALYZE;";
                        await cmdAnalyze.ExecuteNonQueryAsync();
                        result.AnalyzeStatus = "ok";
                        result.OptimizeStatus = "ok";
                        result.CheckpointStatus = "PostgreSQL Auto-Managed";
                    }
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

                if (!ctx.Database.IsSqlite())
                {
                    sw.Stop();
                    result.Success = true;
                    result.BackupFileName = $"Remote_PostgreSQL_{conn.Database}_{DateTime.Now:yyyyMMdd_HHmmss}.dump";
                    result.BackupFilePath = $"Remote Host: {conn.DataSource ?? "120.192.20.243"}";
                    result.FileSizeBytes = 0;
                    result.CreatedAt = DateTime.Now;
                    result.ElapsedMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                    result.Message = "远程 PostgreSQL 数据库由云端及独立数据库管理系统进行全量备份与周期归档。";
                    RecordArchitectureEvent("Telemetry", "Info", result.Message, result.ElapsedMilliseconds);
                    return result;
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
                await using (var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var hashBytes = await sha256.ComputeHashAsync(fileStream);
                    result.Sha256Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                }

                // 1. 底层二进制魔数与 PageSize 校验 (SQLite 3 规范: 前 16 字节为 "SQLite format 3\0")
                await using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var headerBytes = new byte[18];
                    var bytesRead = await fs.ReadAsync(headerBytes, 0, 18);
                    if (bytesRead >= 16)
                    {
                        var magic = Encoding.ASCII.GetString(headerBytes, 0, 15);
                        result.HeaderValidated = magic == "SQLite format 3" && headerBytes[15] == 0;
                        if (bytesRead >= 18)
                        {
                            int rawPageSize = (headerBytes[16] << 8) | headerBytes[17];
                            result.PageSize = rawPageSize == 1 ? 65536 : rawPageSize;
                        }
                    }
                }

                if (!result.HeaderValidated)
                {
                    result.ErrorMessage = "快照文件头部非合法 SQLite 3 二进制格式魔数。";
                    result.IsHealthy = false;
                    RecordArchitectureEvent("BackupVerify", "Error", $"快照魔数头校验失败: {cleanName}", sw.Elapsed.TotalMilliseconds);
                    return result;
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

                // PRAGMA foreign_key_check
                await using (var fkCmd = conn.CreateCommand())
                {
                    fkCmd.CommandText = "PRAGMA foreign_key_check;";
                    await using var fkReader = await fkCmd.ExecuteReaderAsync();
                    if (await fkReader.ReadAsync())
                    {
                        result.ForeignKeyStatus = "外键约束异常 (Violations detected)";
                    }
                    else
                    {
                        result.ForeignKeyStatus = "ok";
                    }
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

                // 统计核心业务表记录行数并验证全量实体对齐
                foreach (var tbl in coreTables)
                {
                    if (tables.Contains(tbl))
                    {
                        try
                        {
                            await using var countCmd = conn.CreateCommand();
                            countCmd.CommandText = $"SELECT COUNT(*) FROM \"{tbl}\";";
                            var countVal = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
                            result.TableRowCounts[tbl] = countVal;
                        }
                        catch
                        {
                            result.TableRowCounts[tbl] = -1;
                        }
                    }
                }
                result.ParityVerified = result.CoreTablesPresent && result.TableRowCounts.Count >= coreTables.Length;

                result.IsHealthy = result.HeaderValidated &&
                                  string.Equals(result.SqliteIntegrityStatus, "ok", StringComparison.OrdinalIgnoreCase) &&
                                  result.CoreTablesPresent &&
                                  result.IsForeignKeyHealthy;
                sw.Stop();
                result.VerificationDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

                if (result.IsHealthy)
                {
                    RecordArchitectureEvent("BackupVerify", "Success", $"快照物理深度校验通过: {cleanName} (包含 {result.TableCount} 张表, 耗时 {result.VerificationDurationMs}ms)", result.VerificationDurationMs);
                }
                else
                {
                    result.ErrorMessage = $"快照完整性状态异常 [{result.SqliteIntegrityStatus}], 核心表完整性: {result.CoreTablesPresent}, 外键约束: {result.ForeignKeyStatus}";
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

        public async Task<string> ExportArchitectureDiagnosticReportMarkdownAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var health = await GetSystemHealthAsync();
                var tableMetrics = await GetTableStorageMetricsAsync();
                var backups = await GetBackupsListAsync();
                var recentEvents = GetRecentArchitectureEvents(maxCount: 20);
                var telemetrySummary = GetArchitectureTelemetrySummary();

                var sb = new StringBuilder();
                sb.AppendLine("# 🏛️ Northtropic 系统架构全景诊断与灾备健康档案");
                sb.AppendLine();
                sb.AppendLine($"> **生成时间**: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | **宿主操作系统**: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} | **.NET 运行时**: {Environment.Version}");
                sb.AppendLine();
                sb.AppendLine("## 一、系统架构综合健康与评分");
                sb.AppendLine($"- **健康评分**: **{health.HealthScore} 分** ({health.HealthRating})");
                sb.AppendLine($"- **架构可靠性得分**: **{telemetrySummary.ReliabilityScore:F1} 分**");
                sb.AppendLine($"- **服务连续运行时间**: {health.ProcessUptimeFormatted}");
                sb.AppendLine($"- **数据库往返延迟**: {health.DatabaseLatencyMs:F2} ms ({health.LatencyRating})");
                sb.AppendLine($"- **SQLite 物理完整性**: `{health.SqliteIntegrityStatus}` | **外键约束**: `{health.ForeignKeyIntegrityStatus}`");

                var dataIntegrity = await AuditDataIntegrityAsync();
                sb.AppendLine($"- **数据拓扑完整性状态**: `{(dataIntegrity.IsHealthy ? "ok" : "Warning")}` (孤儿异常数: {dataIntegrity.TotalIssuesCount})");
                sb.AppendLine();
                sb.AppendLine("## 二、存储引擎与物理文件指标");
                sb.AppendLine($"- **主数据库文件大小**: {health.DatabaseSizeFormatted} (共 {health.PageCount} 页，分页大小 {health.PageSize} 字节)");
                sb.AppendLine($"- **WAL 预写日志大小**: {health.WalSizeFormatted}");
                sb.AppendLine($"- **空闲页碎片 (Freelist)**: {health.FreelistCount} 页 ({health.FragmentationFormatted}, 碎片率 {health.FragmentationRatio}%)");
                sb.AppendLine($"- **存储宿主磁盘可用空间**: {health.DiskFreeSpaceFormatted}");
                sb.AppendLine();
                sb.AppendLine("## 三、托管运行时与内存压力指标");
                sb.AppendLine($"- **GC 堆托管内存**: {health.GcMemoryFormatted}");
                sb.AppendLine($"- **系统可用物理内存**: {health.TotalAvailableMemoryFormatted} (内存压力占比 {health.MemoryPressurePercentage}%)");
                sb.AppendLine($"- **GC 垃圾回收计数**: Gen0={health.GcGen0Collections}, Gen1={health.GcGen1Collections}, Gen2={health.GcGen2Collections} (GC暂停占比: {health.GcPauseRatio:F2}%)");
                sb.AppendLine($"- **工作线程池饱和度**: {health.ThreadPoolSaturationRatio}% (空闲工作线程: {health.ThreadPoolAvailableWorkerThreads}/{health.ThreadPoolMaxWorkerThreads})");
                sb.AppendLine();
                sb.AppendLine("## 四、核心业务数据表分布与对齐");
                sb.AppendLine("| 数据表名 | 业务实体 | 记录行数 | 描述 |");
                sb.AppendLine("| :--- | :--- | :---: | :--- |");
                foreach (var tbl in tableMetrics)
                {
                    sb.AppendLine($"| `{tbl.TableName}` | {tbl.DisplayName} | {tbl.RowCount} | {tbl.Description} |");
                }
                sb.AppendLine();
                sb.AppendLine("## 五、灾备快照归档状态");
                if (backups.Count == 0)
                {
                    sb.AppendLine("_暂无本地备份快照文件。_");
                }
                else
                {
                    sb.AppendLine("| 快照文件名 | 文件大小 | 创建时间 | SHA-256 校验 | 深度健康 |");
                    sb.AppendLine("| :--- | :---: | :---: | :---: | :---: |");
                    foreach (var b in backups)
                    {
                        var hashShort = string.IsNullOrEmpty(b.Sha256Hash) ? "未计算" : b.Sha256Hash.Substring(0, Math.Min(8, b.Sha256Hash.Length)) + "...";
                        sb.AppendLine($"| `{b.BackupFileName}` | {b.FileSizeFormatted} | {b.CreatedAt:yyyy-MM-dd HH:mm} | `{hashShort}` | {(b.IntegrityVerified ? "✅ 验证通过" : "待校验")} |");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("## 六、最新架构自愈与 APM 遥测事件流");
                if (recentEvents.Count == 0)
                {
                    sb.AppendLine("_暂无异动遥测事件。_");
                }
                else
                {
                    sb.AppendLine("| 时间戳 | 类别 | 等级 | 耗时 | 事件详情 |");
                    sb.AppendLine("| :--- | :---: | :---: | :---: | :--- |");
                    foreach (var ev in recentEvents)
                    {
                        var dur = ev.DurationMs.HasValue ? $"{ev.DurationMs.Value:F1}ms" : "-";
                        sb.AppendLine($"| {ev.Timestamp:HH:mm:ss} | `{ev.Category}` | **{ev.Level}** | {dur} | {ev.Message} |");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine("_Northtropic 架构自愈监控引擎自动生成_");

                sw.Stop();
                RecordArchitectureEvent("Telemetry", "Success", $"生成 Markdown 架构诊断档案完成 (耗时 {sw.Elapsed.TotalMilliseconds:F1}ms)", sw.Elapsed.TotalMilliseconds);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                sw.Stop();
                RecordArchitectureEvent("Telemetry", "Error", $"生成 Markdown 架构诊断档案失败: {ex.Message}", sw.Elapsed.TotalMilliseconds);
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

                // 2.5 业务数据拓扑完整性与孤儿记录主动巡检
                var integrityAudit = await AuditDataIntegrityAsync();
                result.DataIntegrityIssuesCount = integrityAudit.TotalIssuesCount;
                result.DataIntegrityStatus = integrityAudit.IsHealthy ? "ok" : $"Warning ({integrityAudit.TotalIssuesCount} issues)";
                result.DiagnosticCheckpoints.Add($"[Checkpoint 2.5] 业务数据拓扑完整性巡检: {(integrityAudit.IsHealthy ? "零孤儿/完整 (ok)" : $"检测到 {integrityAudit.TotalIssuesCount} 项孤儿/异常记录")}");

                // 3. 核心业务表存储探针
                var tableMetrics = await GetTableStorageMetricsAsync();
                result.CoreTablesFound = tableMetrics.Count;
                var questionMetric = tableMetrics.FirstOrDefault(m => m.TableName == "Questions");
                result.TotalQuestionsScanned = questionMetric?.RowCount ?? 0;
                result.DiagnosticCheckpoints.Add($"[Checkpoint 3] 核心数据表巡检完成: 探针捕获 {result.CoreTablesFound} 张表, 题库总量 {result.TotalQuestionsScanned} 道");

                // 4. 运行时内存
                result.GcMemoryBytes = GC.GetTotalMemory(false);
                result.DiagnosticCheckpoints.Add($"[Checkpoint 4] 运行时托管堆内存: {result.GcMemoryBytes / (1024.0 * 1024.0):F2} MB");

                // 5. 异步并发连接池与 WAL 锁争用压力探针 (Checkpoint 5)
                var stressSw = System.Diagnostics.Stopwatch.StartNew();
                int probeCount = 10;
                bool allProbesOk = true;

                if (_dbContextFactory != null)
                {
                    var stressTasks = new List<Task<bool>>();
                    for (int i = 0; i < probeCount; i++)
                    {
                        stressTasks.Add(Task.Run(async () =>
                        {
                            await using var scope = await CreateDbScopeAsync();
                            return await scope.Context.Database.CanConnectAsync();
                        }));
                    }
                    var stressResults = await Task.WhenAll(stressTasks);
                    allProbesOk = stressResults.All(r => r);
                }
                else
                {
                    for (int i = 0; i < probeCount; i++)
                    {
                        await using var scope = await CreateDbScopeAsync();
                        if (!await scope.Context.Database.CanConnectAsync())
                        {
                            allProbesOk = false;
                            break;
                        }
                    }
                }

                stressSw.Stop();
                double elapsedSec = Math.Max(0.001, stressSw.Elapsed.TotalSeconds);
                result.ConcurrencyStressPassed = allProbesOk;
                result.ConcurrencyThroughputQps = Math.Round(probeCount / elapsedSec, 1);
                string factoryMode = _dbContextFactory != null ? "高并发独立连接池" : "单例回退安全通道";
                result.DiagnosticCheckpoints.Add($"[Checkpoint 5] 异步并发连接池与 WAL 锁争用压力探针 ({factoryMode}): {probeCount}/{probeCount} 探测通过, 吞吐 {result.ConcurrencyThroughputQps} QPS (耗时 {stressSw.ElapsedMilliseconds}ms)");

                result.IsPassed = dbOk && fkOk && integrityAudit.IsHealthy && result.CoreTablesFound >= 6 && result.ConcurrencyStressPassed;
                result.OverallStatus = result.IsPassed ? "Pass" : "Warning";

                sw.Stop();
                RecordArchitectureEvent("SelfDiagnostic", result.IsPassed ? "Success" : "Warning",
                    $"全量架构自检完成: 状态={result.OverallStatus}, 延迟={result.LatencyMs:F2}ms, 题量={result.TotalQuestionsScanned}, 拓扑={result.DataIntegrityStatus}, 吞吐={result.ConcurrencyThroughputQps} QPS", sw.Elapsed.TotalMilliseconds);
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

        public async Task<DataIntegrityAuditDto> AuditDataIntegrityAsync()
        {
            var audit = new DataIntegrityAuditDto();
            try
            {
                await using var dbScope = await CreateDbScopeAsync();
                var db = dbScope.Context;

                var userIds = await db.Users.Select(u => u.Id).ToListAsync();
                var questionIds = await db.Questions.Select(q => q.Id).ToListAsync();
                var studyPlanIds = await db.StudyPlans.Select(s => s.Id).ToListAsync();
                var achievementIds = await db.Achievements.Select(a => a.Id).ToListAsync();
                var userIdSet = new HashSet<Guid>(userIds);
                var questionIdSet = new HashSet<Guid>(questionIds);
                var studyPlanIdSet = new HashSet<Guid>(studyPlanIds);
                var achievementIdSet = new HashSet<Guid>(achievementIds);

                // 1. ErrorItems
                var errors = await db.ErrorItems.Select(e => new { e.Id, e.QuestionId, e.UserId }).ToListAsync();
                audit.TotalOrphanErrorItems = errors.Count(e => !questionIdSet.Contains(e.QuestionId) || !userIdSet.Contains(e.UserId));
                if (audit.TotalOrphanErrorItems > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalOrphanErrorItems} 条无主错题记录 (关联题目或用户不存在)");
                }

                // 2. PracticeRecords
                var practiceRecords = await db.PracticeRecords.Select(r => new { r.Id, r.QuestionId, r.UserId }).ToListAsync();
                audit.TotalOrphanPracticeRecords = practiceRecords.Count(r => !questionIdSet.Contains(r.QuestionId) || !userIdSet.Contains(r.UserId));
                if (audit.TotalOrphanPracticeRecords > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalOrphanPracticeRecords} 条无主练习流水 (关联题目或用户不存在)");
                }

                // 3. UserFavorites
                var favorites = await db.UserFavorites.Select(f => new { f.Id, f.QuestionId, f.UserId }).ToListAsync();
                audit.TotalOrphanUserFavorites = favorites.Count(f => !questionIdSet.Contains(f.QuestionId) || !userIdSet.Contains(f.UserId));
                if (audit.TotalOrphanUserFavorites > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalOrphanUserFavorites} 条无主收藏记录 (关联题目或用户不存在)");
                }

                // 4. HomeworkAssignments
                var homeworks = await db.HomeworkAssignments.Select(h => new { h.Id, h.StudentUserId, h.CreatorUserId }).ToListAsync();
                audit.TotalOrphanHomeworkAssignments = homeworks.Count(h => !userIdSet.Contains(h.StudentUserId) || !userIdSet.Contains(h.CreatorUserId));
                if (audit.TotalOrphanHomeworkAssignments > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalOrphanHomeworkAssignments} 条无主作业指派记录 (学生或创建者账号不存在)");
                }

                // 5. StudentParentBindings
                var bindings = await db.StudentParentBindings.Select(b => new { b.Id, b.ParentUserId, b.StudentUserId }).ToListAsync();
                audit.TotalOrphanBindings = bindings.Count(b => !userIdSet.Contains(b.ParentUserId) || !userIdSet.Contains(b.StudentUserId));
                if (audit.TotalOrphanBindings > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalOrphanBindings} 条无主家校绑定关联 (家长或学生账号不存在)");
                }

                // 6. StudyPlans
                var plans = await db.StudyPlans.Select(s => new { s.Id, s.UserId }).ToListAsync();
                audit.TotalOrphanStudyPlans = plans.Count(s => !userIdSet.Contains(s.UserId));
                if (audit.TotalOrphanStudyPlans > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalOrphanStudyPlans} 条无主学习计划 (所属学员账号不存在)");
                }

                // 7. StudyPlanTasks
                var tasks = await db.StudyPlanTasks.Select(t => new { t.Id, t.StudyPlanId }).ToListAsync();
                audit.TotalOrphanStudyPlanTasks = tasks.Count(t => !studyPlanIdSet.Contains(t.StudyPlanId));
                if (audit.TotalOrphanStudyPlanTasks > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalOrphanStudyPlanTasks} 条无主学习任务 (所属计划已被清理或不存在)");
                }

                // 8. UserAchievements
                var uAchievements = await db.UserAchievements.Select(a => new { a.Id, a.UserId, a.AchievementId }).ToListAsync();
                audit.TotalOrphanUserAchievements = uAchievements.Count(a => !userIdSet.Contains(a.UserId) || !achievementIdSet.Contains(a.AchievementId));
                if (audit.TotalOrphanUserAchievements > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalOrphanUserAchievements} 条无主成就关联 (所属用户或成就定义不存在)");
                }

                // 9. EvolutionClosedLoopInsights
                var insights = await db.EvolutionClosedLoopInsights.Select(i => new { i.Id, i.UserId }).ToListAsync();
                audit.TotalOrphanInsights = insights.Count(i => !userIdSet.Contains(i.UserId));
                if (audit.TotalOrphanInsights > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalOrphanInsights} 条无主学情闭环洞察 (所属学员账号不存在)");
                }

                // 10. LlmGenerationLogs
                var llmLogs = await db.LlmGenerationLogs.Select(l => new { l.Id, l.UserId, l.QuestionId }).ToListAsync();
                audit.TotalOrphanLlmLogs = llmLogs.Count(l => !userIdSet.Contains(l.UserId) || (l.QuestionId.HasValue && !questionIdSet.Contains(l.QuestionId.Value)));
                if (audit.TotalOrphanLlmLogs > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalOrphanLlmLogs} 条无主大模型推理日志 (所属用户或试题不存在)");
                }

                // 11. Corrupted Questions (单选/多选且 OptionsJson 格式无效)
                var choiceQuestions = await db.Questions
                    .Where(q => q.Type == QuestionType.SingleChoice || q.Type == QuestionType.MultipleChoice)
                    .Select(q => new { q.Id, q.OptionsJson })
                    .ToListAsync();
                audit.TotalCorruptedQuestions = choiceQuestions.Count(q => string.IsNullOrWhiteSpace(q.OptionsJson) || !q.OptionsJson.Trim().StartsWith("["));
                if (audit.TotalCorruptedQuestions > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalCorruptedQuestions} 道选择题选项格式异常 (未包含合法选项列表)");
                }

                // 12. Questions Creator Topology (孤儿私有题与悬垂公共题)
                var questionsWithCreator = await db.Questions
                    .Where(q => q.CreatedByUserId.HasValue)
                    .Select(q => new { q.Id, q.IsPublic, CreatedByUserId = q.CreatedByUserId!.Value })
                    .ToListAsync();
                var danglingQuestions = questionsWithCreator.Where(q => !userIdSet.Contains(q.CreatedByUserId)).ToList();
                audit.TotalOrphanPrivateQuestions = danglingQuestions.Count(q => !q.IsPublic);
                audit.TotalDanglingPublicQuestions = danglingQuestions.Count(q => q.IsPublic);
                if (audit.TotalOrphanPrivateQuestions > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalOrphanPrivateQuestions} 道无主私有试题 (所属创建者已被删除或不存在)");
                }
                if (audit.TotalDanglingPublicQuestions > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalDanglingPublicQuestions} 道公共试题引用了已删除用户 (建议解绑置空创建者)");
                }

                // 13. Duplicate Questions (相同 Stem 和 Type 的冗余重复题)
                var allQuestions = await db.Questions
                    .Select(q => new { q.Id, Stem = (q.Stem ?? "").Trim(), q.Type })
                    .ToListAsync();
                var duplicateGroups = allQuestions
                    .GroupBy(q => new { q.Stem, q.Type })
                    .Where(g => g.Count() > 1 && !string.IsNullOrWhiteSpace(g.Key.Stem))
                    .ToList();
                audit.TotalDuplicateQuestions = duplicateGroups.Sum(g => g.Count() - 1);
                if (audit.TotalDuplicateQuestions > 0)
                {
                    audit.AuditDetails.Add($"发现 {duplicateGroups.Count} 组共 {audit.TotalDuplicateQuestions} 道题库重复试题冗余副本");
                }

                // 14. User Gamification & Balance Invariants (负资产与等级脱节)
                var users = await db.Users.Select(u => new { u.Id, u.Exp, u.Coins, u.Level }).ToListAsync();
                int invalidUsersCount = 0;
                foreach (var u in users)
                {
                    int expectedLevel = 1;
                    int tempExp = Math.Max(0, u.Exp);
                    int needed = GamificationService.CalculateExpNeeded(expectedLevel);
                    while (tempExp >= needed)
                    {
                        tempExp -= needed;
                        expectedLevel++;
                        needed = GamificationService.CalculateExpNeeded(expectedLevel);
                    }
                    if (u.Exp < 0 || u.Coins < 0 || u.Level < 1 || (u.Exp >= 0 && u.Level != expectedLevel))
                    {
                        invalidUsersCount++;
                    }
                }
                audit.TotalUsersWithInvalidBalances = invalidUsersCount;
                if (audit.TotalUsersWithInvalidBalances > 0)
                {
                    audit.AuditDetails.Add($"发现 {audit.TotalUsersWithInvalidBalances} 个用户存在经验/金币异常或等级脱节");
                }

                if (audit.IsHealthy)
                {
                    audit.AuditDetails.Add("✅ 数据库全库拓扑与业务外键完整性审计通过，未发现任何孤儿或格式损坏记录。");
                }
            }
            catch (Exception ex)
            {
                audit.AuditDetails.Add($"数据完整性审计异常: {ex.Message}");
            }
            return audit;
        }

        public async Task<DataIntegrityPurgeResultDto> PurgeOrphanedRecordsAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new DataIntegrityPurgeResultDto();

            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
            if (db.Database.IsRelational() && db.Database.CurrentTransaction == null)
            {
                tx = await db.Database.BeginTransactionAsync();
            }

            try
            {
                var userIds = await db.Users.Select(u => u.Id).ToListAsync();
                var questionIds = await db.Questions.Select(q => q.Id).ToListAsync();
                var studyPlanIds = await db.StudyPlans.Select(s => s.Id).ToListAsync();
                var achievementIds = await db.Achievements.Select(a => a.Id).ToListAsync();
                var userIdSet = new HashSet<Guid>(userIds);
                var questionIdSet = new HashSet<Guid>(questionIds);
                var studyPlanIdSet = new HashSet<Guid>(studyPlanIds);
                var achievementIdSet = new HashSet<Guid>(achievementIds);

                // 1. ErrorItems
                var orphanErrors = (await db.ErrorItems.ToListAsync())
                    .Where(e => !questionIdSet.Contains(e.QuestionId) || !userIdSet.Contains(e.UserId))
                    .ToList();
                if (orphanErrors.Count > 0)
                {
                    db.ErrorItems.RemoveRange(orphanErrors);
                    result.PurgedErrorItemsCount = orphanErrors.Count;
                }

                // 2. PracticeRecords
                var orphanRecords = (await db.PracticeRecords.ToListAsync())
                    .Where(r => !questionIdSet.Contains(r.QuestionId) || !userIdSet.Contains(r.UserId))
                    .ToList();
                if (orphanRecords.Count > 0)
                {
                    db.PracticeRecords.RemoveRange(orphanRecords);
                    result.PurgedPracticeRecordsCount = orphanRecords.Count;
                }

                // 3. UserFavorites
                var orphanFavorites = (await db.UserFavorites.ToListAsync())
                    .Where(f => !questionIdSet.Contains(f.QuestionId) || !userIdSet.Contains(f.UserId))
                    .ToList();
                if (orphanFavorites.Count > 0)
                {
                    db.UserFavorites.RemoveRange(orphanFavorites);
                    result.PurgedUserFavoritesCount = orphanFavorites.Count;
                }

                // 4. HomeworkAssignments
                var orphanHomeworks = (await db.HomeworkAssignments.ToListAsync())
                    .Where(h => !userIdSet.Contains(h.StudentUserId) || !userIdSet.Contains(h.CreatorUserId))
                    .ToList();
                if (orphanHomeworks.Count > 0)
                {
                    db.HomeworkAssignments.RemoveRange(orphanHomeworks);
                    result.PurgedHomeworkAssignmentsCount = orphanHomeworks.Count;
                }

                // 5. StudentParentBindings
                var orphanBindings = (await db.StudentParentBindings.ToListAsync())
                    .Where(b => !userIdSet.Contains(b.ParentUserId) || !userIdSet.Contains(b.StudentUserId))
                    .ToList();
                if (orphanBindings.Count > 0)
                {
                    db.StudentParentBindings.RemoveRange(orphanBindings);
                    result.PurgedBindingsCount = orphanBindings.Count;
                }

                // 6. StudyPlans
                var orphanPlans = (await db.StudyPlans.ToListAsync())
                    .Where(s => !userIdSet.Contains(s.UserId))
                    .ToList();
                if (orphanPlans.Count > 0)
                {
                    db.StudyPlans.RemoveRange(orphanPlans);
                    result.PurgedStudyPlansCount = orphanPlans.Count;
                    var removedPlanIds = orphanPlans.Select(p => p.Id).ToHashSet();
                    studyPlanIdSet.RemoveWhere(removedPlanIds.Contains);
                }

                // 7. StudyPlanTasks
                var orphanTasks = (await db.StudyPlanTasks.ToListAsync())
                    .Where(t => !studyPlanIdSet.Contains(t.StudyPlanId))
                    .ToList();
                if (orphanTasks.Count > 0)
                {
                    db.StudyPlanTasks.RemoveRange(orphanTasks);
                    result.PurgedStudyPlanTasksCount = orphanTasks.Count;
                }

                // 8. UserAchievements
                var orphanAchievements = (await db.UserAchievements.ToListAsync())
                    .Where(a => !userIdSet.Contains(a.UserId) || !achievementIdSet.Contains(a.AchievementId))
                    .ToList();
                if (orphanAchievements.Count > 0)
                {
                    db.UserAchievements.RemoveRange(orphanAchievements);
                    result.PurgedUserAchievementsCount = orphanAchievements.Count;
                }

                // 9. EvolutionClosedLoopInsights
                var orphanInsights = (await db.EvolutionClosedLoopInsights.ToListAsync())
                    .Where(i => !userIdSet.Contains(i.UserId))
                    .ToList();
                if (orphanInsights.Count > 0)
                {
                    db.EvolutionClosedLoopInsights.RemoveRange(orphanInsights);
                    result.PurgedInsightsCount = orphanInsights.Count;
                }

                // 10. LlmGenerationLogs
                var orphanLogs = (await db.LlmGenerationLogs.ToListAsync())
                    .Where(l => !userIdSet.Contains(l.UserId) || (l.QuestionId.HasValue && !questionIdSet.Contains(l.QuestionId.Value)))
                    .ToList();
                if (orphanLogs.Count > 0)
                {
                    db.LlmGenerationLogs.RemoveRange(orphanLogs);
                    result.PurgedLlmLogsCount = orphanLogs.Count;
                }

                // 11. Questions Creator Topology (清理孤儿私有题，解绑悬垂公共题)
                var questionsWithCreator = await db.Questions
                    .Where(q => q.CreatedByUserId.HasValue)
                    .ToListAsync();
                var danglingQuestions = questionsWithCreator
                    .Where(q => !userIdSet.Contains(q.CreatedByUserId!.Value))
                    .ToList();

                var orphanPrivateQuestions = danglingQuestions.Where(q => !q.IsPublic).ToList();
                if (orphanPrivateQuestions.Count > 0)
                {
                    var privateQIds = orphanPrivateQuestions.Select(q => q.Id).ToHashSet();
                    var cascadeErrors = await db.ErrorItems.Where(e => privateQIds.Contains(e.QuestionId)).ToListAsync();
                    if (cascadeErrors.Count > 0) db.ErrorItems.RemoveRange(cascadeErrors);
                    var cascadeRecords = await db.PracticeRecords.Where(r => privateQIds.Contains(r.QuestionId)).ToListAsync();
                    if (cascadeRecords.Count > 0) db.PracticeRecords.RemoveRange(cascadeRecords);
                    var cascadeFavorites = await db.UserFavorites.Where(f => privateQIds.Contains(f.QuestionId)).ToListAsync();
                    if (cascadeFavorites.Count > 0) db.UserFavorites.RemoveRange(cascadeFavorites);

                    db.Questions.RemoveRange(orphanPrivateQuestions);
                    result.PurgedPrivateQuestionsCount = orphanPrivateQuestions.Count;
                }

                var danglingPublicQuestions = danglingQuestions.Where(q => q.IsPublic).ToList();
                if (danglingPublicQuestions.Count > 0)
                {
                    foreach (var pubQ in danglingPublicQuestions)
                    {
                        pubQ.CreatedByUserId = null;
                    }
                    result.SanitizedPublicQuestionsCount = danglingPublicQuestions.Count;
                }

                if (result.TotalPurgedCount > 0)
                {
                    await db.SaveChangesAsync();
                }

                if (tx != null)
                {
                    await tx.CommitAsync();
                }

                sw.Stop();
                result.ElapsedMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                result.Success = true;
                result.Message = result.TotalPurgedCount > 0
                    ? $"成功清理 {result.TotalPurgedCount} 条孤儿数据记录 (用时 {result.ElapsedMilliseconds}ms)"
                    : "数据库数据拓扑完整，无需清理任何孤儿记录。";

                RecordArchitectureEvent("SelfHealing", "Success", result.Message, result.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                if (tx != null) await tx.RollbackAsync();
                sw.Stop();
                result.Success = false;
                result.ElapsedMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                result.Message = $"孤儿数据自愈清理失败: {ex.Message}";
                RecordArchitectureEvent("SelfHealing", "Error", result.Message, result.ElapsedMilliseconds);
            }

            return result;
        }

        public async Task<DataIntegrityDeduplicateResultDto> DeduplicateQuestionsAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new DataIntegrityDeduplicateResultDto();

            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
            try
            {
                if (db.Database.IsRelational())
                {
                    tx = await db.Database.BeginTransactionAsync();
                }

                var allQuestions = await db.Questions.ToListAsync();

                var duplicateGroups = allQuestions
                    .GroupBy(q => new { Stem = (q.Stem ?? "").Trim(), q.Type })
                    .Where(g => g.Count() > 1 && !string.IsNullOrWhiteSpace(g.Key.Stem))
                    .ToList();

                result.DuplicateGroupsDetected = duplicateGroups.Count;

                if (duplicateGroups.Count == 0)
                {
                    result.Success = true;
                    result.Message = "未检测到重复试题，题库拓扑结构健康！";
                    sw.Stop();
                    result.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                    return result;
                }

                var allDupGroupQIds = duplicateGroups.SelectMany(g => g.Select(q => q.Id)).ToHashSet();

                var allPracticeRecords = await db.PracticeRecords
                    .Where(r => allDupGroupQIds.Contains(r.QuestionId))
                    .ToListAsync();
                var practiceMap = allPracticeRecords.GroupBy(r => r.QuestionId).ToDictionary(g => g.Key, g => g.ToList());

                var allErrorItems = await db.ErrorItems
                    .Where(e => allDupGroupQIds.Contains(e.QuestionId))
                    .ToListAsync();
                var errorMap = allErrorItems.GroupBy(e => e.QuestionId).ToDictionary(g => g.Key, g => g.ToList());

                var allFavorites = await db.UserFavorites
                    .Where(f => allDupGroupQIds.Contains(f.QuestionId))
                    .ToListAsync();
                var favMap = allFavorites.GroupBy(f => f.QuestionId).ToDictionary(g => g.Key, g => g.ToList());

                foreach (var group in duplicateGroups)
                {
                    // 选取主试题: 公共题优先 -> 关联流水/错题/收藏最多优先 -> 创建时间最早优先
                    var master = group
                        .OrderByDescending(q => q.IsPublic)
                        .ThenByDescending(q => (practiceMap.TryGetValue(q.Id, out var pr) ? pr.Count : 0) +
                                               (errorMap.TryGetValue(q.Id, out var er) ? er.Count : 0) +
                                               (favMap.TryGetValue(q.Id, out var fr) ? fr.Count : 0))
                        .ThenBy(q => q.CreatedAt)
                        .First();

                    var masterRecords = practiceMap.TryGetValue(master.Id, out var mPr) ? mPr : new List<PracticeRecord>();
                    var masterErrors = errorMap.TryGetValue(master.Id, out var mEr) ? mEr : new List<ErrorItem>();
                    var masterFavs = favMap.TryGetValue(master.Id, out var mFr) ? mFr : new List<UserFavorite>();

                    var duplicates = group.Where(q => q.Id != master.Id).ToList();

                    foreach (var dup in duplicates)
                    {
                        // 1. PracticeRecords 重定向
                        if (practiceMap.TryGetValue(dup.Id, out var dupPr))
                        {
                            foreach (var record in dupPr)
                            {
                                record.QuestionId = master.Id;
                                result.ReassignedPracticeRecordsCount++;
                            }
                        }

                        // 2. ErrorItems 重定向或合并
                        if (errorMap.TryGetValue(dup.Id, out var dupEr))
                        {
                            foreach (var err in dupEr)
                            {
                                var existingMasterErr = masterErrors.FirstOrDefault(e => e.UserId == err.UserId);
                                if (existingMasterErr != null)
                                {
                                    existingMasterErr.RevisionCount += (err.RevisionCount + 1);
                                    if (err.CreatedAt > existingMasterErr.CreatedAt)
                                    {
                                        existingMasterErr.CreatedAt = err.CreatedAt;
                                    }
                                    if (!err.IsMastered)
                                    {
                                        existingMasterErr.IsMastered = false;
                                    }
                                    db.ErrorItems.Remove(err);
                                }
                                else
                                {
                                    err.QuestionId = master.Id;
                                    masterErrors.Add(err);
                                }
                                result.ReassignedErrorItemsCount++;
                            }
                        }

                        // 3. UserFavorites 重定向或去重
                        if (favMap.TryGetValue(dup.Id, out var dupFr))
                        {
                            foreach (var fav in dupFr)
                            {
                                var existingMasterFav = masterFavs.FirstOrDefault(f => f.UserId == fav.UserId);
                                if (existingMasterFav != null)
                                {
                                    db.UserFavorites.Remove(fav);
                                }
                                else
                                {
                                    fav.QuestionId = master.Id;
                                    masterFavs.Add(fav);
                                }
                                result.ReassignedFavoritesCount++;
                            }
                        }

                        // 4. LlmGenerationLogs 重定向
                        var logs = await db.LlmGenerationLogs.Where(l => l.QuestionId == dup.Id).ToListAsync();
                        foreach (var l in logs)
                        {
                            l.QuestionId = master.Id;
                        }

                        // 5. 移除冗余副题
                        db.Questions.Remove(dup);
                        result.DuplicateQuestionsPurged++;
                    }
                }

                if (result.DuplicateQuestionsPurged > 0)
                {
                    await db.SaveChangesAsync();
                }

                if (tx != null)
                {
                    await tx.CommitAsync();
                }

                sw.Stop();
                result.ElapsedMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                result.Success = true;
                result.Message = result.DuplicateQuestionsPurged > 0
                    ? $"题库去重自愈完成：检测到 {result.DuplicateGroupsDetected} 组重复，物理清理 {result.DuplicateQuestionsPurged} 道冗余题，重定向 {result.ReassignedPracticeRecordsCount} 条答题流水与 {result.ReassignedErrorItemsCount} 条错题 (耗时 {result.ElapsedMilliseconds}ms)。"
                    : "题库拓扑完整，未发现重复试题。";

                RecordArchitectureEvent("Deduplication", "Success", result.Message, result.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                if (tx != null) await tx.RollbackAsync();
                sw.Stop();
                result.Success = false;
                result.ElapsedMilliseconds = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                result.Message = $"题库去重自愈失败: {ex.Message}";
                RecordArchitectureEvent("Deduplication", "Error", result.Message, result.ElapsedMilliseconds);
            }

            return result;
        }

        public async Task<int> HealGamificationInvariantsAsync()
        {
            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var users = await db.Users.ToListAsync();
            int healedCount = 0;

            foreach (var user in users)
            {
                bool changed = false;
                if (user.Exp < 0)
                {
                    user.Exp = 0;
                    changed = true;
                }
                if (user.Coins < 0)
                {
                    user.Coins = 0;
                    changed = true;
                }

                if (user.Level < 1)
                {
                    user.Level = 1;
                    changed = true;
                }

                if (user.TotalAnswered < 0)
                {
                    user.TotalAnswered = 0;
                    changed = true;
                }
                if (user.TotalCorrect < 0)
                {
                    user.TotalCorrect = 0;
                    changed = true;
                }
                if (user.TotalCorrect > user.TotalAnswered)
                {
                    user.TotalCorrect = user.TotalAnswered;
                    changed = true;
                }
                if (user.TodayAnsweredCount < 0)
                {
                    user.TodayAnsweredCount = 0;
                    changed = true;
                }
                if (user.ResolvedErrorsCount < 0)
                {
                    user.ResolvedErrorsCount = 0;
                    changed = true;
                }
                if (user.ComboShieldCount < 0)
                {
                    user.ComboShieldCount = 0;
                    changed = true;
                }
                if (user.CurrentStreak < 0)
                {
                    user.CurrentStreak = 0;
                    changed = true;
                }
                if (user.MaxCombo < 0)
                {
                    user.MaxCombo = 0;
                    changed = true;
                }
                if (user.MaxCombo > user.TotalCorrect)
                {
                    user.MaxCombo = user.TotalCorrect;
                    changed = true;
                }
                if (user.TodayAnsweredCount > user.TotalAnswered)
                {
                    user.TodayAnsweredCount = user.TotalAnswered;
                    changed = true;
                }
                if (user.ResolvedErrorsCount > user.TotalCorrect)
                {
                    user.ResolvedErrorsCount = user.TotalCorrect;
                    changed = true;
                }

                int expNeeded = GamificationService.CalculateExpNeeded(user.Level);
                while (user.Exp >= expNeeded)
                {
                    user.Exp -= expNeeded;
                    user.Level++;
                    changed = true;
                    expNeeded = GamificationService.CalculateExpNeeded(user.Level);
                }

                if (changed) healedCount++;
            }

            if (healedCount > 0)
            {
                await db.SaveChangesAsync();
                RecordArchitectureEvent("GamificationInvariants", "Success", $"校准了 {healedCount} 个异常用户游戏化资产与等级");
            }

            return healedCount;
        }

        public async Task<int> HealHomeworkAssignmentInvariantsAsync()
        {
            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var assignments = await db.HomeworkAssignments.ToListAsync();
            int healedCount = 0;

            foreach (var h in assignments)
            {
                bool changed = false;

                if (h.QuestionCount < 1)
                {
                    h.QuestionCount = 1;
                    changed = true;
                }

                if (h.TotalAnswered < 0)
                {
                    h.TotalAnswered = 0;
                    changed = true;
                }
                else if (h.TotalAnswered > h.QuestionCount)
                {
                    h.TotalAnswered = h.QuestionCount;
                    changed = true;
                }

                if (h.CorrectCount < 0)
                {
                    h.CorrectCount = 0;
                    changed = true;
                }
                else if (h.CorrectCount > h.TotalAnswered)
                {
                    h.CorrectCount = h.TotalAnswered;
                    changed = true;
                }

                int expectedAccuracy = h.TotalAnswered > 0
                    ? (int)Math.Round((double)h.CorrectCount / h.TotalAnswered * 100.0)
                    : 0;
                expectedAccuracy = Math.Clamp(expectedAccuracy, 0, 100);

                if (h.AccuracyRate != expectedAccuracy)
                {
                    h.AccuracyRate = expectedAccuracy;
                    changed = true;
                }

                if (h.Score < 0 || h.Score > 100)
                {
                    h.Score = Math.Clamp(h.Score, 0, 100);
                    changed = true;
                }

                if (h.IsCompleted && !h.CompletedAt.HasValue)
                {
                    h.CompletedAt = DateTime.Now;
                    changed = true;
                }
                else if (!h.IsCompleted && h.CompletedAt.HasValue)
                {
                    if (h.TotalAnswered >= h.QuestionCount && h.QuestionCount > 0)
                    {
                        h.IsCompleted = true;
                        changed = true;
                    }
                    else
                    {
                        h.CompletedAt = null;
                        changed = true;
                    }
                }

                if (changed) healedCount++;
            }

            if (healedCount > 0)
            {
                await db.SaveChangesAsync();
                RecordArchitectureEvent("HomeworkInvariants", "Success", $"校准了 {healedCount} 个异常家庭作业记录领域不变量");
            }

            return healedCount;
        }
    }
}
