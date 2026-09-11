using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxTranscendenceSymphonyTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;

        public ArchitectAndUxTranscendenceSymphonyTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        #region 1. System Architect Tests: Backup Lifecycle, Security & Storage Metrics

        [Fact]
        public async Task SystemArchitect_UserSession_DownloadTickets_TrackingAndDeterministicPurge()
        {
            // 架构师测试：验证下载凭证活跃计数与按需确定性清理机制，消除内存泄漏
            var sessionService = new UserSessionService(_context, new System.Net.Http.HttpClient());
            Guid testUserId = Guid.NewGuid();

            // 生成一个正常有效的凭证
            string ticketValid = await sessionService.GenerateDownloadTicketAsync(testUserId, "backup", "snapshot_valid.db");
            Assert.False(string.IsNullOrWhiteSpace(ticketValid));

            int activeCount = UserSessionService.ActiveDownloadTicketsCountStatic;
            Assert.True(activeCount >= 1, "ActiveDownloadTicketsCountStatic should reflect pending tickets");

            // 执行清理逻辑验证无异常
            UserSessionService.PurgeExpiredTickets();
            Assert.True(UserSessionService.ActiveDownloadTicketsCountStatic >= 0);
        }

        [Theory]
        [InlineData("../../etc/passwd.db")]
        [InlineData("..\\..\\windows\\system32.db")]
        [InlineData("sub/folder/file.db")]
        [InlineData("sub\\folder\\file.db")]
        [InlineData("snapshot.exe")]
        [InlineData("snapshot.txt")]
        [InlineData("")]
        [InlineData("   ")]
        public async Task SystemArchitect_Backup_DeleteBackupAsync_PreventsPathTraversalAndInvalidExtensions(string maliciousName)
        {
            // 架构师测试：验证路径遍历防御与后缀名强校验，拒绝任何跳出快照目录的文件删除请求
            var healthService = new SystemHealthService(_context);
            bool result = await healthService.DeleteBackupAsync(maliciousName);
            Assert.False(result, $"Malicious input '{maliciousName}' must be rejected.");
        }

        [Fact]
        public async Task SystemArchitect_Backup_DeleteBackupAsync_DeletesTargetSafely()
        {
            // 架构师测试：合法快照文件的安全删除
            var healthService = new SystemHealthService(_context);
            string backupDir = Path.Combine(AppContext.BaseDirectory, "backups");
            Directory.CreateDirectory(backupDir);

            string fileName = $"test_delete_snapshot_{Guid.NewGuid():N}.db";
            string filePath = Path.Combine(backupDir, fileName);
            await File.WriteAllTextAsync(filePath, "test content");

            Assert.True(File.Exists(filePath));
            bool deleted = await healthService.DeleteBackupAsync(fileName);
            Assert.True(deleted);
            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public async Task SystemArchitect_Backup_CalculateBackupSha256Async_MatchesStandardSha256()
        {
            // 架构师测试：验证快照文件 SHA-256 物理完整性哈希计算的准确性
            var healthService = new SystemHealthService(_context);
            string backupDir = Path.Combine(AppContext.BaseDirectory, "backups");
            Directory.CreateDirectory(backupDir);

            string fileName = $"test_sha256_{Guid.NewGuid():N}.db";
            string filePath = Path.Combine(backupDir, fileName);
            byte[] rawBytes = Encoding.UTF8.GetBytes("Northtropic SQLite Backup Checksum Integrity Block 2026");
            await File.WriteAllBytesAsync(filePath, rawBytes);

            try
            {
                string expectedHash = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
                string? actualHash = await healthService.CalculateBackupSha256Async(fileName);

                Assert.NotNull(actualHash);
                Assert.Equal(expectedHash, actualHash);
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
            }
        }

        [Fact]
        public async Task SystemArchitect_Backup_CalculateBackupSha256Async_ReturnsNullOnMissingFile()
        {
            var healthService = new SystemHealthService(_context);
            var result = await healthService.CalculateBackupSha256Async($"non_existent_{Guid.NewGuid():N}.db");
            Assert.Null(result);
        }

        [Fact]
        public async Task SystemArchitect_Backup_PruneBackupsAsync_EnforcesRetentionAndReclaimsSpace()
        {
            // 架构师测试：自动化快照生命周期管理（保留最新N份 + 清理超期快照）
            var healthService = new SystemHealthService(_context);
            string backupDir = Path.Combine(AppContext.BaseDirectory, "backups");
            Directory.CreateDirectory(backupDir);

            // 创建 5 个测试快照，模拟不同的创建时间
            var createdFiles = new List<string>();
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    string fName = $"prune_test_{i:D2}_{Guid.NewGuid():N}.db";
                    string fPath = Path.Combine(backupDir, fName);
                    await File.WriteAllBytesAsync(fPath, new byte[1024 * (i + 1)]); // 不同的字节大小
                    // 模拟文件时间间隔
                    File.SetCreationTimeUtc(fPath, DateTime.UtcNow.AddHours(-10 + i * 2));
                    createdFiles.Add(fPath);
                }

                // 执行策略：仅保留最新的 2 份，清理其余 3 份
                var (prunedCount, reclaimedBytes) = await healthService.PruneBackupsAsync(keepCount: 2, maxAgeDays: 365);

                Assert.True(prunedCount >= 3, "Should prune at least 3 older snapshots");
                Assert.True(reclaimedBytes > 0, "ReclaimedBytes should be positive");
            }
            finally
            {
                foreach (var f in createdFiles)
                {
                    if (File.Exists(f))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
        }

        [Fact]
        public async Task SystemArchitect_TableStorageMetrics_QueriesCoreTables()
        {
            // 架构师测试：查询系统核心分表存储行级指标
            var healthService = new SystemHealthService(_context);
            var metrics = await healthService.GetTableStorageMetricsAsync();

            Assert.NotNull(metrics);
            Assert.True(metrics.Count >= 8, "Must track at least 8 core business and log tables");

            var tableNames = metrics.Select(m => m.TableName).ToList();
            Assert.Contains("Questions", tableNames);
            Assert.Contains("Users", tableNames);
            Assert.Contains("PracticeRecords", tableNames);
            Assert.Contains("ErrorItems", tableNames);
            Assert.Contains("HomeworkAssignments", tableNames);
            Assert.Contains("UserFavorites", tableNames);
            Assert.Contains("CurriculumSubjectConfigs", tableNames);
            Assert.Contains("LlmGenerationLogs", tableNames);

            foreach (var m in metrics)
            {
                Assert.False(string.IsNullOrWhiteSpace(m.DisplayName));
                Assert.False(string.IsNullOrWhiteSpace(m.Description));
                Assert.True(m.RowCount >= 0);
            }
        }

        [Fact]
        public async Task SystemArchitect_SystemHealth_IncludesPendingDownloadTicketsCount()
        {
            var healthService = new SystemHealthService(_context);
            var health = await healthService.GetSystemHealthAsync();

            Assert.NotNull(health);
            Assert.True(health.PendingDownloadTicketsCount >= 0);
        }

        #endregion

        #region 2. User Experience (UX) Tests: Math Equivalence & Match Reasons

        [Theory]
        [InlineData("x^2/9 + y^2/4 = 1", "y^2/4 + x^2/9 = 1")]
        [InlineData("x^2/16 + y^2/25 = 1", "y^2/25 + x^2/16 = 1")]
        [InlineData("y^2/4 + x^2/9 = 1", "x^2/9 + y^2/4 = 1")]
        public void UxExpert_Ellipse_StandardForm_CommutativeTerms(string userAns, string correctAns)
        {
            // 用户体验专家测试：解析几何椭圆标准方程各项加法交换律无序等价
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched, $"User answer '{userAns}' should match correct answer '{correctAns}'");
        }

        [Theory]
        [InlineData("\\frac{x^2}{9} + \\frac{y^2}{4} = 1", "\\frac{y^2}{4} + \\frac{x^2}{9} = 1")]
        [InlineData("\\frac{x^2}{25} + \\frac{y^2}{16} = 1", "x^2/25 + y^2/16 = 1")]
        [InlineData("x^2/9 + y^2/4 = 1", "\\frac{x^2}{9} + \\frac{y^2}{4} = 1")]
        public void UxExpert_Ellipse_LaTeX_Fractions(string userAns, string correctAns)
        {
            // 用户体验专家测试：LaTeX 分数格式与简单分式书写的椭圆方程等价
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched, $"LaTeX ellipse '{userAns}' should match '{correctAns}'");
        }

        [Theory]
        [InlineData("4x^2 + 9y^2 = 36", "x^2/9 + y^2/4 = 1")]
        [InlineData("x^2/9 + y^2/4 = 1", "4x^2 + 9y^2 = 36")]
        [InlineData("9x^2 + 16y^2 = 144", "x^2/16 + y^2/9 = 1")]
        public void UxExpert_Ellipse_GeneralMultipliedForm(string userAns, string correctAns)
        {
            // 用户体验专家测试：去分母后的代数展开方程与标准方程等价
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched, $"Ellipse general form '{userAns}' should match standard form '{correctAns}'");
        }

        [Theory]
        [InlineData("(x-1)^2/9 + (y+2)^2/4 = 1", "(y+2)^2/4 + (x-1)^2/9 = 1")]
        [InlineData("(x+3)^2/16 + (y-5)^2/25 = 1", "(y-5)^2/25 + (x+3)^2/16 = 1")]
        public void UxExpert_Ellipse_ShiftedCenter(string userAns, string correctAns)
        {
            // 用户体验专家测试：中心不在原点的椭圆方程加法项对调等价
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched, $"Shifted ellipse '{userAns}' should match '{correctAns}'");
        }

        [Theory]
        [InlineData("(-inf, -1) u (1, +inf)", "(1, +inf) u (-inf, -1)")]
        [InlineData("(-∞, -1) ∪ (1, +∞)", "(1, +∞) ∪ (-∞, -1)")]
        [InlineData("(-inf, 0] u [2, +inf)", "[2, +inf) u (-inf, 0]")]
        [InlineData("[-5, -1] u [2, 6]", "[2, 6] u [-5, -1]")]
        public void UxExpert_MultiIntervalUnion_CommutativeTerms(string userAns, string correctAns)
        {
            // 用户体验专家测试：区间并集交换律无序等价
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched, $"Multi-interval union '{userAns}' should match '{correctAns}'");
        }

        [Theory]
        [InlineData("(-\\infty, -1) \\cup (1, +\\infty)", "(1, +\\infty) \\cup (-\\infty, -1)")]
        [InlineData("(-inf, -1) \\cup [2, 5)", "[2, 5) \\cup (-inf, -1)")]
        public void UxExpert_MultiIntervalUnion_LaTeX_cup(string userAns, string correctAns)
        {
            // 用户体验专家测试：LaTeX \\cup 记号的多区间无序等价
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched, $"LaTeX cup interval '{userAns}' should match '{correctAns}'");
        }

        [Fact]
        public void UxExpert_MultiIntervalUnion_ThreeIntervals()
        {
            // 用户体验专家测试：三段区间并集排列组合无序等价
            string userAns = "(-5, -2) u [0, 2] u (5, 8)";
            string correctAns = "[0, 2] u (5, 8) u (-5, -2)";
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched);
        }

        [Theory]
        [InlineData("2\\vec{i} + 3\\vec{j}", "(2, 3)")]
        [InlineData("2i + 3j", "(2, 3)")]
        [InlineData("(2, 3)", "2i + 3j")]
        [InlineData("(2, 3)", "2\\vec{i} + 3\\vec{j}")]
        [InlineData("i + j", "(1, 1)")]
        [InlineData("-i + 4j", "(-1, 4)")]
        [InlineData("3i - 5j", "(3, -5)")]
        public void UxExpert_VectorBasis_2DDecomposition(string userAns, string correctAns)
        {
            // 用户体验专家测试：向量正交基底分解表达式与坐标表示 (x,y) 等价
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched, $"Vector basis '{userAns}' should match coordinate '{correctAns}'");
        }

        [Theory]
        [InlineData("1/2i - 3/2j", "(0.5, -1.5)")]
        [InlineData("(0.5, -1.5)", "1/2i - 3/2j")]
        public void UxExpert_VectorBasis_FractionalCoefficients(string userAns, string correctAns)
        {
            // 用户体验专家测试：含分数的向量基底分解与浮点坐标等价
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched);
        }

        [Theory]
        [InlineData("\\vec{a} = 2\\vec{i} + 3\\vec{j}", "\\vec{a} = (2, 3)")]
        [InlineData("a = 2i + 3j", "a = (2, 3)")]
        public void UxExpert_VectorBasis_EquationPrefixMatching(string userAns, string correctAns)
        {
            // 用户体验专家测试：带向量符号前缀的基底分解与坐标表示等价
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched);
        }

        [Theory]
        [InlineData("3e8", "3*10^8")]
        [InlineData("3e8", "3×10^8")]
        [InlineData("3e8", "3.0×10^8")]
        [InlineData("3.0*10^8", "3e8")]
        public void UxExpert_ScientificNotation_EngineeringE_ToTimesTen(string userAns, string correctAns)
        {
            // 用户体验专家测试：工程科学记数法 e8 与标准 10^8 等价
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched, $"Scientific '{userAns}' should match '{correctAns}'");
        }

        [Theory]
        [InlineData("1.6e-19", "1.6*10^-19")]
        [InlineData("1.6e-19", "1.6×10^-19")]
        [InlineData("1.6*10^-19", "1.6e-19")]
        public void UxExpert_ScientificNotation_NegativeExponent(string userAns, string correctAns)
        {
            // 用户体验专家测试：带负指数的科学记数法工程简写等价
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched, $"Negative exp '{userAns}' should match '{correctAns}'");
        }

        [Theory]
        [InlineData("6.02E23", "6.02*10^23")]
        [InlineData("6.02E23", "6.02×10^23")]
        public void UxExpert_ScientificNotation_CapitalE(string userAns, string correctAns)
        {
            // 用户体验专家测试：大写 E 的科学记数法工程简写等价
            bool matched = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(matched);
        }

        [Fact]
        public void UxExpert_Ellipse_MatchReason_GeneratesDescriptiveExplanation()
        {
            // 用户体验专家测试：椭圆方程等价时给予鼓励性专向反馈
            string userAns = "y^2/4 + x^2/9 = 1";
            string correctAns = "x^2/9 + y^2/4 = 1";

            string? reason = PracticeService.GenerateEquivalentMatchReason(new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns }, userAns);
            Assert.NotNull(reason);
            Assert.Contains("椭圆", reason);
            Assert.Contains("加法交换律", reason);
        }

        [Fact]
        public void UxExpert_MultiIntervalUnion_MatchReason_GeneratesDescriptiveExplanation()
        {
            // 用户体验专家测试：区间并集等价时给予清晰的集合无序性反馈
            string userAns = "(1, +inf) u (-inf, -1)";
            string correctAns = "(-inf, -1) u (1, +inf)";

            string? reason = PracticeService.GenerateEquivalentMatchReason(new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns }, userAns);
            Assert.NotNull(reason);
            Assert.Contains("并集", reason);
        }

        [Fact]
        public void UxExpert_VectorBasis_MatchReason_GeneratesDescriptiveExplanation()
        {
            // 用户体验专家测试：向量基底分解匹配时给予正交分解反馈
            string userAns = "2i + 3j";
            string correctAns = "(2, 3)";

            string? reason = PracticeService.GenerateEquivalentMatchReason(new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns }, userAns);
            Assert.NotNull(reason);
            Assert.Contains("向量", reason);
            Assert.Contains("基底", reason);
        }

        [Fact]
        public void UxExpert_ScientificNotation_MatchReason_GeneratesDescriptiveExplanation()
        {
            // 用户体验专家测试：科学记数法匹配时提示规范格式反馈
            string userAns = "3e8";
            string correctAns = "3×10^8";

            string? reason = PracticeService.GenerateEquivalentMatchReason(new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns }, userAns);
            Assert.NotNull(reason);
            Assert.Contains("科学记数法", reason);
        }

        #endregion
    }
}
