using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxTranscendenceEvolutionTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;

        public ArchitectAndUxTranscendenceEvolutionTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        #region 1. 系统架构师：题库批量运维闭环与 RBAC 鉴权测试 (BatchRetractToPrivateAsync)

        [Fact]
        public async Task BatchRetractToPrivateAsync_SuperAdmin_CanRetractAnyQuestions()
        {
            // Arrange
            var admin = new User { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.SuperAdmin };
            var teacher = new User { Id = Guid.NewGuid(), Username = "teacher", Role = UserRole.Teacher };
            _context.Users.AddRange(admin, teacher);

            var q1 = new Question { Id = Guid.NewGuid(), Stem = "Q1", Type = QuestionType.SingleChoice, CorrectAnswer = "A", IsPublic = true, PublishStatus = PublishStatusEnum.Approved, CreatedByUserId = teacher.Id };
            var q2 = new Question { Id = Guid.NewGuid(), Stem = "Q2", Type = QuestionType.SingleChoice, CorrectAnswer = "B", IsPublic = true, PublishStatus = PublishStatusEnum.Approved, CreatedByUserId = Guid.NewGuid() };
            _context.Questions.AddRange(q1, q2);
            await _context.SaveChangesAsync();

            var service = new QuestionManagementService(_context);

            // Act
            int retracted = await service.BatchRetractToPrivateAsync(new[] { q1.Id, q2.Id }, admin.Id);

            // Assert
            Assert.Equal(2, retracted);
            var updatedQ1 = await _context.Questions.FindAsync(q1.Id);
            var updatedQ2 = await _context.Questions.FindAsync(q2.Id);
            Assert.False(updatedQ1!.IsPublic);
            Assert.Equal(PublishStatusEnum.Private, updatedQ1.PublishStatus);
            Assert.False(updatedQ2!.IsPublic);
            Assert.Equal(PublishStatusEnum.Private, updatedQ2.PublishStatus);
        }

        [Fact]
        public async Task BatchRetractToPrivateAsync_Teacher_CanOnlyRetractOwnQuestions()
        {
            // Arrange
            var teacher1 = new User { Id = Guid.NewGuid(), Username = "teacher1", Role = UserRole.Teacher };
            var teacher2 = new User { Id = Guid.NewGuid(), Username = "teacher2", Role = UserRole.Teacher };
            _context.Users.AddRange(teacher1, teacher2);

            var qOwn = new Question { Id = Guid.NewGuid(), Stem = "Own Q", Type = QuestionType.SingleChoice, CorrectAnswer = "A", IsPublic = true, PublishStatus = PublishStatusEnum.Approved, CreatedByUserId = teacher1.Id };
            var qOther = new Question { Id = Guid.NewGuid(), Stem = "Other Q", Type = QuestionType.SingleChoice, CorrectAnswer = "B", IsPublic = true, PublishStatus = PublishStatusEnum.Approved, CreatedByUserId = teacher2.Id };
            _context.Questions.AddRange(qOwn, qOther);
            await _context.SaveChangesAsync();

            var service = new QuestionManagementService(_context);

            // Act
            int retracted = await service.BatchRetractToPrivateAsync(new[] { qOwn.Id, qOther.Id }, teacher1.Id);

            // Assert
            Assert.Equal(1, retracted);
            var updatedOwn = await _context.Questions.FindAsync(qOwn.Id);
            var updatedOther = await _context.Questions.FindAsync(qOther.Id);
            Assert.False(updatedOwn!.IsPublic);
            Assert.Equal(PublishStatusEnum.Private, updatedOwn.PublishStatus);
            Assert.True(updatedOther!.IsPublic); // 未被越权修改
            Assert.Equal(PublishStatusEnum.Approved, updatedOther.PublishStatus);
        }

        [Fact]
        public async Task BatchRetractToPrivateAsync_Student_IsBlocked()
        {
            // Arrange
            var student = new User { Id = Guid.NewGuid(), Username = "student", Role = UserRole.Student };
            _context.Users.Add(student);

            var q = new Question { Id = Guid.NewGuid(), Stem = "Q", Type = QuestionType.SingleChoice, CorrectAnswer = "A", IsPublic = true, PublishStatus = PublishStatusEnum.Approved, CreatedByUserId = student.Id };
            _context.Questions.Add(q);
            await _context.SaveChangesAsync();

            var service = new QuestionManagementService(_context);

            // Act
            int retracted = await service.BatchRetractToPrivateAsync(new[] { q.Id }, student.Id);

            // Assert
            Assert.Equal(0, retracted);
            var updatedQ = await _context.Questions.FindAsync(q.Id);
            Assert.True(updatedQ!.IsPublic);
        }

        #endregion

        #region 2. 系统架构师：家长关怀与激励系统防 IDOR 越权与领域不变量测试

        [Fact]
        public async Task UserSessionService_UpdateParentEncouragementNote_IDOR_Defense()
        {
            // Arrange
            var parent1 = new User { Id = Guid.NewGuid(), Username = "parent1", Role = UserRole.Parent };
            var parent2 = new User { Id = Guid.NewGuid(), Username = "parent2", Role = UserRole.Parent };
            var student = new User { Id = Guid.NewGuid(), Username = "student", Role = UserRole.Student };
            _context.Users.AddRange(parent1, parent2, student);

            // 仅 parent1 与 student 建立了绑定
            _context.StudentParentBindings.Add(new StudentParentBinding
            {
                Id = Guid.NewGuid(),
                ParentUserId = parent1.Id,
                StudentUserId = student.Id,
                RelationType = "父亲"
            });
            await _context.SaveChangesAsync();

            var sessionService = new UserSessionService(_context, new System.Net.Http.HttpClient());

            // Act 1: 未绑定的 parent2 试图横向越权给 student 写寄语
            var failResult = await sessionService.UpdateParentEncouragementNoteAsync(student.Id, "恶意越权寄语", callerUserId: parent2.Id);

            // Assert 1: 被安全门禁拦截
            Assert.False(failResult.Success);
            Assert.Contains("越权拦截", failResult.Message);

            // Act 2: 已绑定的 parent1 发送寄语
            var successResult = await sessionService.UpdateParentEncouragementNoteAsync(student.Id, "加油，宝贝！", callerUserId: parent1.Id);

            // Assert 2: 成功发布
            Assert.True(successResult.Success);
            var refreshedStudent = await _context.Users.FindAsync(student.Id);
            Assert.Equal("加油，宝贝！", refreshedStudent!.ParentEncouragementNote);
        }

        [Fact]
        public async Task UserSessionService_AwardParentPraiseReward_IDOR_And_Invariants()
        {
            // Arrange
            var parent = new User { Id = Guid.NewGuid(), Username = "parent", Role = UserRole.Parent };
            var student = new User { Id = Guid.NewGuid(), Username = "student", Role = UserRole.Student, Coins = 50, Exp = 80, Level = 1 };
            _context.Users.AddRange(parent, student);

            _context.StudentParentBindings.Add(new StudentParentBinding
            {
                Id = Guid.NewGuid(),
                ParentUserId = parent.Id,
                StudentUserId = student.Id,
                RelationType = "母亲"
            });
            await _context.SaveChangesAsync();

            var sessionService = new UserSessionService(_context, new System.Net.Http.HttpClient());

            // Act: 颁发赞赏，超出上限的金币被安全截断 (99999 -> 500)，且自动触发升级
            var result = await sessionService.AwardParentPraiseRewardAsync(student.Id, "勤奋标兵", "最近做题很认真！", 99999, callerUserId: parent.Id);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(550, result.NewCoins); // 50 + 500
            var refreshedStudent = await _context.Users.FindAsync(student.Id);
            Assert.True(refreshedStudent!.Level > 1); // 自动升级
            Assert.Equal(refreshedStudent.Exp, result.NewExp); // 经验值与升级后余量完全同步一致
            Assert.Equal(550, refreshedStudent.Coins);
        }

        [Fact]
        public async Task UserSessionService_UnbindStudent_IDOR_Defense()
        {
            // Arrange
            var parentOwner = new User { Id = Guid.NewGuid(), Username = "parent_owner", Role = UserRole.Parent };
            var parentAttacker = new User { Id = Guid.NewGuid(), Username = "parent_attacker", Role = UserRole.Parent };
            var student = new User { Id = Guid.NewGuid(), Username = "student", Role = UserRole.Student };
            _context.Users.AddRange(parentOwner, parentAttacker, student);

            _context.StudentParentBindings.Add(new StudentParentBinding
            {
                Id = Guid.NewGuid(),
                ParentUserId = parentOwner.Id,
                StudentUserId = student.Id,
                RelationType = "家长"
            });
            await _context.SaveChangesAsync();

            var sessionService = new UserSessionService(_context, new System.Net.Http.HttpClient());

            // Act 1: 攻击者试图解绑非本人绑定的学生
            var failResult = await sessionService.UnbindStudentAsync(parentOwner.Id, student.Id, callerUserId: parentAttacker.Id);

            // Assert 1: 被越权拦截
            Assert.False(failResult.Success);
            Assert.Contains("越权拦截", failResult.Message);
            Assert.True(await _context.StudentParentBindings.AnyAsync(b => b.ParentUserId == parentOwner.Id && b.StudentUserId == student.Id));

            // Act 2: 本人解绑
            var okResult = await sessionService.UnbindStudentAsync(parentOwner.Id, student.Id, callerUserId: parentOwner.Id);

            // Assert 2: 解绑成功
            Assert.True(okResult.Success);
            Assert.False(await _context.StudentParentBindings.AnyAsync(b => b.ParentUserId == parentOwner.Id && b.StudentUserId == student.Id));
        }

        #endregion

        #region 3. 系统架构师：错题副本领域不变量自愈引擎测试 (HealErrorBookInvariantsAsync)

        [Fact]
        public async Task SystemHealthService_HealErrorBookInvariantsAsync_RepairsAllAnomalies()
        {
            // Arrange
            var user = new User { Id = Guid.NewGuid(), Username = "u1" };
            _context.Users.Add(user);

            var q1 = new Question { Id = Guid.NewGuid(), Stem = "Q1", Type = QuestionType.SingleChoice, CorrectAnswer = "A" };
            var q2 = new Question { Id = Guid.NewGuid(), Stem = "Q2", Type = QuestionType.SingleChoice, CorrectAnswer = "B" };
            _context.Questions.AddRange(q1, q2);

            // 异常 1: RevisionCount 为负数
            var item1 = new ErrorItem
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = q1.Id,
                RevisionCount = -3,
                IsMastered = false,
                ErrorReasonCategory = "概念模糊"
            };

            // 异常 2: 已掌握但 LastRevisedAt 为 null，且类别为空白
            var item2 = new ErrorItem
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = q2.Id,
                RevisionCount = 2,
                IsMastered = true,
                LastRevisedAt = null,
                ErrorReasonCategory = "   "
            };

            // 异常 3: 重复记录 (同一用户针对 q1 的第二条记录)
            var duplicateItem1 = new ErrorItem
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = q1.Id,
                RevisionCount = 5,
                IsMastered = true, // 更优状态
                LastRevisedAt = DateTime.Now,
                ErrorReasonCategory = "逻辑计算错误"
            };

            _context.ErrorItems.AddRange(item1, item2, duplicateItem1);
            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);

            // Act
            int healedCount = await healthService.HealErrorBookInvariantsAsync();

            // Assert
            Assert.True(healedCount >= 3);

            // 验证去重：q1 应该只保留一条规范记录，且保留掌握状态为 true 与更高的 RevisionCount
            var q1Items = await _context.ErrorItems.Where(e => e.UserId == user.Id && e.QuestionId == q1.Id).ToListAsync();
            Assert.Single(q1Items);
            var retainedQ1 = q1Items.First();
            Assert.True(retainedQ1.IsMastered);
            Assert.Equal(5, retainedQ1.RevisionCount);

            // 验证 item2 属性自愈
            var refreshedItem2 = await _context.ErrorItems.FindAsync(item2.Id);
            Assert.NotNull(refreshedItem2);
            Assert.NotNull(refreshedItem2!.LastRevisedAt);
            Assert.Equal("未分类", refreshedItem2.ErrorReasonCategory);
        }

        #endregion

        #region 4. 用户体验专家：STEM 填空判分引擎深度评测测试 (能量/功、电压、电阻、电流、化学俗名)

        [Theory]
        [InlineData("1 kW·h", "3.6*10^6 J")]
        [InlineData("1 kW*h", "3.6×10^6 J")]
        [InlineData("1度", "3.6e6 J")]
        [InlineData("500 J", "0.5 kJ")]
        [InlineData("2 MJ", "2000 kJ")]
        [InlineData("3000 J", "3 千焦")]
        public void CheckFillInBlankMatch_EnergyAndWork_Equivalence(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("1.5 V", "1500 mV")]
        [InlineData("220 V", "0.22 kV")]
        [InlineData("50 μV", "0.05 mV")]
        [InlineData("380伏", "0.38千伏")]
        public void CheckFillInBlankMatch_Voltage_Equivalence(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("2 kΩ", "2000 Ω")]
        [InlineData("1.5 MΩ", "1500 kΩ")]
        [InlineData("1000 欧姆", "1 kΩ")]
        [InlineData("4.7kohm", "4700 ohm")]
        public void CheckFillInBlankMatch_Resistance_Equivalence(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("0.5 A", "500 mA")]
        [InlineData("2.5 mA", "2500 μA")]
        [InlineData("1000 A", "1 kA")]
        [InlineData("2安培", "2000毫安")]
        public void CheckFillInBlankMatch_Current_Equivalence(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("沼气", "CH4")]
        [InlineData("瓦斯", "甲烷")]
        [InlineData("天然气", "CH4")]
        [InlineData("方解石", "CaCO3")]
        [InlineData("水晶", "SiO2")]
        [InlineData("石英", "二氧化硅")]
        [InlineData("萤石", "CaF2")]
        [InlineData("荧石", "氟化钙")]
        [InlineData("孔雀石", "Cu2(OH)2CO3")]
        [InlineData("菱铁矿", "FeCO3")]
        [InlineData("黄铁矿", "FeS2")]
        [InlineData("生石膏", "CaSO4·2H2O")]
        [InlineData("熟石膏", "2CaSO4·H2O")]
        public void CheckFillInBlankMatch_ChemicalMineralsAndSynonyms_Equivalence(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        #endregion
    }
}
