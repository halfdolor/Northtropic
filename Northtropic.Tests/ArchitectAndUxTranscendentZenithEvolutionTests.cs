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
    public class ArchitectAndUxTranscendentZenithEvolutionTests
    {
        #region 1. 系统架构师：判断题数据不变量全量审计与自愈收敛
        [Fact]
        public async Task SystemHealthService_AuditAndHealMalformedJudgementQuestions_RestoresCanonicalInvariants()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var healthService = new SystemHealthService(context, null);

                // 准备测试数据：包含规范的和损坏的单选/填空判断题
                var goodChoiceJudgement = new Question
                {
                    Subject = "科学",
                    Category = "物理判断",
                    Stem = "声音在真空中无法传播。",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"正确\",\"错误\"]",
                    CorrectAnswer = "正确"
                };

                var malformedChoiceJudgement = new Question
                {
                    Subject = "生物",
                    Category = "概念判断题",
                    Stem = "线粒体是细胞的有氧呼吸主要场所。（对/错）",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"对\",\"错\"]",
                    CorrectAnswer = "对"
                };

                var malformedFillInJudgement = new Question
                {
                    Subject = "物理",
                    Category = "常识判断",
                    Stem = "光在真空中沿直线传播。",
                    Type = QuestionType.FillInBlank,
                    OptionsJson = "[]",
                    CorrectAnswer = "√"
                };

                var malformedBrokenOptionsJudgement = new Question
                {
                    Subject = "化学",
                    Category = "沉淀判断",
                    Stem = "氯化银是难溶于水的白色沉淀。",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[]", // 损坏的空选项
                    CorrectAnswer = "T"
                };

                context.Questions.AddRange(goodChoiceJudgement, malformedChoiceJudgement, malformedFillInJudgement, malformedBrokenOptionsJudgement);
                await context.SaveChangesAsync();

                // 阶段一：审计检测
                var auditBefore = await healthService.AuditDataIntegrityAsync();
                Assert.True(auditBefore.TotalMalformedJudgementQuestions >= 3, $"应检出至少 3 道格式不规范的判断题，实际检出: {auditBefore.TotalMalformedJudgementQuestions}");
                Assert.False(auditBefore.IsStrictlyHealthy);

                // 阶段二：自愈修复
                var healedCount = await healthService.HealCorruptedQuestionsAsync();
                Assert.True(healedCount >= 3, $"应自愈修复至少 3 道题目，实际修复: {healedCount}");

                // 阶段三：复测收敛
                var auditAfter = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, auditAfter.TotalMalformedJudgementQuestions);

                // 验证具体题目不变量规范性
                var q1 = await context.Questions.FindAsync(malformedChoiceJudgement.Id);
                Assert.NotNull(q1);
                Assert.Equal("[\"正确\",\"错误\"]", q1.OptionsJson);
                Assert.Equal("正确", q1.CorrectAnswer);

                var q2 = await context.Questions.FindAsync(malformedFillInJudgement.Id);
                Assert.NotNull(q2);
                Assert.Equal("正确", q2.CorrectAnswer);

                var q3 = await context.Questions.FindAsync(malformedBrokenOptionsJudgement.Id);
                Assert.NotNull(q3);
                Assert.Equal("[\"正确\",\"错误\"]", q3.OptionsJson);
                Assert.Equal("正确", q3.CorrectAnswer);
            }
        }
        #endregion

        #region 2. 用户体验与科学智能：高阶三角函数特殊角与弧度制双向等价
        [Theory]
        [InlineData("0.5", "1/2")]
        [InlineData("0.5", "\\frac{1}{2}")]
        [InlineData("0.5", "sin(30°)")]
        [InlineData("1/2", "sin(pi/6)")]
        [InlineData("1/2", "cos(60°)")]
        [InlineData("0.5", "cos(pi/3)")]
        [InlineData("\\frac{\\sqrt{2}}{2}", "1/\\sqrt{2}")]
        [InlineData("\\frac{\\sqrt{2}}{2}", "sin(45°)")]
        [InlineData("\\frac{\\sqrt{2}}{2}", "cos(45°)")]
        [InlineData("\\frac{\\sqrt{2}}{2}", "sin(pi/4)")]
        [InlineData("\\frac{\\sqrt{3}}{2}", "sin(60°)")]
        [InlineData("\\frac{\\sqrt{3}}{2}", "cos(30°)")]
        [InlineData("\\frac{\\sqrt{3}}{2}", "cos(pi/6)")]
        [InlineData("\\sqrt{3}", "tan(60°)")]
        [InlineData("\\sqrt{3}", "tan(pi/3)")]
        [InlineData("\\frac{\\sqrt{3}}{3}", "1/\\sqrt{3}")]
        [InlineData("\\frac{\\sqrt{3}}{3}", "tan(30°)")]
        [InlineData("\\frac{\\sqrt{3}}{3}", "tan(pi/6)")]
        [InlineData("1", "tan(45°)")]
        [InlineData("1", "sin(90°)")]
        [InlineData("1", "cos(0°)")]
        [InlineData("0", "sin(0°)")]
        [InlineData("0", "cos(90°)")]
        public async Task PracticeService_CheckFillInBlank_ShouldRecognizeTrigonometricSpecialValueEquivalence(string userAnswer, string standardAnswer)
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var question = new Question
                {
                    Stem = "计算三角函数特殊角的值：",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = standardAnswer
                };

                var result = await practiceService.SubmitAnswerAsync(question, userAnswer, 5, 0);
                Assert.True(result.IsCorrect, $"考生作答 '{userAnswer}' 对标准答案 '{standardAnswer}' 应判定为等价正确！");
                Assert.True(result.IsEquivalentMatch);
                Assert.True(result.EquivalentMatchReason?.Contains("特殊角") == true || result.EquivalentMatchReason?.Contains("分母有理化") == true || result.EquivalentMatchReason?.Contains("数值运算等价") == true, $"实际等价原因: {result.EquivalentMatchReason}");
            }
        }
        #endregion

        #region 3. 用户体验与科学智能：现代物理 eV 与国际焦耳 J 等效换算
        [Theory]
        [InlineData("1 eV", "1.6×10^-19 J")]
        [InlineData("1eV", "1.6e-19 J")]
        [InlineData("1.6×10^{-19} J", "1 eV")]
        [InlineData("1 keV", "1000 eV")]
        [InlineData("1 keV", "1.6×10^-16 J")]
        [InlineData("1 MeV", "10^6 eV")]
        [InlineData("1 MeV", "1.6×10^-13 J")]
        [InlineData("1 GeV", "10^9 eV")]
        [InlineData("1 GeV", "1.6×10^-10 J")]
        public async Task PracticeService_CheckFillInBlank_ShouldRecognizeModernPhysicsElectronVoltEquivalence(string userAnswer, string standardAnswer)
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var question = new Question
                {
                    Stem = "求光电效应中逸出功或光子能量：",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = standardAnswer
                };

                var result = await practiceService.SubmitAnswerAsync(question, userAnswer, 5, 0);
                Assert.True(result.IsCorrect, $"作答 '{userAnswer}' 对标准答案 '{standardAnswer}' 应判定为现代物理能级等价正确！");
                Assert.True(result.IsEquivalentMatch);
                Assert.Contains("电子伏特", result.EquivalentMatchReason);
            }
        }
        #endregion

        #region 4. 用户体验与科学智能：电磁学物理量单位等价换算 (Wb, T, Gs, H)
        [Theory]
        [InlineData("1 Wb", "1 T·m²")]
        [InlineData("1 Wb", "1 V·s")]
        [InlineData("1 T·m²", "1 V·s")]
        [InlineData("1 T", "10^4 Gs")]
        [InlineData("1 T", "10000 Gs")]
        [InlineData("10000 高斯", "1 特斯拉")]
        [InlineData("1 H", "1 Wb/A")]
        [InlineData("1 H", "1 Ω·s")]
        [InlineData("1 H", "1 V·s/A")]
        public async Task PracticeService_CheckFillInBlank_ShouldRecognizeElectromagnetismUnitEquivalence(string userAnswer, string standardAnswer)
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var question = new Question
                {
                    Stem = "电磁感应与磁场参数单位换算：",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = standardAnswer
                };

                var result = await practiceService.SubmitAnswerAsync(question, userAnswer, 5, 0);
                Assert.True(result.IsCorrect, $"作答 '{userAnswer}' 对标准答案 '{standardAnswer}' 应判定为电磁学单位等价正确！");
                Assert.True(result.IsEquivalentMatch);
                Assert.True(result.EquivalentMatchReason?.Contains("电磁学") == true || result.EquivalentMatchReason?.Contains("等价") == true, $"实际等价原因: {result.EquivalentMatchReason}");
            }
        }
        #endregion

        #region 5. 用户体验与科学智能：物理化学常数等效换算 (法拉第常数 F、阿伏伽德罗常数、标况气体摩尔体积)
        [Theory]
        [InlineData("1 F", "96485 C/mol")]
        [InlineData("1 F", "96500 C/mol")]
        [InlineData("96500 C/mol", "1 法拉第常数")]
        [InlineData("6.02×10^23 /mol", "6.022×10^23 mol^-1")]
        [InlineData("6.02e23", "6.022×10^{23}")]
        [InlineData("22.4 L/mol", "0.0224 m³/mol")]
        [InlineData("22.4 升/摩尔", "0.0224 立方米/摩尔")]
        public async Task PracticeService_CheckFillInBlank_ShouldRecognizePhysicalChemistryConstantsEquivalence(string userAnswer, string standardAnswer)
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var question = new Question
                {
                    Stem = "电化学与化学计量计算：",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = standardAnswer
                };

                var result = await practiceService.SubmitAnswerAsync(question, userAnswer, 5, 0);
                Assert.True(result.IsCorrect, $"作答 '{userAnswer}' 对标准答案 '{standardAnswer}' 应判定为物化常数等价正确！");
                Assert.True(result.IsEquivalentMatch);
                Assert.True(result.EquivalentMatchReason?.Contains("物理化学常量") == true || result.EquivalentMatchReason?.Contains("理化复合单位") == true, $"实际等价原因: {result.EquivalentMatchReason}");
            }
        }
        #endregion
    }
}
