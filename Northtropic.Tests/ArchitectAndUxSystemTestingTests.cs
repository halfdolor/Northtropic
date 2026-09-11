using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Helpers;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxSystemTestingTests
    {
        #region 1. 架构与算法鲁棒性：题目打乱防二次前缀污染与符号容错

        [Theory]
        [InlineData("(A) 选项内容", "选项内容")]
        [InlineData("（B） 选项内容", "选项内容")]
        [InlineData("[C] 选项内容", "选项内容")]
        [InlineData("【D】 选项内容", "选项内容")]
        [InlineData("① 第一项", "第一项")]
        [InlineData("② 第二项", "第二项")]
        [InlineData("1. 数字项", "数字项")]
        [InlineData("2、顿号项", "顿号项")]
        [InlineData("A. 字母项", "字母项")]
        [InlineData("B: 冒号项", "冒号项")]
        [InlineData("纯文本选项", "纯文本选项")]
        public void QuestionShuffleHelper_CleanPrefix_ShouldStripAllPrefixStylesCleanly(string rawOption, string expected)
        {
            string cleaned = QuestionShuffleHelper.CleanPrefix(rawOption);
            Assert.Equal(expected, cleaned);
        }

        [Fact]
        public void QuestionShuffleHelper_ShuffleQuestionOptions_WithParenthesesPrefix_ShouldNotProduceDoublePrefix()
        {
            var question = new Question
            {
                Stem = "测试题目",
                Type = QuestionType.SingleChoice,
                OptionsJson = "[\"(A) 苹果\", \"(B) 香蕉\", \"(C) 橙子\", \"(D) 葡萄\"]",
                CorrectAnswer = "(A)"
            };

            var shuffled = QuestionShuffleHelper.ShuffleQuestionOptions(question);

            // 打乱后的选项应规范为 "A. 苹果" 格式，绝不允许出现 "A. (A) 苹果"
            foreach (var opt in shuffled.Options)
            {
                Assert.DoesNotContain("(A)", opt);
                Assert.DoesNotContain("(B)", opt);
                Assert.DoesNotContain("(C)", opt);
                Assert.DoesNotContain("(D)", opt);
                Assert.Matches(@"^[A-D]\.\s+", opt);
            }

            // 无论正确选项被打乱到哪个字母，其对应选项必须为 "苹果"
            int correctIndex = shuffled.CorrectAnswer[0] - 'A';
            Assert.Contains("苹果", shuffled.Options[correctIndex]);
        }

        [Fact]
        public void QuestionShuffleHelper_ExtractChoicePrefix_ShouldRecognizeAllFormats()
        {
            Assert.Equal("A", QuestionShuffleHelper.ExtractChoicePrefix("(A)"));
            Assert.Equal("B", QuestionShuffleHelper.ExtractChoicePrefix("（B）"));
            Assert.Equal("C", QuestionShuffleHelper.ExtractChoicePrefix("[C]"));
            Assert.Equal("D", QuestionShuffleHelper.ExtractChoicePrefix("【D】"));
            Assert.Equal("A", QuestionShuffleHelper.ExtractChoicePrefix("①"));
            Assert.Equal("B", QuestionShuffleHelper.ExtractChoicePrefix("②"));
            Assert.Equal("A", QuestionShuffleHelper.ExtractChoicePrefix("1"));
            Assert.Equal("B", QuestionShuffleHelper.ExtractChoicePrefix("2"));
            Assert.Equal("C", QuestionShuffleHelper.ExtractChoicePrefix("C."));
        }

        #endregion

        #region 2. 用户体验与容错算法：灵活判断题与 LaTeX 填空等价判定

        [Fact]
        public async Task PracticeService_CheckFillInBlank_ShouldSupportAllJudgementSynonyms()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var judgementQuestion = new Question
                {
                    Stem = "光在真空中沿直线传播。",
                    Type = QuestionType.FillInBlank,
                    OptionsJson = "[]",
                    CorrectAnswer = "√"
                };

                // 用户输入 v/V, 是, right, 1, 正确 均应判定为对
                string[] correctInputs = new[] { "v", "V", "是", "right", "1", "正确", "√", "T", "true" };
                foreach (var input in correctInputs)
                {
                    var result = await practiceService.SubmitAnswerAsync(judgementQuestion, input, 5, 0);
                    Assert.True(result.IsCorrect, $"输入 '{input}' 应判定为正确！");
                }

                // 用户输入 x/X, 否, wrong, 0, 错误 均应判定为错
                string[] wrongInputs = new[] { "x", "X", "否", "wrong", "0", "错误", "×", "F", "false" };
                foreach (var input in wrongInputs)
                {
                    var result = await practiceService.SubmitAnswerAsync(judgementQuestion, input, 5, 0);
                    Assert.False(result.IsCorrect, $"输入 '{input}' 对正确答案 '√' 应判定为错误！");
                }
            }
        }

        [Fact]
        public async Task PracticeService_CheckFillInBlank_LatexSpacingAndFractionEquivalence()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var latexQuestion = new Question
                {
                    Stem = "求导数",
                    Type = QuestionType.FillInBlank,
                    OptionsJson = "[]",
                    CorrectAnswer = @"\frac{1}{2}"
                };

                // 带 LaTeX 间隔宏或空格分数的作答
                string[] equivalentInputs = new[]
                {
                    @"\frac{1}{2}",
                    @"\frac { 1 } { 2 }",
                    @" \frac{ 1 }{ 2 } ",
                    @"1/2"
                };

                foreach (var input in equivalentInputs)
                {
                    var result = await practiceService.SubmitAnswerAsync(latexQuestion, input, 5, 0);
                    Assert.True(result.IsCorrect, $"LaTeX 输入 '{input}' 应被视为等价！");
                }
            }
        }

        [Fact]
        public async Task PracticeService_CheckChoiceCorrectness_ShouldNormalizeCircledAndBracketedInputs()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var choiceQuestion = new Question
                {
                    Stem = "下列属于原核生物的是",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"A. 蓝细菌\", \"B. 酵母菌\", \"C. 草履虫\", \"D. 衣藻\"]",
                    CorrectAnswer = "A"
                };

                string[] variantUserInputs = new[] { "(A)", "（A）", "[A]", "【A】", "①", "1", "A." };
                foreach (var input in variantUserInputs)
                {
                    var result = await practiceService.SubmitAnswerAsync(choiceQuestion, input, 5, 0);
                    Assert.True(result.IsCorrect, $"选项输入 '{input}' 应自动规约匹配 'A'！");
                }
            }
        }

        #endregion

        #region 3. 架构安全性与 RBAC 访问控制测试

        [Fact]
        public async Task UserSessionService_ApproveUserRegistration_UnauthorizedUser_ShouldBeForbidden()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var normalTeacher = new User { Id = Guid.NewGuid(), Username = "TeacherBob", Role = UserRole.Teacher };
                var superAdmin = new User { Id = Guid.NewGuid(), Username = "SuperAdminAlice", Role = UserRole.SuperAdmin };
                var pendingUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "NewStudent",
                    PhoneNumber = "13800001111",
                    Role = UserRole.Student,
                    AccountStatus = UserAccountStatus.PendingApproval
                };

                context.Users.AddRange(normalTeacher, superAdmin, pendingUser);
                await context.SaveChangesAsync();

                var sessionService = new UserSessionService(context, new HttpClient());

                // 1. 普通教师试图审批 -> 拒绝
                var (successTeacher, msgTeacher) = await sessionService.ApproveUserRegistrationAsync(pendingUser.Id, normalTeacher.Id);
                Assert.False(successTeacher);
                Assert.Contains("权限不足", msgTeacher);

                // 验证账号状态未被篡改
                var userAfterFailed = await context.Users.FindAsync(pendingUser.Id);
                Assert.Equal(UserAccountStatus.PendingApproval, userAfterFailed!.AccountStatus);

                // 2. 超级管理员审批 -> 成功
                var (successAdmin, msgAdmin) = await sessionService.ApproveUserRegistrationAsync(pendingUser.Id, superAdmin.Id);
                Assert.True(successAdmin);
                Assert.Contains("已成功审批通过", msgAdmin);

                var userAfterSuccess = await context.Users.FindAsync(pendingUser.Id);
                Assert.Equal(UserAccountStatus.Approved, userAfterSuccess!.AccountStatus);
                Assert.NotNull(userAfterSuccess.ApprovedAt);
            }
        }

        [Fact]
        public async Task UserSessionService_RejectUserRegistration_UnauthorizedUser_ShouldBeForbidden()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var normalParent = new User { Id = Guid.NewGuid(), Username = "ParentTom", Role = UserRole.Parent };
                var superAdmin = new User { Id = Guid.NewGuid(), Username = "SuperAdminAlice", Role = UserRole.SuperAdmin };
                var pendingUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "FraudStudent",
                    PhoneNumber = "13800002222",
                    Role = UserRole.Student,
                    AccountStatus = UserAccountStatus.PendingApproval
                };

                context.Users.AddRange(normalParent, superAdmin, pendingUser);
                await context.SaveChangesAsync();

                var sessionService = new UserSessionService(context, new HttpClient());

                // 普通家长试图驳回 -> 拒绝
                var (successParent, msgParent) = await sessionService.RejectUserRegistrationAsync(pendingUser.Id, normalParent.Id, "不通过");
                Assert.False(successParent);
                Assert.Contains("权限不足", msgParent);

                // 超级管理员驳回 -> 成功
                var (successAdmin, msgAdmin) = await sessionService.RejectUserRegistrationAsync(pendingUser.Id, superAdmin.Id, "手机号格式不合规");
                Assert.True(successAdmin);

                var userAfterReject = await context.Users.FindAsync(pendingUser.Id);
                Assert.Equal(UserAccountStatus.Rejected, userAfterReject!.AccountStatus);
                Assert.Equal("手机号格式不合规", userAfterReject.RejectReason);
            }
        }

        [Fact]
        public async Task UserSessionService_UpdateUserRole_UnauthorizedCaller_ShouldFail()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var studentCaller = new User { Id = Guid.NewGuid(), Username = "StudentCaller", Role = UserRole.Student };
                var targetUser = new User { Id = Guid.NewGuid(), Username = "TargetUser", Role = UserRole.Student };

                context.Users.AddRange(studentCaller, targetUser);
                await context.SaveChangesAsync();

                var sessionService = new UserSessionService(context, new HttpClient());
                await sessionService.SwitchUserAsync(studentCaller.Id);

                // 学生身份调用升级角色 -> 拒绝
                bool result = await sessionService.UpdateUserRoleAsync(targetUser.Id, UserRole.SuperAdmin);
                Assert.False(result);

                var refreshed = await context.Users.FindAsync(targetUser.Id);
                Assert.Equal(UserRole.Student, refreshed!.Role);
            }
        }

        #endregion

        #region 4. 自适应出题兜底保障测试

        [Fact]
        public async Task PracticeService_GetAdaptiveQuestions_WhenCandidatesInsufficient_ShouldFallbackGracefully()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var student = new User { Id = Guid.NewGuid(), Username = "TestStudent", Grade = "高中一年级" };
                context.Users.Add(student);

                // 数据库中只有 2 道冷门考点试题，但请求 5 道自适应试题
                var publicQ1 = new Question { Stem = "公共数学题1", Subject = "数学", Category = "冷门考点", GradeTarget = "高中一年级", IsPublic = true, OptionsJson = "[]", CorrectAnswer = "A" };
                var publicQ2 = new Question { Stem = "公共数学题2", Subject = "数学", Category = "冷门考点", GradeTarget = "高中一年级", IsPublic = true, OptionsJson = "[]", CorrectAnswer = "B" };
                var fallbackQ = new Question { Stem = "公共通用数学题", Subject = "数学", Category = "其他分类", GradeTarget = "高中一年级", IsPublic = true, OptionsJson = "[]", CorrectAnswer = "C" };
                context.Questions.AddRange(publicQ1, publicQ2, fallbackQ);
                await context.SaveChangesAsync();

                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var questions = await practiceService.GetAdaptiveQuestionsAsync(student.Id, "高中一年级", "数学", "冷门考点", 5);

                // 绝不会返回空列表，自动拉取 fallback 试题或 Demo 试题兜底
                Assert.NotNull(questions);
                Assert.True(questions.Count >= 3);
            }
        }

        #endregion
    }
}
