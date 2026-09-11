using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Northtropic.Tests;

namespace Northtropic.Tests
{
    public class ArchitectAndUxPinnacleMasterpieceTests
    {
        [Fact]
        public void Test_ChemicalNomenclature_Synonyms_And_Reactions()
        {
            // 1. 中文品名与标准化学式双向映射
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("熟石灰", "Ca(OH)2"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("Ca(OH)2", "熟石灰"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("生石灰", "CaO"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("纯碱", "Na2CO3"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("苏打", "Na2CO3"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("小苏打", "NaHCO3"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("烧碱", "NaOH"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("火碱", "NaOH"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("铁锈", "Fe2O3"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("氧化铜", "CuO"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("盐酸", "HCl"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("硫酸", "H2SO4"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("水", "H2O"));

            // 2. 填空题自动判分双向匹配
            Assert.True(PracticeService.CheckFillInBlankMatch("熟石灰", "Ca(OH)2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("Ca(OH)2", "熟石灰"));
            Assert.True(PracticeService.CheckFillInBlankMatch("小苏打", "NaHCO3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("NaHCO3", "小苏打"));
            Assert.True(PracticeService.CheckFillInBlankMatch("烧碱", "NaOH"));

            // 3. 化学反应方程式可交换性与俗名混合匹配
            Assert.True(PracticeService.CheckFillInBlankMatch(
                "2烧碱 + 硫酸铜 = 氢氧化铜 + 硫酸钠",
                "CuSO4 + 2NaOH = Cu(OH)2 + Na2SO4"));
            Assert.True(PracticeService.CheckFillInBlankMatch(
                "生石灰 + 水 = 熟石灰",
                "CaO + H2O = Ca(OH)2"));

            // 4. 理由生成测试
            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "Ca(OH)2" };
            var reason = PracticeService.GenerateEquivalentMatchReason(q, "熟石灰");
            Assert.Contains("化学品名称与化学式智能等价", reason);
        }

        [Fact]
        public void Test_Mathematical_RealNumbers_EmptySet_And_RadicalFractions()
        {
            // 1. 全体实数 / 实数集 / 集合域
            Assert.True(PracticeService.CheckFillInBlankMatch("全体实数", "R"));
            Assert.True(PracticeService.CheckFillInBlankMatch("实数集", "r"));
            Assert.True(PracticeService.CheckFillInBlankMatch("全体实数", "(-inf, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(-∞, +∞)", "全体实数"));

            // 2. 空集 / 无解 / ∅
            Assert.True(PracticeService.CheckFillInBlankMatch("无解", "∅"));
            Assert.True(PracticeService.CheckFillInBlankMatch("空集", "∅"));
            Assert.True(PracticeService.CheckFillInBlankMatch("无实数根", "空集"));
            Assert.True(PracticeService.CheckFillInBlankMatch("不存在", "∅"));
            Assert.True(PracticeService.CheckFillInBlankMatch("\\emptyset", "空集"));

            // 3. 分母有理化与根式分数完全等价求值
            Assert.True(PracticeService.CheckFillInBlankMatch("sqrt(2)/2", "1/sqrt(2)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1/sqrt(2)", "sqrt(2)/2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("sqrt(3)/3", "1/sqrt(3)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2/sqrt(2)", "sqrt(2)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(-sqrt(2))/2", "-1/sqrt(2)"));

            // 4. 解释理由验证
            var qRad = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "1/sqrt(2)" };
            var reasonRad = PracticeService.GenerateEquivalentMatchReason(qRad, "sqrt(2)/2");
            Assert.Contains("分母有理化与根式数值等价", reasonRad);

            var qReal = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "R" };
            var reasonReal = PracticeService.GenerateEquivalentMatchReason(qReal, "全体实数");
            Assert.Contains("全实数集合域等价", reasonReal);

            var qEmpty = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "∅" };
            var reasonEmpty = PracticeService.GenerateEquivalentMatchReason(qEmpty, "无解");
            Assert.Contains("解集与空集等价", reasonEmpty);
        }

        [Fact]
        public void Test_Judgement_Normalization_And_Shortcuts()
        {
            Assert.True(PracticeService.TryNormalizeJudgement("正确", out var val1) && val1);
            Assert.True(PracticeService.TryNormalizeJudgement("对", out var val2) && val2);
            Assert.True(PracticeService.TryNormalizeJudgement("T", out var val3) && val3);
            Assert.True(PracticeService.TryNormalizeJudgement("true", out var val4) && val4);
            Assert.True(PracticeService.TryNormalizeJudgement("1", out var val5) && val5);

            Assert.True(PracticeService.TryNormalizeJudgement("错误", out var val6) && !val6);
            Assert.True(PracticeService.TryNormalizeJudgement("错", out var val7) && !val7);
            Assert.True(PracticeService.TryNormalizeJudgement("F", out var val8) && !val8);
            Assert.True(PracticeService.TryNormalizeJudgement("false", out var val9) && !val9);
            Assert.True(PracticeService.TryNormalizeJudgement("0", out var val10) && !val10);
        }

        [Fact]
        public async Task Test_PrerequisiteDAG_Closure_And_RootDeficiencyDiagnosis()
        {
            // 1. 验证多跳前置依赖闭包获取
            var chainPhysics = PrerequisiteKnowledgeGraph.GetFullDependencyChain("物理", "压强与浮力");
            Assert.NotEmpty(chainPhysics);
            Assert.Contains(chainPhysics, c => c.PrerequisiteCategory == "力学与牛顿定律");
            Assert.Contains(chainPhysics, c => c.PrerequisiteCategory == "代数方程与函数");
            Assert.Contains(chainPhysics, c => c.PrerequisiteCategory == "四则混合运算与绝对值");

            var chainChem = PrerequisiteKnowledgeGraph.GetFullDependencyChain("化学", "酸碱盐及反应");
            Assert.NotEmpty(chainChem);
            Assert.Contains(chainChem, c => c.PrerequisiteCategory == "化学方程式");
            Assert.Contains(chainChem, c => c.PrerequisiteCategory == "物质构成与元素");

            // 2. 验证多跳根因诊断逻辑 (使用内存 SQLite)
            var (ctx, conn) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var userId = Guid.NewGuid();
                var user = new User { Id = userId, Username = "root_test_student", Password = "password123" };
                ctx.Users.Add(user);

                // 在根本基础考点 "数学" - "四则混合运算与绝对值" 上制造大量错题与低正确率
                var qRoot = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "数学",
                    Category = "四则混合运算与绝对值",
                    Stem = "计算 (-3) + | -5 |",
                    CorrectAnswer = "2",
                    OptionsJson = "[\"2\", \"-8\", \"8\", \"-2\"]",
                    Type = QuestionType.SingleChoice
                };
                ctx.Questions.Add(qRoot);

                // 添加未消灭错题
                ctx.ErrorItems.Add(new ErrorItem
                {
                    UserId = userId,
                    QuestionId = qRoot.Id,
                    Question = qRoot,
                    UserWrongAnswer = "-8",
                    RevisionCount = 2,
                    IsMastered = false,
                    ErrorReasonCategory = "逻辑计算错误"
                });

                // 添加做题记录 (10题仅对1题，正确率10%)
                for (int i = 0; i < 9; i++)
                {
                    ctx.PracticeRecords.Add(new PracticeRecord
                    {
                        UserId = userId,
                        QuestionId = qRoot.Id,
                        Question = qRoot,
                        IsCorrect = false,
                        AnsweredAt = DateTime.UtcNow
                    });
                }
                ctx.PracticeRecords.Add(new PracticeRecord
                {
                    UserId = userId,
                    QuestionId = qRoot.Id,
                    Question = qRoot,
                    IsCorrect = true,
                    AnsweredAt = DateTime.UtcNow
                });

                await ctx.SaveChangesAsync();

                // 诊断目标考点 "物理" - "压强与浮力"
                var diagnosis = await PrerequisiteKnowledgeGraph.DiagnoseRootDeficiencyAsync(userId, "物理", "压强与浮力", ctx);

                Assert.NotNull(diagnosis);
                Assert.Equal("物理", diagnosis!.TargetSubject);
                Assert.Equal("压强与浮力", diagnosis.TargetCategory);
                Assert.Equal("数学", diagnosis.RootSubject);
                Assert.Equal("四则混合运算与绝对值", diagnosis.RootCategory);
                Assert.True(diagnosis.Depth >= 3);
                Assert.Equal(10.0, diagnosis.RootAccuracy);
                Assert.Equal(1, diagnosis.RootUnmasteredErrors);
                Assert.NotEmpty(diagnosis.LearningPathRoadmap);
                Assert.Contains("四则混合运算与绝对值", diagnosis.LearningPathRoadmap[0]);
                Assert.True(diagnosis.StrategicAdvice.Contains("知识图谱") || diagnosis.StrategicAdvice.Contains("根因"));
            }
            finally
            {
                conn.Close();
            }
        }

        [Fact]
        public async Task Test_SystemHealth_Optimization_And_Latency()
        {
            var (ctx, conn) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var healthService = new SystemHealthService(ctx);

                // 1. 验证往返延迟测量
                var latency = await healthService.MeasureDatabaseLatencyAsync();
                Assert.True(latency >= 0, $"Database latency should be non-negative, got {latency}");

                // 2. 验证 SQLite 优化自愈执行
                var optResult = await healthService.OptimizeDatabaseAsync();
                Assert.True(optResult.Success);
                Assert.Contains("成功", optResult.Message);
                Assert.True(optResult.DatabaseLatencyMs >= 0);

                // 3. 验证完整体检指标包含了 WAL 与线程数
                var healthDto = await healthService.GetSystemHealthAsync();
                Assert.NotNull(healthDto);
                Assert.True(healthDto.DatabaseLatencyMs >= 0);
                Assert.True(healthDto.IsDatabaseHealthy);
            }
            finally
            {
                conn.Close();
            }
        }
    }
}
