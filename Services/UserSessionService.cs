using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Helpers;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class UserSessionService : IUserSessionService
    {
        private readonly AppDbContext _context;
        private readonly HttpClient _httpClient;
        private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;

        // 当前 Scoped 会话登录态与用户ID (每个浏览器连接会话独立)
        private Guid? _activeUserId = null;
        private bool _isAuthenticated = false;
        private DateTime _lastActivityTime = DateTime.UtcNow;

        // 手机验证码内存暂存：<手机号, (验证码, 过期时间)>
        private static readonly ConcurrentDictionary<string, (string Code, DateTime ExpireAt)> _smsCodeStore = new();
        // 手机验证码发送频次冷却缓存：<手机号, 下次允许发送时间> (60秒防刷频)
        private static readonly ConcurrentDictionary<string, DateTime> _smsCooldownStore = new();
        // 密码连续输错防暴力破解内存缓存：<账号/手机号, (连续失败次数, 锁定截止时间)>
        private static readonly ConcurrentDictionary<string, (int FailCount, DateTime LockoutUntil)> _loginAttemptStore = new();

        private static DateTime _lastPurgeTime = DateTime.MinValue;
        private static readonly object _purgeLock = new();

        public static void EnsureExpiredEntriesPurged(bool force = false)
        {
            var now = DateTime.Now;
            if (!force && (now - _lastPurgeTime).TotalMinutes < 2) return;
            lock (_purgeLock)
            {
                if (!force && (now - _lastPurgeTime).TotalMinutes < 2) return;
                _lastPurgeTime = now;
                PurgeExpiredSmsCodes();
                PurgeExpiredTickets();
            }
        }

        public static void ResetLoginAttempts(string accountOrPhone)
        {
            if (!string.IsNullOrWhiteSpace(accountOrPhone))
            {
                _loginAttemptStore.TryRemove(accountOrPhone.Trim(), out _);
            }
        }

        public static int GetLoginFailCount(string accountOrPhone)
        {
            EnsureExpiredEntriesPurged();
            if (!string.IsNullOrWhiteSpace(accountOrPhone) && _loginAttemptStore.TryGetValue(accountOrPhone.Trim(), out var info))
            {
                if (info.LockoutUntil != DateTime.MinValue && info.LockoutUntil < DateTime.Now)
                {
                    _loginAttemptStore.TryRemove(accountOrPhone.Trim(), out _);
                    return 0;
                }
                return info.FailCount;
            }
            return 0;
        }

        public static bool IsDemoOrTestPhone(string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber)) return false;
            // 预设的演示账号
            if (phoneNumber is "13800000000" or "13800000001" or "13800000002" or "13800000003") return true;
            // 单元测试/特殊测试号段 (以 1380000 开头)
            if (phoneNumber.StartsWith("1380000")) return true;
            return false;
        }

        public static void PurgeExpiredSmsCodes()
        {
            var now = DateTime.Now;
            foreach (var kvp in _smsCodeStore)
            {
                if (kvp.Value.ExpireAt < now)
                {
                    _smsCodeStore.TryRemove(kvp.Key, out _);
                }
            }
            foreach (var kvp in _smsCooldownStore)
            {
                if (kvp.Value < now)
                {
                    _smsCooldownStore.TryRemove(kvp.Key, out _);
                }
            }
            foreach (var kvp in _loginAttemptStore)
            {
                if (kvp.Value.LockoutUntil != DateTime.MinValue && kvp.Value.LockoutUntil < now)
                {
                    _loginAttemptStore.TryRemove(kvp.Key, out _);
                }
            }
        }

        public static int ActiveLockoutsCount
        {
            get
            {
                EnsureExpiredEntriesPurged();
                var now = DateTime.Now;
                return _loginAttemptStore.Count(kv => kv.Value.LockoutUntil > now);
            }
        }

        public static int PendingSmsCodesCount
        {
            get
            {
                EnsureExpiredEntriesPurged();
                var now = DateTime.Now;
                return _smsCodeStore.Count(kv => kv.Value.ExpireAt > now);
            }
        }

        public static int ActiveDownloadTicketsCountStatic
        {
            get
            {
                EnsureExpiredEntriesPurged();
                var now = DateTime.UtcNow;
                return _downloadTicketStore.Count(kv => !kv.Value.IsUsed && kv.Value.ExpiresAt >= now);
            }
        }

        public int ActiveDownloadTicketsCount => ActiveDownloadTicketsCountStatic;

        public bool IsAuthenticated => _isAuthenticated && _activeUserId.HasValue;
        public Guid? CurrentUserId => _activeUserId;
        public DateTime LastActivityTime => _lastActivityTime;

        public event Action? OnUserChanged;

        public UserSessionService(AppDbContext context, HttpClient httpClient, IDbContextFactory<AppDbContext>? dbContextFactory = null)
        {
            _context = context;
            _httpClient = httpClient;
            _dbContextFactory = dbContextFactory;
        }

        private ValueTask<AsyncDbScope> CreateDbScopeAsync()
        {
            return AsyncDbScope.CreateAsync(_dbContextFactory, _context);
        }

        public void RecordUserActivity()
        {
            _lastActivityTime = DateTime.UtcNow;
        }

        public async Task<bool> CheckInactivityTimeoutAsync()
        {
            if (!_isAuthenticated || _activeUserId == null)
            {
                return false;
            }

            var user = await GetActiveUserAsync();
            int timeoutMinutes = user?.SessionTimeoutMinutes ?? 30;

            if (DateTime.UtcNow - _lastActivityTime > TimeSpan.FromMinutes(timeoutMinutes))
            {
                await LogoutAsync();
                return true;
            }

            return false;
        }

        public async Task<User?> GetActiveUserAsync()
        {
            if (_activeUserId.HasValue)
            {
                await using var dbScope = await CreateDbScopeAsync();
                var user = await dbScope.Context.Users.FirstOrDefaultAsync(u => u.Id == _activeUserId.Value);
                if (user != null) return user;
            }

            return null;
        }

        public async Task<List<User>> GetAllUsersAsync()
        {
            await using var dbScope = await CreateDbScopeAsync();
            return await dbScope.Context.Users
                .AsNoTracking()
                .OrderByDescending(u => u.LastStudyDate)
                .ToListAsync();
        }

        public async Task<bool> IsProductionModeAsync()
        {
            await using var dbScope = await CreateDbScopeAsync();
            var admin = await dbScope.Context.Users.FirstOrDefaultAsync(u => u.Role == UserRole.SuperAdmin);
            return admin != null &&
                   !string.IsNullOrWhiteSpace(admin.PhoneNumber) &&
                   admin.PhoneNumber.Trim() != "13800000000";
        }

        public async Task SyncDemoAccountsLifecycleAsync()
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var admin = await context.Users.FirstOrDefaultAsync(u => u.Role == UserRole.SuperAdmin);
            bool isProduction = admin != null &&
                                !string.IsNullOrWhiteSpace(admin.PhoneNumber) &&
                                admin.PhoneNumber.Trim() != "13800000000";

            var demoUsers = await context.Users
                .Where(u => u.IsBuiltInDemo && u.Role != UserRole.SuperAdmin)
                .ToListAsync();

            bool changed = false;
            foreach (var user in demoUsers)
            {
                var targetStatus = isProduction ? UserAccountStatus.Disabled : UserAccountStatus.Approved;
                if (user.AccountStatus != targetStatus)
                {
                    user.AccountStatus = targetStatus;
                    changed = true;
                }
            }

            if (changed)
            {
                await context.SaveChangesAsync();
                OnUserChanged?.Invoke();
            }
        }

        public async Task<User> CreateUserAsync(string username, string grade)
        {
            string randomSuffix = Random.Shared.Next(1000, 9999).ToString();
            string bindingCode = "STU" + randomSuffix;

            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = username,
                Grade = grade,
                Role = UserRole.Student,
                PhoneNumber = string.Empty,
                BindingCode = bindingCode,
                Level = 1,
                Exp = 0,
                Coins = 100,
                CurrentStreak = 1,
                LastStudyDate = DateTime.Now,
                OcrProvider = "PaddleOcr"
            };

            await using var dbScope = await CreateDbScopeAsync();
            dbScope.Context.Users.Add(user);
            await dbScope.Context.SaveChangesAsync();

            _activeUserId = user.Id;
            _isAuthenticated = true;
            _lastActivityTime = DateTime.UtcNow;
            OnUserChanged?.Invoke();

            return user;
        }

        public async Task<(bool Success, User? User, string Message)> LoginWithPasswordAsync(string accountOrPhone, string password)
        {
            accountOrPhone = accountOrPhone?.Trim() ?? string.Empty;
            password = password?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(accountOrPhone) || string.IsNullOrWhiteSpace(password))
            {
                return (false, null, "账号/手机号与密码不能为空！");
            }
            accountOrPhone = accountOrPhone.Trim();

            // 自动清理过期会话防积压
            EnsureExpiredEntriesPurged();

            // 防暴力破解锁定校验 (连续 5 次错误密码锁定 5 分钟)
            var now = DateTime.Now;
            if (_loginAttemptStore.TryGetValue(accountOrPhone, out var attemptInfo))
            {
                if (now < attemptInfo.LockoutUntil)
                {
                    var remainingSeconds = (int)Math.Ceiling((attemptInfo.LockoutUntil - now).TotalSeconds);
                    return (false, null, $"⚠️ 密码连续输错次数过多，账号已临时锁定保护！请在 {remainingSeconds} 秒后再试。");
                }
            }

            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var user = await context.Users.FirstOrDefaultAsync(u => u.PhoneNumber == accountOrPhone || u.Username == accountOrPhone);
            if (user == null)
            {
                return (false, null, "该账号不存在！请检查手机号或前往注册。");
            }

            // 审核与禁用状态拦截
            if (user.AccountStatus == UserAccountStatus.PendingApproval)
            {
                return (false, null, "您的账号正在等待超级管理员审批中，审批通过后方可登录，请耐心等待！");
            }
            if (user.AccountStatus == UserAccountStatus.Rejected)
            {
                string reason = string.IsNullOrWhiteSpace(user.RejectReason) ? "注册信息审核未通过" : user.RejectReason;
                return (false, null, $"您的注册申请已被管理员拒绝。拒绝理由：{reason}");
            }
            if (user.AccountStatus == UserAccountStatus.Disabled)
            {
                return (false, null, "系统已进入正式使用阶段，内置演示体验账号已停用！请使用您自行注册并审批通过的账号登录。");
            }

            // 密码核验 (支持 PBKDF2-SHA256 加盐哈希 与 旧明文自适应无感升级)
            bool isPasswordValid = PasswordHasher.VerifyPassword(password, user.Password, out bool needsRehash);
            if (!isPasswordValid)
            {
                int currentFailCount = 1;
                _loginAttemptStore.AddOrUpdate(accountOrPhone,
                    _ => (1, DateTime.MinValue),
                    (_, existing) =>
                    {
                        int newCount = (now < existing.LockoutUntil) ? existing.FailCount : existing.FailCount + 1;
                        DateTime lockout = (newCount >= 5) ? DateTime.Now.AddMinutes(5) : DateTime.MinValue;
                        currentFailCount = newCount;
                        return (newCount, lockout);
                    });

                int remaining = Math.Max(0, 5 - currentFailCount);
                string failMsg = remaining > 0
                    ? $"登录密码错误！还可尝试 {remaining} 次，连续输错 5 次账号将锁定 5 分钟。（默认初始密码为 123456）"
                    : "⚠️ 登录密码错误！已连续输错 5 次，账号已临时锁定 5 分钟，请稍后再试。";
                return (false, null, failMsg);
            }

            // 登录成功，重置输错失败计数
            _loginAttemptStore.TryRemove(accountOrPhone, out _);

            if (needsRehash)
            {
                // 旧明文密码核验通过，立即在数据库透明升级为加盐哈希串
                user.Password = PasswordHasher.HashPassword(password);
            }

            _activeUserId = user.Id;
            _isAuthenticated = true;
            _lastActivityTime = DateTime.UtcNow;
            await context.SaveChangesAsync();

            OnUserChanged?.Invoke();

            return (true, user, user.MustChangePassword
                ? "登录成功！新账号默认密码为 123456，为确保安全请在弹窗中修改您的新密码。"
                : $"登录成功，欢迎 {user.RoleDisplayName} {user.Username}！");
        }

        public async Task<(bool Success, User? User, string Message)> SwitchUserAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var user = await context.Users.FindAsync(userId);
            if (user == null)
            {
                return (false, null, "所选用户不存在！");
            }

            // 审核状态拦截安全防护：非管理员严禁切换进入未审批或已拒绝账号
            bool isCallerSuperAdmin = false;
            if (_activeUserId.HasValue)
            {
                var caller = await context.Users.FindAsync(_activeUserId.Value);
                isCallerSuperAdmin = caller?.Role == UserRole.SuperAdmin;
            }

            if (!isCallerSuperAdmin)
            {
                if (user.AccountStatus == UserAccountStatus.PendingApproval)
                {
                    return (false, null, "该账号正在等待超级管理员审批中，审批通过后方可登录！");
                }
                if (user.AccountStatus == UserAccountStatus.Rejected)
                {
                    string reason = string.IsNullOrWhiteSpace(user.RejectReason) ? "注册信息审核未通过" : user.RejectReason;
                    return (false, null, $"该账号注册申请已被管理员拒绝（理由：{reason}），无法登录！");
                }
                if (user.AccountStatus == UserAccountStatus.Disabled)
                {
                    return (false, null, "系统已进入正式使用阶段，内置演示体验账号已停用！请使用您自行注册并审批通过的账号登录。");
                }
            }

            _activeUserId = user.Id;
            _isAuthenticated = true;
            _lastActivityTime = DateTime.UtcNow;
            await context.SaveChangesAsync();

            OnUserChanged?.Invoke();
            return (true, user, $"已成功切换至【{user.RoleDisplayName} · {user.Username}】！");
        }

        public async Task<(bool Success, User? User, string Message)> QuickLoginDemoUserAsync(string phoneOrRole)
        {
            phoneOrRole = phoneOrRole?.Trim() ?? string.Empty;
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var user = await context.Users
                .OrderByDescending(u => u.IsBuiltInDemo)
                .FirstOrDefaultAsync(u => 
                    u.PhoneNumber == phoneOrRole || 
                    u.Username == phoneOrRole ||
                    (phoneOrRole == "Student" && u.Role == UserRole.Student) ||
                    (phoneOrRole == "Parent" && u.Role == UserRole.Parent) ||
                    (phoneOrRole == "Teacher" && u.Role == UserRole.Teacher) ||
                    (phoneOrRole == "Admin" && u.Role == UserRole.SuperAdmin)
                );

            if (user == null)
            {
                return (false, null, $"未找到匹配的体验账号（{phoneOrRole}）！");
            }

            if (user.AccountStatus == UserAccountStatus.Disabled)
            {
                return (false, null, "系统已进入正式使用阶段，内置演示体验账号已停用！请使用您自行注册并审批通过的账号登录。");
            }

            _activeUserId = user.Id;
            _isAuthenticated = true;
            _lastActivityTime = DateTime.UtcNow;
            await context.SaveChangesAsync();

            OnUserChanged?.Invoke();
            return (true, user, $"已成功以【{user.RoleDisplayName} · {user.Username}】身份进入系统！");
        }

        public async Task<(bool Success, string Message)> ChangePasswordAsync(Guid userId, string oldPassword, string newPassword)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            if (_activeUserId != null && _activeUserId.Value != userId)
            {
                var caller = await context.Users.FindAsync(_activeUserId.Value);
                if (caller?.Role != UserRole.SuperAdmin)
                {
                    return (false, "越权操作：您无权修改其他用户的登录密码！");
                }
            }

            var user = await context.Users.FindAsync(userId);
            if (user == null)
            {
                return (false, "用户不存在！");
            }

            bool isOldValid = PasswordHasher.VerifyPassword(oldPassword, user.Password, out _);
            if (!isOldValid && !user.MustChangePassword)
            {
                return (false, "原密码不正确，请重新输入！");
            }

            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            {
                return (false, "新密码长度不能少于 6 位！");
            }

            user.Password = PasswordHasher.HashPassword(newPassword);
            user.MustChangePassword = false;
            await context.SaveChangesAsync();

            _lastActivityTime = DateTime.UtcNow;
            OnUserChanged?.Invoke();

            return (true, "密码修改成功！请妥善保管您的新密码。");
        }

        public async Task<(bool Success, string Code, string Message)> SendSmsCodeAsync(string phoneNumber, string? customToken = null, string? customEndpoint = null)
        {
            phoneNumber = phoneNumber?.Trim() ?? string.Empty;
            if (!Regex.IsMatch(phoneNumber, @"^1[3-9]\d{9}$"))
            {
                return (false, "", "请输入正确的 11 位中国大陆手机号码！");
            }

            // 60秒防刷频冷却校验
            if (_smsCooldownStore.TryGetValue(phoneNumber, out var nextAllowedTime) && DateTime.Now < nextAllowedTime)
            {
                int remainingSeconds = (int)Math.Ceiling((nextAllowedTime - DateTime.Now).TotalSeconds);
                return (false, "", $"短信发送过于频繁，请等待 {remainingSeconds} 秒后再试！");
            }

            // 清理已过期验证码，防止内存无界增长
            PurgeExpiredSmsCodes();

            // 生成 6 位随机数字验证码
            string code = Random.Shared.Next(100000, 999999).ToString();
            var expireAt = DateTime.Now.AddMinutes(5);

            _smsCodeStore[phoneNumber] = (code, expireAt);
            _smsCooldownStore[phoneNumber] = DateTime.Now.AddSeconds(60);

            // 获取配置中的超级管理员配置
            await using var dbScope = await CreateDbScopeAsync();
            var adminUser = await dbScope.Context.Users.FirstOrDefaultAsync(u => u.Role == UserRole.SuperAdmin);

            // 优先使用阿里云短信服务 (Aliyun Dysmsapi)
            if (adminUser != null && !string.IsNullOrWhiteSpace(adminUser.AliyunAccessKeyId) && !string.IsNullOrWhiteSpace(adminUser.AliyunAccessKeySecret))
            {
                string paramName = string.IsNullOrWhiteSpace(adminUser.AliyunSmsTemplateParam) ? "code" : adminUser.AliyunSmsTemplateParam.Trim();
                string templateParamJson = $"{{\"{paramName}\":\"{code}\"}}";

                var aliyunRes = await SendAliyunSmsCoreAsync(
                    phoneNumber,
                    adminUser.AliyunAccessKeyId,
                    adminUser.AliyunAccessKeySecret,
                    adminUser.AliyunSmsSignName,
                    adminUser.AliyunSmsTemplateCode,
                    templateParamJson,
                    adminUser.AliyunSmsEndpoint,
                    adminUser.AliyunSmsRegionId
                );

                if (aliyunRes.Success)
                {
                    return (true, code, $"验证码已通过阿里云短信发送至手机 {phoneNumber}，请查收并输入！（5分钟有效）");
                }
                else
                {
                    return (false, "", $"阿里云短信发送未成功 [{aliyunRes.Message}]。您可以切换为【手工设置密码注册】直接提交申请。");
                }
            }

            // 备用：通用旧版网关或未配置提示
            if (adminUser != null && !string.IsNullOrWhiteSpace(adminUser.SmsToken) && adminUser.SmsApiEndpoint.Contains("api.iorai.com"))
            {
                string token = customToken ?? adminUser.SmsToken;
                string endpoint = customEndpoint ?? adminUser.SmsApiEndpoint;
                string message = $"您的登录验证码为{code}";

                try
                {
                    string encodedMessage = Uri.EscapeDataString(message);
                    string url = $"{endpoint}?token={Uri.EscapeDataString(token)}&account={Uri.EscapeDataString(phoneNumber)}&message={encodedMessage}";

                    var response = await _httpClient.GetAsync(url);
                    string responseBody = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode && responseBody.Contains("短信发送成功"))
                    {
                        return (true, code, $"验证码短信已发送至手机 {phoneNumber}，请查收并输入！（5分钟内有效）");
                    }
                    else
                    {
                        string errDetail = string.IsNullOrWhiteSpace(responseBody) ? $"HTTP {(int)response.StatusCode}" : responseBody.Trim();
                        return (false, "", $"短信发送未成功 [{errDetail}]。请超级管理员在【系统配置】配置阿里云短信，或使用【手工设置密码注册】。");
                    }
                }
                catch (Exception ex)
                {
                    return (false, "", $"连接短信网关异常: {ex.Message}。请超级管理员在【系统配置】配置阿里云短信，或使用【手工设置密码注册】。");
                }
            }

            return (false, "", "尚未配置阿里云短信服务（请超级管理员在【系统配置】中填入 AccessKeyId / Secret / 签名 / 模板Code），或直接使用【手工设置密码注册】。");
        }

        public async Task<TestConnectionResult> TestAliyunSmsAsync(string phoneNumber, string accessKeyId, string accessKeySecret, string signName, string templateCode, string templateParamName = "code", string endpoint = "dysmsapi.aliyuncs.com")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            phoneNumber = phoneNumber?.Trim() ?? string.Empty;
            if (!Regex.IsMatch(phoneNumber, @"^1[3-9]\d{9}$"))
            {
                return new TestConnectionResult
                {
                    IsSuccess = false,
                    Message = "请输入正确的 11 位测试接收手机号码！"
                };
            }

            if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(accessKeySecret))
            {
                return new TestConnectionResult
                {
                    IsSuccess = false,
                    Message = "AccessKeyId 与 AccessKeySecret 不能为空！"
                };
            }

            if (string.IsNullOrWhiteSpace(signName))
            {
                return new TestConnectionResult
                {
                    IsSuccess = false,
                    Message = "短信签名 (SignName) 不能为空，须与阿里云控制台报备的签名一致！"
                };
            }

            if (string.IsNullOrWhiteSpace(templateCode))
            {
                return new TestConnectionResult
                {
                    IsSuccess = false,
                    Message = "短信模板代码 (TemplateCode) 不能为空，例如: SMS_154950909！"
                };
            }

            string testCode = Random.Shared.Next(100000, 999999).ToString();
            string paramKey = string.IsNullOrWhiteSpace(templateParamName) ? "code" : templateParamName.Trim();
            string paramJson = $"{{\"{paramKey}\":\"{testCode}\"}}";

            try
            {
                var res = await SendAliyunSmsCoreAsync(phoneNumber, accessKeyId, accessKeySecret, signName, templateCode, paramJson, endpoint);
                sw.Stop();

                if (res.Success)
                {
                    return new TestConnectionResult
                    {
                        IsSuccess = true,
                        LatencyMs = (int)sw.ElapsedMilliseconds,
                        ModelName = "阿里云短信服务 (Aliyun Dysmsapi)",
                        Message = $"🎉 阿里云短信发送成功！测试验证码: {testCode} 已下发至手机 {phoneNumber}。",
                        SampleResponse = res.RawResponse
                    };
                }
                else
                {
                    return new TestConnectionResult
                    {
                        IsSuccess = false,
                        LatencyMs = (int)sw.ElapsedMilliseconds,
                        Message = $"❌ 阿里云短信发送失败: {res.Message}",
                        SampleResponse = res.RawResponse
                    };
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new TestConnectionResult
                {
                    IsSuccess = false,
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                    Message = $"连接阿里云短信服务异常: {ex.Message}"
                };
            }
        }

        private async Task<(bool Success, string Code, string Message, string RawResponse)> SendAliyunSmsCoreAsync(
            string phoneNumber,
            string accessKeyId,
            string accessKeySecret,
            string signName,
            string templateCode,
            string templateParamJson,
            string endpoint = "dysmsapi.aliyuncs.com",
            string regionId = "cn-hangzhou")
        {
            endpoint = string.IsNullOrWhiteSpace(endpoint) ? "dysmsapi.aliyuncs.com" : endpoint.Trim();
            regionId = string.IsNullOrWhiteSpace(regionId) ? "cn-hangzhou" : regionId.Trim();

            var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                { "AccessKeyId", accessKeyId.Trim() },
                { "Action", "SendSms" },
                { "Format", "JSON" },
                { "PhoneNumbers", phoneNumber.Trim() },
                { "RegionId", regionId },
                { "SignName", signName.Trim() },
                { "SignatureMethod", "HMAC-SHA1" },
                { "SignatureNonce", Guid.NewGuid().ToString("N") },
                { "SignatureVersion", "1.0" },
                { "TemplateCode", templateCode.Trim() },
                { "TemplateParam", templateParamJson.Trim() },
                { "Timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
                { "Version", "2017-05-25" }
            };

            // 1. 构造 Canonicalized Query String
            var canonicalizedQueryStringBuilder = new StringBuilder();
            foreach (var p in parameters)
            {
                if (canonicalizedQueryStringBuilder.Length > 0)
                {
                    canonicalizedQueryStringBuilder.Append('&');
                }
                canonicalizedQueryStringBuilder.Append(AliyunPercentEncode(p.Key))
                    .Append('=')
                    .Append(AliyunPercentEncode(p.Value));
            }
            string canonicalizedQueryString = canonicalizedQueryStringBuilder.ToString();

            // 2. 构造 StringToSign
            string stringToSign = "GET&" + AliyunPercentEncode("/") + "&" + AliyunPercentEncode(canonicalizedQueryString);

            // 3. 计算 HMAC-SHA1 签名
            string signature;
            using (var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(accessKeySecret.Trim() + "&")))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
                signature = Convert.ToBase64String(hash);
            }

            // 4. 组装请求 URL
            string requestUrl = $"https://{endpoint}/?Signature={AliyunPercentEncode(signature)}&{canonicalizedQueryString}";

            var response = await _httpClient.GetAsync(requestUrl);
            string responseBody = await response.Content.ReadAsStringAsync();

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                string respCode = root.TryGetProperty("Code", out var codeElem) ? codeElem.GetString() ?? "" : "";
                string respMsg = root.TryGetProperty("Message", out var msgElem) ? msgElem.GetString() ?? "" : "";
                string bizId = root.TryGetProperty("BizId", out var bizElem) ? bizElem.GetString() ?? "" : "";

                if (string.Equals(respCode, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    return (true, respCode, $"短信发送成功 (BizId: {bizId})", responseBody);
                }
                else
                {
                    return (false, respCode, $"[{respCode}] {respMsg}", responseBody);
                }
            }
            catch
            {
                if (response.IsSuccessStatusCode && responseBody.Contains("\"Code\":\"OK\""))
                {
                    return (true, "OK", "短信发送成功", responseBody);
                }
                return (false, "HTTP_ERR", $"网关响应异常 (HTTP {(int)response.StatusCode}): {responseBody}", responseBody);
            }
        }

        private static string AliyunPercentEncode(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var encoded = Uri.EscapeDataString(value);
            return encoded
                .Replace("+", "%20")
                .Replace("*", "%2A")
                .Replace("%7E", "~");
        }

        public async Task<TestConnectionResult> TestSmsGatewayAsync(string phoneNumber, string token, string endpoint)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            phoneNumber = phoneNumber?.Trim() ?? string.Empty;
            if (!Regex.IsMatch(phoneNumber, @"^1[3-9]\d{9}$"))
            {
                return new TestConnectionResult
                {
                    IsSuccess = false,
                    Message = "请输入正确的 11 位测试接收手机号码！"
                };
            }

            string code = Random.Shared.Next(100000, 999999).ToString();
            string message = $"您的登录验证码为{code}";

            try
            {
                string encodedMessage = Uri.EscapeDataString(message);
                string url = $"{endpoint}?token={Uri.EscapeDataString(token)}&account={Uri.EscapeDataString(phoneNumber)}&message={encodedMessage}";

                var response = await _httpClient.GetAsync(url);
                sw.Stop();
                string responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode && responseBody.Contains("短信发送成功"))
                {
                    return new TestConnectionResult
                    {
                        IsSuccess = true,
                        LatencyMs = (int)sw.ElapsedMilliseconds,
                        ModelName = "SMS 短信网关 (api.iorai.com)",
                        Message = $"短信发送成功！网关响应：{responseBody.Trim()}",
                        SampleResponse = $"接口返回: {responseBody} | 验证码: {code}"
                    };
                }
                else
                {
                    return new TestConnectionResult
                    {
                        IsSuccess = false,
                        LatencyMs = (int)sw.ElapsedMilliseconds,
                        Message = $"短信发送未成功 (网关返回: {responseBody.Trim()})"
                    };
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new TestConnectionResult
                {
                    IsSuccess = false,
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                    Message = $"连接短信网关异常: {ex.Message}"
                };
            }
        }

        public async Task<(bool Success, User? User, string Message)> LoginOrRegisterWithSmsAsync(string phoneNumber, string code, UserRole role = UserRole.Student, string username = "", string grade = "")
        {
            phoneNumber = phoneNumber?.Trim() ?? string.Empty;
            code = code?.Trim() ?? string.Empty;

            if (!Regex.IsMatch(phoneNumber, @"^1[3-9]\d{9}$"))
            {
                return (false, null, "手机号码格式不正确！");
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return (false, null, "请输入 6 位短信验证码！");
            }

            // 校验验证码 (真实验证码或仅限预设演示账号的备用测试码 123456)
            PurgeExpiredSmsCodes();
            bool isDemoPhone = IsDemoOrTestPhone(phoneNumber);
            bool isCodeValid = isDemoPhone && code == "123456";
            if (!isCodeValid && _smsCodeStore.TryGetValue(phoneNumber, out var storedInfo))
            {
                if (storedInfo.ExpireAt >= DateTime.Now && storedInfo.Code == code)
                {
                    isCodeValid = true;
                }
            }

            if (!isCodeValid)
            {
                return (false, null, "验证码无效或已过期！请重新获取短信验证码。");
            }

            // 验证通过，销毁使用过的验证码防止重放
            _smsCodeStore.TryRemove(phoneNumber, out _);

            // 查找用户
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var user = await context.Users.FirstOrDefaultAsync(u => u.PhoneNumber == phoneNumber);
            if (user == null)
            {
                return (false, null, "该手机号尚未注册！请切换至【新用户注册】提交真实姓名、角色、手机号与邮箱进行注册申请。");
            }

            // 审核与禁用状态拦截
            if (user.AccountStatus == UserAccountStatus.PendingApproval)
            {
                return (false, null, "您的账号正在等待超级管理员审批中，审批通过后方可登录，请耐心等待！");
            }
            if (user.AccountStatus == UserAccountStatus.Rejected)
            {
                string reason = string.IsNullOrWhiteSpace(user.RejectReason) ? "注册信息审核未通过" : user.RejectReason;
                return (false, null, $"您的注册申请已被管理员拒绝。拒绝理由：{reason}");
            }
            if (user.AccountStatus == UserAccountStatus.Disabled)
            {
                return (false, null, "系统已进入正式使用阶段，内置演示体验账号已停用！请使用您自行注册并审批通过的账号登录。");
            }

            _activeUserId = user.Id;
            _isAuthenticated = true;
            _lastActivityTime = DateTime.UtcNow;
            await context.SaveChangesAsync();

            OnUserChanged?.Invoke();

            return (true, user, user.MustChangePassword
                ? "登录成功！新账号默认密码为 123456，请在弹窗中修改您的新密码。"
                : $"登录成功，欢迎 {user.RoleDisplayName} {user.Username}！");
        }

        public async Task<(bool Success, User? User, string Message)> RegisterWithSmsAsync(string username, UserRole role, string phoneNumber, string email, string smsCode, string grade = "初中二年级")
        {
            username = username?.Trim() ?? string.Empty;
            phoneNumber = phoneNumber?.Trim() ?? string.Empty;
            email = email?.Trim() ?? string.Empty;
            smsCode = smsCode?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(username))
            {
                return (false, null, "请输入真实姓名！");
            }
            if (!Regex.IsMatch(phoneNumber, @"^1[3-9]\d{9}$"))
            {
                return (false, null, "请输入正确的 11 位中国大陆手机号码！");
            }
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@") || !email.Contains("."))
            {
                return (false, null, "请输入格式正确的电子邮箱地址！");
            }
            if (string.IsNullOrWhiteSpace(smsCode))
            {
                return (false, null, "请输入 6 位短信验证码！");
            }

            // 校验验证码 (真实验证码或仅限预设演示账号的备用测试码 123456)
            PurgeExpiredSmsCodes();
            bool isDemoPhone = IsDemoOrTestPhone(phoneNumber);
            bool isCodeValid = isDemoPhone && smsCode == "123456";
            if (!isCodeValid && _smsCodeStore.TryGetValue(phoneNumber, out var storedInfo))
            {
                if (storedInfo.ExpireAt >= DateTime.Now && storedInfo.Code == smsCode)
                {
                    isCodeValid = true;
                }
            }

            if (!isCodeValid)
            {
                return (false, null, "短信验证码无效或已过期，请重新获取！");
            }

            // 验证通过，销毁使用过的验证码防止重放
            _smsCodeStore.TryRemove(phoneNumber, out _);

            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var existingUser = await context.Users.FirstOrDefaultAsync(u => u.PhoneNumber == phoneNumber);
            if (existingUser != null)
            {
                if (existingUser.AccountStatus == UserAccountStatus.PendingApproval)
                {
                    return (false, null, "该手机号已提交过注册申请，当前正在等待超级管理员审批中！");
                }
                if (existingUser.AccountStatus == UserAccountStatus.Approved)
                {
                    return (false, null, "该手机号已注册且审批通过，请直接在登录页面进行登录！");
                }

                // 若之前被拒绝，允许重新提交更新信息
                existingUser.Username = username;
                existingUser.Role = role;
                existingUser.Email = email;
                existingUser.Grade = string.IsNullOrWhiteSpace(grade) ? "初中二年级" : grade;
                existingUser.AccountStatus = UserAccountStatus.PendingApproval;
                existingUser.RejectReason = string.Empty;
                existingUser.RegisteredAt = DateTime.Now;
                existingUser.ApprovedAt = null;
                await context.SaveChangesAsync();

                return (true, existingUser, "🎉 注册申请已重新提交成功！请耐心等待超级管理员审批通过后再进行登录。");
            }

            string randomSuffix = Random.Shared.Next(1000, 9999).ToString();
            string bindingCode = "STU" + randomSuffix;

            var newUser = new User
            {
                Id = Guid.NewGuid(),
                Username = username,
                Role = role,
                PhoneNumber = phoneNumber,
                Email = email,
                Grade = string.IsNullOrWhiteSpace(grade) ? "初中二年级" : grade,
                Password = "123456",
                MustChangePassword = true,
                AccountStatus = UserAccountStatus.PendingApproval,
                RegisteredAt = DateTime.Now,
                BindingCode = bindingCode,
                Level = 1,
                Exp = 0,
                Coins = 100,
                CurrentStreak = 1,
                LastStudyDate = DateTime.Now,
                OcrProvider = "PaddleOcr"
            };

            context.Users.Add(newUser);
            await context.SaveChangesAsync();

            return (true, newUser, "🎉 注册成功！您的账号已提交等待超级管理员审批，审批通过前暂无法登录，请耐心等待。");
        }

        public async Task<(bool Success, User? User, string Message)> RegisterWithPasswordAsync(string username, UserRole role, string phoneNumber, string email, string password, string confirmPassword, string grade = "初中二年级")
        {
            username = username?.Trim() ?? string.Empty;
            phoneNumber = phoneNumber?.Trim() ?? string.Empty;
            email = email?.Trim() ?? string.Empty;
            password = password ?? string.Empty;
            confirmPassword = confirmPassword ?? string.Empty;

            if (string.IsNullOrWhiteSpace(username))
            {
                return (false, null, "请输入真实姓名！");
            }
            if (!Regex.IsMatch(phoneNumber, @"^1[3-9]\d{9}$"))
            {
                return (false, null, "请输入正确的 11 位中国大陆手机号码！");
            }
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@") || !email.Contains("."))
            {
                return (false, null, "请输入格式正确的电子邮箱地址！");
            }

            // 密码常规复杂度校验：长度>=8位，必须同时包含字母和数字，不能使用常见纯数字弱口令
            if (string.IsNullOrWhiteSpace(password))
            {
                return (false, null, "登录密码不能为空！");
            }
            if (password.Length < 8)
            {
                return (false, null, "密码复杂度不满足要求：密码长度不能少于 8 位字符！");
            }
            if (!Regex.IsMatch(password, @"[a-zA-Z]") || !Regex.IsMatch(password, @"[0-9]"))
            {
                return (false, null, "密码复杂度不满足要求：密码必须同时包含英文字母和阿拉伯数字！");
            }
            if (password == "12345678" || password == "87654321" || password == "123456789" || password == "abcdefgh" || password == "password123")
            {
                return (false, null, "密码过于简单，不能使用顺序字符或弱口令！");
            }
            if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
            {
                return (false, null, "两次输入的密码不一致，请核对后重新输入！");
            }

            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var existingUser = await context.Users.FirstOrDefaultAsync(u => u.PhoneNumber == phoneNumber);
            if (existingUser != null)
            {
                if (existingUser.AccountStatus == UserAccountStatus.PendingApproval)
                {
                    return (false, null, "该手机号已提交过注册申请，当前正在等待超级管理员审批中！");
                }
                if (existingUser.AccountStatus == UserAccountStatus.Approved)
                {
                    return (false, null, "该手机号已注册且审批通过，请直接前往【密码登录】进行登录！");
                }

                // 若之前被拒绝，允许重新提交更新密码与信息
                existingUser.Username = username;
                existingUser.Role = role;
                existingUser.Email = email;
                existingUser.Grade = string.IsNullOrWhiteSpace(grade) ? "初中二年级" : grade;
                existingUser.Password = PasswordHasher.HashPassword(password);
                existingUser.MustChangePassword = false;
                existingUser.AccountStatus = UserAccountStatus.PendingApproval;
                existingUser.RejectReason = string.Empty;
                existingUser.RegisteredAt = DateTime.Now;
                existingUser.ApprovedAt = null;
                await context.SaveChangesAsync();

                return (true, existingUser, "🎉 注册申请已重新提交成功！已保存您设置的安全密码，请等待超级管理员审批通过后登录。");
            }

            string randomSuffix = Random.Shared.Next(1000, 9999).ToString();
            string bindingCode = "STU" + randomSuffix;

            var newUser = new User
            {
                Id = Guid.NewGuid(),
                Username = username,
                Role = role,
                PhoneNumber = phoneNumber,
                Email = email,
                Grade = string.IsNullOrWhiteSpace(grade) ? "初中二年级" : grade,
                Password = PasswordHasher.HashPassword(password),
                MustChangePassword = false, // 用户已手工设置高复杂度密码
                AccountStatus = UserAccountStatus.PendingApproval,
                RegisteredAt = DateTime.Now,
                BindingCode = bindingCode,
                Level = 1,
                Exp = 0,
                Coins = 100,
                CurrentStreak = 1,
                LastStudyDate = DateTime.Now,
                OcrProvider = "PaddleOcr"
            };

            context.Users.Add(newUser);
            await context.SaveChangesAsync();

            return (true, newUser, "🎉 注册申请提交成功！已保存您设置的登录密码，账号正在等待超级管理员审批，审批通过后方可登录。");
        }

        public Task LogoutAsync()
        {
            _activeUserId = null;
            _isAuthenticated = false;
            _lastActivityTime = DateTime.MinValue;
            OnUserChanged?.Invoke();
            return Task.CompletedTask;
        }

        public async Task<bool> DeleteUserAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            if (_activeUserId != null && _activeUserId.Value != userId)
            {
                var caller = await context.Users.FindAsync(_activeUserId.Value);
                if (caller?.Role != UserRole.SuperAdmin)
                {
                    return false;
                }
            }

            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user != null)
            {
                var bounds = await context.StudentParentBindings.Where(r => r.ParentUserId == userId || r.StudentUserId == userId).ToListAsync();
                if (bounds.Count > 0) context.StudentParentBindings.RemoveRange(bounds);

                var favs = await context.UserFavorites.Where(f => f.UserId == userId).ToListAsync();
                if (favs.Count > 0) context.UserFavorites.RemoveRange(favs);

                var errors = await context.ErrorItems.Where(e => e.UserId == userId).ToListAsync();
                if (errors.Count > 0) context.ErrorItems.RemoveRange(errors);

                var records = await context.PracticeRecords.Where(p => p.UserId == userId).ToListAsync();
                if (records.Count > 0) context.PracticeRecords.RemoveRange(records);

                var achs = await context.UserAchievements.Where(a => a.UserId == userId).ToListAsync();
                if (achs.Count > 0) context.UserAchievements.RemoveRange(achs);

                var assignments = await context.HomeworkAssignments.Where(h => h.StudentUserId == userId || h.CreatorUserId == userId).ToListAsync();
                if (assignments.Count > 0) context.HomeworkAssignments.RemoveRange(assignments);

                var llmLogs = await context.LlmGenerationLogs.Where(l => l.UserId == userId).ToListAsync();
                if (llmLogs.Count > 0) context.LlmGenerationLogs.RemoveRange(llmLogs);

                context.Users.Remove(user);
                await context.SaveChangesAsync();
                await SyncDemoAccountsLifecycleAsync();

                if (_activeUserId == userId)
                {
                    await LogoutAsync();
                }
                else
                {
                    OnUserChanged?.Invoke();
                }
                return true;
            }
            return false;
        }

        public async Task<bool> UpdateUserRoleAsync(Guid userId, UserRole newRole)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            if (_activeUserId != null)
            {
                var caller = await context.Users.FindAsync(_activeUserId.Value);
                if (caller?.Role != UserRole.SuperAdmin)
                {
                    return false;
                }
            }

            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user != null)
            {
                user.Role = newRole;
                await context.SaveChangesAsync();
                await SyncDemoAccountsLifecycleAsync();
                OnUserChanged?.Invoke();
                return true;
            }
            return false;
        }

        public async Task<(bool Success, string Message)> UpdateUserProfileAsync(Guid userId, string username, string avatar, string email, string phoneNumber, string? grade = null)
        {
            username = username?.Trim() ?? string.Empty;
            avatar = string.IsNullOrWhiteSpace(avatar) ? "🎓" : avatar.Trim();
            email = email?.Trim() ?? string.Empty;
            phoneNumber = phoneNumber?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(username))
            {
                return (false, "用户姓名 / 昵称不能为空！");
            }

            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                return (false, "用户不存在！");
            }

            // 若修改了手机号，校验格式与查重
            if (!string.IsNullOrWhiteSpace(phoneNumber) && phoneNumber != user.PhoneNumber)
            {
                if (!Regex.IsMatch(phoneNumber, @"^1[3-9]\d{9}$"))
                {
                    return (false, "请输入正确的 11 位手机号码！");
                }
                var phoneExists = await context.Users.AnyAsync(u => u.PhoneNumber == phoneNumber && u.Id != userId);
                if (phoneExists)
                {
                    return (false, "该手机号码已被其他账号绑定，请更换！");
                }
                user.PhoneNumber = phoneNumber;
            }

            user.Username = username;
            user.Avatar = avatar;
            user.Email = email;
            if (!string.IsNullOrWhiteSpace(grade))
            {
                user.Grade = grade;
            }

            await context.SaveChangesAsync();
            await SyncDemoAccountsLifecycleAsync();
            OnUserChanged?.Invoke();
            return (true, "个人资料修改成功！");
        }

        public async Task<bool> UpdateDailyTargetAsync(Guid userId, int targetQuestions)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user != null)
            {
                user.DailyTargetQuestions = Math.Max(1, Math.Min(200, targetQuestions));
                await context.SaveChangesAsync();
                OnUserChanged?.Invoke();
                return true;
            }
            return false;
        }

        public async Task<bool> UpdateSoundEffectsEnabledAsync(Guid userId, bool enabled)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user != null)
            {
                user.SoundEffectsEnabled = enabled;
                await context.SaveChangesAsync();
                OnUserChanged?.Invoke();
                return true;
            }
            return false;
        }

        public async Task<bool> UpdateUserGradeAsync(Guid userId, string newGrade)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user != null)
            {
                user.Grade = newGrade;
                await context.SaveChangesAsync();
                OnUserChanged?.Invoke();
                return true;
            }
            return false;
        }

        public async Task<bool> UpdateUserInfoAsync(Guid userId, string username, string grade, UserRole role, string phoneNumber)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            if (_activeUserId != null)
            {
                var caller = await context.Users.FindAsync(_activeUserId.Value);
                if (caller?.Role != UserRole.SuperAdmin)
                {
                    return false;
                }
            }

            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user != null)
            {
                user.Username = username.Trim();
                user.Grade = grade;
                user.Role = role;
                user.PhoneNumber = phoneNumber.Trim();
                await context.SaveChangesAsync();
                await SyncDemoAccountsLifecycleAsync();
                OnUserChanged?.Invoke();
                return true;
            }
            return false;
        }

        public async Task<bool> ResetUserPasswordAsync(Guid userId, string newPassword = "123456", Guid? callerUserId = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var callerId = callerUserId ?? _activeUserId;
            if (callerId != null)
            {
                var caller = await context.Users.FindAsync(callerId.Value);
                if (caller == null) return false;

                if (caller.Role != UserRole.SuperAdmin)
                {
                    // 严格基于合法家长与学生关联的 RBAC 权限放行，防横向越权 IDOR
                    bool isBoundParent = caller.Role == UserRole.Parent &&
                        await context.StudentParentBindings.AnyAsync(b => b.ParentUserId == caller.Id && b.StudentUserId == userId);

                    if (!isBoundParent)
                    {
                        return false;
                    }
                }
            }

            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user != null)
            {
                user.Password = PasswordHasher.HashPassword(newPassword);
                user.MustChangePassword = true;
                await context.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public async Task<List<User>> GetPendingApprovalUsersAsync()
        {
            await using var dbScope = await CreateDbScopeAsync();
            return await dbScope.Context.Users
                .Where(u => u.AccountStatus == UserAccountStatus.PendingApproval || u.AccountStatus == UserAccountStatus.Rejected)
                .OrderByDescending(u => u.RegisteredAt)
                .ToListAsync();
        }

        public async Task<(bool Success, string Message)> ApproveUserRegistrationAsync(Guid userId, Guid adminUserId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var admin = await context.Users.FirstOrDefaultAsync(u => u.Id == adminUserId);
            if (admin == null || admin.Role != UserRole.SuperAdmin)
            {
                return (false, "权限不足：只有超级管理员方可执行注册审批！");
            }

            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return (false, "用户不存在！");

            user.AccountStatus = UserAccountStatus.Approved;
            user.ApprovedAt = DateTime.Now;
            user.RejectReason = string.Empty;
            await context.SaveChangesAsync();
            return (true, $"已成功审批通过用户【{user.Username} ({user.PhoneNumber})】！该账号现已激活，可正常登录。");
        }

        public async Task<(bool Success, string Message)> RejectUserRegistrationAsync(Guid userId, Guid adminUserId, string reason)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var admin = await context.Users.FirstOrDefaultAsync(u => u.Id == adminUserId);
            if (admin == null || admin.Role != UserRole.SuperAdmin)
            {
                return (false, "权限不足：只有超级管理员方可执行注册审批！");
            }

            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return (false, "用户不存在！");

            user.AccountStatus = UserAccountStatus.Rejected;
            user.RejectReason = string.IsNullOrWhiteSpace(reason) ? "注册信息审核未通过，请补充准确信息后重试" : reason.Trim();
            await context.SaveChangesAsync();
            return (true, $"已驳回用户【{user.Username} ({user.PhoneNumber})】的注册申请，已记录拒绝理由。");
        }

        public async Task<List<User>> GetBoundStudentsAsync(Guid parentId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var studentIds = await dbScope.Context.StudentParentBindings
                .Where(r => r.ParentUserId == parentId)
                .Select(r => r.StudentUserId)
                .ToListAsync();

            return await dbScope.Context.Users
                .AsNoTracking()
                .Where(u => studentIds.Contains(u.Id))
                .ToListAsync();
        }

        public async Task<(bool Success, string Message)> BindStudentByCodeAsync(Guid parentId, string bindingCode, string relation = "监护人")
        {
            bindingCode = (bindingCode ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(bindingCode))
            {
                return (false, "绑定码不能为空！");
            }

            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var student = await context.Users.FirstOrDefaultAsync(u => u.BindingCode == bindingCode);
            if (student == null)
            {
                return (false, "未找到该绑定码对应的学生账号，请核对后重试。");
            }

            var exists = await context.StudentParentBindings.AnyAsync(r => r.ParentUserId == parentId && r.StudentUserId == student.Id);
            if (exists)
            {
                return (false, $"该学生【{student.Username}】已在您的关联列表中，无需重复绑定。");
            }

            var rel = new StudentParentBinding
            {
                Id = Guid.NewGuid(),
                ParentUserId = parentId,
                StudentUserId = student.Id,
                RelationType = relation,
                CreatedAt = DateTime.Now
            };

            context.StudentParentBindings.Add(rel);
            await context.SaveChangesAsync();

            OnUserChanged?.Invoke();
            return (true, $"成功绑定学生账号：{student.Username} ({student.Grade})");
        }

        public async Task<(bool Success, User? Student, string Message)> CreateChildStudentAsync(Guid parentId, string username, string grade, string relation = "监护人")
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return (false, null, "学生姓名/昵称不能为空！");
            }

            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var parent = await context.Users.FirstOrDefaultAsync(u => u.Id == parentId);
            if (parent == null)
            {
                return (false, null, "家长账号不存在！");
            }

            string randomSuffix = Random.Shared.Next(1000, 9999).ToString();
            string bindingCode = "STU" + randomSuffix;

            var student = new User
            {
                Id = Guid.NewGuid(),
                Username = username.Trim(),
                Role = UserRole.Student,
                Grade = string.IsNullOrWhiteSpace(grade) ? "初中二年级" : grade,
                BindingCode = bindingCode,
                Password = PasswordHasher.HashPassword("123456"),
                MustChangePassword = true,
                PhoneNumber = "",
                Level = 1,
                Exp = 0,
                Coins = 100,
                CurrentStreak = 1,
                LastStudyDate = DateTime.Now,
                OcrProvider = "PaddleOcr"
            };

            context.Users.Add(student);

            var rel = new StudentParentBinding
            {
                Id = Guid.NewGuid(),
                ParentUserId = parentId,
                StudentUserId = student.Id,
                RelationType = relation,
                CreatedAt = DateTime.Now
            };
            context.StudentParentBindings.Add(rel);

            await context.SaveChangesAsync();

            OnUserChanged?.Invoke();
            return (true, student, $"成功开通学生账号【{student.Username}】，默认密码为 123456，专属绑定码为：{bindingCode}");
        }

        public async Task<(bool Success, string Message)> UnbindStudentAsync(Guid parentId, Guid studentId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var rel = await context.StudentParentBindings.FirstOrDefaultAsync(r => r.ParentUserId == parentId && r.StudentUserId == studentId);
            if (rel != null)
            {
                context.StudentParentBindings.Remove(rel);
                await context.SaveChangesAsync();
                OnUserChanged?.Invoke();
                return (true, "已成功解除与该学生的关联！");
            }
            return (false, "未找到关联记录。");
        }

        public async Task<(bool Success, string Message)> UpdateParentEncouragementNoteAsync(Guid studentId, string note)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var student = await context.Users.FirstOrDefaultAsync(u => u.Id == studentId);
            if (student == null)
            {
                return (false, "未找到指定学生账号！");
            }

            student.ParentEncouragementNote = note?.Trim() ?? string.Empty;
            student.ParentNoteUpdatedAt = DateTime.Now;
            await context.SaveChangesAsync();
            OnUserChanged?.Invoke();
            return (true, "💌 家长关怀寄语已成功发布，孩子登录首页即可看到！");
        }

        public async Task<(bool Success, string Message, int? NewCoins, int? NewExp, string? Note, DateTime? NoteTime)> AwardParentPraiseRewardAsync(Guid studentId, string badge, string comment, int rewardCoins)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var student = await context.Users.FirstOrDefaultAsync(u => u.Id == studentId);
            if (student == null)
            {
                return (false, "未找到指定学生账号！", null, null, null, null);
            }

            if (rewardCoins > 0)
            {
                student.Coins += rewardCoins;
                student.Exp += rewardCoins;
            }

            student.ParentEncouragementNote = $"【家长颁发 {badge}】{comment}".Trim();
            student.ParentNoteUpdatedAt = DateTime.Now;
            await context.SaveChangesAsync();
            OnUserChanged?.Invoke();
            return (true, "🎉 赞赏激励与暖心寄语已同步送达！", student.Coins, student.Exp, student.ParentEncouragementNote, student.ParentNoteUpdatedAt);
        }

        public async Task<(int TotalUsers, int PendingUsers)> GetUserStatisticsAsync()
        {
            await using var dbScope = await CreateDbScopeAsync();
            var total = await dbScope.Context.Users.CountAsync();
            var pending = await dbScope.Context.Users.CountAsync(u => u.AccountStatus == UserAccountStatus.PendingApproval);
            return (total, pending);
        }

        public async Task<bool> UpdateUserSettingsAsync(User user)
        {
            if (user == null || user.Id == Guid.Empty) return false;
            await using var dbScope = await CreateDbScopeAsync();
            var context = dbScope.Context;
            var existing = await context.Users.FirstOrDefaultAsync(u => u.Id == user.Id);
            if (existing == null) return false;

            existing.LlmApiKey = user.LlmApiKey;
            existing.LlmBaseUrl = user.LlmBaseUrl;
            existing.LlmModelName = user.LlmModelName;
            existing.AliyunAccessKeyId = user.AliyunAccessKeyId;
            existing.AliyunAccessKeySecret = user.AliyunAccessKeySecret;
            existing.AliyunSmsSignName = user.AliyunSmsSignName;
            existing.AliyunSmsTemplateCode = user.AliyunSmsTemplateCode;
            existing.AliyunSmsTemplateParam = user.AliyunSmsTemplateParam;
            existing.AliyunSmsEndpoint = user.AliyunSmsEndpoint;
            existing.AliyunSmsRegionId = user.AliyunSmsRegionId;
            existing.OcrProvider = user.OcrProvider;
            existing.BaiduApiKey = user.BaiduApiKey;
            existing.BaiduSecretKey = user.BaiduSecretKey;
            existing.BaiduOcrEndpoint = user.BaiduOcrEndpoint;
            existing.SessionTimeoutMinutes = user.SessionTimeoutMinutes;

            await context.SaveChangesAsync();
            OnUserChanged?.Invoke();
            return true;
        }

        // 架构安全：短时一次性安全下载票据 (OTAC) 结构
        public class DownloadTicketInfo
        {
            public string Ticket { get; set; } = string.Empty;
            public Guid UserId { get; set; }
            public string Purpose { get; set; } = string.Empty;
            public string? Resource { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
            public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddSeconds(60);
            public bool IsUsed { get; set; } = false;
        }

        private static readonly ConcurrentDictionary<string, DownloadTicketInfo> _downloadTicketStore = new();

        public Task<string> GenerateDownloadTicketAsync(Guid userId, string purpose, string? resource = null)
        {
            if (userId == Guid.Empty) throw new ArgumentException("用户ID不能为空", nameof(userId));
            if (string.IsNullOrWhiteSpace(purpose)) throw new ArgumentException("票据用途不能为空", nameof(purpose));

            PurgeExpiredTickets();

            byte[] randomBytes = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomBytes);
            }
            string ticket = Convert.ToHexString(randomBytes).ToLowerInvariant();

            var ticketInfo = new DownloadTicketInfo
            {
                Ticket = ticket,
                UserId = userId,
                Purpose = purpose.Trim(),
                Resource = resource?.Trim(),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddSeconds(60),
                IsUsed = false
            };

            _downloadTicketStore[ticket] = ticketInfo;
            return Task.FromResult(ticket);
        }

        public Task<(bool Valid, Guid UserId, string Purpose, string? Resource)> ValidateAndConsumeDownloadTicketAsync(string ticket)
        {
            if (string.IsNullOrWhiteSpace(ticket))
            {
                return Task.FromResult<(bool, Guid, string, string?)>((false, Guid.Empty, string.Empty, null));
            }

            ticket = ticket.Trim();
            if (_downloadTicketStore.TryRemove(ticket, out var info))
            {
                if (!info.IsUsed && info.ExpiresAt >= DateTime.UtcNow)
                {
                    info.IsUsed = true;
                    return Task.FromResult<(bool, Guid, string, string?)>((true, info.UserId, info.Purpose, info.Resource));
                }
            }

            return Task.FromResult<(bool, Guid, string, string?)>((false, Guid.Empty, string.Empty, null));
        }

        public static void PurgeExpiredTickets()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _downloadTicketStore)
            {
                if (kvp.Value.ExpiresAt < now || kvp.Value.IsUsed)
                {
                    _downloadTicketStore.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}
