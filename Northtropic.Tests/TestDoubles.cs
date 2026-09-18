using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Northtropic.Models;
using Northtropic.Services;

namespace Northtropic.Tests
{
    public class FakeUserSessionService : IUserSessionService
    {
        public User? ActiveUser { get; set; }
        public bool IsAuthenticated => ActiveUser != null;
        public Guid? CurrentUserId => ActiveUser?.Id;
        public DateTime LastActivityTime { get; set; } = DateTime.Now;
#pragma warning disable CS0067
        public event Action? OnUserChanged;
#pragma warning restore CS0067

        public void RecordUserActivity() { }
        public Task<bool> CheckInactivityTimeoutAsync() => Task.FromResult(false);
        public Task<User?> GetActiveUserAsync() => Task.FromResult(ActiveUser);
        public Task<List<User>> GetAllUsersAsync() => Task.FromResult(new List<User>());
        public Task<User> CreateUserAsync(string username, string grade) => Task.FromResult(new User { Username = username, Grade = grade });
        public Task<bool> IsProductionModeAsync() => Task.FromResult(false);
        public Task SyncDemoAccountsLifecycleAsync() => Task.CompletedTask;
        public Task<(bool Success, User? User, string Message)> SwitchUserAsync(Guid userId) => Task.FromResult<(bool, User?, string)>((true, ActiveUser, "OK"));
        public Task<(bool Success, User? User, string Message)> QuickLoginDemoUserAsync(string phoneOrRole) => Task.FromResult<(bool, User?, string)>((true, ActiveUser, "OK"));
        public Task<(bool Success, User? User, string Message)> LoginWithPasswordAsync(string accountOrPhone, string password) => Task.FromResult<(bool, User?, string)>((true, ActiveUser, "OK"));
        public Task<(bool Success, string Message)> ChangePasswordAsync(Guid userId, string oldPassword, string newPassword) => Task.FromResult((true, "OK"));
        public Task<(bool Success, string Code, string Message)> SendSmsCodeAsync(string phoneNumber, string? customToken = null, string? customEndpoint = null) => Task.FromResult((true, "123456", "OK"));
        public Task<(bool Success, User? User, string Message)> LoginOrRegisterWithSmsAsync(string phoneNumber, string code, UserRole role = UserRole.Student, string username = "", string grade = "") => Task.FromResult<(bool, User?, string)>((true, ActiveUser, "OK"));
        public Task<(bool Success, User? User, string Message)> RegisterWithSmsAsync(string username, UserRole role, string phoneNumber, string email, string smsCode, string grade = "初中二年级") => Task.FromResult<(bool, User?, string)>((true, ActiveUser, "OK"));
        public Task<(bool Success, User? User, string Message)> RegisterWithPasswordAsync(string username, UserRole role, string phoneNumber, string email, string password, string confirmPassword, string grade = "初中二年级") => Task.FromResult<(bool, User?, string)>((true, ActiveUser, "OK"));
        public Task<TestConnectionResult> TestSmsGatewayAsync(string phoneNumber, string token, string endpoint) => Task.FromResult(new TestConnectionResult { Success = true });
        public Task<TestConnectionResult> TestAliyunSmsAsync(string phoneNumber, string accessKeyId, string accessKeySecret, string signName, string templateCode, string templateParamName = "code", string endpoint = "dysmsapi.aliyuncs.com") => Task.FromResult(new TestConnectionResult { Success = true });
        public Task LogoutAsync() { ActiveUser = null; return Task.CompletedTask; }
        public Task<(bool Success, string Message)> UpdateUserProfileAsync(Guid userId, string username, string avatar, string email, string phoneNumber, string? grade = null) => Task.FromResult((true, "OK"));
        public Task<bool> UpdateDailyTargetAsync(Guid userId, int targetQuestions) => Task.FromResult(true);
        public Task<bool> DeleteUserAsync(Guid userId) => Task.FromResult(true);
        public Task<bool> UpdateUserRoleAsync(Guid userId, UserRole newRole) => Task.FromResult(true);
        public Task<bool> UpdateUserGradeAsync(Guid userId, string newGrade) => Task.FromResult(true);
        public Task<bool> UpdateSoundEffectsEnabledAsync(Guid userId, bool enabled) => Task.FromResult(true);
        public Task<bool> UpdateUserSettingsAsync(User user) => Task.FromResult(true);
        public Task<bool> UpdateUserInfoAsync(Guid userId, string username, string grade, UserRole role, string phoneNumber) => Task.FromResult(true);
        public Task<bool> ResetUserPasswordAsync(Guid userId, string newPassword = "123456", Guid? callerUserId = null) => Task.FromResult(true);
        public Task<List<User>> GetPendingApprovalUsersAsync() => Task.FromResult(new List<User>());
        public Task<(bool Success, string Message)> ApproveUserRegistrationAsync(Guid userId, Guid adminUserId) => Task.FromResult((true, "OK"));
        public Task<(bool Success, string Message)> RejectUserRegistrationAsync(Guid userId, Guid adminUserId, string reason) => Task.FromResult((true, "OK"));
        public Task<List<User>> GetBoundStudentsAsync(Guid parentId) => Task.FromResult(new List<User>());
        public Task<(bool Success, string Message)> BindStudentByCodeAsync(Guid parentId, string bindingCode, string relation = "监护人") => Task.FromResult((true, "OK"));
        public Task<(bool Success, User? Student, string Message)> CreateChildStudentAsync(Guid parentId, string username, string grade, string relation = "监护人") => Task.FromResult<(bool, User?, string)>((true, new User(), "OK"));
        public Task<(bool Success, string Message)> UnbindStudentAsync(Guid parentId, Guid studentId) => Task.FromResult((true, "OK"));
        public Task<(bool Success, string Message)> UpdateParentEncouragementNoteAsync(Guid studentId, string note) => Task.FromResult((true, "OK"));
        public Task<(bool Success, string Message, int? NewCoins, int? NewExp, string? Note, DateTime? NoteTime)> AwardParentPraiseRewardAsync(Guid studentId, string badge, string comment, int rewardCoins) => Task.FromResult<(bool, string, int?, int?, string?, DateTime?)>((true, "OK", 10, 10, "Nice!", DateTime.UtcNow));
        public Task<(int TotalUsers, int PendingUsers)> GetUserStatisticsAsync() => Task.FromResult((10, 2));
        public Task<string> GenerateDownloadTicketAsync(Guid userId, string purpose, string? resource = null) => Task.FromResult(Guid.NewGuid().ToString("N"));
        public Task<(bool Valid, Guid UserId, string Purpose, string? Resource)> ValidateAndConsumeDownloadTicketAsync(string ticket) => Task.FromResult<(bool, Guid, string, string?)>((true, ActiveUser?.Id ?? Guid.NewGuid(), "backup_download", null));
        public int ActiveDownloadTicketsCount => 0;

