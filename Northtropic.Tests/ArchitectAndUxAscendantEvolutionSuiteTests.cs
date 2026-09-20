using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxAscendantEvolutionSuiteTests
    {
        #region 1. 系统架构师：选择题选项重复与空白项自愈及多重前缀污染收敛测试
        [Fact]
        public async Task SystemHealthService_ChoiceOptionDeduplicationAndAnalysisPrefixCleaning_ShouldHealAndConverge()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var healthService = new SystemHealthService(context, null);

                // 准备测试数据：包含重复选项与空白选项的选择题，以及包含多重重复解析前缀的试题
                var duplicateOptionQuestion = new Question
                {
                    Subject = "化学",
                    Category = "物质分类",
                    Stem = "下列属于纯净物的是？",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"A. 冰水混合物\",\"A. 冰水混合物\",\"B. 洁净的空气\",\"C. 澄清石灰水\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "解析：解析：【解析】冰水混合物只含水一种物质，属于纯净物。"
                };

                var blankOptionQuestion = new Question
                {
                    Subject = "物理",
                    Category = "热学",
                    Stem = "关于内能与热量，下列说法正确的是？",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"A. 物体温度升高内能一定增加\",\"   \",\"B. 热量总是从内能大的物体传递到内能小的物体\",\"C. 物体吸收热量温度一定升高\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "【解析】【解析】温度是分子平均动能的标志，温度升高内能一定增加。"
                };

                context.Questions.AddRange(duplicateOptionQuestion, blankOptionQuestion);
                await context.SaveChangesAsync();

                // 阶段 1：审计检测出异常
                var auditBefore = await healthService.AuditDataIntegrityAsync();
                Assert.True(auditBefore.MalformedChoiceQuestionsCount >= 2,
                    $"应检出至少 2 道选项存在重复或空白项的选择题，实际检出: {auditBefore.MalformedChoiceQuestionsCount}");
                Assert.False(auditBefore.IsStrictlyHealthy);

                // 阶段 2：执行全自动拓扑自愈
                var healedCount = await healthService.HealCorruptedQuestionsAsync();
                Assert.True(healedCount >= 2, $"自愈题数应至少包含这两道题，实际自愈数: {healedCount}");

                // 阶段 3：自愈后复测审计指标收敛
                var auditAfter = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, auditAfter.MalformedChoiceQuestionsCount);

                // 阶段 4：验证实体数据字段清洗与规范化结果
                var healedQ1 = await context.Questions.FindAsync(duplicateOptionQuestion.Id);
                Assert.NotNull(healedQ1);
                var opts1 = System.Text.Json.JsonSerializer.Deserialize<List<string>>(healedQ1.OptionsJson);
                Assert.NotNull(opts1);
                Assert.True(opts1.Count >= 4, "选项数应不少于4个规范项");
                Assert.Equal(opts1.Count, new HashSet<string>(opts1).Count); // 无重复项
                Assert.All(opts1, o => Assert.False(string.IsNullOrWhiteSpace(o))); // 无空白项
                Assert.DoesNotContain("解析：解析：", healedQ1.StandardAnalysis);
                Assert.DoesNotContain("【解析】", healedQ1.StandardAnalysis);

                var healedQ2 = await context.Questions.FindAsync(blankOptionQuestion.Id);
                Assert.NotNull(healedQ2);
                var opts2 = System.Text.Json.JsonSerializer.Deserialize<List<string>>(healedQ2.OptionsJson);
                Assert.NotNull(opts2);
                Assert.True(opts2.Count >= 4);
                Assert.Equal(opts2.Count, new HashSet<string>(opts2).Count);
                Assert.All(opts2, o => Assert.False(string.IsNullOrWhiteSpace(o)));
                Assert.DoesNotContain("【解析】【解析】", healedQ2.StandardAnalysis);
            }
        }
        #endregion

        #region 2. 用户体验专家：STEM 智能判卷 - 化学电解质与酸碱平衡等价引擎
        [Theory]
        [InlineData("pH = 7", "pOH = 7")]
        [InlineData("pH = 3", "pOH = 11")]
        [InlineData("pH = 2", "pOH = 12")]
        [InlineData("pH = 13", "pOH = 1")]
        [InlineData("c(H+) = 1.0 * 10^-7 mol/L", "pH = 7")]
        [InlineData("10^-3 mol/L", "pH = 3")]
        [InlineData("c(OH-) = 10^-4 mol/L", "pOH = 4")]
        [InlineData("c(OH-) = 10^-4 mol/L", "pH = 10")]
        [InlineData("Ksp", "K_{sp}")]
        [InlineData("Ka", "K_{a}")]
        [InlineData("Kb", "K_{b}")]
        [InlineData("Kw", "K_{w}")]
        [InlineData("1.0 * 10^-14", "10^-14")]
        [InlineData("1*10^-14", "1.0x10^-14")]
        [InlineData("pH = 7", "中性")]
        public void PracticeService_CheckChemicalPhAndElectrolyteEquivalence_ShouldRecognizeEquivalences(string user, string correct)
        {
            var match1 = PracticeService.CheckChemicalPhAndElectrolyteEquivalence(user, correct);
            var match2 = PracticeService.CheckChemicalPhAndElectrolyteEquivalence(correct, user);
            var fullMatch = PracticeService.CheckFillInBlankMatch(user, correct);

            Assert.True(match1, $"用户答案 [{user}] 与标准答案 [{correct}] 应当判定化学酸碱平衡科学等价");
            Assert.True(match2, $"双向对称性成立：[{correct}] <=> [{user}]");
            Assert.True(fullMatch, "应当在综合填空判卷路由 CheckFillInBlankMatch 中正确通过");
        }
        #endregion

        #region 3. 用户体验专家：STEM 智能判卷 - 物理复合单位与科学换算等价引擎
        [Theory]
        [InlineData("10 m/s", "36 km/h")]
        [InlineData("20 m/s", "72 km/h")]
        [InlineData("5 m/s", "18 km/h")]
        [InlineData("36 km/h", "10 m/s")]
        [InlineData("1 g/cm^3", "1000 kg/m^3")]
        [InlineData("1 g/cm³", "1000 kg/m³")]
        [InlineData("0.8 g/cm³", "800 kg/m³")]
        [InlineData("1.2 g/cm3", "1200 kg/m3")]
        [InlineData("1 kW·h", "3.6 * 10^6 J")]
        [InlineData("1 kWh", "3600000 J")]
        [InlineData("1 kW*h", "1度")]
        [InlineData("1 度电", "3.6 MJ")]
        [InlineData("2 kWh", "7.2 MJ")]
        [InlineData("9.8 m/s^2", "9.8 N/kg")]
        [InlineData("10 m/s^2", "10 N/kg")]
        public void PracticeService_CheckPhysicalCompoundUnitAndConversionEquivalence_ShouldRecognizeEquivalences(string user, string correct)
        {
            var match1 = PracticeService.CheckPhysicalCompoundUnitAndConversionEquivalence(user, correct);
            var match2 = PracticeService.CheckPhysicalCompoundUnitAndConversionEquivalence(correct, user);
            var fullMatch = PracticeService.CheckFillInBlankMatch(user, correct);

            Assert.True(match1, $"用户答案 [{user}] 与标准答案 [{correct}] 应当判定物理复合单位科学等价");
            Assert.True(match2, $"双向对称性成立：[{correct}] <=> [{user}]");
            Assert.True(fullMatch, "应当在综合填空判卷路由 CheckFillInBlankMatch 中正确通过");
        }
        #endregion

        #region 4. 用户体验专家：STEM 智能判卷 - 立体几何与几何公式符号等价引擎
        [Theory]
        [InlineData("V = \\frac{4}{3}\\pi r^3", "4/3 pi R^3")]
        [InlineData("4/3 * pi * r^3", "4/3pir^3")]
        [InlineData("S = 4\\pi r^2", "4 pi R^2")]
        [InlineData("4pir^2", "4*pi*r^2")]
        [InlineData("S = \\pi r^2", "pi R^2")]
        [InlineData("C = 2\\pi r", "2 pi R")]
        [InlineData("V = \\pi r^2 h", "V = Sh")]
        [InlineData("V = \\frac{1}{3}\\pi r^2 h", "V = 1/3 Sh")]
        [InlineData("1/3pir^2h", "1/3 Sh")]
        public void PracticeService_CheckGeometricFormulaEquivalence_ShouldRecognizeEquivalences(string user, string correct)
        {
            var match1 = PracticeService.CheckGeometricFormulaEquivalence(user, correct);
            var match2 = PracticeService.CheckGeometricFormulaEquivalence(correct, user);
            var fullMatch = PracticeService.CheckFillInBlankMatch(user, correct);

            Assert.True(match1, $"用户答案 [{user}] 与标准答案 [{correct}] 应当判定几何公式等价");
            Assert.True(match2, $"双向对称性成立：[{correct}] <=> [{user}]");
            Assert.True(fullMatch, "应当在综合填空判卷路由 CheckFillInBlankMatch 中正确通过");
        }
        #endregion

        #region 5. 用户体验专家：作答提交与等价原因反馈及作答用时端到端闭环测试
        [Fact]
        public async Task PracticeService_SubmitAnswerAsync_ChemicalPhEquivalence_ShouldReturnTrueWithDetailedReason()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var question = new Question
                {
                    Subject = "化学",
                    Category = "溶液的酸碱性",
                    Stem = "25℃ 时，某稀盐酸溶液中氢离子浓度为 10^-3 mol/L，该溶液的 pOH 为________。",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "pOH = 11",
                    StandardAnalysis = "由 c(H+) = 10^-3 mol/L 可得 pH = 3，常温下 pH + pOH = 14，故 pOH = 11。"
                };

                // 用户直接输入等价的 pH = 3
                var result = await practiceService.SubmitAnswerAsync(question, "pH = 3", timeTakenSeconds: 8, currentCombo: 0);

                Assert.True(result.IsCorrect, "等价答案应当判定为正确");
                Assert.True(result.IsEquivalentMatch, "应当标记为智能等价认可");
                Assert.Contains("化学溶液与酸碱平衡等价", result.EquivalentMatchReason);
                Assert.Equal(8, result.TimeTakenSeconds);
            }
        }

        [Fact]
        public async Task PracticeService_SubmitAnswerAsync_PhysicsSpeedEquivalence_ShouldReturnTrueWithDetailedReason()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var question = new Question
                {
                    Subject = "物理",
                    Category = "匀速直线运动",
                    Stem = "一辆汽车在公路上匀速行驶，速度为________。",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "36 km/h",
                    StandardAnalysis = "36 km/h = 10 m/s。"
                };

                // 用户输入 10 m/s
                var result = await practiceService.SubmitAnswerAsync(question, "10 m/s", timeTakenSeconds: 15, currentCombo: 0);

                Assert.True(result.IsCorrect);
                Assert.True(result.IsEquivalentMatch);
                Assert.Contains("物理复合单位与常用度量换算等价", result.EquivalentMatchReason);
                Assert.Equal(15, result.TimeTakenSeconds);
            }
        }

        [Fact]
        public async Task PracticeService_SubmitAnswerAsync_GeometricSphereFormulaEquivalence_ShouldReturnTrueWithDetailedReason()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var question = new Question
                {
                    Subject = "数学",
                    Category = "立体几何",
                    Stem = "半径为 r 的球体体积计算公式为 V = ________。",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "V = 4/3 \\pi r^3",
                    StandardAnalysis = "球体体积公式为 V = 4/3 * pi * r^3。"
                };

                // 用户输入大写字母且无斜杠的 4/3 pi R^3
                var result = await practiceService.SubmitAnswerAsync(question, "4/3 pi R^3", timeTakenSeconds: 22, currentCombo: 0);

                Assert.True(result.IsCorrect);
                Assert.True(result.IsEquivalentMatch);
                Assert.Contains("立体几何核心形体公式等价", result.EquivalentMatchReason);
                Assert.Equal(22, result.TimeTakenSeconds);
            }
        }
        #endregion
    }
}
