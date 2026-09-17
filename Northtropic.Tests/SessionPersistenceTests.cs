using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class SessionPersistenceTests
    {
        [Fact]
        public void GenerateSessionToken_WithValidUserId_ReturnsNonEmptyToken()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using var _ = connection;
            using var __ = context;

            var service = new UserSessionService(context, new HttpClient());
            var userId = Guid.NewGuid();

            var token = service.GenerateSessionToken(userId);

            Assert.False(string.IsNullOrWhiteSpace(token));
        }

        [Fact]
        public void GenerateSessionToken_WithEmptyGuid_ReturnsEmpty()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using var _ = connection;
            using var __ = context;

            var service = new UserSessionService(context, new HttpClient());

            var token = service.GenerateSessionToken(Guid.Empty);

            Assert.Equal(string.Empty, token);
        }

        [Fact]
        public async Task RestoreSessionFromToken_SimulatingPageRefresh_RestoresUserAndAuthenticatedState()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using var _ = connection;
            using var __ = context;

            var testUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "刷新测试学员",
                Role = UserRole.Student,
                Grade = "初中二年级",
                PhoneNumber = "13800112233"
            };
            context.Users.Add(testUser);
            await context.SaveChangesAsync();

            // 1. 模拟初次登录会话
            var loginSession = new UserSessionService(context, new HttpClient());
            var token = loginSession.GenerateSessionToken(testUser.Id);
            Assert.False(string.IsNullOrWhiteSpace(token));

            // 2. 模拟用户按下 F5 刷新浏览器 (全新创建的 Scoped 会话实例)
            var refreshedSession = new UserSessionService(context, new HttpClient());
            Assert.False(refreshedSession.IsAuthenticated);
            Assert.Null(refreshedSession.CurrentUserId);

            // 3. 执行刷新静默恢复
            var (success, restoredUser) = await refreshedSession.RestoreSessionFromTokenAsync(token);

            Assert.True(success);
            Assert.NotNull(restoredUser);
            Assert.Equal(testUser.Id, restoredUser.Id);
            Assert.Equal("刷新测试学员", restoredUser.Username);

            // 验证刷新后的会话直接进入已登录态
            Assert.True(refreshedSession.IsAuthenticated);
            Assert.Equal(testUser.Id, refreshedSession.CurrentUserId);
        }

        [Fact]
        public async Task RestoreSessionFromToken_WithTamperedToken_FailsGracefully()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using var _ = connection;
            using var __ = context;

            var session = new UserSessionService(context, new HttpClient());

            var (success, user) = await session.RestoreSessionFromTokenAsync("tampered_malicious_token_payload");

            Assert.False(success);
            Assert.Null(user);
            Assert.False(session.IsAuthenticated);
        }

        [Fact]
        public async Task RestoreSessionFromToken_WhenUserIsDisabled_FailsAuthentication()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using var _ = connection;
            using var __ = context;

            var disabledUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "已封禁学员",
                Role = UserRole.Student,
                AccountStatus = UserAccountStatus.Disabled
            };
            context.Users.Add(disabledUser);
            await context.SaveChangesAsync();

            var session = new UserSessionService(context, new HttpClient());
            var token = session.GenerateSessionToken(disabledUser.Id);

            var refreshedSession = new UserSessionService(context, new HttpClient());
            var (success, user) = await refreshedSession.RestoreSessionFromTokenAsync(token);

            Assert.False(success);
            Assert.Null(user);
            Assert.False(refreshedSession.IsAuthenticated);
        }

        [Fact]
        public async Task RestoreSessionFromToken_WhenUserDeleted_FailsAuthentication()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using var _ = connection;
            using var __ = context;

            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "待注销用户",
                Role = UserRole.Student
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();

            var session = new UserSessionService(context, new HttpClient());
            var token = session.GenerateSessionToken(user.Id);

            // 删除该用户
            context.Users.Remove(user);
            await context.SaveChangesAsync();

            var refreshedSession = new UserSessionService(context, new HttpClient());
            var (success, restoredUser) = await refreshedSession.RestoreSessionFromTokenAsync(token);

            Assert.False(success);
            Assert.Null(restoredUser);
            Assert.False(refreshedSession.IsAuthenticated);
        }
    }
}
