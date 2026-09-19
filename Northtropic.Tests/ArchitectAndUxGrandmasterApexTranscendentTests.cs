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
    public class ArchitectAndUxGrandmasterApexTranscendentTests
    {
        private static (AppDbContext Context, SqliteConnection Connection) CreateInMemoryDbContext()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AppDbContext(options);
            context.Database.EnsureCreated();
            return (context, connection);
        }

        #region 1. 系统架构师维度：题目创建者拓扑自愈、私有孤儿级联清理与公有资产脱敏保留

        [Fact]
        public async Task SystemHealth_AuditDataIntegrity_DetectsOrphanPrivateAndDanglingPublicQuestions()
        {
            var (context, connection) = CreateInMemoryDbContext();
            try
            {
                var healthService = new SystemHealthService(context, null);

                var legitimateUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "legit_creator"
                };
                context.Users.Add(legitimateUser);

                // 正常公有题目与私有题目
                var legitPublic = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "合法公有题",
                    Subject = "数学",
                    GradeTarget = "高一",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "1",
                    IsPublic = true,
                    CreatedByUserId = legitimateUser.Id
                };
                var legitPrivate = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "合法私有题",
                    Subject = "数学",
                    GradeTarget = "高一",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "2",
                    IsPublic = false,
                    CreatedByUserId = legitimateUser.Id
                };

                // 悬空公共题 (创建者已被注销，但作为平台公有试题需保留)
                var ghostUserId = Guid.NewGuid();
                var danglingPublic = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "优质中考模拟题（创建者已销户）",
                    Subject = "物理",
                    GradeTarget = "九年级",
                    Type = QuestionType.SingleChoice,
                    CorrectAnswer = "A",
                    IsPublic = true,
                    CreatedByUserId = ghostUserId
                };

                // 孤儿私有题 (创建者已销户，属于私有孤儿死数据，需级联清除)
                var orphanPrivate = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "死者私人未公开草稿题",
                    Subject = "化学",
                    GradeTarget = "高二",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "B",
                    IsPublic = false,
                    CreatedByUserId = ghostUserId
                };

                context.Questions.AddRange(legitPublic, legitPrivate, danglingPublic, orphanPrivate);

                // 为孤儿私有题绑定关联记录 (验证级联删除)
                var orphanError = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = legitimateUser.Id,
                    QuestionId = orphanPrivate.Id,
                    UserWrongAnswer = "C",
                    CreatedAt = DateTime.UtcNow
                };
                var orphanFavorite = new UserFavorite
                {
                    Id = Guid.NewGuid(),
                    UserId = legitimateUser.Id,
                    QuestionId = orphanPrivate.Id,
                    CreatedAt = DateTime.UtcNow
                };
                var orphanRecord = new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = legitimateUser.Id,
                    QuestionId = orphanPrivate.Id,
                    UserAnswer = "C",
                    IsCorrect = false,
                    AnsweredAt = DateTime.UtcNow
                };

                context.ErrorItems.Add(orphanError);
                context.UserFavorites.Add(orphanFavorite);
                context.PracticeRecords.Add(orphanRecord);

                await context.SaveChangesAsync();

                // 1. 运行巡检
                var auditResult = await healthService.AuditDataIntegrityAsync();
                Assert.False(auditResult.IsHealthy);
                Assert.True(auditResult.TotalOrphanPrivateQuestions >= 1);
                Assert.True(auditResult.TotalDanglingPublicQuestions >= 1);
                Assert.Equal(1, auditResult.OrphanPrivateQuestionsCount);
                Assert.Equal(1, auditResult.DanglingPublicQuestionsCount);

                // 2. 执行自愈清理
                var purgeResult = await healthService.PurgeOrphanedRecordsAsync();
                Assert.Equal(1, purgeResult.PurgedPrivateQuestionsCount);
                Assert.Equal(1, purgeResult.SanitizedPublicQuestionsCount);

                // 3. 验证数据库状态
                // 孤儿私有题及其级联数据已被删除
                Assert.Null(await context.Questions.FindAsync(orphanPrivate.Id));
                Assert.Null(await context.ErrorItems.FindAsync(orphanError.Id));
                Assert.Null(await context.UserFavorites.FindAsync(orphanFavorite.Id));
                Assert.Null(await context.PracticeRecords.FindAsync(orphanRecord.Id));

                // 悬空公共题已解除创建者绑定，公有试题仍然完好保存在库中
                var preservedPublic = await context.Questions.FindAsync(danglingPublic.Id);
                Assert.NotNull(preservedPublic);
                Assert.True(preservedPublic.IsPublic);
                Assert.Null(preservedPublic.CreatedByUserId);

                // 合法公有与私有题目不受任何影响
                Assert.NotNull(await context.Questions.FindAsync(legitPublic.Id));
                Assert.NotNull(await context.Questions.FindAsync(legitPrivate.Id));

                // 4. 再次审计确认健康
                var postAudit = await healthService.AuditDataIntegrityAsync();
                Assert.True(postAudit.IsHealthy);
                Assert.Equal(0, postAudit.TotalIssuesCount);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        #endregion

        #region 2. 系统架构师维度：GC 垃圾回收分代指标与诊断探测

        [Fact]
        public void GC_CollectionGenerations_AreValidAndTrackable()
        {
            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);

            Assert.True(gen0 >= 0, "Gen0 回收计数必须非负");
            Assert.True(gen1 >= 0, "Gen1 回收计数必须非负");
            Assert.True(gen2 >= 0, "Gen2 回收计数必须非负");
            Assert.True(gen0 >= gen1, "Gen0 频次必须大于等于 Gen1");
            Assert.True(gen1 >= gen2, "Gen1 频次必须大于等于 Gen2");
        }

        #endregion

        #region 3. 用户体验专家维度：压强、能量、功率与频率等科学物理单位智能换算等价评判

        [Theory]
        // 压强单位：标准大气压、千帕、帕斯卡、毫米汞柱、巴、百帕
        [InlineData("1 atm", "101325 Pa")]
        [InlineData("1 atm", "101.3 kPa")]
        [InlineData("1 标准大气压", "101325 帕")]
        [InlineData("1 atm", "1.01e5 Pa")]
        [InlineData("760 mmHg", "101325 Pa")]
        [InlineData("760 毫米汞柱", "1 atm")]
        [InlineData("1 bar", "100 kPa")]
        [InlineData("1 巴", "100000 Pa")]
        [InlineData("1000 hPa", "100 kPa")]
        [InlineData("1013 hPa", "1 atm")]
        [InlineData("1000 mbar", "1e5 Pa")]
        // 能量单位：度 / 千瓦时 / 兆焦 / 焦耳
        [InlineData("1 kWh", "3.6e6 J")]
        [InlineData("1 kW·h", "3.6 MJ")]
        [InlineData("1 kW*h", "3600 kJ")]
        [InlineData("1 度", "3.6e6 J")]
        [InlineData("1 千瓦·时", "3.6e6 焦")]
        [InlineData("1 千瓦时", "3.6e6 J")]
        [InlineData("2 度", "7.2 MJ")]
        // 功率单位：马力 / 瓦特
        [InlineData("1 hp", "735 W")]
        [InlineData("1 马力", "735 瓦")]
        [InlineData("2 hp", "1470 W")]
        // 频率单位：GHz / MHz / kHz / Hz
        [InlineData("1 GHz", "1000 MHz")]
        [InlineData("1 MHz", "1000 kHz")]
        [InlineData("1 kHz", "1000 Hz")]
        [InlineData("2.4 GHz", "2.4e9 Hz")]
        public void CheckFillInBlankMatch_ScientificUnits_MatchesEquivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"科学单位换算应该等价通过: '{userAns}' 与 '{correctAns}'");
        }

        #endregion

        #region 4. 用户体验专家维度：结晶水合物与中学化学核心矿物俗名无缝等价

        [Theory]
        [InlineData("明矾", "KAl(SO4)2·12H2O")]
        [InlineData("十二水合硫酸铝钾", "kal(so4)2*12h2o")]
        [InlineData("白矾", "KAl(SO4)2·12H2O")]
        [InlineData("胆矾", "CuSO4·5H2O")]
        [InlineData("蓝矾", "cuso4*5h2o")]
        [InlineData("五水硫酸铜", "CuSO4·5H2O")]
        [InlineData("绿矾", "FeSO4·7H2O")]
        [InlineData("七水硫酸亚铁", "feso4*7h2o")]
        [InlineData("生石膏", "CaSO4·2H2O")]
        [InlineData("二水硫酸钙", "caso4*2h2o")]
        [InlineData("熟石膏", "2CaSO4·H2O")]
        [InlineData("半水硫酸钙", "2caso4*h2o")]
        [InlineData("熟石膏", "(CaSO4)2·H2O")]
        [InlineData("大苏打", "Na2S2O3")]
        [InlineData("海波", "na2s2o3")]
        [InlineData("硫代硫酸钠", "Na2S2O3")]
        [InlineData("芒硝", "Na2SO4·10H2O")]
        [InlineData("十水合硫酸钠", "na2so4*10h2o")]
        public void CheckFillInBlankMatch_ChemicalHydratesAndMinerals_MatchesEquivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"结晶水合物与矿物俗名应该等价匹配: '{userAns}' 与 '{correctAns}'");
        }

        #endregion

        #region 5. 用户体验专家维度：数学空集表示与二次因式分解不等式极速等价映射

        [Theory]
        // 空集等价表示
        [InlineData("∅", "\\emptyset")]
        [InlineData("∅", "\\varnothing")]
        [InlineData("∅", "\\phi")]
        [InlineData("empty", "∅")]
        [InlineData("phi", "∅")]
        [InlineData("{}", "∅")]
        [InlineData("空集", "∅")]
        [InlineData("无解", "∅")]
        [InlineData("无实数根", "∅")]
        // 二次因式分解不等式
        [InlineData("(x-1)(x-3)<0", "(1, 3)")]
        [InlineData("(x-1)(x-3)<0", "1 < x < 3")]
        [InlineData("(x+2)(x-4)<=0", "[-2, 4]")]
        [InlineData("(x+2)(x-4)<=0", "-2 <= x <= 4")]
        [InlineData("(x-3)(x-1)<0", "(1, 3)")] // 因式反序
        [InlineData("(x-1)(x-3)>0", "(-inf, 1)u(3, +inf)")]
        [InlineData("(x-1)(x-3)>0", "x < 1 或 x > 3")]
        [InlineData("x(x-2)<0", "(0, 2)")]
        [InlineData("x(x-2)<0", "0 < x < 2")]
        public void CheckFillInBlankMatch_EmptySetAndFactoredInequalities_MatchesEquivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"空集或因式分解不等式应该等价匹配: '{userAns}' 与 '{correctAns}'");
        }

        #endregion
    }
}
