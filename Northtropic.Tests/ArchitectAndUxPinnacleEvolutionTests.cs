using System;
using System.Collections.Generic;
using System.Linq;
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
    public class ArchitectAndUxPinnacleEvolutionTests
    {
        [Fact]
        public void CheckFillInBlankMatch_ScientificNotation_Equivalence()
        {
            // 1. 标准浮点 vs LaTeX 乘方
            Assert.True(PracticeService.CheckFillInBlankMatch(@"3.0 \times 10^8", "3e8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("3e8", @"3.0 \times 10^8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("3.0x10^8", "3*10^{8}"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"3.0 \times 10^8 m/s", "3e8")); // 带单位容错

            // 2. 极微小物理常数 (如元电荷 1.6e-19)
            Assert.True(PracticeService.CheckFillInBlankMatch("1.6*10^-19", "1.6e-19"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"1.60 \times 10^{-19}", "1.6e-19"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1.6×10^-19", @"1.6 * 10^{-19}"));

            // 3. 错误数值应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("5e8", "3e8"));
            Assert.False(PracticeService.CheckFillInBlankMatch("1.6*10^-18", "1.6*10^-19"));
        }

        [Fact]
        public void CheckFillInBlankMatch_ChemicalFormulas_SubscriptsAndIons()
        {
            // 1. 化学分子式下标下划线容错: Ca(OH)_2 vs Ca(OH)2
            Assert.True(PracticeService.CheckFillInBlankMatch("Ca(OH)_2", "Ca(OH)2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("h_2o", "H2O"));
            Assert.True(PracticeService.CheckFillInBlankMatch("CuSO_4", "CuSO4"));
            Assert.True(PracticeService.CheckFillInBlankMatch("co_2", "CO2"));

            // 2. 离子符号电荷上标容错: Fe^{3+} vs Fe3+ vs Fe^3+
            Assert.True(PracticeService.CheckFillInBlankMatch("Fe^{3+}", "Fe3+"));
            Assert.True(PracticeService.CheckFillInBlankMatch("Fe^3+", "Fe^{3+}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("SO_4^{2-}", "so42-"));

            // 3. 不同化学价态/化学式应判定不匹配
            Assert.False(PracticeService.CheckFillInBlankMatch("Fe^{2+}", "Fe3+"));
            Assert.False(PracticeService.CheckFillInBlankMatch("H2O2", "H2O"));
        }

        [Fact]
        public void CheckFillInBlankMatch_MultiBlank_StructuredParsing()
        {
            // 1. 有序题号标号切分: (1) 动能 (2) 势能 vs 动能; 势能
            Assert.True(PracticeService.CheckFillInBlankMatch("(1) 动能 (2) 势能", "动能; 势能"));
            Assert.True(PracticeService.CheckFillInBlankMatch("动能；势能", "(1) 动能 (2) 势能"));
            Assert.True(PracticeService.CheckFillInBlankMatch("① 3 ② 5", "(1) 3 (2) 5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("① 3.0*10^8 ② 1.6*10^-19", "(1) 3e8 (2) 1.6e-19"));

            // 2. 无序多空等价比对 (如某些物理简答填空 "动能" 与 "势能" 次序颠倒)
            Assert.True(PracticeService.CheckFillInBlankMatch("势能; 动能", "(1) 动能 (2) 势能"));

            // 3. 错误内容应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("(1) 动能 (2) 内能", "(1) 动能 (2) 势能"));
        }

        [Fact]
        public void CheckFillInBlankMatch_IntervalAndSetNotation()
        {
            // 1. 数学区间无穷符号等价
            Assert.True(PracticeService.CheckFillInBlankMatch(@"(-\infty, 2]", "(-inf, 2]"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(-∞, 2]", @"(-\infty, 2]"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"[1, +\infty)", "[1, +inf)"));

            // 2. 空集符号与中文等价
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\emptyset", "∅"));
            Assert.True(PracticeService.CheckFillInBlankMatch("空集", "∅"));
            Assert.True(PracticeService.CheckFillInBlankMatch("空集", @"\emptyset"));
        }

        [Fact]
        public async Task ResetUserPasswordAsync_ParentRBAC_Enforcement()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var parent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "家长张三",
                    Role = UserRole.Parent,
                    Password = PasswordHasher.HashPassword("parent123")
                };
                var boundChild = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "孩子小张",
                    Role = UserRole.Student,
                    Password = PasswordHasher.HashPassword("oldpass123")
                };
                var strangerChild = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "陌生孩子小李",
                    Role = UserRole.Student,
                    Password = PasswordHasher.HashPassword("oldpass123")
                };
                var superAdmin = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "超级管理员",
                    Role = UserRole.SuperAdmin,
                    Password = PasswordHasher.HashPassword("admin123")
                };

                context.Users.AddRange(parent, boundChild, strangerChild, superAdmin);
                context.StudentParentBindings.Add(new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = parent.Id,
                    StudentUserId = boundChild.Id,
                    RelationType = "监护人",
                    CreatedAt = DateTime.Now
                });
                await context.SaveChangesAsync();

                using var httpClient = new HttpClient();
                var sessionService = new UserSessionService(context, httpClient);

                // 1. 越权尝试：家长张三尝试重置非绑定的陌生孩子小李的密码 -> 拦截，返回 false
                bool idorResult = await sessionService.ResetUserPasswordAsync(strangerChild.Id, "hacked123", callerUserId: parent.Id);
                Assert.False(idorResult);
                var strangerInDb = await context.Users.FindAsync(strangerChild.Id);
                Assert.NotNull(strangerInDb);
                Assert.True(PasswordHasher.VerifyPassword("oldpass123", strangerInDb.Password));

                // 2. 合法操作：家长张三重置已绑定子女小张的密码 -> 成功，且密码经过 PBKDF2 哈希加密
                bool legitResult = await sessionService.ResetUserPasswordAsync(boundChild.Id, "childNew666", callerUserId: parent.Id);
                Assert.True(legitResult);
                var boundInDb = await context.Users.FindAsync(boundChild.Id);
                Assert.NotNull(boundInDb);
                Assert.True(PasswordHasher.VerifyPassword("childNew666", boundInDb.Password));
                Assert.True(boundInDb.MustChangePassword);

                // 3. 超管特权：超级管理员可以重置任何账号密码
                bool adminResult = await sessionService.ResetUserPasswordAsync(strangerChild.Id, "adminReset888", callerUserId: superAdmin.Id);
                Assert.True(adminResult);
                strangerInDb = await context.Users.FindAsync(strangerChild.Id);
                Assert.NotNull(strangerInDb);
                Assert.True(PasswordHasher.VerifyPassword("adminReset888", strangerInDb.Password));
            }
        }

        [Fact]
        public async Task SendHomeworkReminderNudgeAsync_RBACAndSecurity()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var parent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "关心家长",
                    Role = UserRole.Parent
                };
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "待督促学生",
                    Role = UserRole.Student,
                    ParentEncouragementNote = ""
                };
                var stranger = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "路人甲",
                    Role = UserRole.Parent
                };

                context.Users.AddRange(parent, student, stranger);
                context.StudentParentBindings.Add(new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = parent.Id,
                    StudentUserId = student.Id,
                    RelationType = "监护人",
                    CreatedAt = DateTime.Now
                });

                var assignment = new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    Title = "欧姆定律专项课后巩固",
                    CreatorUserId = parent.Id,
                    StudentUserId = student.Id,
                    Subject = "初中物理",
                    QuestionCount = 5,
                    IsCompleted = false
                };
                context.HomeworkAssignments.Add(assignment);
                await context.SaveChangesAsync();

                var sessionMock = new FakeUserSessionService { ActiveUser = parent };
                var gamificationService = new GamificationService(context, sessionMock);
                var practiceService = new PracticeService(context, gamificationService, sessionMock, new FakeAiTutorService());

                // 1. 越权尝试：陌生家长尝试对该作业催促 -> 拦截
                var strangerNudge = await practiceService.SendHomeworkReminderNudgeAsync(stranger.Id, assignment.Id);
                Assert.False(strangerNudge.Success);
                Assert.Contains("无权", strangerNudge.Message);

                // 2. 合法家长发送催促提醒 -> 成功，学生档案实时更新寄语
                var parentNudge = await practiceService.SendHomeworkReminderNudgeAsync(parent.Id, assignment.Id);
                Assert.True(parentNudge.Success);
                Assert.Contains(student.Username, parentNudge.Message);

                var studentInDb = await context.Users.FindAsync(student.Id);
                Assert.NotNull(studentInDb);
                Assert.NotNull(studentInDb.ParentEncouragementNote);
                Assert.Contains(assignment.Title, studentInDb.ParentEncouragementNote);
                Assert.Contains("冲刺加油", studentInDb.ParentEncouragementNote);

                // 3. 若作业已完成，拒绝重复提醒
                assignment.IsCompleted = true;
                await context.SaveChangesAsync();

                var completedNudge = await practiceService.SendHomeworkReminderNudgeAsync(parent.Id, assignment.Id);
                Assert.False(completedNudge.Success);
                Assert.Contains("无需重复提醒", completedNudge.Message);
            }
        }

        [Fact]
        public async Task SubmitAnswerAsync_TargetUserId_DecoupledGrading()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var studentA = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "会话活跃用户A",
                    Role = UserRole.Student,
                    Exp = 100,
                    Coins = 50
                };
                var studentB = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "指定受测用户B",
                    Role = UserRole.Student,
                    Exp = 100,
                    Coins = 50
                };
                context.Users.AddRange(studentA, studentB);

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "真空中的光速约为多少？",
                    CorrectAnswer = @"3.0 \times 10^8 m/s",
                    Type = QuestionType.FillInBlank,
                    Subject = "初中物理",
                    BaseExpReward = 20,
                    Difficulty = 3
                };
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                // 会话上下文绑定用户 A
                var sessionService = new FakeUserSessionService { ActiveUser = studentA };
                var gamificationService = new GamificationService(context, sessionService);
                var practiceService = new PracticeService(context, gamificationService, sessionService, new FakeAiTutorService());

                // 提交答案，但显式指定 targetUserId 为 用户 B，且输入科学记数法 3e8
                var checkResult = await practiceService.SubmitAnswerAsync(
                    activeQuestion: question,
                    userAnswer: "3e8",
                    timeTakenSeconds: 15,
                    currentCombo: 2,
                    targetUserId: studentB.Id
                );

                // 验证答案识别成功
                Assert.True(checkResult.IsCorrect);

                // 验证奖励只发放给受测用户 B，而不是当前活跃会话用户 A
                var studentBInDb = await context.Users.FindAsync(studentB.Id);
                var studentAInDb = await context.Users.FindAsync(studentA.Id);

                Assert.NotNull(studentBInDb);
                Assert.NotNull(studentAInDb);

                Assert.True(studentBInDb.Exp > 100 || studentBInDb.Level > 1);
                Assert.True(studentBInDb.Coins > 50);
                Assert.Equal(100, studentAInDb.Exp); // 用户 A 未发生任何变更
                Assert.Equal(50, studentAInDb.Coins);

                // 答题记录也精准持久化绑定至用户 B
                var recordsB = await context.PracticeRecords.Where(r => r.UserId == studentB.Id).ToListAsync();
                Assert.Single(recordsB);
                Assert.True(recordsB[0].IsCorrect);
                Assert.Equal("3e8", recordsB[0].UserAnswer);
            }
        }
    }
}
