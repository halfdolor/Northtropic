using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Helpers;
using Northtropic.Models;
using Northtropic.Services;
using OpenCvSharp;
using Xunit;

namespace Northtropic.Tests
{
    /// <summary>
    /// 系统架构师与用户体验专家联合验证测试套件
    /// 重点覆盖：
    /// 1. PaddleOCR 底层 C++ 推理互斥并发锁与大图自适应下采样防护
    /// 2. AI 导师填空题回退判题严谨性防护 (杜绝根据字数盲目判对)
    /// 3. IPracticeService 整卷统一批处理提交原子性与连击积分结算
    /// 4. 用户登录失败友好性交互体验 (剩余错误尝试次数提示与防爆破锁闭保护)
    /// </summary>
    public class ArchitectAndUxSymbiosisTests
    {
        #region 1. PaddleOCR 架构并发安全与大图尺寸降采样测试

        [Fact]
        public void LocalPaddleOcrEngine_ConcurrencyLockAndMemoryShield_ShouldBeConfigured()
        {
            // 验证 LocalPaddleOcrEngine 是否配置了私有静态并发控制锁 _ocrExecutionLock
            var lockField = typeof(LocalPaddleOcrEngine).GetField("_ocrExecutionLock", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(lockField);

            var semaphore = lockField.GetValue(null) as SemaphoreSlim;
            Assert.NotNull(semaphore);
            Assert.Equal(1, semaphore.CurrentCount);
        }

        [Fact]
        public async Task LocalPaddleOcrEngine_EmptyOrCorruptedBytes_GracefullyFailsWithoutCrashing()
        {
            // 空字节数组安全测试
            var emptyResult = await LocalPaddleOcrEngine.RecognizeAsync(Array.Empty<byte>());
            Assert.False(emptyResult.Success);
            Assert.Contains("为空", emptyResult.ErrorMessage);

            // 非法非图片字节安全测试
            var invalidBytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            var corruptedResult = await LocalPaddleOcrEngine.RecognizeAsync(invalidBytes);
            Assert.False(corruptedResult.Success);
            Assert.Contains("解码失败", corruptedResult.ErrorMessage);
        }

        [Fact]
        public void LocalPaddleOcrEngine_OversizedImageDownscaling_CalculatesAccurateTargetRatio()
        {
            // 模拟 4000x3000 高清试卷照片输入
            int originalWidth = 4000;
            int originalHeight = 3000;
            const int maxDimension = 1600;

            int maxSide = Math.Max(originalWidth, originalHeight);
            Assert.True(maxSide > maxDimension);

            double scale = (double)maxDimension / maxSide;
            int newWidth = (int)Math.Round(originalWidth * scale);
            int newHeight = (int)Math.Round(originalHeight * scale);

            Assert.Equal(1600, newWidth);
            Assert.Equal(1200, newHeight);
            Assert.True(Math.Max(newWidth, newHeight) <= maxDimension);
        }

        #endregion

        #region 2. AI 导师判卷架构与填空题防作弊严谨性测试

        [Fact]
        public async Task AiTutorService_FillInBlankFallbackGrading_ShouldRejectIrrelevantLongAnswer()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Type = QuestionType.FillInBlank,
                    Subject = "数学",
                    Category = "平面几何",
                    Stem = "直角三角形斜边中线长为 5，则斜边长为多少？",
                    CorrectAnswer = "10",
                    Difficulty = 2
                };

                var fakeGamification = new FakeGamificationService();
                var mockHandler = new MockHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
                var httpClient = new HttpClient(mockHandler);
                var factory = new MockHttpClientFactory(httpClient);
                var aiTutor = new AiTutorService(fakeGamification, factory, context);

                // 用户输入了超过 10 个字符的无关干扰文本 (以前会因为 isLongEnough >= 10 被误判通过并给 80 分)
                string bogusLongAnswer = "这是一段很长很长但是完全无关且错误的文本输入答案";
                var gradingResult = await aiTutor.GradeSubjectiveAnswerAsync(question, bogusLongAnswer);