        public User? SystemAdminUser { get; set; } = null;
        public Task<User?> GetSystemAdminUserAsync() => Task.FromResult(SystemAdminUser);
        public Task<User> ResolveEffectiveUserLlmConfigAsync(User? user = null)
        {
            user ??= ActiveUser ?? new User { Id = Guid.Empty, Username = "测试用户", Role = UserRole.Student };
            if (!string.IsNullOrWhiteSpace(user.LlmApiKey)) return Task.FromResult(user);
            if (SystemAdminUser != null && !string.IsNullOrWhiteSpace(SystemAdminUser.LlmApiKey))
            {
                var effective = new User
                {
                    Id = user.Id,
                    Username = user.Username,
                    Grade = user.Grade,
                    Role = user.Role,
                    LlmApiKey = SystemAdminUser.LlmApiKey,
                    LlmBaseUrl = SystemAdminUser.LlmBaseUrl,
                    LlmModelName = SystemAdminUser.LlmModelName,
                    BaiduApiKey = string.IsNullOrWhiteSpace(user.BaiduApiKey) ? SystemAdminUser.BaiduApiKey : user.BaiduApiKey,
                    BaiduSecretKey = string.IsNullOrWhiteSpace(user.BaiduSecretKey) ? SystemAdminUser.BaiduSecretKey : user.BaiduSecretKey,
                    BaiduOcrEndpoint = string.IsNullOrWhiteSpace(user.BaiduOcrEndpoint) ? SystemAdminUser.BaiduOcrEndpoint : user.BaiduOcrEndpoint
                };
                return Task.FromResult(effective);
            }
            return Task.FromResult(user);
        }

