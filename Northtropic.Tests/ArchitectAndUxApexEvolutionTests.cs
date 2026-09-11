using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxApexEvolutionTests
    {
        [Fact]
        public void CheckFillInBlankMatch_ChineseCommaAndWhitespaceDelimiters_MatchesCorrectly()
        {
            // 1. 中文顿号分隔符与分号/逗号互认
            Assert.True(PracticeService.CheckFillInBlankMatch("光合作用、蒸腾作用", "光合作用；蒸腾作用"));
            Assert.True(PracticeService.CheckFillInBlankMatch("光合作用；蒸腾作用", "光合作用、蒸腾作用"));
            Assert.True(PracticeService.CheckFillInBlankMatch("4、5", "4; 5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2、-3", "2; -3"));

            // 2. 中文词语之间使用多空格或换行分隔
            Assert.True(PracticeService.CheckFillInBlankMatch("光合作用  蒸腾作用", "光合作用；蒸腾作用"));
            Assert.True(PracticeService.CheckFillInBlankMatch("光合作用\t蒸腾作用", "光合作用；蒸腾作用"));

            // 3. 明确多空编号时，空白项缺失或多填应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("(1) 光合作用", "(1) 光合作用 (2) 蒸腾作用"));
            Assert.False(PracticeService.CheckFillInBlankMatch("(1) 光合作用 (2) 蒸腾作用 (3) 呼吸作用", "(1) 光合作用 (2) 蒸腾作用"));
        }

        [Fact]
        public void CheckFillInBlankMatch_PlusMinusNotation_MatchesCorrectly()
        {
            // 1. ± 与 +- 标准化互认
            Assert.True(PracticeService.CheckFillInBlankMatch("±2", "+-2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("+-2", "±2"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\pm 2", "±2"));

            // 2. ± 与 “或” / 逗号枚举候选展开
            Assert.True(PracticeService.CheckFillInBlankMatch("±3", "3或-3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("±3", "-3或3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("±3", "3,-3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("±3", "-3,3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("±3", "3和-3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("±3", "-3和3"));

            // 3. 反向匹配 (用户输入 3或-3，标准答案为 ±3)
            Assert.True(PracticeService.CheckFillInBlankMatch("3或-3", "±3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("-3或3", "±3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("3,-3", "±3"));

            // 4. 不符数值应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("±3", "4或-4"));
            Assert.False(PracticeService.CheckFillInBlankMatch("+3", "±3"));
        }

        [Fact]
        public void CheckFillInBlankMatch_NegativeAndParenthesizedFractions_MatchesCorrectly()
        {
            // 1. 负分数与小数
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\frac{-1}{2}", "-0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"-\frac{1}{2}", "-0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(-1)/2", "-0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("-(1/2)", "-0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("-1/2", "-0.5"));

            // 2. 双重负号分数化简为正
            Assert.True(PracticeService.CheckFillInBlankMatch("(-3)/(-4)", "0.75"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\frac{-3}{-4}", "0.75"));

            // 3. 负分数反向匹配 (用户输入 -0.75，标准答案为 \frac{-3}{4})
            Assert.True(PracticeService.CheckFillInBlankMatch("-0.75", @"\frac{-3}{4}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("-0.75", @"-\frac{3}{4}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("-0.75", "(-3)/4"));

            // 4. 正负相反应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("0.5", @"-\frac{1}{2}"));
            Assert.False(PracticeService.CheckFillInBlankMatch("-0.5", @"\frac{1}{2}"));
        }

        [Fact]
        public void CheckFillInBlankMatch_SetBuilderAndAngleDegreeNotation_MatchesCorrectly()
        {
            // 1. 集合构建符号 {x | ...} vs {x : ...} vs \{x \mid ...\}
            Assert.True(PracticeService.CheckFillInBlankMatch("{x | x > 2}", "{x : x > 2}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("{x|x>2}", "{x : x > 2}"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\{x \mid x > 2\}", "{x | x > 2}"));

            // 2. 角度与度数表达: 30°, 30^\circ, 30 deg, 30度
            Assert.True(PracticeService.CheckFillInBlankMatch("30°", "30度"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"30^\circ", "30°"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"30^{\circ}", "30°"));
            Assert.True(PracticeService.CheckFillInBlankMatch("30 deg", "30°"));
            Assert.True(PracticeService.CheckFillInBlankMatch("45°", "45 deg"));

            // 3. 几何角符号 \angle vs ∠
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\angle A", "∠A"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\angle ABC = 60°", "∠ABC=60度"));
        }

        [Fact]
        public async Task SubmitAnswerAsync_EquivalentMatch_SetsFlagCorrectly()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var dummyUser = new User { Id = Guid.NewGuid(), Username = "apex_tester", Grade = "初中三年级" };
                context.Users.Add(dummyUser);
                await context.SaveChangesAsync();

                var session = new FakeUserSessionService { ActiveUser = dummyUser };
                var gamification = new FakeGamificationService();
                var aiTutor = new FakeAiTutorService();
                var practiceService = new PracticeService(context, gamification, session, aiTutor);

                var q = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "数学",
                    Category = "一元二次方程",
                    Type = QuestionType.FillInBlank,
                    Stem = "解方程 $x^2 = 4$ 的解为：",
                    CorrectAnswer = "2或-2",
                    Difficulty = 2,
                    BaseExpReward = 20
                };

                // 1. 用户输入等价但非字面完全相同的表达 (±2)
                var resultEquiv = await practiceService.SubmitAnswerAsync(q, "±2", 10, 0, dummyUser.Id);
                Assert.True(resultEquiv.IsCorrect);
                Assert.True(resultEquiv.IsEquivalentMatch); // 触发智能等价认可标记

                // 2. 用户输入与标准答案字面完全一致的表达 (2或-2)
                var resultExact = await practiceService.SubmitAnswerAsync(q, "2或-2", 10, 0, dummyUser.Id);
                Assert.True(resultExact.IsCorrect);
                Assert.False(resultExact.IsEquivalentMatch); // 字面完全一致，无需降级等价提示
            }
        }

        [Fact]
        public async Task QuestionImportService_BatchCsvDeduplication_EliminatesDuplicatesInMemory()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var existingQ = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "数学",
                    Category = "代数",
                    GradeTarget = "七年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "这是数据库中已经存在的数学题目A",
                    OptionsJson = "[\"A. 1\", \"B. 2\"]",
                    CorrectAnswer = "A",
                    Difficulty = 2,
                    BaseExpReward = 20
                };
                context.Questions.Add(existingQ);
                await context.SaveChangesAsync();

                var importService = new QuestionImportService(context);

                var csvContent = new StringBuilder();
                csvContent.AppendLine("学科,考点分类,适学年级,题型,题干,选项,正确答案,标准解析");
                // 第 1 题: 与数据库已有题目重复
                csvContent.AppendLine("数学,代数,七年级,单选题,这是数据库中已经存在的数学题目A,\"A. 1|B. 2\",A,解析A");
                // 第 2 题: 全新题目
                csvContent.AppendLine("数学,代数,七年级,单选题,这是全新导入的数学题目B,\"A. 3|B. 4\",B,解析B");
                // 第 3 题: 与同批次第 2 题重复
                csvContent.AppendLine("数学,代数,七年级,单选题,这是全新导入的数学题目B,\"A. 3|B. 4\",B,解析B");

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvContent.ToString()));
                var result = await importService.ParseAndImportQuestionsByFileFormatAsync(stream, "test_batch.csv", Guid.NewGuid());

                Assert.Equal(1, result.SuccessCount);
                Assert.Equal(2, result.DuplicateCount);
                Assert.Equal(0, result.FailureCount);

                // 验证数据库最终只有 2 道不重复的题目
                var allStems = await context.Questions.Select(q => q.Stem).ToListAsync();
                Assert.Equal(2, allStems.Count);
                Assert.Contains("这是数据库中已经存在的数学题目A", allStems);
                Assert.Contains("这是全新导入的数学题目B", allStems);
            }
        }

        [Fact]
        public async Task QuestionImportService_BatchJsonDeduplication_EliminatesDuplicatesInMemory()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var existingQ = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "物理",
                    Category = "力学",
                    GradeTarget = "八年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "这是物理已有题目P1",
                    OptionsJson = "[\"A. 10N\", \"B. 20N\"]",
                    CorrectAnswer = "A"
                };
                context.Questions.Add(existingQ);
                await context.SaveChangesAsync();

                var importService = new QuestionImportService(context);

                string jsonContent = @"[
                    {
                        ""subject"": ""物理"",
                        ""category"": ""力学"",
                        ""gradeTarget"": ""八年级"",
                        ""type"": ""单选题"",
                        ""stem"": ""这是物理已有题目P1"",
                        ""options"": [""A. 10N"", ""B. 20N""],
                        ""correctAnswer"": ""A"",
                        ""standardAnalysis"": ""分析1""
                    },
                    {
                        ""subject"": ""物理"",
                        ""category"": ""力学"",
                        ""gradeTarget"": ""八年级"",
                        ""type"": ""单选题"",
                        ""stem"": ""这是全新物理题目P2"",
                        ""options"": [""A. 30N"", ""B. 40N""],
                        ""correctAnswer"": ""B"",
                        ""standardAnalysis"": ""分析2""
                    },
                    {
                        ""subject"": ""物理"",
                        ""category"": ""力学"",
                        ""gradeTarget"": ""八年级"",
                        ""type"": ""单选题"",
                        ""stem"": ""这是全新物理题目P2"",
                        ""options"": [""A. 30N"", ""B. 40N""],
                        ""correctAnswer"": ""B"",
                        ""standardAnalysis"": ""分析2""
                    }
                ]";

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonContent));
                var result = await importService.ParseAndImportQuestionsByFileFormatAsync(stream, "test_batch.json", Guid.NewGuid());

                Assert.Equal(1, result.SuccessCount);
                Assert.Equal(2, result.DuplicateCount);

                var totalQuestions = await context.Questions.CountAsync();
                Assert.Equal(2, totalQuestions);
            }
        }

        [Fact]
        public async Task QuestionImportService_BatchTxtDeduplication_EliminatesDuplicatesInMemory()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var existingQ = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "化学",
                    Category = "元素",
                    GradeTarget = "九年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "这是化学已有题目C1",
                    OptionsJson = "[\"A. H\", \"B. O\"]",
                    CorrectAnswer = "A"
                };
                context.Questions.Add(existingQ);
                await context.SaveChangesAsync();

                var importService = new QuestionImportService(context);

                string txtContent = @"
【学科】化学
【考点分类】元素
【适学年级】九年级
【题型】单选题
【题干】这是化学已有题目C1
【选项】A. H | B. O
【正确答案】A
【标准解析】现有重复题目

---

【学科】化学
【考点分类】元素
【适学年级】九年级
【题型】单选题
【题干】这是全新化学题目C2
【选项】A. C | B. N
【正确答案】B
【标准解析】全新题目

---

【学科】化学
【考点分类】元素
【适学年级】九年级
【题型】单选题
【题干】这是全新化学题目C2
【选项】A. C | B. N
【正确答案】B
【标准解析】批次内重复题目
";

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(txtContent));
                var result = await importService.ParseAndImportQuestionsByFileFormatAsync(stream, "test_batch.txt", Guid.NewGuid());

                Assert.Equal(1, result.SuccessCount);
                Assert.Equal(2, result.DuplicateCount);

                var totalQuestions = await context.Questions.CountAsync();
                Assert.Equal(2, totalQuestions);
            }
        }
    }
}
