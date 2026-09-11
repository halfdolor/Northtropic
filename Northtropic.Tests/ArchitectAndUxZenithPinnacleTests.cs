using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    public class ArchitectAndUxZenithPinnacleTests
    {
        public class SqliteDbContextFactory : IDbContextFactory<AppDbContext>
        {
            private readonly DbContextOptions<AppDbContext> _options;
            public SqliteDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
            public AppDbContext CreateDbContext() => new AppDbContext(_options);
        }

        private static (SqliteConnection Connection, DbContextOptions<AppDbContext> Options) CreateSharedInMemoryDb()
        {
            var dbName = $"memdb_{Guid.NewGuid():N}";
            var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
            var connection = new SqliteConnection(connectionString);
            connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;

            using (var ctx = new AppDbContext(options))
            {
                ctx.Database.EnsureCreated();
            }

            return (connection, options);
        }

        #region 1. 系统架构师：ErrorBookService 数据库隔离与高并发压力测试

        [Fact]
        public async Task ErrorBookService_ConcurrentDatabaseAccess_HandlesHighConcurrencyWithoutException()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);

            var studentId = Guid.NewGuid();
            var student = new User
            {
                Id = studentId,
                Username = "concurrency_student",
                Grade = "初中三年级",
                Role = UserRole.Student
            };

            var questions = new List<Question>();
            var errorItems = new List<ErrorItem>();

            using (var seedCtx = factory.CreateDbContext())
            {
                seedCtx.Users.Add(student);
                for (int i = 1; i <= 10; i++)
                {
                    var q = new Question
                    {
                        Id = Guid.NewGuid(),
                        Subject = "数学",
                        Stem = $"并发测试数学试题第 {i} 题",
                        Type = QuestionType.FillInBlank,
                        CorrectAnswer = $"{i}",
                        StandardAnalysis = "解析内容",
                        CreatedByUserId = studentId
                    };
                    questions.Add(q);
                    seedCtx.Questions.Add(q);

                    var item = new ErrorItem
                    {
                        Id = Guid.NewGuid(),
                        UserId = studentId,
                        QuestionId = q.Id,
                        UserWrongAnswer = $"{i + 1}",
                        ErrorReasonCategory = "概念模糊",
                        CreatedAt = DateTime.UtcNow.AddMinutes(-i),
                        RevisionCount = 0
                    };
                    errorItems.Add(item);
                    seedCtx.ErrorItems.Add(item);
                }
                await seedCtx.SaveChangesAsync();
            }

            var fakeSession = new FakeUserSessionService { ActiveUser = student };
            var gamificationService = new GamificationService(
                factory.CreateDbContext(),
                fakeSession,
                factory);

            var errorBookService = new ErrorBookService(
                factory.CreateDbContext(),
                gamificationService,
                fakeSession,
                factory);

            // 启动 10 个并发读写任务，涵盖查询、更新反思、标记掌握、重练以及单题查询
            var tasks = new List<Task>();

            for (int i = 0; i < 10; i++)
            {
                int index = i;
                tasks.Add(Task.Run(async () =>
                {
                    var item = errorItems[index];
                    var q = questions[index];

                    // 1. 查询未掌握错题
                    var unmastered = await errorBookService.GetUnmasteredErrorsAsync(studentId, studentId);
                    Assert.NotNull(unmastered);

                    // 2. 更新错因分析
                    await errorBookService.UpdateErrorReasonAsync(item.Id, "粗心大意", studentId);

                    // 3. 单题查询
                    var fetchedItem = await errorBookService.GetErrorItemByQuestionAsync(studentId, q.Id);
                    Assert.NotNull(fetchedItem);

                    // 4. 模拟错题重练 (正确回答)
                    var reviseResult = await errorBookService.ReviseErrorAsync(item.Id, true, studentId);
                    Assert.NotNull(reviseResult);

                    // 5. 标记掌握
                    var markResult = await errorBookService.MarkErrorAsMasteredAsync(item.Id, studentId);
                    Assert.NotNull(markResult);
                }));
            }

            // 所有并发任务必须平稳完成，绝不出现 EF Core 线程冲突异常
            await Task.WhenAll(tasks);

            // 最终状态校验
            using (var verifyCtx = factory.CreateDbContext())
            {
                var remainingUnmastered = await verifyCtx.ErrorItems
                    .Where(e => e.UserId == studentId && !e.IsMastered)
                    .CountAsync();
                Assert.Equal(0, remainingUnmastered);
            }
        }

        #endregion

        #region 2. 数学题判题引擎：方程多根/解集无序与多样化表达等价测试

        [Fact]
        public void CheckFillInBlankMatch_EquationRootsUnorderedPermutations_MatchesCorrectly()
        {
            // 1. 纯集合解集无序等价
            Assert.True(PracticeService.CheckFillInBlankMatch("{1, -2}", "{-2, 1}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("{-2, 1}", "{1, -2}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("{0, 3, -5}", "{-5, 0, 3}"));

            // 2. 下标根表示与集合等价
            Assert.True(PracticeService.CheckFillInBlankMatch("x_1=1, x_2=-2", "x_1=-2, x_2=1"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x_1=1, x_2=-2", "{1, -2}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("{1, -2}", "x_1=1, x_2=-2"));

            // 3. 逻辑“或”表示与下标根 / 集合等价
            Assert.True(PracticeService.CheckFillInBlankMatch("x=1或x=-2", "x_1=1, x_2=-2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x=1或者x=-2", "{-2, 1}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1或-2", "x_1=1, x_2=-2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x_1=1, x_2=-2", "1或-2"));

            // 4. 逗号列出解与下标根 / 集合等价
            Assert.True(PracticeService.CheckFillInBlankMatch("1, -2", "x_1=1, x_2=-2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("-2, 1", "x_1=1, x_2=-2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1, -2", "{1, -2}"));

            // 5. 错误数值必须严谨拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("{1, 2}", "{1, -2}"));
            Assert.False(PracticeService.CheckFillInBlankMatch("x_1=1, x_2=2", "x_1=1, x_2=-2"));
            Assert.False(PracticeService.CheckFillInBlankMatch("1, 3", "{1, -2}"));
        }

        #endregion

        #region 3. 数学题判题引擎：分式与根式不等式端点、反向不等式与无穷符号等价测试

        [Fact]
        public void CheckFillInBlankMatch_FractionalAndRadicalInequalityBounds_MatchesCorrectly()
        {
            // 1. 分式端点单侧不等式与正无穷区间等价
            Assert.True(PracticeService.CheckFillInBlankMatch("x >= 1/2", "[1/2, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x > 1/2", "(1/2, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x <= -3/4", "(-inf, -3/4]"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x < -3/4", "(-inf, -3/4)"));

            // 2. 变量在右侧的反向单侧不等式
            Assert.True(PracticeService.CheckFillInBlankMatch("1/2 <= x", "[1/2, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1/2 < x", "(1/2, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("5 >= x", "(-inf, 5]"));
            Assert.True(PracticeService.CheckFillInBlankMatch("5 > x", "(-inf, 5)"));

            // 3. 分式端点双侧复合不等式
            Assert.True(PracticeService.CheckFillInBlankMatch("1/2 <= x <= 3/2", "[1/2, 3/2]"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1/2 < x < 3/2", "(1/2, 3/2)"));

            // 4. 反向双侧复合不等式 (如 3/2 >= x >= 1/2)
            Assert.True(PracticeService.CheckFillInBlankMatch("3/2 >= x >= 1/2", "[1/2, 3/2]"));
            Assert.True(PracticeService.CheckFillInBlankMatch("3/2 > x > 1/2", "(1/2, 3/2)"));

            // 5. 分数与小数端点数值等价容错
            Assert.True(PracticeService.CheckFillInBlankMatch("[1/2, 3/2]", "[0.5, 1.5]"));
            Assert.True(PracticeService.CheckFillInBlankMatch("[1/2, +inf)", "[0.5, +inf)"));

            // 6. LaTeX 无穷符号与省略正号的正无穷容错
            Assert.True(PracticeService.CheckFillInBlankMatch(@"[1, \infty)", "[1, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"[1, +\infty)", "[1, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"[1, \infty)", @"[1, +\infty)"));
        }

        #endregion

        #region 4. 物理化学题判题引擎：摩尔与复合化学单位智能识别测试

        [Fact]
        public void CheckFillInBlankMatch_ChemicalMolarUnits_MatchesCorrectly()
        {
            // 1. 摩尔质量常用单位与中文等价
            Assert.True(PracticeService.CheckFillInBlankMatch("56 g/mol", "56 克/摩尔"));
            Assert.True(PracticeService.CheckFillInBlankMatch("56 g/mol", "56"));
            Assert.True(PracticeService.CheckFillInBlankMatch("56", "56 g/mol"));
            Assert.True(PracticeService.CheckFillInBlankMatch("56 g·mol^-1", "56 g/mol"));
            Assert.True(PracticeService.CheckFillInBlankMatch("56 g*mol^-1", "56 g/mol"));

            // 2. 气体摩尔体积常用单位与中文等价
            Assert.True(PracticeService.CheckFillInBlankMatch("22.4 L/mol", "22.4 升/摩尔"));
            Assert.True(PracticeService.CheckFillInBlankMatch("22.4 l/mol", "22.4"));
            Assert.True(PracticeService.CheckFillInBlankMatch("22.4", "22.4 L/mol"));

            // 3. 错误数值应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("58 g/mol", "56 g/mol"));
            Assert.False(PracticeService.CheckFillInBlankMatch("22.5 L/mol", "22.4 L/mol"));
        }

        #endregion

        #region 5. 系统架构师：QuestionImportService RFC 4180 跨行多行 CSV 导入测试

        [Fact]
        public async Task ParseCsvImportAsync_MultilineQuotedRecords_ParsesCorrectly()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);

            var studentId = Guid.NewGuid();
            using (var seedCtx = factory.CreateDbContext())
            {
                seedCtx.Users.Add(new User
                {
                    Id = studentId,
                    Username = "csv_import_tester",
                    Grade = "初中二年级",
                    Role = UserRole.Student
                });
                await seedCtx.SaveChangesAsync();
            }

            var service = new QuestionImportService(
                factory.CreateDbContext(),
                null,
                null,
                factory);

            // 构造包含换行题干与选项的双引号封装 RFC 4180 CSV
            var csvContent = new StringBuilder();
            csvContent.AppendLine("学科,分类,年级,题型,题干,选项,正确答案,解析");
            csvContent.AppendLine("\"物理\",\"力学\",\"初中二年级\",\"单选题\",\"关于牛顿第一定律，\n下列说法中正确的是：\",\"A. 物体静止时不受力|B. 物体运动必须有力维持|C. 一切物体都有惯性|D. 速度越大惯性越大\",\"C\",\"惯性是物体的固有属性，只与质量有关。\"");
            csvContent.AppendLine("\"数学\",\"代数\",\"初中二年级\",\"填空题\",\"已知二次方程的解为：\nx_1=1, x_2=-2\",\"\",\"{1, -2}\",\"由因式分解求根。\"");

            var csvBytes = Encoding.UTF8.GetBytes(csvContent.ToString());
            using var stream = new MemoryStream(csvBytes);

            var result = await service.ParseAndImportQuestionsByFileFormatAsync(stream, "multiline_questions.csv", studentId);

            Assert.True(result.IsSuccess, $"导入失败，错误信息: {string.Join("; ", result.ErrorMessages)}");
            Assert.Equal(2, result.SuccessCount);
            Assert.Empty(result.ErrorMessages);

            // 验证数据库中持久化的题干保留了换行符，且字段未错位
            using (var verifyCtx = factory.CreateDbContext())
            {
                var questions = await verifyCtx.Questions
                    .Where(q => q.CreatedByUserId == studentId)
                    .OrderBy(q => q.Subject)
                    .ToListAsync();

                Assert.Equal(2, questions.Count);

                var mathQ = questions.First(q => q.Subject == "数学");
                Assert.Contains("已知二次方程的解为：\nx_1=1, x_2=-2", mathQ.Stem);
                Assert.Equal(QuestionType.FillInBlank, mathQ.Type);
                Assert.Equal("{1, -2}", mathQ.CorrectAnswer);

                var physicsQ = questions.First(q => q.Subject == "物理");
                Assert.Contains("关于牛顿第一定律，\n下列说法中正确的是：", physicsQ.Stem);
                Assert.Equal(QuestionType.SingleChoice, physicsQ.Type);
                Assert.Equal("C", physicsQ.CorrectAnswer);
                Assert.Contains(physicsQ.Options, opt => opt.Contains("一切物体都有惯性"));
            }
        }

        #endregion

        #region 6. 用户体验专家：智能等价判题反馈文案（教学化正向赋能）测试

        [Fact]
        public void GenerateEquivalentMatchReason_EquationRootsAndChemicalUnits_ReturnsIntuitiveExplanations()
        {
            // 1. 方程多根解集等价原因测试
            var qRoots = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "x_1=1, x_2=-2"
            };
            var reasonRoots = PracticeService.GenerateEquivalentMatchReason(qRoots, "{1, -2}");
            Assert.NotNull(reasonRoots);
            Assert.Contains("方程根解集等价", reasonRoots);

            // 2. 摩尔与化学复合单位等价原因测试
            var qMolar = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "56 g/mol"
            };
            var reasonMolar = PracticeService.GenerateEquivalentMatchReason(qMolar, "56");
            Assert.NotNull(reasonMolar);
            Assert.Contains("理化复合单位智能对齐等价", reasonMolar);

            // 3. 不等式与区间并集等价原因测试
            var qIneq = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "(-inf, 1] u [3, +inf)"
            };
            var reasonIneq = PracticeService.GenerateEquivalentMatchReason(qIneq, "x <= 1 或 x >= 3");
            Assert.NotNull(reasonIneq);
            Assert.Contains("不等式或关系与区间并集等价", reasonIneq);
        }

        #endregion
    }
}
