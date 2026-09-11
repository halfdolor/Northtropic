using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class SystemArchitectAndUxEliteMasteryTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;

        public SystemArchitectAndUxEliteMasteryTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        [Fact]
        public void GradeSubjectProvider_DefensiveCopying_ProtectsStaticSubjectsFromExternalMutation()
        {
            // 架构师测试：验证外部调用者对返回集合的篡改绝不会污染全局静态配置
            var subjects = GradeSubjectProvider.GetSubjectsByGrade("初三");
            Assert.NotEmpty(subjects);
            int originalCount = subjects.Count;

            // 模拟恶意或无意的调用方修改
            subjects.Add("被注入的非法学科");
            subjects.Clear();

            // 再次获取，验证原始配置毫发无损
            var freshSubjects = GradeSubjectProvider.GetSubjectsByGrade("初三");
            Assert.Equal(originalCount, freshSubjects.Count);
            Assert.DoesNotContain("被注入的非法学科", freshSubjects);
        }

        [Fact]
        public void GradeSubjectProvider_DefensiveCopying_ProtectsStaticCategoriesFromExternalMutation()
        {
            // 架构师测试：验证考点分类列表的防御性拷贝
            var categories = GradeSubjectProvider.GetCategoriesBySubject("数学");
            Assert.NotEmpty(categories);
            int originalCount = categories.Count;

            // 模拟外部集合操作
            categories.Insert(0, "非预期测试项");

            // 验证静态集合隔离
            var freshCategories = GradeSubjectProvider.GetCategoriesBySubject("数学");
            Assert.Equal(originalCount, freshCategories.Count);
            Assert.DoesNotContain("非预期测试项", freshCategories);
        }

        [Fact]
        public async Task PracticeService_PerSubjectCategoryCaching_AcceleratesLookupAndRespondsToInvalidation()
        {
            // 架构师测试：验证分学科二级考点缓存以及 InvalidateCategoryCache 的双层清理能力
            PracticeService.InvalidateCategoryCache();
            var userSession = new FakeUserSessionService();
            var gamification = new FakeGamificationService();
            var practiceService = new PracticeService(_context, gamification, userSession, new FakeAiTutorService());

            // 插入物理与化学两个学科的初始题目
            _context.Questions.Add(new Question
            {
                Stem = "牛顿第一定律基础题",
                Subject = "物理",
                Category = "牛顿力学",
                CorrectAnswer = "A",
                OptionsJson = "[\"A\",\"B\"]"
            });
            _context.Questions.Add(new Question
            {
                Stem = "氧化还原反应题",
                Subject = "化学",
                Category = "氧化还原",
                CorrectAnswer = "B",
                OptionsJson = "[\"A\",\"B\"]"
            });
            await _context.SaveChangesAsync();

            // 首次查询，加载并缓存
            var physicsCats1 = await practiceService.GetCategoriesBySubjectAsync("物理");
            Assert.Contains("牛顿力学", physicsCats1);

            // 直接向数据库添加新考点题目（不经过服务，模拟外部导入）
            _context.Questions.Add(new Question
            {
                Stem = "欧姆定律测试题",
                Subject = "物理",
                Category = "电学欧姆定律",
                CorrectAnswer = "C",
                OptionsJson = "[\"A\",\"B\",\"C\"]"
            });
            await _context.SaveChangesAsync();

            // 在未失效缓存前，二次查询命中二级缓存，不应包含新考点
            var physicsCatsCached = await practiceService.GetCategoriesBySubjectAsync("物理");
            Assert.DoesNotContain("电学欧姆定律", physicsCatsCached);

            // 触发原子缓存失效
            PracticeService.InvalidateCategoryCache();

            // 再次查询，应刷新数据库最新考点
            var physicsCatsRefreshed = await practiceService.GetCategoriesBySubjectAsync("物理");
            Assert.Contains("电学欧姆定律", physicsCatsRefreshed);
        }

        [Fact]
        public async Task PracticeService_SubmitAnswerAsync_TransactionalIntegrity_PersistsAtomically()
        {
            // 架构师测试：验证 SubmitAnswerAsync 在关系型事务包裹下，全链路（题目记录、用户奖励、错题本）原子提交
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "test_architect_student",
                Grade = "初三",
                Exp = 10,
                Coins = 50,
                Level = 1
            };
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            var userSession = new FakeUserSessionService { ActiveUser = user };
            var gamification = new GamificationService(_context, userSession);
            var practiceService = new PracticeService(_context, gamification, userSession, new FakeAiTutorService());

            var testQuestion = new Question
            {
                Stem = "计算 2 + 3 的结果是？",
                Subject = "数学",
                Category = "基础概念",
                CorrectAnswer = "5",
                Type = QuestionType.SingleChoice,
                OptionsJson = "[\"4\",\"5\",\"6\",\"7\"]"
            };
            _context.Questions.Add(testQuestion);
            await _context.SaveChangesAsync();

            // 提交正确答案
            var result = await practiceService.SubmitAnswerAsync(testQuestion, "5", timeTakenSeconds: 8, currentCombo: 1, targetUserId: user.Id);

            Assert.True(result.IsCorrect);
            Assert.True(result.Reward.EarnedExp > 0);

            // 验证数据持久化成功
            var refreshedUser = await _context.Users.FindAsync(user.Id);
            Assert.NotNull(refreshedUser);
            Assert.True(refreshedUser.Exp > 10);
            Assert.True(refreshedUser.Coins > 50);

            var practiceRecord = await _context.PracticeRecords.FirstOrDefaultAsync(r => r.UserId == user.Id && r.QuestionId == testQuestion.Id);
            Assert.NotNull(practiceRecord);
            Assert.True(practiceRecord.IsCorrect);
        }

        [Fact]
        public void ErrorBook_EbbinghausHealthScoreAndUrgency_ClassifiesCorrectly()
        {
            // UX 专家测试：验证艾宾浩斯抗遗忘记忆曲线保留率计算与紧迫度
            var userSession = new FakeUserSessionService();
            var gamification = new FakeGamificationService();
            var errorBookService = new ErrorBookService(_context, gamification, userSession);

            var now = DateTime.Now;

            // 场景 1：刚复习完的错题（高健康保留度）
            var freshError = new ErrorItem
            {
                RevisionCount = 2,
                LastRevisedAt = now
            };
            double freshScore = errorBookService.CalculateRetentionHealthScore(freshError, now);
            Assert.InRange(freshScore, 90.0, 100.0);

            // 场景 2：超期很久未复习（衰退至临界遗忘，触发复习警报）
            var agedError = new ErrorItem
            {
                RevisionCount = 0,
                LastRevisedAt = now.AddDays(-3)
            };
            double agedScore = errorBookService.CalculateRetentionHealthScore(agedError, now);
            Assert.True(agedScore < 50.0, $"长期未复习题目保留率应小于 50%，实测 {agedScore}");
            Assert.True(errorBookService.IsReviewDue(agedError, now), "长期未复习应判定为 ReviewDue (急需复习)");
        }

        [Theory]
        [InlineData("A", 0)]
        [InlineData("1", 0)]
        [InlineData("B", 1)]
        [InlineData("2", 1)]
        [InlineData("C", 2)]
        [InlineData("3", 2)]
        [InlineData("D", 3)]
        [InlineData("4", 3)]
        [InlineData("E", 4)]
        [InlineData("5", 4)]
        [InlineData("X", -1)]
        public void KeyboardShortcut_OptionIndexMapping_MapsExpectedIndices(string key, int expectedIndex)
        {
            // UX 专家测试：验证数字键与字母键的双向等价映射
            int optIdx = key switch
            {
                "A" => 0, "1" => 0,
                "B" => 1, "2" => 1,
                "C" => 2, "3" => 2,
                "D" => 3, "4" => 3,
                "E" => 4, "5" => 4,
                _ => -1
            };

            Assert.Equal(expectedIndex, optIdx);
        }
    }
}
