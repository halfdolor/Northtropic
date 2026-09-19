using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxZenithEvolutionSuiteTests
    {
        private class TestHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;
            public TestHttpClientFactory(HttpClient client) => _client = client;
            public HttpClient CreateClient(string name) => _client;
        }

        [Fact]
        public async Task SystemHealthService_AuditAndHealStudyPlanInvariants_FullyAlignsDomainInvariants()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var studentId = Guid.NewGuid();
                var student = new User
                {
                    Id = studentId,
                    Username = "zenith_student_plan",
                    Password = "hash",
                    Role = UserRole.Student,
                    Grade = "初中二年级"
                };
                context.Users.Add(student);

                // 构造异常学习计划 1: 负数目标、超界正确率、倒置日期、子任务不变量破损
                var invalidPlan1 = new StudyPlan
                {
                    Id = Guid.NewGuid(),
                    UserId = studentId,
                    DailyTargetQuestions = -10, // 异常：负数
                    TargetAccuracyRate = 120.0, // 异常：超过100
                    StartDate = DateTime.Now,
                    TargetEndDate = DateTime.Now.AddDays(-5), // 异常：倒置
                    Status = StudyPlanStatus.Active,
                    SupervisionNudgeCount = -3,
                    CreatedAt = DateTime.Now.AddDays(-2),
                    Tasks = new List<StudyPlanTask>
                    {
                        new StudyPlanTask
                        {
                            Id = Guid.NewGuid(),
                            Subject = "数学",
                            TargetCount = 0, // 异常：目标小于1
                            CompletedCount = 10, // 完成数大于目标但未标记完成
                            IsCompleted = false,
                            TargetAccuracy = 150.0
                        },
                        new StudyPlanTask
                        {
                            Id = Guid.NewGuid(),
                            Subject = "物理",
                            TargetCount = 5,
                            CompletedCount = 2,
                            IsCompleted = true, // 异常：完成标记但实际数量不足
                            TargetAccuracy = 40.0
                        },
                        new StudyPlanTask
                        {
                            Id = Guid.NewGuid(),
                            Subject = "英语",
                            TargetCount = 10,
                            CompletedCount = 2,
                            IsCompleted = false,
                            TargetAccuracy = 80.0
                        }
                    }
                };

                // 构造异常学习计划 2: 并存的第二个 Active 计划（冲突）
                var invalidPlan2 = new StudyPlan
                {
                    Id = Guid.NewGuid(),
                    UserId = studentId,
                    DailyTargetQuestions = 20,
                    TargetAccuracyRate = 85.0,
                    StartDate = DateTime.Now,
                    TargetEndDate = DateTime.Now.AddDays(7),
                    Status = StudyPlanStatus.Active,
                    CreatedAt = DateTime.Now.AddDays(-1), // 较新
                    Tasks = new List<StudyPlanTask>()
                };

                context.StudyPlans.AddRange(invalidPlan1, invalidPlan2);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 1. 审计检测
                var auditBefore = await healthService.AuditDataIntegrityAsync();
                Assert.True(auditBefore.TotalInvalidStudyPlans > 0, "应检测出异常的学习计划不变量");
                Assert.True(auditBefore.InvalidStudyPlansCount > 0);

                // 2. 执行自愈
                var healedCount = await healthService.HealStudyPlanInvariantsAsync();
                Assert.True(healedCount > 0, "应成功自愈异常项");

                // 3. 验证数据库状态
                var updatedPlan1 = await context.StudyPlans.Include(p => p.Tasks).FirstAsync(p => p.Id == invalidPlan1.Id);
                var updatedPlan2 = await context.StudyPlans.Include(p => p.Tasks).FirstAsync(p => p.Id == invalidPlan2.Id);

                // 计划1参数应已修复
                Assert.Equal(15, updatedPlan1.DailyTargetQuestions);
                Assert.Equal(100.0, updatedPlan1.TargetAccuracyRate);
                Assert.True(updatedPlan1.TargetEndDate >= updatedPlan1.StartDate);
                Assert.Equal(0, updatedPlan1.SupervisionNudgeCount);

                // 计划1任务状态对齐
                var task1 = updatedPlan1.Tasks.First(t => t.Subject == "数学");
                var task2 = updatedPlan1.Tasks.First(t => t.Subject == "物理");
                Assert.True(task1.IsCompleted, "完成数已达标的任务应被自动标为完成");
                Assert.Equal(5, task2.CompletedCount); // 已标为完成的任务其完成数应与目标对齐

                // 活跃计划去重收敛验证：较新的 plan2 保持 Active，较旧的 plan1 转为 Adjusted
                Assert.Equal(StudyPlanStatus.Adjusted, updatedPlan1.Status);
                Assert.Equal(StudyPlanStatus.Active, updatedPlan2.Status);

                // 4. 二次审计应无异常
                var auditAfter = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, auditAfter.TotalInvalidStudyPlans);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        [Fact]
        public async Task SystemHealthService_AuditAndHealStudentParentBindingInvariants_PurgesSelfAndDuplicateBindings()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "zenith_student_bind",
                    Password = "hash",
                    Role = UserRole.Student
                };
                var parent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "zenith_parent_bind",
                    Password = "hash",
                    Role = UserRole.Parent
                };
                context.Users.AddRange(student, parent);

                // 1. 自我绑定 (ParentUserId == StudentUserId)
                var selfBinding = new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = student.Id,
                    StudentUserId = student.Id,
                    CreatedAt = DateTime.Now.AddDays(-5)
                };

                // 2. 重复绑定 (两条相同的 ParentUserId -> StudentUserId)
                var duplicate1 = new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = parent.Id,
                    StudentUserId = student.Id,
                    CreatedAt = DateTime.Now.AddDays(-3)
                };
                var duplicate2 = new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = parent.Id,
                    StudentUserId = student.Id,
                    CreatedAt = DateTime.Now.AddDays(-1)
                };

                context.StudentParentBindings.AddRange(selfBinding, duplicate1, duplicate2);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 审计应检测出自我绑定与重复绑定
                var auditBefore = await healthService.AuditDataIntegrityAsync();
                Assert.True(auditBefore.TotalInvalidBindings >= 2, "应审计出自我绑定和冗余重复绑定");
                Assert.True(auditBefore.InvalidBindingsCount >= 2);

                // 自愈修复
                var healedCount = await healthService.HealStudentParentBindingInvariantsAsync();
                Assert.True(healedCount >= 2);

                // 数据库校验
                var remainingBindings = await context.StudentParentBindings.Where(b => b.StudentUserId == student.Id).ToListAsync();
                Assert.Single(remainingBindings);
                Assert.Equal(parent.Id, remainingBindings[0].ParentUserId);
                Assert.Equal(student.Id, remainingBindings[0].StudentUserId);
                Assert.Equal(duplicate1.Id, remainingBindings[0].Id); // 保留较早创建的一条

                // 二次审计应通过
                var auditAfter = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, auditAfter.TotalInvalidBindings);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        [Fact]
        public async Task StudentEvolutionService_EnforcesIdorProtection_BlocksUnauthorizedCallers()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var studentA = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "student_a",
                    Password = "pw",
                    Role = UserRole.Student,
                    Grade = "初中二年级"
                };
                var studentB = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "student_b",
                    Password = "pw",
                    Role = UserRole.Student,
                    Grade = "初中二年级"
                };
                var parentA = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "parent_a",
                    Password = "pw",
                    Role = UserRole.Parent
                };
                var strangerParent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "stranger_parent",
                    Password = "pw",
                    Role = UserRole.Parent
                };
                var teacher = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "teacher_smith",
                    Password = "pw",
                    Role = UserRole.Teacher
                };
                var superAdmin = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "admin_root",
                    Password = "pw",
                    Role = UserRole.SuperAdmin
                };

                context.Users.AddRange(studentA, studentB, parentA, strangerParent, teacher, superAdmin);

                // 绑定 parentA 到 studentA
                context.StudentParentBindings.Add(new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = parentA.Id,
                    StudentUserId = studentA.Id
                });

                // 插入若干测试试题与练习记录
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "物理受力平衡试题",
                    Subject = "初中物理",
                    Category = "力学综合",
                    Type = QuestionType.SingleChoice,
                    CorrectAnswer = "A",
                    OptionsJson = "[\"A. 对\",\"B. 错\"]"
                };
                context.Questions.Add(question);

                context.PracticeRecords.Add(new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = studentA.Id,
                    QuestionId = question.Id,
                    IsCorrect = true,
                    TimeTakenSeconds = 25,
                    AnsweredAt = DateTime.Now
                });

                await context.SaveChangesAsync();

                var session = new UserSessionService(context, new HttpClient());
                var gamification = new GamificationService(context, session);
                var clientFactory = new TestHttpClientFactory(new HttpClient());
                var evolutionService = new StudentEvolutionService(context, gamification, clientFactory, null);

                // 1. 学员本人访问 studentA -> 成功
                var selfOverview = await evolutionService.GetMasteryOverviewAsync(studentA.Id, requestorUserId: studentA.Id);
                Assert.NotEmpty(selfOverview);

                // 2. 绑定家长 parentA 访问 studentA -> 成功
                var parentOverview = await evolutionService.GetMasteryOverviewAsync(studentA.Id, requestorUserId: parentA.Id);
                Assert.NotEmpty(parentOverview);

                // 3. 教师访问 studentA -> 成功
                var teacherOverview = await evolutionService.GetMasteryOverviewAsync(studentA.Id, requestorUserId: teacher.Id);
                Assert.NotEmpty(teacherOverview);

                // 4. 超级管理员访问 studentA -> 成功
                var adminOverview = await evolutionService.GetMasteryOverviewAsync(studentA.Id, requestorUserId: superAdmin.Id);
                Assert.NotEmpty(adminOverview);

                // 5. 非法陌生家长访问 studentA -> 拦截，返回空列表
                var strangerOverview = await evolutionService.GetMasteryOverviewAsync(studentA.Id, requestorUserId: strangerParent.Id);
                Assert.Empty(strangerOverview);

                // 6. 其他学生 studentB 越权访问 studentA -> 拦截，返回空列表
                var studentBOverview = await evolutionService.GetMasteryOverviewAsync(studentA.Id, requestorUserId: studentB.Id);
                Assert.Empty(studentBOverview);

                // 7. 诊断报告 IDOR 防护测试
                var strangerReport = await evolutionService.GenerateDiagnosisReportAsync(studentA.Id, requestorUserId: strangerParent.Id);
                Assert.Contains("无权访问", strangerReport.AiGrowthAdvice);

                var boundParentReport = await evolutionService.GenerateDiagnosisReportAsync(studentA.Id, requestorUserId: parentA.Id);
                Assert.DoesNotContain("无权访问", boundParentReport.AiGrowthAdvice);

                // 8. 艾宾浩斯复习节点 IDOR 防护
                var strangerSpacedNodes = await evolutionService.GetPendingSpacedReviewNodesAsync(studentA.Id, requestorUserId: strangerParent.Id);
                Assert.Empty(strangerSpacedNodes);

                // 9. 弱项前置知识溯源 IDOR 防护
                var strangerPrereqs = await evolutionService.TraceWeakPrerequisitesAsync(studentA.Id, "初中物理", "力学综合", requestorUserId: strangerParent.Id);
                Assert.Empty(strangerPrereqs);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        [Fact]
        public void DataIntegrityAuditDto_TotalIssuesCount_ReflectsStudyPlansAndBindingsAnomalies()
        {
            var audit = new DataIntegrityAuditDto
            {
                TotalOrphanErrorItems = 2,
                TotalOrphanPracticeRecords = 1,
                TotalInvalidStudyPlans = 3,
                TotalInvalidBindings = 4,
                TotalUsersWithInvalidBalances = 1,
                TotalDuplicateQuestions = 5
            };

            Assert.False(audit.IsHealthy);
            // TotalIssuesCount (structural integrity): 2 + 1 + 3 + 4 = 10
            Assert.Equal(10, audit.TotalIssuesCount);
            // TotalOptimizationCandidatesCount (business optimizations): 3 + 4 + 1 + 5 = 13
            Assert.Equal(13, audit.TotalOptimizationCandidatesCount);
            Assert.Equal(3, audit.InvalidStudyPlansCount);
            Assert.Equal(4, audit.InvalidBindingsCount);
        }
    }
}
