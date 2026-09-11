using System;
using System.Collections.Generic;
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
    public class ArchitectAndUxComprehensiveEvolutionTests
    {
        #region 1. 课程配置权限控制 (RBAC) 架构安全测试

        [Fact]
        public async Task CurriculumConfig_SaveSubjectTopics_NonSuperAdmin_ShouldBeForbidden()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var normalTeacher = new User { Id = Guid.NewGuid(), Username = "Teacher1", Role = UserRole.Teacher };
                var superAdmin = new User { Id = Guid.NewGuid(), Username = "Admin", Role = UserRole.SuperAdmin };
                context.Users.AddRange(normalTeacher, superAdmin);
                await context.SaveChangesAsync();

                var config = new CurriculumSubjectConfig
                {
                    Id = Guid.NewGuid(),
                    Grade = "高中一年级",
                    Subject = "数学",
                    TopicsJson = "[\"函数\"]"
                };
                context.CurriculumSubjectConfigs.Add(config);
                await context.SaveChangesAsync();

                var service = new CurriculumConfigService(context);

                // 普通教师调用 -> 拒绝
                var resultForbidden = await service.SaveSubjectTopicsAsync(config.Id, new List<string> { "导数", "圆锥曲线" }, normalTeacher.Id);
                Assert.False(resultForbidden.Success);
                Assert.Contains("权限不足", resultForbidden.Message);

                // 超级管理员调用 -> 允许
                var resultSuccess = await service.SaveSubjectTopicsAsync(config.Id, new List<string> { "导数", "圆锥曲线" }, superAdmin.Id);
                Assert.True(resultSuccess.Success);
            }
        }

        [Fact]
        public async Task CurriculumConfig_ResetToDefault_NonSuperAdmin_ShouldBeForbidden()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var student = new User { Id = Guid.NewGuid(), Username = "Student1", Role = UserRole.Student };
                var superAdmin = new User { Id = Guid.NewGuid(), Username = "SuperAdmin", Role = UserRole.SuperAdmin };
                context.Users.AddRange(student, superAdmin);
                await context.SaveChangesAsync();

                var service = new CurriculumConfigService(context);

                // 学生身份重置 -> 拒绝
                var resultForbidden = await service.ResetToDefaultCurriculumAsync(student.Id);
                Assert.False(resultForbidden.Success);

                // 超级管理员重置 -> 允许
                var resultSuccess = await service.ResetToDefaultCurriculumAsync(superAdmin.Id);
                Assert.True(resultSuccess.Success);
            }
        }

        #endregion

        #region 2. 题库批量原子操作、防横向越权 (IDOR) 与 CSV 导出测试

        [Fact]
        public async Task QuestionManagement_BatchDelete_IDORProtection_ShouldRejectUnauthorizedDeletion()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var teacherA = new User { Id = Guid.NewGuid(), Username = "TeacherA", Role = UserRole.Teacher };
                var teacherB = new User { Id = Guid.NewGuid(), Username = "TeacherB", Role = UserRole.Teacher };
                var questionA = new Question { Id = Guid.NewGuid(), CreatedByUserId = teacherA.Id, Stem = "StemA", CorrectAnswer = "A" };
                var questionB = new Question { Id = Guid.NewGuid(), CreatedByUserId = teacherB.Id, Stem = "StemB", CorrectAnswer = "B" };

                context.Users.AddRange(teacherA, teacherB);
                context.Questions.AddRange(questionA, questionB);
                await context.SaveChangesAsync();

                var qService = new QuestionManagementService(context);

                // TeacherA 试图删除属于 TeacherB 的题目 -> 仅能删除属于自己的，TeacherB的题目受保护
                var deletedCount = await qService.BatchDeleteQuestionsAsync(new List<Guid> { questionA.Id, questionB.Id }, teacherA.Id);
                Assert.Equal(1, deletedCount);

                // 数据库中 TeacherB 的题目依然安全保留 (防越权保护)
                var remainingQuestions = await context.Questions.ToListAsync();
                Assert.Single(remainingQuestions);
                Assert.Equal(questionB.Id, remainingQuestions[0].Id);
            }
        }

        [Fact]
        public async Task QuestionManagement_BatchDelete_SuperAdmin_ShouldDeleteAtomically()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var superAdmin = new User { Id = Guid.NewGuid(), Username = "SuperAdmin", Role = UserRole.SuperAdmin };
                var teacher = new User { Id = Guid.NewGuid(), Username = "Teacher", Role = UserRole.Teacher };
                var question1 = new Question { Id = Guid.NewGuid(), CreatedByUserId = teacher.Id, Stem = "Stem1", CorrectAnswer = "A" };
                var question2 = new Question { Id = Guid.NewGuid(), CreatedByUserId = teacher.Id, Stem = "Stem2", CorrectAnswer = "B" };

                context.Users.AddRange(superAdmin, teacher);
                context.Questions.AddRange(question1, question2);
                await context.SaveChangesAsync();

                var qService = new QuestionManagementService(context);

                var deletedCount = await qService.BatchDeleteQuestionsAsync(new List<Guid> { question1.Id, question2.Id }, superAdmin.Id);
                Assert.Equal(2, deletedCount);
                Assert.Equal(0, await context.Questions.CountAsync());
            }
        }

        [Fact]
        public async Task QuestionManagement_ExportCsv_ShouldGenerateValidUtf8BomCsv()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var admin = new User { Id = Guid.NewGuid(), Username = "Admin", Role = UserRole.SuperAdmin };
                context.Users.Add(admin);

                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "求 lim (x->0) sin(x)/x 的值，包含逗号,与\"双引号\"",
                    Type = QuestionType.SingleChoice,
                    GradeTarget = "大学一年级",
                    Subject = "高等数学",
                    Category = "极限与连续",
                    Difficulty = 3,
                    CorrectAnswer = "1",
                    StandardAnalysis = "根据重要极限，值为1"
                };
                context.Questions.Add(q1);
                await context.SaveChangesAsync();

                var qService = new QuestionManagementService(context);

                var fileBytes = await qService.ExportQuestionsCsvAsync(admin.Id);

                Assert.NotNull(fileBytes);
                Assert.True(fileBytes.Length > 3);
                // 验证 UTF-8 BOM: 0xEF, 0xBB, 0xBF
                Assert.Equal(0xEF, fileBytes[0]);
                Assert.Equal(0xBB, fileBytes[1]);
                Assert.Equal(0xBF, fileBytes[2]);

                var csvText = Encoding.UTF8.GetString(fileBytes);
                Assert.Contains("序号,学科,考点专题,学段年级,试题类型,难度", csvText);
                Assert.Contains("高等数学", csvText);
                Assert.Contains("\"求 lim (x->0) sin(x)/x 的值，包含逗号,与\"\"双引号\"\"\"", csvText);
            }
        }

        #endregion

        #region 3. 艾宾浩斯抗遗忘记忆曲线与健康度测试

        [Theory]
        [InlineData(0, 720)]   // 12小时
        [InlineData(1, 2160)]  // 36小时 (1.5天)
        [InlineData(2, 5040)]  // 84小时 (3.5天)
        [InlineData(3, 9360)]  // 156小时 (6.5天)
        [InlineData(4, 20160)] // 336小时 (14天)
        [InlineData(5, 20160)] // 14天
        public void ErrorBook_GetRecommendedReviewInterval_ShouldMatchScientificCurve(int revisionCount, int expectedMinutes)
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var session = new FakeUserSessionService();
                var gamification = new FakeGamificationService();
                var service = new ErrorBookService(context, gamification, session);

                var interval = service.GetRecommendedReviewInterval(revisionCount);
                Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), interval);
            }
        }

        [Fact]
        public void ErrorBook_IsReviewDue_ShouldDetermineDueAccurately()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var session = new FakeUserSessionService();
                var gamification = new FakeGamificationService();
                var service = new ErrorBookService(context, gamification, session);

                // 刚复习完 1 小时前（阶段0需12小时），尚未到期
                var freshItem = new ErrorItem
                {
                    RevisionCount = 0,
                    LastRevisedAt = DateTime.Now.AddHours(-1)
                };
                Assert.False(service.IsReviewDue(freshItem));

                // 13 小时前复习的（阶段0需12小时），已到期
                var dueItem = new ErrorItem
                {
                    RevisionCount = 0,
                    LastRevisedAt = DateTime.Now.AddHours(-13)
                };
                Assert.True(service.IsReviewDue(dueItem));
            }
        }

        [Fact]
        public void ErrorBook_CalculateRetentionHealthScore_ShouldDecayPredictably()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var session = new FakeUserSessionService();
                var gamification = new FakeGamificationService();
                var service = new ErrorBookService(context, gamification, session);

                // 刚复习完：健康度应接近 100
                var itemFresh = new ErrorItem { RevisionCount = 2, LastRevisedAt = DateTime.Now };
                double scoreFresh = service.CalculateRetentionHealthScore(itemFresh);
                Assert.InRange(scoreFresh, 95.0, 100.0);

                // 超期很久：健康度应低于 40
                var itemOld = new ErrorItem { RevisionCount = 0, LastRevisedAt = DateTime.Now.AddDays(-2) };
                double scoreOld = service.CalculateRetentionHealthScore(itemOld);
                Assert.InRange(scoreOld, 0.0, 40.0);
            }
        }

        #endregion

        #region 4. 最近发展区 (ZPD) 自适应动态难度测试

        [Fact]
        public async Task PracticeService_AdaptiveQuestions_HighAccuracyStudent_ShouldPromoteToHighDifficulty()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var studentId = Guid.NewGuid();
                var user = new User { Id = studentId, Username = "TopStudent", Role = UserRole.Student, Grade = "初中二年级" };
                context.Users.Add(user);

                // 添加高难度和中难度题目
                var questions = new List<Question>();
                for (int i = 1; i <= 10; i++)
                {
                    var q = new Question
                    {
                        Id = Guid.NewGuid(),
                        Stem = $"Stem_{i}",
                        GradeTarget = "初中二年级",
                        Subject = "物理",
                        Difficulty = i <= 5 ? 4 : 5,
                        CorrectAnswer = "A",
                        IsPublic = true,
                        PublishStatus = PublishStatusEnum.Approved
                    };
                    questions.Add(q);
                    context.Questions.Add(q);
                }
                await context.SaveChangesAsync();

                // 注入该学生近期 100% 正确率的历史记录
                for (int i = 0; i < 10; i++)
                {
                    context.PracticeRecords.Add(new PracticeRecord
                    {
                        UserId = studentId,
                        QuestionId = questions[i].Id,
                        IsCorrect = true,
                        AnsweredAt = DateTime.Now.AddMinutes(-i * 5)
                    });
                }
                await context.SaveChangesAsync();

                var session = new FakeUserSessionService { ActiveUser = user };
                var gamification = new FakeGamificationService();
                var aiTutor = new FakeAiTutorService();
                var service = new PracticeService(context, gamification, session, aiTutor);
                var adaptiveQuestions = await service.GetAdaptiveQuestionsAsync(studentId, "初中二年级", "物理", count: 5);

                Assert.NotEmpty(adaptiveQuestions);
                // 学霸型学生基准难度为4或5，获取的题目应以难度4与5为主
                Assert.All(adaptiveQuestions, q => Assert.True(q.Difficulty >= 3));
            }
        }

        #endregion
    }
}
