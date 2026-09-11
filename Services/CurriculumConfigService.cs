using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class CurriculumConfigService : ICurriculumConfigService
    {
        private readonly AppDbContext _context;
        private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;
        private readonly IUserSessionService? _userSessionService;
        public event Action? OnCurriculumChanged;

        private ValueTask<AsyncDbScope> CreateDbScopeAsync()
        {
            return AsyncDbScope.CreateAsync(_dbContextFactory, _context);
        }

        public CurriculumConfigService(AppDbContext context, IUserSessionService? userSessionService = null, IDbContextFactory<AppDbContext>? dbContextFactory = null)
        {
            _context = context;
            _userSessionService = userSessionService;
            _dbContextFactory = dbContextFactory;
        }

        private async Task<(bool IsAuthorized, string Message)> VerifySuperAdminPermissionAsync(AppDbContext ctx, Guid? userId)
        {
            var targetId = userId ?? _userSessionService?.CurrentUserId;
            if (!targetId.HasValue && _userSessionService != null)
            {
                var activeUser = await _userSessionService.GetActiveUserAsync();
                targetId = activeUser?.Id;
            }

            if (!targetId.HasValue || targetId.Value == Guid.Empty)
            {
                // 如果未提供 userId 且会话中也无活跃用户（如纯内部无上下文调用），为保证兼容性允许执行；但在有用户上下文或明确传入 userId 时必须鉴权
                if (userId.HasValue)
                {
                    return (false, "权限不足：必须提供有效的操作者身份，仅超级管理员允许维护学科课程配置！");
                }
                return (true, string.Empty);
            }

            var user = await ctx.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetId.Value);
            if (user == null || user.Role != UserRole.SuperAdmin)
            {
                return (false, "权限不足：仅系统超级管理员允许新增、修改、删除或重置学科课程配置！");
            }

            return (true, string.Empty);
        }

        public List<string> GetAvailableGrades()
        {
            return GradeSubjectProvider.AvailableGrades;
        }

        public async Task EnsureInitializedAsync()
        {
            try
            {
                await using var dbScope = await CreateDbScopeAsync();
                var ctx = dbScope.Context;

                var hasAny = await ctx.CurriculumSubjectConfigs.AnyAsync();
                if (!hasAny)
                {
                    await SeedDefaultCurriculumAsync(ctx);
                }
                else
                {
                    await RefreshStaticCacheAsync(ctx);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CurriculumConfigService] EnsureInitializedAsync warning: {ex.Message}");
            }
        }

        private async Task SeedDefaultCurriculumAsync(AppDbContext ctx)
        {
            var configs = new List<CurriculumSubjectConfig>();
            int order = 0;

            foreach (var grade in GradeSubjectProvider.AvailableGrades)
            {
                if (GradeSubjectProvider.DefaultGradeSubjects.TryGetValue(grade, out var subjects))
                {
                    foreach (var subject in subjects)
                    {
                        var topics = GradeSubjectProvider.DefaultSubjectCategories.TryGetValue(subject, out var defaultTopics)
                            ? defaultTopics
                            : new List<string> { "全部", "基础概念", "核心考点", "综合提升" };

                        configs.Add(new CurriculumSubjectConfig
                        {
                            Id = Guid.NewGuid(),
                            Grade = grade,
                            Subject = subject,
                            TopicsJson = JsonSerializer.Serialize(topics),
                            SortOrder = ++order,
                            IsBuiltIn = true,
                            UpdatedAt = DateTime.Now
                        });
                    }
                }
            }

            await ctx.CurriculumSubjectConfigs.AddRangeAsync(configs);
            await ctx.SaveChangesAsync();
            await RefreshStaticCacheAsync(ctx);
        }

        private async Task RefreshStaticCacheAsync(AppDbContext ctx)
        {
            var all = await ctx.CurriculumSubjectConfigs
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Grade)
                .ToListAsync();

            var gradeSubjects = new Dictionary<string, List<string>>();
            var subjectCategories = new Dictionary<string, List<string>>();

            foreach (var config in all)
            {
                if (!gradeSubjects.ContainsKey(config.Grade))
                {
                    gradeSubjects[config.Grade] = new List<string>();
                }
                if (!gradeSubjects[config.Grade].Contains(config.Subject))
                {
                    gradeSubjects[config.Grade].Add(config.Subject);
                }

                try
                {
                    var topics = JsonSerializer.Deserialize<List<string>>(config.TopicsJson) ?? new List<string>();
                    if (!topics.Contains("全部"))
                    {
                        topics.Insert(0, "全部");
                    }

                    if (!subjectCategories.ContainsKey(config.Subject))
                    {
                        subjectCategories[config.Subject] = topics;
                    }
                    else
                    {
                        // 合并专题考点
                        foreach (var t in topics)
                        {
                            if (!subjectCategories[config.Subject].Contains(t))
                            {
                                subjectCategories[config.Subject].Add(t);
                            }
                        }
                    }
                }
                catch { }
            }

            GradeSubjectProvider.SyncDynamicCurriculum(gradeSubjects, subjectCategories);
        }

        public async Task<List<string>> GetSubjectsByGradeAsync(string grade)
        {
            await EnsureInitializedAsync();
            return GradeSubjectProvider.GetSubjectsByGrade(grade);
        }

        public async Task<List<string>> GetCategoriesBySubjectAsync(string subject)
        {
            await EnsureInitializedAsync();
            return GradeSubjectProvider.GetCategoriesBySubject(subject);
        }

        public async Task<List<CurriculumSubjectConfig>> GetAllConfigsAsync()
        {
            await EnsureInitializedAsync();
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            return await ctx.CurriculumSubjectConfigs
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Grade)
                .ToListAsync();
        }

        public async Task<List<CurriculumSubjectConfig>> GetConfigsByGradeAsync(string grade)
        {
            await EnsureInitializedAsync();
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            return await ctx.CurriculumSubjectConfigs
                .Where(c => c.Grade == grade)
                .OrderBy(c => c.SortOrder)
                .ToListAsync();
        }

        public async Task<(bool Success, string Message)> SaveSubjectTopicsAsync(Guid configId, List<string> topics, Guid? userId = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var auth = await VerifySuperAdminPermissionAsync(ctx, userId);
            if (!auth.IsAuthorized)
            {
                return (false, auth.Message);
            }

            var config = await ctx.CurriculumSubjectConfigs.FindAsync(configId);
            if (config == null)
            {
                return (false, "未找到指定的学科配置！");
            }

            if (topics == null || topics.Count == 0)
            {
                return (false, "考点专题列表不能为空！");
            }

            var cleanTopics = topics
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct()
                .ToList();

            if (!cleanTopics.Contains("全部"))
            {
                cleanTopics.Insert(0, "全部");
            }

            config.TopicsJson = JsonSerializer.Serialize(cleanTopics);
            config.UpdatedAt = DateTime.Now;

            await ctx.SaveChangesAsync();
            await RefreshStaticCacheAsync(ctx);
            OnCurriculumChanged?.Invoke();
            return (true, $"已成功更新【{config.Grade} - {config.Subject}】的专题知识点考点列表！");
        }

        public async Task<(bool Success, string Message)> AddSubjectConfigAsync(string grade, string subject, List<string> topics, Guid? userId = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var auth = await VerifySuperAdminPermissionAsync(ctx, userId);
            if (!auth.IsAuthorized)
            {
                return (false, auth.Message);
            }

            grade = grade?.Trim() ?? string.Empty;
            subject = subject?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(grade) || string.IsNullOrWhiteSpace(subject))
            {
                return (false, "所属年级与学科名称均不能为空！");
            }

            var exists = await ctx.CurriculumSubjectConfigs.AnyAsync(c => c.Grade == grade && c.Subject == subject);
            if (exists)
            {
                return (false, $"【{grade}】下已存在名为【{subject}】的学科配置！");
            }

            var cleanTopics = (topics ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct()
                .ToList();

            if (!cleanTopics.Contains("全部"))
            {
                cleanTopics.Insert(0, "全部");
            }
            if (cleanTopics.Count <= 1)
            {
                cleanTopics.Add("基础概念");
                cleanTopics.Add("核心考点");
                cleanTopics.Add("综合提升");
            }

            var maxOrder = await ctx.CurriculumSubjectConfigs.MaxAsync(c => (int?)c.SortOrder) ?? 0;

            var newConfig = new CurriculumSubjectConfig
            {
                Id = Guid.NewGuid(),
                Grade = grade,
                Subject = subject,
                TopicsJson = JsonSerializer.Serialize(cleanTopics),
                SortOrder = maxOrder + 1,
                IsBuiltIn = false,
                UpdatedAt = DateTime.Now
            };

            await ctx.CurriculumSubjectConfigs.AddAsync(newConfig);
            await ctx.SaveChangesAsync();
            await RefreshStaticCacheAsync(ctx);
            OnCurriculumChanged?.Invoke();
            return (true, $"已成功在【{grade}】中新增【{subject}】学科！");
        }

        public async Task<(bool Success, string Message)> DeleteSubjectConfigAsync(Guid configId, Guid? userId = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var auth = await VerifySuperAdminPermissionAsync(ctx, userId);
            if (!auth.IsAuthorized)
            {
                return (false, auth.Message);
            }

            var config = await ctx.CurriculumSubjectConfigs.FindAsync(configId);
            if (config == null)
            {
                return (false, "未找到指定的学科配置！");
            }

            ctx.CurriculumSubjectConfigs.Remove(config);
            await ctx.SaveChangesAsync();
            await RefreshStaticCacheAsync(ctx);
            OnCurriculumChanged?.Invoke();
            return (true, $"已成功移除【{config.Grade} - {config.Subject}】学科！");
        }

        public async Task<(bool Success, string Message)> ResetToDefaultCurriculumAsync(Guid? userId = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var auth = await VerifySuperAdminPermissionAsync(ctx, userId);
            if (!auth.IsAuthorized)
            {
                return (false, auth.Message);
            }

            var all = await ctx.CurriculumSubjectConfigs.ToListAsync();
            ctx.CurriculumSubjectConfigs.RemoveRange(all);
            await ctx.SaveChangesAsync();

            await SeedDefaultCurriculumAsync(ctx);
            OnCurriculumChanged?.Invoke();
            return (true, "已成功恢复系统全部年级官方标准科目与专题考点体系！");
        }
    }
}