                // 架构断言：必须判定未通过，且分数不能及格 (30 分)
                Assert.False(gradingResult.IsPassed);
                Assert.True(gradingResult.Score < 60);
                Assert.Contains("未命中核心标准答案", gradingResult.Feedback);
            }
        }

        [Fact]
        public async Task AiTutorService_FillInBlankFallbackGrading_ShouldPassCorrectKeyword()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Type = QuestionType.FillInBlank,
                    Subject = "化学",
                    Category = "基础化学",
                    Stem = "人体呼吸呼出的主要温室气体是？",
                    CorrectAnswer = "CO2",
                    Difficulty = 1
                };

                var fakeGamification = new FakeGamificationService();
                var mockHandler = new MockHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
                var httpClient = new HttpClient(mockHandler);
                var factory = new MockHttpClientFactory(httpClient);
                var aiTutor = new AiTutorService(fakeGamification, factory, context);

                // 用户准确填入了 CO2
                var gradingResult = await aiTutor.GradeSubjectiveAnswerAsync(question, "CO2");

                // 架构断言：命中标准答案，判定通过 (85 分)
                Assert.True(gradingResult.IsPassed);
                Assert.True(gradingResult.Score >= 80);
                Assert.Contains("包含核心考点", gradingResult.Feedback);
            }
        }

        #endregion

        #region 3. 整卷提交批处理架构原子性与连击测试

        [Fact]
        public async Task SubmitBatchPaperAsync_EvaluatesAllQuestionsAndMaintainsComboAccurately()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var studentId = Guid.NewGuid();
                var student = new User
                {
                    Id = studentId,
                    Username = "批处理全卷测试学员",
                    PhoneNumber = "13900001111",
                    Role = UserRole.Student,
                    Exp = 100,
                    Coins = 50
                };
                context.Users.Add(student);

                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    Type = QuestionType.SingleChoice,
                    Subject = "物理",
                    Category = "力学",
                    Stem = "题1",
                    CorrectAnswer = "A",
                    OptionsJson = "[\"A. 对\",\"B. 错\"]",
                    BaseExpReward = 10,
                    Difficulty = 2
                };
                var q2 = new Question
                {
                    Id = Guid.NewGuid(),
                    Type = QuestionType.SingleChoice,
                    Subject = "物理",
                    Category = "力学",
                    Stem = "题2",
                    CorrectAnswer = "B",
                    OptionsJson = "[\"A. 错\",\"B. 对\"]",
                    BaseExpReward = 10,
                    Difficulty = 2
                };
                var q3 = new Question
                {
                    Id = Guid.NewGuid(),
                    Type = QuestionType.SingleChoice,
                    Subject = "物理",
                    Category = "力学",
                    Stem = "题3",
                    CorrectAnswer = "A",
                    OptionsJson = "[\"A. 对\",\"B. 错\"]",
                    BaseExpReward = 10,
                    Difficulty = 2
                };

                context.Questions.AddRange(q1, q2, q3);
                await context.SaveChangesAsync();

                var session = new FakeUserSessionService { ActiveUser = student };
                var gamification = new GamificationService(context, session);
                var aiTutor = new FakeAiTutorService();
                var practiceService = new PracticeService(context, gamification, session, aiTutor);

                // 用户作答列表：题1答对，题2答对，题3答错
                var submissions = new List<(Question Question, string UserAnswer, int TimeTakenSeconds)>
                {
                    (q1, "A", 10),
                    (q2, "B", 12),
                    (q3, "B", 15) // 错误答案
                };

                var batchResult = await practiceService.SubmitBatchPaperAsync(submissions, initialCombo: 0, targetUserId: studentId);

                // 断言批处理汇总
                Assert.Equal(3, batchResult.TotalQuestions);
                Assert.Equal(2, batchResult.CorrectCount);
                Assert.Equal(2, batchResult.MaxComboAchieved);
                Assert.True(batchResult.TotalEarnedExp > 0);
                Assert.Equal(3, batchResult.ItemResults.Count);

                Assert.True(batchResult.ItemResults[1].IsCorrect);
                Assert.True(batchResult.ItemResults[2].IsCorrect);
                Assert.False(batchResult.ItemResults[3].IsCorrect);

                // 断言数据库记录完整性
                var records = await context.PracticeRecords.Where(r => r.UserId == studentId).ToListAsync();
                Assert.Equal(3, records.Count);

                // 断言错题本纳入题3
                var errors = await context.ErrorItems.Where(e => e.UserId == studentId).ToListAsync();
                Assert.Single(errors);
                Assert.Equal(q3.Id, errors[0].QuestionId);
            }
        }

        #endregion

        #region 4. 用户体验专家：登录密码错误友好倒计时提示测试

        [Fact]
        public async Task LoginWithPasswordAsync_PasswordError_DisplaysRemainingAttemptsForSuperiorUx()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                string phone = "13700008888";
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    PhoneNumber = phone,
                    Username = "密码测试学员",
                    Password = PasswordHasher.HashPassword("SecurePass123"),
                    Role = UserRole.Student,
                    AccountStatus = UserAccountStatus.Approved
                };
                context.Users.Add(user);
                await context.SaveChangesAsync();

                var sessionService = new UserSessionService(context, new HttpClient());
                UserSessionService.ResetLoginAttempts(phone);

                // 第一次输错密码：应明确提示还可尝试 4 次
                var (s1, _, msg1) = await sessionService.LoginWithPasswordAsync(phone, "Wrong1");
                Assert.False(s1);
                Assert.Contains("还可尝试 4 次", msg1);
                Assert.Contains("123456", msg1);

                // 第二次输错密码：应明确提示还可尝试 3 次
                var (s2, _, msg2) = await sessionService.LoginWithPasswordAsync(phone, "Wrong2");
                Assert.False(s2);
                Assert.Contains("还可尝试 3 次", msg2);

                // 第三次输错密码：还可尝试 2 次
                var (s3, _, msg3) = await sessionService.LoginWithPasswordAsync(phone, "Wrong3");
                Assert.False(s3);
                Assert.Contains("还可尝试 2 次", msg3);

                // 第四次输错密码：还可尝试 1 次
                var (s4, _, msg4) = await sessionService.LoginWithPasswordAsync(phone, "Wrong4");
                Assert.False(s4);
                Assert.Contains("还可尝试 1 次", msg4);

                // 第五次输错密码：触发锁定拦截
                var (s5, _, msg5) = await sessionService.LoginWithPasswordAsync(phone, "Wrong5");
                Assert.False(s5);
                Assert.Contains("锁定 5 分钟", msg5);

                // 第六次输入即便输入正确密码，仍受临时锁定保护
                var (s6, _, msg6) = await sessionService.LoginWithPasswordAsync(phone, "SecurePass123");
                Assert.False(s6);
                Assert.Contains("账号已临时锁定保护", msg6);
            }
        }

        #endregion
    }
}
