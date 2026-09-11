using System;
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
    public class DemoAccountsLifecycleTests
    {
        private static async Task ClearSeedUsersAsync(AppDbContext context)
        {
            context.StudentParentBindings.RemoveRange(context.StudentParentBindings);
            context.Users.RemoveRange(context.Users);
            await context.SaveChangesAsync();
        }

        [Fact]
        public async Task InitialEvaluationMode_DefaultAdminPhone_DemoAccountsRemainApproved()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                await ClearSeedUsersAsync(context);

                var admin = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "超级管理员",
                    Role = UserRole.SuperAdmin,
                    PhoneNumber = "13800000000",
                    Password = PasswordHasher.HashPassword("123456"),
                    AccountStatus = UserAccountStatus.Approved,
                    IsBuiltInDemo = false
                };
                var demoStudent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "李小明",
                    Role = UserRole.Student,
                    PhoneNumber = "13800000001",
                    Password = PasswordHasher.HashPassword("123456"),
                    AccountStatus = UserAccountStatus.Approved,
                    IsBuiltInDemo = true
                };
                context.Users.AddRange(admin, demoStudent);
                await context.SaveChangesAsync();

                var service = new UserSessionService(context, new HttpClient());
                await service.SyncDemoAccountsLifecycleAsync();

                var isProd = await service.IsProductionModeAsync();
                Assert.False(isProd);

                var reloadedStudent = await context.Users.FindAsync(demoStudent.Id);
                Assert.NotNull(reloadedStudent);
                Assert.Equal(UserAccountStatus.Approved, reloadedStudent.AccountStatus);

                // Try demo login
                var (success, user, msg) = await service.QuickLoginDemoUserAsync("Student");
                Assert.True(success);
                Assert.NotNull(user);
                Assert.Equal("李小明", user.Username);
            }
        }

        [Fact]
        public async Task SwitchToProductionMode_AdminPhoneChanged_DemoAccountsAreDisabled()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                await ClearSeedUsersAsync(context);

                var admin = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "超级管理员",
                    Role = UserRole.SuperAdmin,
                    PhoneNumber = "13800000000",
                    Password = PasswordHasher.HashPassword("123456"),
                    AccountStatus = UserAccountStatus.Approved,
                    IsBuiltInDemo = false
                };
                var demoStudent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "李小明",
                    Role = UserRole.Student,
                    PhoneNumber = "13800000001",
                    Password = PasswordHasher.HashPassword("123456"),
                    AccountStatus = UserAccountStatus.Approved,
                    IsBuiltInDemo = true
                };
                var regularUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "真实学生",
                    Role = UserRole.Student,
                    PhoneNumber = "13900000099",
                    Password = PasswordHasher.HashPassword("123456"),
                    AccountStatus = UserAccountStatus.Approved,
                    IsBuiltInDemo = false
                };
                context.Users.AddRange(admin, demoStudent, regularUser);
                await context.SaveChangesAsync();

                var service = new UserSessionService(context, new HttpClient());

                // Admin updates phone to production number
                var (updateSuccess, _) = await service.UpdateUserProfileAsync(
                    admin.Id,
                    "正式管理员",
                    "🛡️",
                    "admin@example.com",
                    "13912345678",
                    "高中三年级"
                );
                Assert.True(updateSuccess);

                // Verify production mode is now active
                var isProd = await service.IsProductionModeAsync();
                Assert.True(isProd);

                // Verify demo account is disabled
                var reloadedStudent = await context.Users.FindAsync(demoStudent.Id);
                Assert.NotNull(reloadedStudent);
                Assert.Equal(UserAccountStatus.Disabled, reloadedStudent.AccountStatus);

                // Quick login should fail
                var (quickOk, _, quickMsg) = await service.QuickLoginDemoUserAsync("Student");
                Assert.False(quickOk);
                Assert.Contains("已停用", quickMsg);

                // Direct password login for demo user should fail
                var (loginOk, _, loginMsg) = await service.LoginWithPasswordAsync("13800000001", "123456");
                Assert.False(loginOk);
                Assert.Contains("已停用", loginMsg);

                // Direct password login for regular user should succeed
                var (regLoginOk, regUser, _) = await service.LoginWithPasswordAsync("13900000099", "123456");
                Assert.True(regLoginOk);
                Assert.NotNull(regUser);
                Assert.Equal("真实学生", regUser.Username);
            }
        }

        [Fact]
        public async Task RevertToEvaluationMode_AdminPhoneRestored_DemoAccountsReactivated()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                await ClearSeedUsersAsync(context);

                var admin = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "超级管理员",
                    Role = UserRole.SuperAdmin,
                    PhoneNumber = "13912345678", // Production phone initially
                    Password = PasswordHasher.HashPassword("123456"),
                    AccountStatus = UserAccountStatus.Approved,
                    IsBuiltInDemo = false
                };
                var demoStudent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "李小明",
                    Role = UserRole.Student,
                    PhoneNumber = "13800000001",
                    Password = PasswordHasher.HashPassword("123456"),
                    AccountStatus = UserAccountStatus.Disabled,
                    IsBuiltInDemo = true
                };
                context.Users.AddRange(admin, demoStudent);
                await context.SaveChangesAsync();

                var service = new UserSessionService(context, new HttpClient());

                // Admin updates phone back to default 13800000000
                var (updateSuccess, _) = await service.UpdateUserProfileAsync(
                    admin.Id,
                    "超级管理员",
                    "🛡️",
                    "admin@northtropic.com",
                    "13800000000",
                    "初中二年级"
                );
                Assert.True(updateSuccess);

                // Production mode should be false
                var isProd = await service.IsProductionModeAsync();
                Assert.False(isProd);

                // Demo account should be restored to Approved
                var reloadedStudent = await context.Users.FindAsync(demoStudent.Id);
                Assert.NotNull(reloadedStudent);
                Assert.Equal(UserAccountStatus.Approved, reloadedStudent.AccountStatus);

                // Quick login succeeds again
                var (quickOk, user, _) = await service.QuickLoginDemoUserAsync("Student");
                Assert.True(quickOk);
                Assert.NotNull(user);
                Assert.Equal("李小明", user.Username);
            }
        }

        [Fact]
        public async Task RevertToEvaluationMode_AdminDeleted_DemoAccountsReactivated()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                await ClearSeedUsersAsync(context);

                var admin = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "超级管理员",
                    Role = UserRole.SuperAdmin,
                    PhoneNumber = "13912345678",
                    Password = PasswordHasher.HashPassword("123456"),
                    AccountStatus = UserAccountStatus.Approved,
                    IsBuiltInDemo = false
                };
                var demoTeacher = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "张老师",
                    Role = UserRole.Teacher,
                    PhoneNumber = "13800000003",
                    Password = PasswordHasher.HashPassword("123456"),
                    AccountStatus = UserAccountStatus.Disabled,
                    IsBuiltInDemo = true
                };
                context.Users.AddRange(admin, demoTeacher);
                await context.SaveChangesAsync();

                var service = new UserSessionService(context, new HttpClient());

                // Delete the production admin
                var deleted = await service.DeleteUserAsync(admin.Id);
                Assert.True(deleted);

                var isProd = await service.IsProductionModeAsync();
                Assert.False(isProd);

                var reloadedTeacher = await context.Users.FindAsync(demoTeacher.Id);
                Assert.NotNull(reloadedTeacher);
                Assert.Equal(UserAccountStatus.Approved, reloadedTeacher.AccountStatus);
            }
        }

        [Fact]
        public void DbInitializer_SyncDemoAccountsLifecycle_SwitchesCorrectly()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                // Run full DbInitializer
                DbInitializer.Initialize(context);

                var admin = context.Users.FirstOrDefault(u => u.Role == UserRole.SuperAdmin);
                Assert.NotNull(admin);
                Assert.Equal("13800000000", admin.PhoneNumber);

                var demoUsers = context.Users.Where(u => u.IsBuiltInDemo).ToList();
                Assert.NotEmpty(demoUsers);
                Assert.All(demoUsers, u => Assert.Equal(UserAccountStatus.Approved, u.AccountStatus));

                // Change admin to production phone
                admin.PhoneNumber = "13987654321";
                context.SaveChanges();

                DbInitializer.SyncDemoAccountsLifecycle(context);

                var disabledDemos = context.Users.Where(u => u.IsBuiltInDemo).ToList();
                Assert.All(disabledDemos, u => Assert.Equal(UserAccountStatus.Disabled, u.AccountStatus));

                // Reset back to default phone
                admin.PhoneNumber = "13800000000";
                context.SaveChanges();

                DbInitializer.SyncDemoAccountsLifecycle(context);

                var restoredDemos = context.Users.Where(u => u.IsBuiltInDemo).ToList();
                Assert.All(restoredDemos, u => Assert.Equal(UserAccountStatus.Approved, u.AccountStatus));
            }
        }
    }
}
