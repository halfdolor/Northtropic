using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxTranscendenceEvolutionTests
    {
        [Fact]
        public void CheckFillInBlankMatch_ChemicalArrowsAndEquations_MatchesCorrectly()
        {
            // 1. 化学方程式中各种箭头与等号等价: \rightarrow, \longrightarrow, -->, ==, =
            Assert.True(PracticeService.CheckFillInBlankMatch(@"2H_2 + O_2 \longrightarrow 2H_2O", "2H2+O2=2H2O"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"2H_2 + O_2 \rightarrow 2H_2O", "2H2+O2==2H2O"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2H2 + O2 --> 2H2O", "2H2+O2=2H2O"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2H2 + O2 === 2H2O", "2H2+O2->2H2O"));

            // 2. 反应条件标注自动剥离容错 (点燃、加热、高温、催化剂、△)
            Assert.True(PracticeService.CheckFillInBlankMatch(@"C + O_2 \stackrel{点燃}{=} CO_2", "C+O2=CO2"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"CaCO3 \xrightarrow{高温} CaO + CO2", "CaCO3=CaO+CO2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2KClO3 [催化剂] 2KCl + 3O2", "2KClO3=2KCl+3O2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("Cu + 2H2SO4 (加热) CuSO4 + SO2 + 2H2O", "Cu+2H2SO4=CuSO4+SO2+2H2O"));

            // 3. 化学离子符号电荷多加号/多减号容错: Fe+++ vs Fe^{3+} vs Fe3+; SO4-- vs SO4^{2-}
            Assert.True(PracticeService.CheckFillInBlankMatch("Fe+++", "Fe^{3+}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("Fe+++", "Fe3+"));
            Assert.True(PracticeService.CheckFillInBlankMatch("SO4--", "SO_4^{2-}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("SO4--", "so42-"));
        }

        [Fact]
        public void CheckFillInBlankMatch_IntervalsAndSetsSpacing_MatchesCorrectly()
        {
            // 1. 数学区间内部逗号两侧空格与括号空格容错
            Assert.True(PracticeService.CheckFillInBlankMatch("[1, 5)", "[1,5)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("[ 1 , 5 )", "[1,5)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("( 2 , 8 ]", "(2,8]"));

            // 2. 包含无穷大的区间与不同表示法
            Assert.True(PracticeService.CheckFillInBlankMatch(@"(-\infty, 2]", "(-inf, 2]"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"(- \infty , 2 ]", "(-inf,2]"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"(-\infty, +\infty)", "(-inf, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(-∞, +∞)", "(-inf, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(-∞, +∞)", @"(-\infty, +\infty)"));

            // 3. 错误区间应明确拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("[1, 5]", "[1, 5)"));
            Assert.False(PracticeService.CheckFillInBlankMatch("(1, 5)", "[1, 5)"));
        }

        [Fact]
        public void CheckFillInBlankMatch_ScientificNotationWithParentheses_MatchesCorrectly()
        {
            // 1. 科学记数法指数带圆括号与花括号: 10^(8), 10^{(8)}, 10^-19, 10^(-19)
            Assert.True(PracticeService.CheckFillInBlankMatch("3.0*10^(8)", "3e8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("3.0*10^(8)", "3*10^8"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"3.0 \times 10^{(8)}", "3e8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1.6*10^(-19)", "1.6e-19"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"1.6 \times 10^{-19}", "1.6*10^(-19)"));

            // 2. 大写 E 科学记数法支持
            Assert.True(PracticeService.CheckFillInBlankMatch("3.0E8", "3*10^8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1.6E-19", "1.6*10^-19"));

            // 3. 数值不同时应判定不匹配
            Assert.False(PracticeService.CheckFillInBlankMatch("3.0*10^(7)", "3e8"));
        }

        [Fact]
        public void CheckFillInBlankMatch_RadicalsAndRoots_EvaluatesCorrectly()
        {
            // 1. 根号内空格规范化: \sqrt{ 3 } vs \sqrt{3}
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\sqrt{ 3 }", @"\sqrt{3}"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\sqrt{ 5 }", "sqrt(5)"));

            // 2. 整数开平方根数值等价: \sqrt{4} 等价于 2, \sqrt{9} 等价于 3
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\sqrt{4}", "2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2", @"\sqrt{4}"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\sqrt{ 9 }", "3"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\sqrt{16}", "4"));

            // 3. 立方根数值等价: \sqrt[3]{8} 等价于 2
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\sqrt[3]{8}", "2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2", @"\sqrt[3]{8}"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\sqrt[3]{27}", "3"));

            // 4. 不相等的开方应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch(@"\sqrt{5}", "2"));
        }

        [Fact]
        public void CheckFillInBlankMatch_DefensiveAndEmptyGuards_ReturnsFalse()
        {
            // 避免空字符串、空白符等误判或抛异常
            Assert.False(PracticeService.CheckFillInBlankMatch("", ""));
            Assert.False(PracticeService.CheckFillInBlankMatch("   ", ""));
            Assert.False(PracticeService.CheckFillInBlankMatch("", "123"));
            Assert.False(PracticeService.CheckFillInBlankMatch("123", ""));
            Assert.False(PracticeService.CheckFillInBlankMatch(null!, "123"));
            Assert.False(PracticeService.CheckFillInBlankMatch("123", null!));
        }

        [Fact]
        public async Task SubmitBatchPaperAsync_AtomicTransactionCommit_Succeeds()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "全卷模考学生",
                    Role = UserRole.Student,
                    Exp = 100,
                    Coins = 50,
                    Level = 1
                };
                context.Users.Add(student);

                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "光在真空中传播速度约为多少 m/s？",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "3e8",
                    Subject = "初中物理",
                    Category = "光学",
                    BaseExpReward = 20
                };
                var q2 = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "氧气分子式？",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "O2",
                    Subject = "初中化学",
                    Category = "物质构成的奥秘",
                    BaseExpReward = 20
                };
                context.Questions.AddRange(q1, q2);
                await context.SaveChangesAsync();

                var sessionMock = new FakeUserSessionService { ActiveUser = student };
                var gamificationService = new GamificationService(context, sessionMock);
                var practiceService = new PracticeService(context, gamificationService, sessionMock, new FakeAiTutorService());

                var submissions = new List<(Question Question, string UserAnswer, int TimeTakenSeconds)>
                {
                    (q1, "3.0*10^(8)", 15),
                    (q2, "O_2", 10)
                };

                // 执行整卷原子性批量提交
                var batchResult = await practiceService.SubmitBatchPaperAsync(submissions, initialCombo: 2, targetUserId: student.Id);

                Assert.NotNull(batchResult);
                Assert.Equal(2, batchResult.TotalQuestions);
                Assert.Equal(2, batchResult.CorrectCount);
                Assert.True(batchResult.TotalEarnedExp > 0);
                Assert.True(batchResult.TotalEarnedCoins > 0);

                // 验证数据库事务成功提交后，练习记录与金币经验正确入库
                var records = await context.PracticeRecords.Where(r => r.UserId == student.Id).ToListAsync();
                Assert.Equal(2, records.Count);
                Assert.True(records.All(r => r.IsCorrect));

                var updatedUser = await context.Users.FindAsync(student.Id);
                Assert.NotNull(updatedUser);
                Assert.True(updatedUser.Level >= 2);
                Assert.True(updatedUser.Coins > 50);
            }
        }

        [Fact]
        public async Task SubmitBatchPaperAsync_WithCancellation_RollsBackCleanly()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "取消测试学生",
                    Role = UserRole.Student,
                    Exp = 50,
                    Coins = 30
                };
                context.Users.Add(student);

                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "测试题目1",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "A",
                    Subject = "测试",
                    Category = "测试",
                    BaseExpReward = 10
                };
                context.Questions.Add(q1);
                await context.SaveChangesAsync();

                var sessionMock = new FakeUserSessionService { ActiveUser = student };
                var gamificationService = new GamificationService(context, sessionMock);
                var practiceService = new PracticeService(context, gamificationService, sessionMock, new FakeAiTutorService());

                var submissions = new List<(Question Question, string UserAnswer, int TimeTakenSeconds)>
                {
                    (q1, "A", 5)
                };

                // 传入已请求取消的 CancellationToken
                using var cts = new CancellationTokenSource();
                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    await practiceService.SubmitBatchPaperAsync(submissions, initialCombo: 0, targetUserId: student.Id, cancellationToken: cts.Token);
                });

                // 验证由于事务回滚，没有生成任何练习记录，用户数据保持不变
                var records = await context.PracticeRecords.Where(r => r.UserId == student.Id).ToListAsync();
                Assert.Empty(records);

                var userInDb = await context.Users.FindAsync(student.Id);
                Assert.NotNull(userInDb);
                Assert.Equal(50, userInDb.Exp);
                Assert.Equal(30, userInDb.Coins);
            }
        }
    }
}
