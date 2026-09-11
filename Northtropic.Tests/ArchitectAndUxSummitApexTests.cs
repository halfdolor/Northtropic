using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxSummitApexTests
    {
        [Fact]
        public async Task GetAdaptiveQuestionsAsync_PrioritizesSameSubject_WhenCategoryCountInsufficient()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                // 题库灌入：初中数学 - 勾股定理 只有 2 题；初中数学 - 一次函数 5 题；初中英语 10 题
                var mathGougu1 = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "初中数学",
                    Category = "勾股定理",
                    GradeTarget = "初中二年级",
                    Stem = "在直角三角形ABC中，已知两直角边长为3和4，则斜边长为？",
                    OptionsJson = "[\"4\",\"5\",\"6\",\"7\"]",
                    CorrectAnswer = "B",
                    Difficulty = 2,
                    IsPublic = true
                };
                var mathGougu2 = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "初中数学",
                    Category = "勾股定理",
                    GradeTarget = "初中二年级",
                    Stem = "直角三角形两边长分别为5和12，则第三边长可能为？",
                    OptionsJson = "[\"13\",\"\\u221a119\",\"13或\\u221a119\",\"15\"]",
                    CorrectAnswer = "C",
                    Difficulty = 3,
                    IsPublic = true
                };

                // 同学科其他考点
                var mathOtherQuestions = Enumerable.Range(1, 5).Select(i => new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "初中数学",
                    Category = "一次函数",
                    GradeTarget = "初中二年级",
                    Stem = $"一次函数 y = {i}x + 1 的图象经过第几象限？",
                    OptionsJson = "[\"一、二、三\",\"一、二、四\",\"二、三、四\",\"一、三、四\"]",
                    CorrectAnswer = "A",
                    Difficulty = 2,
                    IsPublic = true
                }).ToList();

                // 跨学科题目 (英语)
                var englishQuestions = Enumerable.Range(1, 10).Select(i => new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "初中英语",
                    Category = "阅读理解",
                    GradeTarget = "初中二年级",
                    Stem = $"English reading comprehension question {i}",
                    OptionsJson = "[\"A\",\"B\",\"C\",\"D\"]",
                    CorrectAnswer = "A",
                    Difficulty = 2,
                    IsPublic = true
                }).ToList();

                context.Questions.Add(mathGougu1);
                context.Questions.Add(mathGougu2);
                context.Questions.AddRange(mathOtherQuestions);
                context.Questions.AddRange(englishQuestions);
                await context.SaveChangesAsync();

                var sessionMock = new FakeUserSessionService();
                var gamificationService = new GamificationService(context, sessionMock);
                var practiceService = new PracticeService(context, gamificationService, sessionMock, new FakeAiTutorService());

                // 学生发起“初中数学” - “勾股定理” 5 题自适应练习 (该考点仅有 2 题)
                var result = await practiceService.GetAdaptiveQuestionsAsync(
                    userId: Guid.NewGuid(),
                    grade: "初中二年级",
                    subject: "初中数学",
                    category: "勾股定理",
                    count: 5
                );

                // 架构师契约：返回题目数量应达标 (5 题)
                Assert.Equal(5, result.Count);

                // 架构师与用户体验核心契约：同科降级优先，绝对不能出现跨学科英语题目！
                Assert.All(result, q =>
                {
                    Assert.Equal("初中数学", q.Subject);
                });
            }
        }

        [Fact]
        public async Task CreateHomeworkAssignment_DefensiveClamping_PreventsInvalidData()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var parent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "全职家长",
                    Role = UserRole.Parent
                };
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "好学儿童",
                    Role = UserRole.Student
                };

                context.Users.AddRange(parent, student);
                context.StudentParentBindings.Add(new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = parent.Id,
                    StudentUserId = student.Id,
                    RelationType = "监护人"
                });
                await context.SaveChangesAsync();

                var sessionMock = new FakeUserSessionService { ActiveUser = parent };
                var gamificationService = new GamificationService(context, sessionMock);
                var practiceService = new PracticeService(context, gamificationService, sessionMock, new FakeAiTutorService());

                // 1. 测试溢出边界输入：题量 999 题、难度 99 级
                var overflowAssignment = await practiceService.CreateHomeworkAssignmentAsync(
                    creatorUserId: parent.Id,
                    studentUserId: student.Id,
                    title: "",
                    subject: "初中数学",
                    category: "几何全等",
                    questionCount: 999,
                    difficulty: 99,
                    deadline: DateTime.Now.AddDays(1),
                    note: "加油做！"
                );

                Assert.Equal(100, overflowAssignment.QuestionCount);
                Assert.Equal(5, overflowAssignment.TargetDifficulty);
                Assert.Contains("靶向强化作业", overflowAssignment.Title);

                // 2. 测试下溢边界输入：题量 -5 题、难度 -1 级
                var underflowAssignment = await practiceService.CreateHomeworkAssignmentAsync(
                    creatorUserId: parent.Id,
                    studentUserId: student.Id,
                    title: "   ",
                    subject: "初中物理",
                    category: "电学",
                    questionCount: -5,
                    difficulty: -1,
                    deadline: null,
                    note: ""
                );

                Assert.Equal(1, underflowAssignment.QuestionCount);
                Assert.Equal(1, underflowAssignment.TargetDifficulty);
                Assert.Contains("靶向强化作业", underflowAssignment.Title);
            }
        }

        [Fact]
        public async Task CompleteHomeworkAssignment_DefensiveClamping_AndIdempotency()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var creator = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "布置老师",
                    Role = UserRole.Teacher
                };

                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "答题学员",
                    Role = UserRole.Student,
                    Exp = 100,
                    Coins = 50,
                    Level = 1
                };

                var assignment = new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    CreatorUserId = creator.Id,
                    StudentUserId = student.Id,
                    Title = "浮力强化",
                    Subject = "初中物理",
                    QuestionCount = 10,
                    IsCompleted = false
                };

                context.Users.AddRange(creator, student);
                context.HomeworkAssignments.Add(assignment);
                await context.SaveChangesAsync();

                var sessionMock = new FakeUserSessionService { ActiveUser = student };
                var gamificationService = new GamificationService(context, sessionMock);
                var practiceService = new PracticeService(context, gamificationService, sessionMock, new FakeAiTutorService());

                // 1. 提交溢出数值：做对 15 题 (总共 10 题)，得分 150 分
                bool completeSuccess = await practiceService.CompleteHomeworkAssignmentAsync(
                    assignmentId: assignment.Id,
                    correctCount: 15,
                    totalAnswered: 10,
                    score: 150,
                    studentUserId: student.Id
                );

                Assert.True(completeSuccess);
                var completed = await context.HomeworkAssignments.FindAsync(assignment.Id);
                Assert.NotNull(completed);
                Assert.True(completed.IsCompleted);
                Assert.Equal(10, completed.CorrectCount); // 钳位为总题数 10
                Assert.Equal(100, completed.Score); // 钳位为满分 100
                Assert.Equal(100, completed.AccuracyRate);

                // 2. 幂等性校验：再次提交不会重复派发奖励
                int expBefore = student.Exp;
                bool secondComplete = await practiceService.CompleteHomeworkAssignmentAsync(
                    assignmentId: assignment.Id,
                    correctCount: 5,
                    totalAnswered: 10,
                    score: 50,
                    studentUserId: student.Id
                );
                Assert.True(secondComplete);
                Assert.Equal(expBefore, student.Exp);
            }
        }

        [Fact]
        public async Task Gamification_ExpAndGoldPotion_StacksDurationCumulatively()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "药水大师",
                    Coins = 200,
                    Exp = 0,
                    ExpBoostUntil = null,
                    GoldBoostUntil = null
                };
                context.Users.Add(user);
                await context.SaveChangesAsync();

                var sessionMock = new FakeUserSessionService { ActiveUser = user };
                var gamificationService = new GamificationService(context, sessionMock);

                // 1. 首次购买经验药水 (消耗 40 金币)
                var (expSuccess1, _) = await gamificationService.BuyExpPotionAsync(user.Id);
                Assert.True(expSuccess1);
                Assert.Equal(160, user.Coins);
                Assert.NotNull(user.ExpBoostUntil);
                var firstExpiry = user.ExpBoostUntil.Value;
                Assert.True(firstExpiry > DateTime.Now.AddMinutes(55));

                // 2. 再次购买经验药水 -> 触发顺延叠加，截止时间应再增加 1 小时 (累计约 2 小时)
                var (expSuccess2, _) = await gamificationService.BuyExpPotionAsync(user.Id);
                Assert.True(expSuccess2);
                Assert.Equal(120, user.Coins);
                Assert.NotNull(user.ExpBoostUntil);
                Assert.True(user.ExpBoostUntil.Value > firstExpiry.AddMinutes(59));

                // 3. 购买金币暴击符也是同样的顺延叠加
                var (goldSuccess1, _) = await gamificationService.BuyGoldPotionAsync(user.Id);
                Assert.True(goldSuccess1);
                var firstGoldExpiry = user.GoldBoostUntil!.Value;

                var (goldSuccess2, _) = await gamificationService.BuyGoldPotionAsync(user.Id);
                Assert.True(goldSuccess2);
                Assert.True(user.GoldBoostUntil!.Value > firstGoldExpiry.AddMinutes(59));
            }
        }

        [Fact]
        public async Task SendHomeworkReminderNudge_CustomWarmTemplate_PersistsAndDeliversNote()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var parent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "温暖妈妈",
                    Role = UserRole.Parent
                };
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "可爱宝贝",
                    Role = UserRole.Student,
                    ParentEncouragementNote = ""
                };

                context.Users.AddRange(parent, student);
                context.StudentParentBindings.Add(new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = parent.Id,
                    StudentUserId = student.Id,
                    RelationType = "母亲"
                });

                var assignment = new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    Title = "圆的切线判定定理精练",
                    CreatorUserId = parent.Id,
                    StudentUserId = student.Id,
                    Subject = "初中数学",
                    QuestionCount = 5,
                    IsCompleted = false
                };
                context.HomeworkAssignments.Add(assignment);
                await context.SaveChangesAsync();

                var sessionMock = new FakeUserSessionService { ActiveUser = parent };
                var gamificationService = new GamificationService(context, sessionMock);
                var practiceService = new PracticeService(context, gamificationService, sessionMock, new FakeAiTutorService());

                string warmTemplate = "🌟 宝贝，做完作业记得休息眼睛，劳逸结合最棒！";
                var nudgeResult = await practiceService.SendHomeworkReminderNudgeAsync(parent.Id, assignment.Id, warmTemplate);

                Assert.True(nudgeResult.Success);
                Assert.Contains("提醒", nudgeResult.Message);

                // 校验学生用户数据：暖心寄语已实时更新并同步
                var refreshedStudent = await context.Users.FindAsync(student.Id);
                Assert.NotNull(refreshedStudent);
                Assert.Contains("做完作业记得休息眼睛", refreshedStudent.ParentEncouragementNote);
                Assert.NotNull(refreshedStudent.ParentNoteUpdatedAt);
            }
        }
    }
}
