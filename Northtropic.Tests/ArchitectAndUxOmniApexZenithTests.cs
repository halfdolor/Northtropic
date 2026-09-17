using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    /// <summary>
    /// 系统架构师与用户体验专家顶峰综合评测自动化测试套件。
    /// 覆盖：
    /// 1. AsyncDbScope SQLite 并发锁冲突自适应抖动退避重试机制
    /// 2. 架构诊断审计档案 Markdown 格式导出与契约验证
    /// 3. PracticeService 纯标量方程根列表无序等价性与坐标/区间边界保护
    /// 4. 用户安全会话临时下载凭证 (Ticket) 签发、核销与生命周期治理
    /// </summary>
    public class ArchitectAndUxOmniApexZenithTests
    {
        #region 1. 架构师维度：SQLite 瞬态锁冲突检测与自适应退避重试 (AsyncDbScope Resilience)

        private static SqliteException CreateSqliteException(string message, int errorCode)
        {
            var ctor = typeof(SqliteException).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(int), typeof(int) },
                null);

            if (ctor != null)
            {
                return (SqliteException)ctor.Invoke(new object[] { message, errorCode, errorCode });
            }

            var fallbackCtor = typeof(SqliteException).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(int) },
                null);

            if (fallbackCtor != null)
            {
                return (SqliteException)fallbackCtor.Invoke(new object[] { message, errorCode });
            }

            throw new InvalidOperationException("Could not find suitable SqliteException constructor.");
        }

        [Fact]
        public void AsyncDbScope_IsTransientSqliteLockException_ShouldDetectErrorCode5And6()
        {
            var busyEx = CreateSqliteException("database is locked (SQLITE_BUSY)", 5);
            var lockedEx = CreateSqliteException("database table is locked (SQLITE_LOCKED)", 6);
            var syntaxEx = CreateSqliteException("syntax error", 1);

            Assert.Equal(5, busyEx.SqliteErrorCode);
            Assert.Equal(6, lockedEx.SqliteErrorCode);
            Assert.Equal(1, syntaxEx.SqliteErrorCode);

            // 直接异常判定
            Assert.True(AsyncDbScope.IsTransientSqliteLockException(busyEx));
            Assert.True(AsyncDbScope.IsTransientSqliteLockException(lockedEx));
            Assert.False(AsyncDbScope.IsTransientSqliteLockException(syntaxEx));
            Assert.False(AsyncDbScope.IsTransientSqliteLockException(new InvalidOperationException("Generic error")));

            // 包装在 DbUpdateException 中
            var dbBusyEx = new DbUpdateException("EF Core save failure", busyEx);
            var dbLockedEx = new DbUpdateException("EF Core save failure", lockedEx);
            var dbSyntaxEx = new DbUpdateException("EF Core save failure", syntaxEx);

            Assert.True(AsyncDbScope.IsTransientSqliteLockException(dbBusyEx));
            Assert.True(AsyncDbScope.IsTransientSqliteLockException(dbLockedEx));
            Assert.False(AsyncDbScope.IsTransientSqliteLockException(dbSyntaxEx));
        }

        [Fact]
        public async Task AsyncDbScope_ExecuteWithRetryAsync_ShouldRecoverAfterTransientLock()
        {
            var busyEx = CreateSqliteException("database is locked (SQLITE_BUSY)", 5);

            int executionCount = 0;

            var result = await AsyncDbScope.ExecuteWithRetryAsync(async () =>
            {
                executionCount++;
                if (executionCount < 3)
                {
                    throw busyEx;
                }
                await Task.Yield();
                return "Resilience Success";
            }, maxRetries: 4, initialDelayMs: 10);

            Assert.Equal(3, executionCount);
            Assert.Equal("Resilience Success", result);
        }

        [Fact]
        public async Task AsyncDbScope_ExecuteWithRetryAsync_ShouldThrowWhenExceedingMaxRetries()
        {
            var lockedEx = CreateSqliteException("database table is locked (SQLITE_LOCKED)", 6);

            int executionCount = 0;

            await Assert.ThrowsAsync<SqliteException>(async () =>
            {
                await AsyncDbScope.ExecuteWithRetryAsync<int>(async () =>
                {
                    executionCount++;
                    throw lockedEx;
                }, maxRetries: 2, initialDelayMs: 5);
            });

            Assert.Equal(3, executionCount); // 初次尝试 + 2 次重试
        }

        #endregion

        #region 2. 判分引擎与用户体验：纯标量方程根列表无序等价判定 (Scalar Roots Equivalence)

        [Theory]
        [InlineData("1, 2", "2, 1", true)]
        [InlineData("2; 1", "1; 2", true)]
        [InlineData("1、2", "2、1", true)]
        [InlineData("1, -2", "-2, 1", true)]
        [InlineData("-1/2, 3/4", "3/4, -1/2", true)]
        [InlineData("{1, 2}", "2, 1", true)]
        [InlineData("1, 2", "{2, 1}", true)]
        [InlineData("x_1=1, x_2=2", "2, 1", true)]
        [InlineData("1, 2", "x=2或x=1", true)]
        [InlineData("1, 2, 3", "3, 1, 2", true)]
        [InlineData("1, 2", "1, 3", false)]
        [InlineData("1, 2", "2, 3", false)]
        public void PracticeService_CheckFillInBlankMatch_ScalarRootsUnorderedList_ShouldMatchAccurately(
            string userAnswer, string correctAnswer, bool expected)
        {
            bool actual = PracticeService.CheckFillInBlankMatch(userAnswer, correctAnswer);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData("(1, 2)", "(2, 1)", false)] // 空间/平面坐标顺序不可逆
        [InlineData("(1, 2, 3)", "(3, 2, 1)", false)]
        [InlineData("[1, 2]", "[2, 1]", false)] // 区间端点有序
        public void PracticeService_CheckFillInBlankMatch_CoordinatesAndIntervals_ShouldPreserveOrder(
            string userAnswer, string correctAnswer, bool expected)
        {
            bool actual = PracticeService.CheckFillInBlankMatch(userAnswer, correctAnswer);
            Assert.Equal(expected, actual);
        }

        #endregion

        #region 3. 架构可观测性与档案导出：Markdown 架构诊断档案生成与契约验证

        [Fact]
        public async Task SystemHealthService_ExportArchitectureDiagnosticReportMarkdownAsync_ShouldGenerateFullReport()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var healthService = new SystemHealthService(context, null);

                var markdown = await healthService.ExportArchitectureDiagnosticReportMarkdownAsync();

                Assert.NotNull(markdown);
                Assert.Contains("# 🏛️ Northtropic 系统架构全景诊断与灾备健康档案", markdown);
                Assert.Contains("## 一、系统架构综合健康与评分", markdown);
                Assert.Contains("## 二、存储引擎与物理文件指标", markdown);
                Assert.Contains("## 三、托管运行时与内存压力指标", markdown);
                Assert.Contains("## 四、核心业务数据表分布与对齐", markdown);
            }
        }

        #endregion

        #region 4. 用户体验与安全：临时下载凭证 (Download Ticket) 闭环验证

        [Fact]
        public async Task UserSessionService_DiagnosticsExportTicket_ShouldBeSingleUseAndValid()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            using (var httpClient = new System.Net.Http.HttpClient())
            {
                var sessionService = new UserSessionService(context, httpClient, null);

                var adminId = Guid.NewGuid();
                var adminUser = new User
                {
                    Id = adminId,
                    Username = "SysAdmin",
                    Role = UserRole.SuperAdmin
                };
                context.Users.Add(adminUser);
                await context.SaveChangesAsync();

                // 签发针对 diagnostics_export 的下载凭证
                string ticket = await sessionService.GenerateDownloadTicketAsync(adminId, "diagnostics_export");
                Assert.False(string.IsNullOrWhiteSpace(ticket));

                // 首次核销凭据：应当成功
                var (valid1, uId1, purpose1, _) = await sessionService.ValidateAndConsumeDownloadTicketAsync(ticket);
                Assert.True(valid1);
                Assert.Equal(adminId, uId1);
                Assert.Equal("diagnostics_export", purpose1);

                // 二次核销凭据：单次消耗策略，应当失败
                var (valid2, _, _, _) = await sessionService.ValidateAndConsumeDownloadTicketAsync(ticket);
                Assert.False(valid2);
            }
        }

        #endregion
    }
}
