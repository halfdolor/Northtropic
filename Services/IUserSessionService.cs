using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Northtropic.Models;

namespace Northtropic.Services
{
    public interface IUserSessionService
    {
        bool IsAuthenticated { get; }
        Guid? CurrentUserId { get; }
        DateTime LastActivityTime { get; }
        event Action? OnUserChanged;

        void RecordUserActivity();
        Task<bool> CheckInactivityTimeoutAsync();

        Task<User?> GetActiveUserAsync();
        Task<List<User>> GetAllUsersAsync();
        Task<User> CreateUserAsync(string username, string grade);

        // 快捷体验与身份无缝切换
        Task<(bool Success, User? User, string Message)> SwitchUserAsync(Guid userId);
        Task<(bool Success, User? User, string Message)> QuickLoginDemoUserAsync(string phoneOrRole);

        // 密码登录与修改密码
        Task<(bool Success, User? User, string Message)> LoginWithPasswordAsync(string accountOrPhone, string password);
        Task<(bool Success, string Message)> ChangePasswordAsync(Guid userId, string oldPassword, string newPassword);

        // 短信登录与网关调用 (支持阿里云短信 Dysmsapi)
        Task<(bool Success, string Code, string Message)> SendSmsCodeAsync(string phoneNumber, string? customToken = null, string? customEndpoint = null);
        Task<(bool Success, User? User, string Message)> LoginOrRegisterWithSmsAsync(string phoneNumber, string code, UserRole role = UserRole.Student, string username = "", string grade = "");
        Task<(bool Success, User? User, string Message)> RegisterWithSmsAsync(string username, UserRole role, string phoneNumber, string email, string smsCode, string grade = "初中二年级");
        Task<(bool Success, User? User, string Message)> RegisterWithPasswordAsync(string username, UserRole role, string phoneNumber, string email, string password, string confirmPassword, string grade = "初中二年级");
        Task<TestConnectionResult> TestSmsGatewayAsync(string phoneNumber, string token, string endpoint);
        Task<TestConnectionResult> TestAliyunSmsAsync(string phoneNumber, string accessKeyId, string accessKeySecret, string signName, string templateCode, string templateParamName = "code", string endpoint = "dysmsapi.aliyuncs.com");

        Task LogoutAsync();

        // 用户个人资料与信息维护
        Task<(bool Success, string Message)> UpdateUserProfileAsync(Guid userId, string username, string avatar, string email, string phoneNumber, string? grade = null);
        Task<bool> UpdateDailyTargetAsync(Guid userId, int targetQuestions);
        Task<bool> UpdateSoundEffectsEnabledAsync(Guid userId, bool enabled);
        Task<bool> UpdateUserSettingsAsync(User user);

        // 超级管理员：用户管理与注册审批
        Task<bool> DeleteUserAsync(Guid userId);
        Task<bool> UpdateUserRoleAsync(Guid userId, UserRole newRole);
        Task<bool> UpdateUserGradeAsync(Guid userId, string newGrade);
        Task<bool> UpdateUserInfoAsync(Guid userId, string username, string grade, UserRole role, string phoneNumber);
        Task<bool> ResetUserPasswordAsync(Guid userId, string newPassword = "123456", Guid? callerUserId = null);
        Task<List<User>> GetPendingApprovalUsersAsync();
        Task<(bool Success, string Message)> ApproveUserRegistrationAsync(Guid userId, Guid adminUserId);
        Task<(bool Success, string Message)> RejectUserRegistrationAsync(Guid userId, Guid adminUserId, string reason);

        // 家长 & 学生关联管理
        Task<List<User>> GetBoundStudentsAsync(Guid parentId);
        Task<(bool Success, string Message)> BindStudentByCodeAsync(Guid parentId, string bindingCode, string relation = "监护人");
        Task<(bool Success, User? Student, string Message)> CreateChildStudentAsync(Guid parentId, string username, string grade, string relation = "监护人");
        Task<(bool Success, string Message)> UnbindStudentAsync(Guid parentId, Guid studentId);
        Task<(bool Success, string Message)> UpdateParentEncouragementNoteAsync(Guid studentId, string note);
        Task<(bool Success, string Message, int? NewCoins, int? NewExp, string? Note, DateTime? NoteTime)> AwardParentPraiseRewardAsync(Guid studentId, string badge, string comment, int rewardCoins);
        Task<(int TotalUsers, int PendingUsers)> GetUserStatisticsAsync();

        // 架构安全：短时一次性安全下载票据 (OTAC)，解决跨上下文/无状态 HTTP 文件导出与热备份下载鉴权
        Task<string> GenerateDownloadTicketAsync(Guid userId, string purpose, string? resource = null);
        Task<(bool Valid, Guid UserId, string Purpose, string? Resource)> ValidateAndConsumeDownloadTicketAsync(string ticket);
        int ActiveDownloadTicketsCount { get; }
    }
}
