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
    public class ArchitectAndUxAscendanceZenithEvolutionTests
    {
        #region 1. 系统架构师：多选题与填空/简答题领域不变量精准审计与全自动自愈闭环
        [Fact]
        public async Task SystemHealthService_AuditAndHealMalformedMultipleChoiceQuestions_RestoresCanonicalInvariants()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var healthService = new SystemHealthService(context, null);

                // 准备测试数据：包含规范与各种非规范格式的多选题
                var canonicalMultipleChoice = new Question
                {
                    Subject = "化学",
                    Category = "多选常规",
                    Stem = "下列属于碱性氧化物的是？",
                    Type = QuestionType.MultipleChoice,
                    OptionsJson = "[\"Na2O\",\"CaO\",\"SO2\",\"CO2\"]",
                    CorrectAnswer = "AB"
                };

                var malformedUnsortedCommas = new Question
                {
                    Subject = "物理",
                    Category = "力学多选",
                    Stem = "关于匀变速直线运动，下列说法正确的有？",
                    Type = QuestionType.MultipleChoice,
                    OptionsJson = "[\"选项A\",\"选项B\",\"选项C\",\"选项D\"]",
                    CorrectAnswer = " a,b " // 未去除首尾空格且小写
                };

                var malformedLowercaseUnsorted = new Question
                {
                    Subject = "生物",
                    Category = "遗传多选",
                    Stem = "下列哪些是孟德尔遗传规律的适用条件？",
                    Type = QuestionType.MultipleChoice,
                    OptionsJson = "[\"选项A\",\"选项B\",\"选项C\",\"选项D\"]",
                    CorrectAnswer = "c,b,a" // 小写
                };

                var malformedDuplicatesAndDelimiters = new Question
                {
                    Subject = "地理",
                    Category = "自然地理",
                    Stem = "下列属于季风气候成因的有？",
                    Type = QuestionType.MultipleChoice,
                    OptionsJson = "[\"选项A\",\"选项B\",\"选项C\",\"选项D\"]",
                    CorrectAnswer = "A、B、D。" // 句号污染
                };

                var malformedBracketedOptions = new Question
                {
                    Subject = "数学",
                    Category = "函数多选",
                    Stem = "关于二次函数的对称性，下列结论正确的有？",
                    Type = QuestionType.MultipleChoice,
                    OptionsJson = "[]", // 破损空选项
                    CorrectAnswer = "[\"A\", \"C\"]" // JSON 数组格式
                };

                context.Questions.AddRange(
                    canonicalMultipleChoice,
                    malformedUnsortedCommas,
                    malformedLowercaseUnsorted,
                    malformedDuplicatesAndDelimiters,
                    malformedBracketedOptions);
                await context.SaveChangesAsync();

                // 阶段一：审计检出
                var auditBefore = await healthService.AuditDataIntegrityAsync();
                Assert.True(auditBefore.TotalMalformedMultipleChoiceQuestions >= 4,
                    $"应检出至少 4 道非规范多选题，实际检出: {auditBefore.TotalMalformedMultipleChoiceQuestions}");
                Assert.False(auditBefore.IsStrictlyHealthy);

                // 阶段二：全量自愈
                var healedCount = await healthService.HealCorruptedQuestionsAsync();
                Assert.True(healedCount >= 4, $"应自愈修复至少 4 道多选题，实际自愈数: {healedCount}");

                // 阶段三：审计收敛验证
                var auditAfter = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, auditAfter.TotalMalformedMultipleChoiceQuestions);

                // 验证具体数据项规范性
                var q1 = await context.Questions.FindAsync(malformedUnsortedCommas.Id);
                Assert.NotNull(q1);
                Assert.Equal("A,B", q1.CorrectAnswer);

                var q2 = await context.Questions.FindAsync(malformedLowercaseUnsorted.Id);
                Assert.NotNull(q2);
                Assert.Equal("C,B,A", q2.CorrectAnswer);

                var q3 = await context.Questions.FindAsync(malformedDuplicatesAndDelimiters.Id);
                Assert.NotNull(q3);
                Assert.Equal("A、B、D", q3.CorrectAnswer);

                var q4 = await context.Questions.FindAsync(malformedBracketedOptions.Id);
                Assert.NotNull(q4);
                Assert.Equal("A,C", q4.CorrectAnswer);
                Assert.NotEqual("[]", q4.OptionsJson);
            }
        }

        [Fact]
        public async Task SystemHealthService_AuditAndHealMalformedFillInAndSubjectiveQuestions_CleansOptionsAndQuotes()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var healthService = new SystemHealthService(context, null);

                var malformedOptionsOnFillIn = new Question
                {
                    Subject = "语文",
                    Category = "诗词填空",
                    Stem = "床前明月光，疑是地上霜。举头望明月，________。",
                    Type = QuestionType.FillInBlank,
                    OptionsJson = "[\"低头思故乡\",\"低头看地上\"]", // 填空题误残留了选项
                    CorrectAnswer = "低头思故乡"
                };

                var malformedChineseQuotes = new Question
                {
                    Subject = "生物",
                    Category = "概念填空",
                    Stem = "植物利用光能将水和二氧化碳转化为有机物的过程称为________。",
                    Type = QuestionType.FillInBlank,
                    OptionsJson = "[]",
                    CorrectAnswer = "“光合作用”" // 被中文双引号包裹
                };

                var malformedEnglishQuotes = new Question
                {
                    Subject = "数学",
                    Category = "数值填空",
                    Stem = "圆周率精确到小数点后两位的值为________。",
                    Type = QuestionType.FillInBlank,
                    OptionsJson = "[]",
                    CorrectAnswer = "\"3.14\"" // 被英文双引号包裹
                };

                var malformedBracketsOnShortAnswer = new Question
                {
                    Subject = "物理",
                    Category = "简答题",
                    Stem = "请简述动量守恒定律的核心适用条件。",
                    Type = QuestionType.ShortAnswer,
                    OptionsJson = "[]",
                    CorrectAnswer = "【合外力为零或系统内力远大于外力】" // 被中文方括号包裹
                };

                context.Questions.AddRange(
                    malformedOptionsOnFillIn,
                    malformedChineseQuotes,
                    malformedEnglishQuotes,
                    malformedBracketsOnShortAnswer);
                await context.SaveChangesAsync();

                // 阶段一：审计检测
                var auditBefore = await healthService.AuditDataIntegrityAsync();
                Assert.True(auditBefore.TotalMalformedFillInQuestions >= 4,
                    $"应检出至少 4 道非规范填空/简答题，实际检出: {auditBefore.TotalMalformedFillInQuestions}");
                Assert.False(auditBefore.IsStrictlyHealthy);

                // 阶段二：自愈修复
                var healedCount = await healthService.HealCorruptedQuestionsAsync();
                Assert.True(healedCount >= 4, $"应自愈修复至少 4 道填空/主观题，实际自愈数: {healedCount}");

                // 阶段三：审计复测
                var auditAfter = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, auditAfter.TotalMalformedFillInQuestions);

                // 验证具体数据项格式
                var q1 = await context.Questions.FindAsync(malformedOptionsOnFillIn.Id);
                Assert.NotNull(q1);
                Assert.Equal("[]", q1.OptionsJson);
                Assert.Equal("低头思故乡", q1.CorrectAnswer);

                var q2 = await context.Questions.FindAsync(malformedChineseQuotes.Id);
                Assert.NotNull(q2);
                Assert.Equal("光合作用", q2.CorrectAnswer);

                var q3 = await context.Questions.FindAsync(malformedEnglishQuotes.Id);
                Assert.NotNull(q3);
                Assert.Equal("3.14", q3.CorrectAnswer);

                var q4 = await context.Questions.FindAsync(malformedBracketsOnShortAnswer.Id);
                Assert.NotNull(q4);
                Assert.Equal("合外力为零或系统内力远大于外力", q4.CorrectAnswer);
            }
        }
        #endregion

        #region 2. 用户体验专家：热力学温标与气体压强等价引擎
        [Theory]
        [InlineData("0℃", "273.15 K")]
        [InlineData("0°C", "273 K")]
        [InlineData("25℃", "298.15 K")]
        [InlineData("25 celsius", "298.15K")]
        [InlineData("100℃", "373.15 K")]
        [InlineData("-273.15℃", "0 K")]
        [InlineData("273.15K", "0℃")]
        [InlineData("298.15K", "25°C")]
        [InlineData("1 atm", "101.3 kPa")]
        [InlineData("1 atm", "760 mmHg")]
        [InlineData("1 atm", "760 Torr")]
        [InlineData("1 atm", "1.013*10^5 Pa")]
        [InlineData("101.3 kPa", "760 mmHg")]
        [InlineData("101325 Pa", "1 atm")]
        [InlineData("标准大气压", "101.3 kPa")]
        public void PracticeService_ThermodynamicTemperatureAndPressureEquivalence_RecognizedSuccessfully(string user, string correct)
        {
            bool isEquiv = PracticeService.CheckThermodynamicAndPressureEquivalence(user, correct);
            Assert.True(isEquiv, $"[{user}] 与 [{correct}] 应被判定为热力学温标或气体压强等价。");
        }
        #endregion

        #region 3. 用户体验专家：近代物理与通用科学基本物理常数等价引擎
        [Theory]
        // 真空中光速 c
        [InlineData("c", "3*10^8 m/s")]
        [InlineData("3*10^8 m/s", "3.0*10^8 m/s")]
        [InlineData("3e8 m/s", "3*10^8 m/s")]
        [InlineData("c", "3*10^5 km/s")]
        [InlineData("c", "300000 km/s")]
        [InlineData("光速", "3*10^8 m/s")]
        // 普朗克常量 h
        [InlineData("h", "6.626*10^-34 J*s")]
        [InlineData("h", "6.63*10^-34 J*s")]
        [InlineData("6.626*10^-34 J·s", "6.63*10^-34 J*s")]
        [InlineData("普朗克常量", "6.626*10^-34 J*s")]
        [InlineData("普朗克常数", "h")]
        // 基本元电荷 e
        [InlineData("e", "1.6*10^-19 C")]
        [InlineData("e", "1.60*10^-19 C")]
        [InlineData("1.6*10^-19 C", "1.602*10^-19 C")]
        [InlineData("元电荷", "1.6*10^-19 C")]
        [InlineData("基本电荷", "e")]
        public void PracticeService_FundamentalPhysicalConstantsEquivalence_RecognizedSuccessfully(string user, string correct)
        {
            bool isEquiv = PracticeService.CheckFundamentalPhysicalConstantsEquivalence(user, correct);
            Assert.True(isEquiv, $"[{user}] 与 [{correct}] 应被判定为基本物理常数等价。");
        }
        #endregion

        #region 4. 用户体验专家：高阶对数特殊值与实数集区间/集合等价引擎
        [Theory]
        // 特殊对数值
        [InlineData("ln(e)", "1")]
        [InlineData("ln(1)", "0")]
        [InlineData("lg(10)", "1")]
        [InlineData("lg(100)", "2")]
        [InlineData("lg(1000)", "3")]
        [InlineData("lg(1)", "0")]
        [InlineData("log2(2)", "1")]
        [InlineData("log2(4)", "2")]
        [InlineData("log2(8)", "3")]
        [InlineData("log2(16)", "4")]
        [InlineData("log2(1)", "0")]
        // 实数集与区间等价
        [InlineData("(-∞, +∞)", "R")]
        [InlineData("(-inf, +inf)", "R")]
        [InlineData("(-∞, ∞)", "\\mathbb{R}")]
        [InlineData("(-inf, inf)", "\\mathbf{R}")]
        [InlineData("实数集", "(-∞, +∞)")]
        [InlineData("全体实数", "(-∞, +∞)")]
        // 空集等价
        [InlineData("∅", "空集")]
        [InlineData("\\emptyset", "∅")]
        [InlineData("{}", "空集")]
        public void PracticeService_LogarithmAndIntervalNotationEquivalence_RecognizedSuccessfully(string user, string correct)
        {
            bool isEquiv = PracticeService.CheckLogarithmAndIntervalNotationEquivalence(user, correct);
            Assert.True(isEquiv, $"[{user}] 与 [{correct}] 应被判定为对数特殊值或实数集/空集记法等价。");
        }
        #endregion

        #region 5. 综合端到端判卷与等价原因反馈测试
        [Fact]
        public async Task PracticeService_SubmitAnswerAsync_EndToEndSTEMIntegration()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                // 1. 测试热力学温标判卷
                var qTemp = new Question
                {
                    Stem = "室温下理想气体状态初温为：",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "298.15 K"
                };
                var resultTemp = await practiceService.SubmitAnswerAsync(qTemp, "25℃", 5, 0);
                Assert.True(resultTemp.IsCorrect);
                Assert.True(resultTemp.IsEquivalentMatch);
                Assert.Contains("热力学温标等价", resultTemp.EquivalentMatchReason);

                // 2. 测试基本常数判卷
                var qConst = new Question
                {
                    Stem = "真空中电磁波的传播速度：",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "3*10^8 m/s"
                };
                var resultConst = await practiceService.SubmitAnswerAsync(qConst, "c", 5, 0);
                Assert.True(resultConst.IsCorrect);
                Assert.True(resultConst.IsEquivalentMatch);
                Assert.Contains("基本物理常数等价", resultConst.EquivalentMatchReason);

                // 3. 测试对数与实数集判卷
                var qMath = new Question
                {
                    Stem = "函数 f(x)=e^x 的定义域为：",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "(-∞, +∞)"
                };
                var resultMath = await practiceService.SubmitAnswerAsync(qMath, "R", 5, 0);
                Assert.True(resultMath.IsCorrect);
                Assert.True(resultMath.IsEquivalentMatch);
                Assert.Contains("全实数集合域等价", resultMath.EquivalentMatchReason);
            }
        }
        #endregion
    }
}
