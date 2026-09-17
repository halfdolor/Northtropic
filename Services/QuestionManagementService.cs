using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class QuestionManagementService : IQuestionManagementService
    {
        private readonly AppDbContext _dbContext;
        private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;

        private ValueTask<AsyncDbScope> CreateDbScopeAsync()
        {
            return AsyncDbScope.CreateAsync(_dbContextFactory, _dbContext);
        }

        public QuestionManagementService(AppDbContext dbContext, IDbContextFactory<AppDbContext>? dbContextFactory = null)
        {
            _dbContext = dbContext;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<List<Question>> GetQuestionsForManagementAsync(Guid currentUserId, string? subject = null, string? category = null, bool? isPublic = null, PublishStatusEnum? status = null, IEnumerable<Guid>? specificQuestionIds = null, QuestionType? questionType = null, int? difficulty = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var currentUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUserId);
            bool isSuperAdmin = currentUser?.Role == UserRole.SuperAdmin;

            var query = db.Questions.AsNoTracking().AsQueryable();

            if (specificQuestionIds != null && specificQuestionIds.Any())
            {
                var idList = specificQuestionIds.ToList();
                query = query.Where(q => idList.Contains(q.Id));
            }
            if (!string.IsNullOrWhiteSpace(subject) && subject != "全部分科")
            {
                query = query.Where(q => q.Subject == subject);
            }
            if (!string.IsNullOrWhiteSpace(category) && category != "全部考点")
            {
                query = query.Where(q => q.Category == category);
            }
            if (isPublic.HasValue)
            {
                query = query.Where(q => q.IsPublic == isPublic.Value);
            }
            if (status.HasValue)
            {
                query = query.Where(q => q.PublishStatus == status.Value);
            }
            if (questionType.HasValue)
            {
                query = query.Where(q => q.Type == questionType.Value);
            }
            if (difficulty.HasValue && difficulty.Value > 0)
            {
                query = query.Where(q => q.Difficulty == difficulty.Value);
            }

            // 规则：超级管理员可统览所有题目；其它用户只看全网共享公共题库 (IsPublic==true) 与自己的私有题库 (CreatedByUserId==currentUserId)
            if (!isSuperAdmin)
            {
                query = query.Where(q => q.IsPublic || q.CreatedByUserId == currentUserId);
            }

            return await query.OrderByDescending(q => q.CreatedAt).ToListAsync();
        }

        public async Task<Question?> GetQuestionByIdAsync(Guid id)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;
            return await db.Questions.FirstOrDefaultAsync(q => q.Id == id);
        }

        public async Task<(bool Success, string Message, Question? Question)> AddQuestionAsync(Question question, Guid? userId = null)
        {
            if (question == null)
            {
                return (false, "试题对象不能为空！", null);
            }

            if (!userId.HasValue || userId.Value == Guid.Empty)
            {
                return (false, "用户未登录，无法录入试题！", null);
            }

            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
            if (user == null || !user.CanModifyQuestionBank)
            {
                return (false, "权限不足，仅教师或管理员允许录入试题！", null);
            }

            if (string.IsNullOrWhiteSpace(question.Stem))
            {
                return (false, "试题题干不能为空！", null);
            }

            if (string.IsNullOrWhiteSpace(question.Subject))
            {
                return (false, "所属学科不能为空！", null);
            }

            if (string.IsNullOrWhiteSpace(question.Category))
            {
                return (false, "考点分类不能为空！", null);
            }

            if (string.IsNullOrWhiteSpace(question.CorrectAnswer))
            {
                return (false, "正确答案不能为空！", null);
            }

            if (question.Type == QuestionType.SingleChoice || question.Type == QuestionType.MultipleChoice)
            {
                if (string.IsNullOrWhiteSpace(question.OptionsJson) || question.OptionsJson.Trim() == "[]")
                {
                    return (false, "选择题必须包含有效选项列表！", null);
                }
                try
                {
                    var opts = System.Text.Json.JsonSerializer.Deserialize<List<string>>(question.OptionsJson);
                    if (opts == null || opts.Count < 2)
                    {
                        return (false, "选择题至少需要提供两个选项！", null);
                    }
                }
                catch
                {
                    return (false, "选项格式不合法（非有效 JSON 数组）！", null);
                }
            }

            if (question.Id == Guid.Empty)
            {
                question.Id = Guid.NewGuid();
            }

            question.CreatedByUserId = user.Id;
            question.CreatedAt = DateTime.Now;

            // 超级管理员可直接指定公共库状态；普通教师录入默认为私有题目
            if (user.Role != UserRole.SuperAdmin)
            {
                question.PublishStatus = PublishStatusEnum.Private;
                question.IsPublic = false;
            }

            db.Questions.Add(question);
            await db.SaveChangesAsync();
            PracticeService.InvalidateCategoryCache();

            return (true, "试题录入成功！", question);
        }

        public async Task<bool> UpdateQuestionAsync(Question question, Guid? userId = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var existing = await db.Questions.FirstOrDefaultAsync(q => q.Id == question.Id);
            if (existing == null) return false;

            if (userId.HasValue && userId.Value != Guid.Empty)
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
                if (user == null) return false;

                bool isSuperAdmin = user.Role == UserRole.SuperAdmin;
                bool isAuthor = existing.CreatedByUserId == userId.Value;
                // 架构安全：超级管理员可修改所有试题；教师仅能修改自己创建的试题（防水平越权 IDOR）
                if (!isSuperAdmin && (!user.CanModifyQuestionBank || !isAuthor))
                {
                    return false;
                }
            }

            existing.Subject = question.Subject;
            existing.Category = question.Category;
            existing.GradeTarget = question.GradeTarget;
            existing.Type = question.Type;
            existing.Stem = question.Stem;
            existing.OptionsJson = question.OptionsJson;
            existing.CorrectAnswer = question.CorrectAnswer;
            existing.StandardAnalysis = question.StandardAnalysis;
            existing.Difficulty = question.Difficulty;

            await db.SaveChangesAsync();
            PracticeService.InvalidateCategoryCache();
            return true;
        }

        public async Task<bool> DeleteQuestionAsync(Guid id, Guid? userId = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var existing = await db.Questions.FirstOrDefaultAsync(q => q.Id == id);
            if (existing == null) return false;

            if (userId.HasValue && userId.Value != Guid.Empty)
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
                if (user == null) return false;

                bool isSuperAdmin = user.Role == UserRole.SuperAdmin;
                bool isAuthor = existing.CreatedByUserId == userId.Value;
                // 架构安全：超级管理员可删除所有试题；教师仅能删除自己创建的试题（防水平越权 IDOR）
                if (!isSuperAdmin && (!user.CanModifyQuestionBank || !isAuthor))
                {
                    return false;
                }
            }

            // 级联清理外键关联，避免孤儿数据与外键冲突
            var relatedLogs = await db.LlmGenerationLogs.Where(l => l.QuestionId == id).ToListAsync();
            foreach (var log in relatedLogs)
            {
                log.QuestionId = null;
            }

            var relatedFavorites = await db.UserFavorites.Where(f => f.QuestionId == id).ToListAsync();
            if (relatedFavorites.Count > 0)
            {
                db.UserFavorites.RemoveRange(relatedFavorites);
            }

            var relatedErrors = await db.ErrorItems.Where(e => e.QuestionId == id).ToListAsync();
            if (relatedErrors.Count > 0)
            {
                db.ErrorItems.RemoveRange(relatedErrors);
            }

            var relatedPracticeRecords = await db.PracticeRecords.Where(r => r.QuestionId == id).ToListAsync();
            if (relatedPracticeRecords.Count > 0)
            {
                db.PracticeRecords.RemoveRange(relatedPracticeRecords);
            }

            db.Questions.Remove(existing);
            await db.SaveChangesAsync();
            PracticeService.InvalidateCategoryCache();
            return true;
        }

        public async Task<bool> ApplyForPublicPublishAsync(Guid questionId, Guid currentUserId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var question = await db.Questions.FirstOrDefaultAsync(q => q.Id == questionId);
            if (question == null) return false;

            if (question.CreatedByUserId != currentUserId && question.CreatedByUserId != null) return false;

            question.PublishStatus = PublishStatusEnum.Pending;
            await db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> ApprovePublishRequestAsync(Guid questionId, Guid adminUserId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var adminUser = await db.Users.FirstOrDefaultAsync(u => u.Id == adminUserId);
            if (adminUser == null || !adminUser.CanManageQuestionBank) return false;

            var question = await db.Questions.FirstOrDefaultAsync(q => q.Id == questionId);
            if (question == null) return false;

            question.IsPublic = true;
            question.PublishStatus = PublishStatusEnum.Approved;

            // 如果该题由某位学员创建/上传，发放奖励 (+50 EXP & +20 金币)
            if (question.CreatedByUserId.HasValue)
            {
                var author = await db.Users.FirstOrDefaultAsync(u => u.Id == question.CreatedByUserId.Value);
                if (author != null)
                {
                    author.Exp += 50;
                    author.Coins += 20;
                    GamificationService.CheckAndProcessLevelUp(author);
                }
            }

            await db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> RejectPublishRequestAsync(Guid questionId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var question = await db.Questions.FirstOrDefaultAsync(q => q.Id == questionId);
            if (question == null) return false;

            question.PublishStatus = PublishStatusEnum.Rejected;
            await db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DirectPublishAsync(Guid questionId, Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null || !user.CanManageQuestionBank) return false;

            var question = await db.Questions.FirstOrDefaultAsync(q => q.Id == questionId);
            if (question == null) return false;

            question.IsPublic = true;
            question.PublishStatus = PublishStatusEnum.Approved;
            await db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> RetractToPrivateAsync(Guid questionId, Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return false;

            var question = await db.Questions.FirstOrDefaultAsync(q => q.Id == questionId);
            if (question == null) return false;

            // 仅题库管理员或创建者本人有权撤回为私有
            if (!user.CanManageQuestionBank && question.CreatedByUserId != userId) return false;

            question.IsPublic = false;
            question.PublishStatus = PublishStatusEnum.Private;
            await db.SaveChangesAsync();
            return true;
        }

        public async Task<string> ExportQuestionsJsonAsync(Guid currentUserId, string? subject = null, string? category = null, bool? isPublic = null, PublishStatusEnum? status = null, IEnumerable<Guid>? specificQuestionIds = null)
        {
            var questions = await GetQuestionsForManagementAsync(currentUserId, subject, category, isPublic, status, specificQuestionIds);
            var exportList = questions.Select(q => new
            {
                q.Subject,
                q.Category,
                q.GradeTarget,
                Type = q.Type.ToString(),
                q.Stem,
                Options = System.Text.Json.JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(q.OptionsJson) ? "[]" : q.OptionsJson),
                q.CorrectAnswer,
                q.StandardAnalysis,
                q.Difficulty,
                q.IsPublic
            }).ToList();

            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            return System.Text.Json.JsonSerializer.Serialize(exportList, options);
        }

        public async Task<byte[]> ExportQuestionsCsvAsync(Guid currentUserId, string? subject = null, string? category = null, bool? isPublic = null, PublishStatusEnum? status = null, IEnumerable<Guid>? specificQuestionIds = null)
        {
            var questions = await GetQuestionsForManagementAsync(currentUserId, subject, category, isPublic, status, specificQuestionIds);
            var sb = new System.Text.StringBuilder();

            // CSV 标头行
            sb.AppendLine("序号,学科,考点专题,学段年级,试题类型,难度,题干,选项,正确答案,官方解析,是否全网公开");

            int idx = 1;
            foreach (var q in questions)
            {
                string typeStr = q.Type switch
                {
                    QuestionType.SingleChoice => "单选题",
                    QuestionType.MultipleChoice => "多选题",
                    QuestionType.FillInBlank => "填空题",
                    QuestionType.ShortAnswer => "简答题",
                    QuestionType.EssayAnalysis => "综合大题",
                    _ => q.Type.ToString()
                };

                string optionsStr = string.Empty;
                if (!string.IsNullOrWhiteSpace(q.OptionsJson))
                {
                    try
                    {
                        var opts = System.Text.Json.JsonSerializer.Deserialize<List<string>>(q.OptionsJson);
                        if (opts != null && opts.Count > 0)
                        {
                            optionsStr = string.Join(" | ", opts);
                        }
                    }
                    catch { }
                }

                static string EscapeCsv(string? val)
                {
                    if (string.IsNullOrEmpty(val)) return "\"\"";
                    string text = val;
                    // CWE-1236 架构安全防范：防御 CSV / Excel 电子表格公式注入 (Formula Injection)
                    // 当单元格以 '=', '@', '\t', '\r' 开头，或以 '+', '-' 开头但非纯数字时，前置单引号转义为纯文本
                    char firstChar = text[0];
                    if (firstChar == '=' || firstChar == '@' || firstChar == '\t' || firstChar == '\r' ||
                        ((firstChar == '+' || firstChar == '-') && !double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
                    {
                        text = "'" + text;
                    }
                    string escaped = text.Replace("\"", "\"\"");
                    return $"\"{escaped}\"";
                }

                sb.AppendLine($"{idx},{EscapeCsv(q.Subject)},{EscapeCsv(q.Category)},{EscapeCsv(q.GradeTarget)},{EscapeCsv(typeStr)},{q.Difficulty},{EscapeCsv(q.Stem)},{EscapeCsv(optionsStr)},{EscapeCsv(q.CorrectAnswer)},{EscapeCsv(q.StandardAnalysis)},{(q.IsPublic ? "公开" : "私有")}");
                idx++;
            }

            // 添加 UTF-8 BOM，确保 Excel 中文不乱码
            var encoding = new System.Text.UTF8Encoding(true);
            return encoding.GetPreamble().Concat(encoding.GetBytes(sb.ToString())).ToArray();
        }

        public async Task<byte[]> ExportQuestionsXlsxAsync(Guid currentUserId, string? subject = null, string? category = null, bool? isPublic = null, PublishStatusEnum? status = null, IEnumerable<Guid>? specificQuestionIds = null)
        {
            var questions = await GetQuestionsForManagementAsync(currentUserId, subject, category, isPublic, status, specificQuestionIds);
            var headers = new List<string> { "序号", "学科", "考点专题", "学段年级", "试题类型", "难度", "题干", "选项", "正确答案", "官方解析", "是否全网公开" };

            var rows = new List<List<string>>();
            int idx = 1;
            foreach (var q in questions)
            {
                string typeStr = q.Type switch
                {
                    QuestionType.SingleChoice => "单选题",
                    QuestionType.MultipleChoice => "多选题",
                    QuestionType.FillInBlank => "填空题",
                    QuestionType.ShortAnswer => "简答题",
                    QuestionType.EssayAnalysis => "综合大题",
                    _ => q.Type.ToString()
                };

                string optionsStr = string.Empty;
                if (!string.IsNullOrWhiteSpace(q.OptionsJson))
                {
                    try
                    {
                        var opts = System.Text.Json.JsonSerializer.Deserialize<List<string>>(q.OptionsJson);
                        if (opts != null && opts.Count > 0)
                        {
                            optionsStr = string.Join(" | ", opts);
                        }
                    }
                    catch { }
                }

                rows.Add(new List<string>
                {
                    idx.ToString(),
                    q.Subject ?? "",
                    q.Category ?? "",
                    q.GradeTarget ?? "",
                    typeStr,
                    q.Difficulty.ToString(),
                    q.Stem ?? "",
                    optionsStr,
                    q.CorrectAnswer ?? "",
                    q.StandardAnalysis ?? "",
                    q.IsPublic ? "公开" : "私有"
                });
                idx++;
            }

            return OpenXmlSpreadsheetHelper.CreateSpreadsheet("题库导出数据", headers, rows);
        }

        public async Task<int> BatchDeleteQuestionsAsync(IEnumerable<Guid> ids, Guid userId)
        {
            if (ids == null) return 0;
            var idList = ids.Distinct().ToList();
            if (idList.Count == 0) return 0;

            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null || !user.CanModifyQuestionBank)
            {
                return 0;
            }

            bool isSuperAdmin = user.Role == UserRole.SuperAdmin;

            // 超级管理员可删除所选任何题目；教师仅能批量删除自己创建的题目（防水平越权 IDOR）
            var query = db.Questions.Where(q => idList.Contains(q.Id));
            if (!isSuperAdmin)
            {
                query = query.Where(q => q.CreatedByUserId == userId);
            }

            var questionsToDelete = await query.ToListAsync();
            if (questionsToDelete.Count == 0) return 0;

            var matchedIds = questionsToDelete.Select(q => q.Id).ToList();

            var relatedLogs = await db.LlmGenerationLogs.Where(l => l.QuestionId.HasValue && matchedIds.Contains(l.QuestionId.Value)).ToListAsync();
            foreach (var log in relatedLogs)
            {
                log.QuestionId = null;
            }

            var relatedFavorites = await db.UserFavorites.Where(f => matchedIds.Contains(f.QuestionId)).ToListAsync();
            if (relatedFavorites.Count > 0)
            {
                db.UserFavorites.RemoveRange(relatedFavorites);
            }

            var relatedErrors = await db.ErrorItems.Where(e => matchedIds.Contains(e.QuestionId)).ToListAsync();
            if (relatedErrors.Count > 0)
            {
                db.ErrorItems.RemoveRange(relatedErrors);
            }

            var relatedPracticeRecords = await db.PracticeRecords.Where(r => matchedIds.Contains(r.QuestionId)).ToListAsync();
            if (relatedPracticeRecords.Count > 0)
            {
                db.PracticeRecords.RemoveRange(relatedPracticeRecords);
            }

            db.Questions.RemoveRange(questionsToDelete);
            await db.SaveChangesAsync();
            PracticeService.InvalidateCategoryCache();

            return questionsToDelete.Count;
        }

        public async Task<int> BatchDirectPublishAsync(IEnumerable<Guid> ids, Guid userId)
        {
            if (ids == null) return 0;
            var idList = ids.Distinct().ToList();
            if (idList.Count == 0) return 0;

            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null || !user.CanManageQuestionBank)
            {
                return 0;
            }

            bool isSuperAdmin = user.Role == UserRole.SuperAdmin;
            var query = db.Questions.Where(q => idList.Contains(q.Id) && !q.IsPublic);
            if (!isSuperAdmin)
            {
                query = query.Where(q => q.CreatedByUserId == userId);
            }

            var questionsToPublish = await query.ToListAsync();
            if (questionsToPublish.Count == 0) return 0;

            var authorIds = questionsToPublish
                .Where(q => q.CreatedByUserId.HasValue)
                .Select(q => q.CreatedByUserId!.Value)
                .Distinct()
                .ToList();

            var authors = await db.Users.Where(u => authorIds.Contains(u.Id)).ToListAsync();

            foreach (var q in questionsToPublish)
            {
                q.IsPublic = true;
                q.PublishStatus = PublishStatusEnum.Approved;

                if (q.CreatedByUserId.HasValue)
                {
                    var author = authors.FirstOrDefault(u => u.Id == q.CreatedByUserId.Value);
                    if (author != null)
                    {
                        author.Exp += 50;
                        author.Coins += 20;
                        GamificationService.CheckAndProcessLevelUp(author);
                    }
                }
            }

            await db.SaveChangesAsync();
            return questionsToPublish.Count;
        }

        public async Task<(int TotalQuestions, int PublicQuestions, int MyQuestions)> GetQuestionStatisticsAsync(Guid? userId = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var db = dbScope.Context;

            var total = await db.Questions.CountAsync();
            var pub = await db.Questions.CountAsync(q => q.IsPublic);
            var my = 0;
            if (userId.HasValue)
            {
                my = await db.Questions.CountAsync(q => q.CreatedByUserId == userId.Value);
            }
            return (total, pub, my);
        }
    }
}