        public string GenerateSessionToken(Guid userId) => $"fake_token_{userId}";
        public Task<(bool Success, User? User)> RestoreSessionFromTokenAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("fake_token_"))
            {
                return Task.FromResult<(bool, User?)>((false, null));
            }
            var idStr = token.Substring("fake_token_".Length);
            if (Guid.TryParse(idStr, out var id))
            {
                var user = ActiveUser ?? new User { Id = id, Username = "恢复测试用户" };
                user.Id = id;
                ActiveUser = user;
                return Task.FromResult<(bool, User?)>((true, user));
            }
            return Task.FromResult<(bool, User?)>((false, null));
        }
    }

    public class FakeGamificationService : IGamificationService
    {
        public User CurrentUser { get; set; } = new User();
        public virtual Task<User> GetCurrentUserAsync() => Task.FromResult(CurrentUser);
        public Task<RewardResult> ProcessAnswerRewardAsync(bool isCorrect, int baseExp, int combo, int difficulty = 3, int timeTakenSeconds = 20, bool isHistoryWrong = false, Guid? targetUserId = null, Northtropic.Data.AppDbContext? existingContext = null) => Task.FromResult(new RewardResult());
        public Task<RewardResult> ProcessErrorRevisionRewardAsync(Guid errorItemId, Guid? targetUserId = null) => Task.FromResult(new RewardResult { EarnedExp = 15, EarnedCoins = 5 });
        public Task UpdateStreakAsync(Guid? targetUserId = null) => Task.CompletedTask;
        public Task<bool> UnlockAchievementAsync(string achievementCode, Guid? targetUserId = null, Northtropic.Data.AppDbContext? existingContext = null) => Task.FromResult(true);
        public Task ApplyBuffAsync(string buffType, int durationMinutes) => Task.CompletedTask;
        public Task AddComboShieldAsync(int count) => Task.CompletedTask;
        public Task SetUserTitleAsync(string title) => Task.CompletedTask;
        public string GetLevelTitle(int level) => "初试锋芒";
        public Task<(bool Success, string Message)> BuyExpPotionAsync(Guid userId) => Task.FromResult((true, "OK"));
        public Task<(bool Success, string Message)> BuyGoldPotionAsync(Guid userId) => Task.FromResult((true, "OK"));
        public Task<(bool Success, string Message)> BuyComboShieldAsync(Guid userId) => Task.FromResult((true, "OK"));
        public Task<(bool Success, string Message)> BuyStreakRepairCardAsync(Guid userId) => Task.FromResult((true, "OK"));
        public Task<List<string>> GetAvailableTitlesAsync(Guid userId) => Task.FromResult(new List<string> { "初试锋芒" });
        public Task EquipTitleAsync(Guid userId, string title) => Task.CompletedTask;
        public Task<(bool Success, string Message, int Exp, int Coins)> ClaimDailyTargetRewardAsync(Guid userId) => Task.FromResult((true, "OK", 50, 20));
        public Task<AchievementsOverviewDto> GetAchievementsOverviewAsync(Guid userId) => Task.FromResult(new AchievementsOverviewDto());
    }

    public class FakeAiTutorService : IAiTutorService
    {
        public Task<AiExplanationResult> GetExplanationAsync(Question question, string? userAnswer = null) => Task.FromResult(new AiExplanationResult());
        public Task<SocraticGuidanceResult> GetSocraticGuidanceAsync(Question question, string? userAnswer = null) => Task.FromResult(new SocraticGuidanceResult());
        public Task<Question> GenerateVariationQuestionAsync(Question originalQuestion) => Task.FromResult(originalQuestion);
        public Task<string> AskAiTutorAsync(string questionContext, string userPrompt) => Task.FromResult("AI Tutor Response");
        public Task<SubjectiveGradingResult> GradeSubjectiveAnswerAsync(Question question, string userAnswer) => Task.FromResult(new SubjectiveGradingResult { IsPassed = true, Score = 90 });
        public Task<TestConnectionResult> TestConnectionAsync(string apiKey, string baseUrl, string modelName) => Task.FromResult(new TestConnectionResult { IsSuccess = true });
    }

    public class FakeAppVersionService : IAppVersionService
    {
        public string Version { get; set; } = "1.0.12";
        public string DisplayVersion => $"v{Version}";
        public string ShortCommitHash { get; set; } = "e8dcc36";
        public string BuildTimestamp { get; set; } = "2026-09-18 08:20";
        public string FullVersionInfo => $"Northtropic {DisplayVersion} (构建: {BuildTimestamp}, Commit: {ShortCommitHash})";
    }
}

