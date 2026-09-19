using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;

namespace Northtropic.Services
{
    public enum HyperbolaOrientation
    {
        Horizontal, // (x-x0)^2 / a^2 - (y-y0)^2 / b^2 = 1 (焦点在 x 轴 / 水平轴)
        Vertical    // (y-y0)^2 / a^2 - (x-x0)^2 / b^2 = 1 (焦点在 y 轴 / 垂直轴)
    }

    public enum ParabolaOrientation
    {
        Horizontal, // (y-y0)^2 = 2p(x-x0) (对称轴平行于 x 轴)
        Vertical    // (x-x0)^2 = 2p(y-y0) (对称轴平行于 y 轴)
    }

    public class PracticeService : IPracticeService
    {
        private readonly AppDbContext _context;
        private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;
        private readonly IGamificationService _gamificationService;
        private readonly IUserSessionService _userSessionService;
        private readonly IAiTutorService _aiTutorService;

        public PracticeService(
            AppDbContext context,
            IGamificationService gamificationService,
            IUserSessionService userSessionService,
            IAiTutorService aiTutorService,
            IDbContextFactory<AppDbContext>? dbContextFactory = null)
        {
            _context = context;
            _dbContextFactory = dbContextFactory;
            _gamificationService = gamificationService;
            _userSessionService = userSessionService;
            _aiTutorService = aiTutorService;
        }

        private ValueTask<AsyncDbScope> CreateDbScopeAsync()
        {
            return AsyncDbScope.CreateAsync(_dbContextFactory, _context);
        }

        public async Task<List<Question>> GetQuestionsAsync(string? category = null, int count = 10)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var currentUserId = _userSessionService.CurrentUserId;
            var query = ctx.Questions.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(category) && category != "全部")
            {
                query = query.Where(q => q.Category == category || q.Subject == category);
            }
            if (currentUserId.HasValue)
            {
                query = query.Where(q => q.IsPublic || q.CreatedByUserId == currentUserId.Value);
            }
            else
            {
                query = query.Where(q => q.IsPublic);
            }
            return await query.OrderBy(r => EF.Functions.Random()).Take(count).ToListAsync();
        }

        public async Task<List<Question>> GetRandomQuestionsAsync(Guid userId, string grade, string subject, string? category = null, int count = 5)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var query = ctx.Questions.AsNoTracking().AsQueryable();

            // 过滤匹配学科
            if (!string.IsNullOrWhiteSpace(subject) && subject != "通用学科")
            {
                query = query.Where(q => q.Subject == subject);
            }
            // 过滤匹配知识点
            if (!string.IsNullOrWhiteSpace(category) && category != "全部" && category != "全部知识点")
            {
                query = query.Where(q => q.Category == category);
            }

            // 规则：拉取全网公共库，或者属于该用户的私有库题目
            query = query.Where(q => q.IsPublic || q.CreatedByUserId == userId);

            var list = await query.OrderBy(r => EF.Functions.Random()).Take(count * 3).ToListAsync();
            var distinctList = list.DistinctBy(q => q.Stem.Trim()).Take(count).ToList();
            var shuffledList = distinctList.Select(q => Northtropic.Helpers.QuestionShuffleHelper.ShuffleQuestionOptions(q)).ToList();
            return shuffledList;
        }

        public async Task<List<Question>> GetAdaptiveQuestionsAsync(Guid userId, string grade, string subject, string? category = null, int count = 5)
        {
            if (count <= 0) count = 5;

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            // 1. 分析该学生在该学科下的近期作答表现 (取近 20 次练习记录)
            var recentRecords = await ctx.PracticeRecords
                .AsNoTracking()
                .Include(r => r.Question)
                .Where(r => r.UserId == userId && (string.IsNullOrWhiteSpace(subject) || subject == "通用学科" || (r.Question != null && r.Question.Subject == subject)))
                .OrderByDescending(r => r.AnsweredAt)
                .Take(20)
                .ToListAsync();

            int baselineDifficulty = 3; // 默认为中等难度
            if (recentRecords.Count >= 3)
            {
                double accuracy = (double)recentRecords.Count(r => r.IsCorrect) / recentRecords.Count;
                if (accuracy >= 0.8)
                {
                    baselineDifficulty = 4; // 学霸型：主推难度4，向难度5进阶
                }
                else if (accuracy <= 0.45)
                {
                    baselineDifficulty = 2; // 薄弱型：主推难度2，从难度1巩固
                }
                else
                {
                    baselineDifficulty = 3; // 稳步提升型：主推难度3
                }
            }

            // 2. 检查是否有相关知识点的历史未攻克错题优先需要巩固
            var unmasteredErrorQuestionIds = await ctx.ErrorItems
                .AsNoTracking()
                .Where(e => e.UserId == userId && !e.IsMastered)
                .Select(e => e.QuestionId)
                .ToListAsync();

            // 3. 构造候选试题池
            var baseQuery = ctx.Questions.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(subject) && subject != "通用学科")
            {
                baseQuery = baseQuery.Where(q => q.Subject == subject);
            }
            if (!string.IsNullOrWhiteSpace(category) && category != "全部" && category != "全部知识点")
            {
                baseQuery = baseQuery.Where(q => q.Category == category);
            }
            baseQuery = baseQuery.Where(q => q.IsPublic || q.CreatedByUserId == userId);

            var candidatePool = await baseQuery.ToListAsync();
            if (candidatePool.Count == 0)
            {
                return await GetRandomQuestionsAsync(userId, grade, subject, category, count);
            }

            // 4. 按最近发展区 (ZPD) 梯度智能分发配比：
            // - 巩固题 (Difficulty = baseline - 1, 最低1): 占 20%
            // - 核心对标题 (Difficulty = baseline): 占 60%
            // - 进阶挑战题 (Difficulty = baseline + 1, 最高5): 占 20%
            int warmupDifficulty = Math.Max(1, baselineDifficulty - 1);
            int coreDifficulty = baselineDifficulty;
            int challengeDifficulty = Math.Min(5, baselineDifficulty + 1);

            var selectedQuestions = new List<Question>();
            var rnd = new Random();

            // 优先检查是否有对应考点的错题待攻克
            if (unmasteredErrorQuestionIds.Count > 0)
            {
                var errorMatch = candidatePool.FirstOrDefault(q => unmasteredErrorQuestionIds.Contains(q.Id));
                if (errorMatch != null)
                {
                    selectedQuestions.Add(errorMatch);
                }
            }

            // 补充巩固题
            var warmupPool = candidatePool.Where(q => q.Difficulty <= warmupDifficulty && !selectedQuestions.Any(s => s.Id == q.Id)).OrderBy(_ => rnd.Next()).ToList();
            if (warmupPool.Count > 0 && selectedQuestions.Count < count)
            {
                selectedQuestions.Add(warmupPool[0]);
            }

            // 补充核心对标题
            int neededCore = Math.Max(1, count - selectedQuestions.Count - 1);
            var corePool = candidatePool.Where(q => q.Difficulty == coreDifficulty && !selectedQuestions.Any(s => s.Id == q.Id)).OrderBy(_ => rnd.Next()).Take(neededCore).ToList();
            selectedQuestions.AddRange(corePool);

            // 补充进阶挑战题
            var challengePool = candidatePool.Where(q => q.Difficulty >= challengeDifficulty && !selectedQuestions.Any(s => s.Id == q.Id)).OrderBy(_ => rnd.Next()).ToList();
            if (challengePool.Count > 0 && selectedQuestions.Count < count)
            {
                selectedQuestions.Add(challengePool[0]);
            }

            // 若依然不足设定题数，从剩余候选池随机补齐
            if (selectedQuestions.Count < count)
            {
                var remaining = candidatePool.Where(q => !selectedQuestions.Any(s => s.Id == q.Id)).OrderBy(_ => rnd.Next()).Take(count - selectedQuestions.Count).ToList();
                selectedQuestions.AddRange(remaining);
            }

            // 保证题目不重复，且选项顺序进行安全打乱
            var finalQuestions = selectedQuestions
                .DistinctBy(q => q.Stem.Trim())
                .Take(count)
                .Select(q => Northtropic.Helpers.QuestionShuffleHelper.ShuffleQuestionOptions(q))
                .ToList();

            if (finalQuestions.Count < count)
            {
                var existingIds = finalQuestions.Select(f => f.Id).ToList();

                // 架构师优化：优先从同学科（Subject == subject）补充试题，避免出现跨学科出题违和感
                if (!string.IsNullOrWhiteSpace(subject) && subject != "全部分科" && subject != "通用学科")
                {
                    var sameSubjectFallback = await ctx.Questions
                        .AsNoTracking()
                        .Where(q => q.IsPublic && q.Subject == subject && !existingIds.Contains(q.Id))
                        .OrderBy(q => q.Difficulty)
                        .Take(count - finalQuestions.Count)
                        .ToListAsync();

                    foreach (var fq in sameSubjectFallback)
                    {
                        finalQuestions.Add(Northtropic.Helpers.QuestionShuffleHelper.ShuffleQuestionOptions(fq));
                        existingIds.Add(fq.Id);
                    }
                }

                // 若同学科全库依然不足，再降级从全网公共库补充
                if (finalQuestions.Count < count)
                {
                    var fallbackQuestions = await ctx.Questions
                        .AsNoTracking()
                        .Where(q => q.IsPublic && !existingIds.Contains(q.Id))
                        .OrderBy(q => q.Difficulty)
                        .Take(count - finalQuestions.Count)
                        .ToListAsync();

                    foreach (var fq in fallbackQuestions)
                    {
                        finalQuestions.Add(Northtropic.Helpers.QuestionShuffleHelper.ShuffleQuestionOptions(fq));
                    }
                }

                if (finalQuestions.Count == 0)
                {
                    finalQuestions = (await GetDemoQuestionsAsync(count)).Select(q => Northtropic.Helpers.QuestionShuffleHelper.ShuffleQuestionOptions(q)).ToList();
                }
            }

            return finalQuestions;
        }

        public async Task<List<Question>> GetDemoQuestionsAsync(int count = 5)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var demoQuestions = await ctx.Questions
                .AsNoTracking()
                .Where(q => q.IsPublic)
                .OrderBy(q => q.Difficulty)
                .Take(count)
                .ToListAsync();

            if (demoQuestions.Count == 0)
            {
                demoQuestions = new List<Question>
                {
                    new Question
                    {
                        Id = Guid.NewGuid(),
                        Subject = "数学",
                        Category = "勾股定理与几何计算",
                        GradeTarget = "初中二年级",
                        Type = QuestionType.SingleChoice,
                        Difficulty = 2,
                        Stem = "在直角三角形 ABC 中，∠C = 90°，两直角边长分别为 a = 3，b = 4。求斜边 c 的长是多少？",
                        OptionsJson = "[\"A. 5\", \"B. 6\", \"C. 7\", \"D. 8\"]",
                        CorrectAnswer = "A",
                        StandardAnalysis = "根据勾股定理公式 $c = \\sqrt{a^2 + b^2} = \\sqrt{3^2 + 4^2} = \\sqrt{9 + 16} = 5$。",
                        BaseExpReward = 15,
                        IsPublic = true
                    },
                    new Question
                    {
                        Id = Guid.NewGuid(),
                        Subject = "物理",
                        Category = "欧姆定律与电路分析",
                        GradeTarget = "初中三年级",
                        Type = QuestionType.SingleChoice,
                        Difficulty = 3,
                        Stem = "已知某定值电阻的阻值为 10 Ω，当在其两端加上 5 V 的恒定电压时，通过该电阻的电流大小为多少？",
                        OptionsJson = "[\"A. 0.2 A\", \"B. 0.5 A\", \"C. 2 A\", \"D. 50 A\"]",
                        CorrectAnswer = "B",
                        StandardAnalysis = "根据欧姆定律基本公式 $I = \\frac{U}{R} = \\frac{5\\text{V}}{10\\,\\Omega} = 0.5\\text{ A}$。",
                        BaseExpReward = 20,
                        IsPublic = true
                    },
                    new Question
                    {
                        Id = Guid.NewGuid(),
                        Subject = "化学",
                        Category = "质量守恒定律与化学方程式",
                        GradeTarget = "初中三年级",
                        Type = QuestionType.SingleChoice,
                        Difficulty = 2,
                        Stem = "根据质量守恒定律，化学反应前后肯定没有发生改变的是：",
                        OptionsJson = "[\"A. 分子的种类\", \"B. 分子的总数目\", \"C. 原子的种类和数目\", \"D. 物质的状态\"]",
                        CorrectAnswer = "C",
                        StandardAnalysis = "化学反应的实质是分子破裂成原子，原子重新组合成新的分子。因此在反应前后原子的种类、数目、质量均守恒不变。",
                        BaseExpReward = 15,
                        IsPublic = true
                    }
                };
            }

            return demoQuestions.Select(q => Northtropic.Helpers.QuestionShuffleHelper.ShuffleQuestionOptions(q)).ToList();
        }

        private static List<string>? _cachedCategories;
        private static DateTime _categoryCacheTime = DateTime.MinValue;
        private static readonly SemaphoreSlim _categoryCacheLock = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan _categoryCacheTtl = TimeSpan.FromMinutes(5);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime CachedAt, List<string> Categories)> _subjectCategoriesCache = new();

        // 架构遥测：分类考点缓存吞吐与命中率统计
        private static long _categoryCacheHitCount = 0;
        private static long _categoryCacheMissCount = 0;

        public static long CategoryCacheHitCount => Interlocked.Read(ref _categoryCacheHitCount);
        public static long CategoryCacheMissCount => Interlocked.Read(ref _categoryCacheMissCount);
        public static double CategoryCacheHitRatio
        {
            get
            {
                long hits = CategoryCacheHitCount;
                long misses = CategoryCacheMissCount;
                long total = hits + misses;
                return total > 0 ? Math.Round((double)hits / total * 100.0, 1) : 100.0;
            }
        }

        public static void ResetCategoryCacheTelemetry()
        {
            Interlocked.Exchange(ref _categoryCacheHitCount, 0);
            Interlocked.Exchange(ref _categoryCacheMissCount, 0);
        }

        public static void InvalidateCategoryCache()
        {
            _categoryCacheLock.Wait();
            try
            {
                _cachedCategories = null;
                _categoryCacheTime = DateTime.MinValue;
                _subjectCategoriesCache.Clear();
            }
            finally
            {
                _categoryCacheLock.Release();
            }
        }

        public async Task<List<string>> GetCategoriesAsync(bool forceRefresh = false)
        {
            var fastSnapshot = _cachedCategories;
            if (!forceRefresh && fastSnapshot != null && (DateTime.UtcNow - _categoryCacheTime) < _categoryCacheTtl)
            {
                Interlocked.Increment(ref _categoryCacheHitCount);
                return new List<string>(fastSnapshot);
            }

            await _categoryCacheLock.WaitAsync();
            try
            {
                var lockSnapshot = _cachedCategories;
                if (!forceRefresh && lockSnapshot != null && (DateTime.UtcNow - _categoryCacheTime) < _categoryCacheTtl)
                {
                    Interlocked.Increment(ref _categoryCacheHitCount);
                    return new List<string>(lockSnapshot);
                }

                Interlocked.Increment(ref _categoryCacheMissCount);
                await using var dbScope = await CreateDbScopeAsync();
                var ctx = dbScope.Context;
                var categories = await ctx.Questions.AsNoTracking()
                    .Where(q => !string.IsNullOrEmpty(q.Category))
                    .Select(q => q.Category)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToListAsync();

                categories.Insert(0, "全部");
                _cachedCategories = categories;
                _categoryCacheTime = DateTime.UtcNow;
                return new List<string>(categories);
            }
            finally
            {
                _categoryCacheLock.Release();
            }
        }

        public async Task<List<string>> GetCategoriesBySubjectAsync(string? subject)
        {
            if (string.IsNullOrWhiteSpace(subject) || subject == "全部" || subject == "全部学科")
            {
                return await GetCategoriesAsync();
            }

            var trimmedSubject = subject.Trim();
            if (_subjectCategoriesCache.TryGetValue(trimmedSubject, out var cachedEntry) && (DateTime.UtcNow - cachedEntry.CachedAt) < _categoryCacheTtl)
            {
                Interlocked.Increment(ref _categoryCacheHitCount);
                return new List<string>(cachedEntry.Categories);
            }

            Interlocked.Increment(ref _categoryCacheMissCount);
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var cats = await ctx.Questions.AsNoTracking()
                .Where(q => q.Subject == trimmedSubject && !string.IsNullOrEmpty(q.Category))
                .Select(q => q.Category)
                .Distinct()
                .OrderBy(c => c)
                .ToListAsync();

            if (cats.Count == 0)
            {
                // 知识图谱自适应兜底：从 GradeSubjectProvider 获取学科内置考点模块
                var defaultCats = GradeSubjectProvider.GetCategoriesBySubject(trimmedSubject);
                if (defaultCats != null && defaultCats.Count > 0)
                {
                    cats = defaultCats.Where(c => c != "全部").Distinct().OrderBy(c => c).ToList();
                }
            }

            cats.Insert(0, "全部");
            _subjectCategoriesCache[trimmedSubject] = (DateTime.UtcNow, cats);
            return new List<string>(cats);
        }

        public async Task<AnswerCheckResult> SubmitAnswerAsync(Question activeQuestion, string userAnswer, int timeTakenSeconds, int currentCombo, Guid? targetUserId = null, CancellationToken cancellationToken = default)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            // 架构保障：引入事务保障单题提交的全链路 ACID 原子性
            // 题库持久化、经验金币结算、答题流水与错题艾宾浩斯状态推进属于不可分割的原子操作
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
            if (ctx.Database.IsRelational() && ctx.Database.CurrentTransaction == null)
            {
                transaction = await ctx.Database.BeginTransactionAsync(cancellationToken);
            }

            try
            {
                var result = await SubmitAnswerInternalAsync(ctx, activeQuestion, userAnswer, timeTakenSeconds, currentCombo, targetUserId, cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return result;
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                throw;
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }
        }

        private async Task<AnswerCheckResult> SubmitAnswerInternalAsync(AppDbContext ctx, Question activeQuestion, string userAnswer, int timeTakenSeconds, int currentCombo, Guid? targetUserId = null, CancellationToken cancellationToken = default)
        {
            var questionId = activeQuestion.Id;
            var trackedLocal = ctx.Questions.Local.FirstOrDefault(q => q.Id == questionId);
            var question = trackedLocal ?? await ctx.Questions.FirstOrDefaultAsync(q => q.Id == questionId, cancellationToken);
            
            // 如果是一道 AI 动态生成的非数据库存量题目，先持久化保存入库（防止 ChangeTracker 冲突）
            if (question == null)
            {
                var existingEntry = ctx.ChangeTracker.Entries<Question>().FirstOrDefault(e => e.Entity.Id == questionId);
                if (existingEntry != null)
                {
                    question = existingEntry.Entity;
                }
                else
                {
                    question = activeQuestion;
                    ctx.Questions.Add(question);
                    await ctx.SaveChangesAsync(cancellationToken);
                }
            }

            SubjectiveGradingResult? subjectiveGrading = null;
            // 修复关键缺陷：判卷必须基于当前活跃展示给用户的题目 activeQuestion (包含打乱后的选项与重映射的答案)
            bool isCorrect = CheckAnswerCorrectness(activeQuestion, userAnswer);

            // 若为主观大题 (简答题、问答综合大题) 或复杂填空题，调用 AI 进行深度打分批改 (但若是判断题且用户给出了确定相反的判断，绝不误判放行)
            bool isBinaryJudgement = TryNormalizeJudgement(activeQuestion.CorrectAnswer ?? "", out _) || TryNormalizeJudgement(userAnswer, out _);
            if (activeQuestion.Type == QuestionType.ShortAnswer || activeQuestion.Type == QuestionType.EssayAnalysis || (activeQuestion.Type == QuestionType.FillInBlank && !isCorrect && !isBinaryJudgement))
            {
                subjectiveGrading = await _aiTutorService.GradeSubjectiveAnswerAsync(activeQuestion, userAnswer);
                isCorrect = subjectiveGrading.IsPassed;
            }

            var currentUserId = targetUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id ?? Guid.Empty;

            // 1. 结算动态金币与经验奖励/惩罚
            bool isHistoryWrong = currentUserId != Guid.Empty && (
                ctx.ErrorItems.Local.Any(e => e.UserId == currentUserId && e.QuestionId == questionId && !e.IsMastered) ||
                await ctx.ErrorItems.AnyAsync(e => e.UserId == currentUserId && e.QuestionId == questionId && !e.IsMastered, cancellationToken));
            var rewardResult = await _gamificationService.ProcessAnswerRewardAsync(isCorrect, activeQuestion.BaseExpReward, currentCombo, activeQuestion.Difficulty, timeTakenSeconds, isHistoryWrong, currentUserId, ctx);

            string? planFeedback = null;

            // 2. 若为有效登录用户，持久化保存答题记录与错题本；若为未鉴权访客（currentUserId == Guid.Empty），仅返回判分与模拟奖励，杜绝写入孤儿记录与数据库污染
            if (currentUserId != Guid.Empty)
            {
                var record = new PracticeRecord
                {
                    UserId = currentUserId,
                    QuestionId = questionId,
                    UserAnswer = userAnswer,
                    IsCorrect = isCorrect,
                    TimeTakenSeconds = timeTakenSeconds,
                    ComboAtAnswer = currentCombo,
                    EarnedExp = rewardResult.EarnedExp,
                    EarnedCoins = rewardResult.EarnedCoins
                };
                ctx.PracticeRecords.Add(record);

                // 3. 错题净化副本 (ErrorItem) 双向联动与艾宾浩斯强化
                if (!isCorrect)
                {
                    // 只有全新的 AI 实时生成题目 (非公共库且未绑定归属) 答错时，才纳入该学员的个人专属私有题库，避免从现有题库抽到的题目重复归集
                    if (question.CreatedByUserId == null && !question.IsPublic)
                    {
                        question.CreatedByUserId = currentUserId;
                    }

                    var existingError = ctx.ErrorItems.Local.FirstOrDefault(e => e.UserId == currentUserId && e.QuestionId == questionId)
                        ?? await ctx.ErrorItems.FirstOrDefaultAsync(e => e.UserId == currentUserId && e.QuestionId == questionId, cancellationToken);
                    if (existingError == null)
                    {
                        ctx.ErrorItems.Add(new ErrorItem
                        {
                            UserId = currentUserId,
                            QuestionId = questionId,
                            UserWrongAnswer = userAnswer,
                            ErrorReasonCategory = "概念模糊", // 默认假定分类，改错时可重置
                            IsMastered = false,
                            CreatedAt = DateTime.Now
                        });
                    }
                    else
                    {
                        existingError.UserWrongAnswer = userAnswer;
                        existingError.IsMastered = false; // 再次答错则取消消灭状态
                        existingError.LastRevisedAt = DateTime.Now;
                        if (existingError.RevisionCount > 0)
                        {
                            existingError.RevisionCount--;
                        }
                    }
                }
                else if (isHistoryWrong)
                {
                    // 核心联动：若答对历史未掌握错题，自动同步艾宾浩斯复练记忆曲线与掌握度
                    var existingError = ctx.ErrorItems.Local.FirstOrDefault(e => e.UserId == currentUserId && e.QuestionId == questionId && !e.IsMastered)
                        ?? await ctx.ErrorItems.FirstOrDefaultAsync(e => e.UserId == currentUserId && e.QuestionId == questionId && !e.IsMastered, cancellationToken);
                    if (existingError != null)
                    {
                        existingError.LastRevisedAt = DateTime.Now;
                        existingError.RevisionCount++;
                        if (existingError.RevisionCount >= 3)
                        {
                            existingError.IsMastered = true;
                            planFeedback = "🎉 绝杀突破！该题已连续 3 次复练答对，成功达成【已净化】！错题本消灭总数 +1！";
                            var user = ctx.Users.Local.FirstOrDefault(u => u.Id == currentUserId)
                                ?? await ctx.Users.FirstOrDefaultAsync(u => u.Id == currentUserId, cancellationToken);
                            if (user != null)
                            {
                                user.ResolvedErrorsCount++;
                                if (user.ResolvedErrorsCount >= 5)
                                {
                                    await _gamificationService.UnlockAchievementAsync("ERROR_KILLER_5", currentUserId, ctx);
                                }
                            }
                        }
                    }
                }

                // 4. 智能学习计划任务同步推进与自适应闭环联动
                try
                {
                    var activePlan = ctx.StudyPlans.Local.FirstOrDefault(p => p.UserId == currentUserId && p.Status == StudyPlanStatus.Active)
                        ?? await ctx.StudyPlans
                            .Include(p => p.Tasks)
                            .Where(p => p.UserId == currentUserId && p.Status == StudyPlanStatus.Active)
                            .OrderByDescending(p => p.CreatedAt)
                            .FirstOrDefaultAsync(cancellationToken);

                    if (activePlan != null)
                    {
                        var targetTask = activePlan.Tasks
                            .Where(t => !t.IsCompleted && t.Subject == activeQuestion.Subject && t.Category == activeQuestion.Category)
                            .FirstOrDefault()
                            ?? activePlan.Tasks
                                .Where(t => !t.IsCompleted && t.Subject == activeQuestion.Subject && (t.Category == "全部分类" || t.TaskType == StudyPlanTaskType.ComprehensiveSprint))
                                .FirstOrDefault()
                            ?? activePlan.Tasks.Where(t => !t.IsCompleted).FirstOrDefault();

                        if (targetTask != null)
                        {
                            targetTask.CompletedCount++;
                            if (targetTask.CompletedCount >= targetTask.TargetCount)
                            {
                                targetTask.IsCompleted = true;
                                targetTask.CompletedAt = DateTime.Now;
                            }
                            activePlan.UpdatedAt = DateTime.Now;

                            if (activePlan.Tasks.All(t => t.IsCompleted))
                            {
                                activePlan.Status = StudyPlanStatus.Completed;
                                activePlan.CompletedDate = DateTime.Now;
                                string taskMsg = "🏆 太棒了！你已圆满达成当前提分学习计划全部任务指标！";
                                planFeedback = string.IsNullOrEmpty(planFeedback) ? taskMsg : $"{planFeedback} | {taskMsg}";
                            }
                            else
                            {
                                string taskMsg = targetTask.IsCompleted
                                    ? $"🎉 恭喜达成学习计划任务【{targetTask.Title}】({targetTask.CompletedCount}/{targetTask.TargetCount})！"
                                    : $"🎯 学习计划推进：【{targetTask.Title}】已完成 {targetTask.CompletedCount}/{targetTask.TargetCount} 题！";
                                planFeedback = string.IsNullOrEmpty(planFeedback) ? taskMsg : $"{planFeedback} | {taskMsg}";
                            }
                        }
                    }
                }
                catch { }

                await ctx.SaveChangesAsync(cancellationToken);
            }

            bool isEquivalentMatch = isCorrect && !string.Equals((userAnswer ?? "").Trim(), (activeQuestion.CorrectAnswer ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            string? matchReason = isEquivalentMatch ? GenerateEquivalentMatchReason(activeQuestion, userAnswer ?? string.Empty) : null;

            string cognitiveClass = isCorrect 
                ? (timeTakenSeconds <= 12 ? "AgileMastery" : "SteadyMastery") 
                : (timeTakenSeconds <= 12 ? "Careless" : "Struggling");
            string cognitiveBadge = cognitiveClass switch
            {
                "AgileMastery" => "⚡ 敏捷秒杀",
                "SteadyMastery" => "🧘 稳健深思",
                "Careless" => "⚠️ 急躁粗心",
                "Struggling" => "🧩 攻坚盲区",
                _ => "⚡ 答题完成"
            };

            return new AnswerCheckResult
            {
                IsCorrect = isCorrect,
                StandardAnswer = activeQuestion.CorrectAnswer ?? string.Empty,
                Analysis = !string.IsNullOrWhiteSpace(activeQuestion.StandardAnalysis) ? activeQuestion.StandardAnalysis : (question.StandardAnalysis ?? string.Empty),
                Reward = rewardResult,
                SubjectiveGrading = subjectiveGrading,
                IsEquivalentMatch = isEquivalentMatch,
                EquivalentMatchReason = matchReason,
                CognitiveClassification = cognitiveClass,
                CognitiveBadgeText = cognitiveBadge,
                StudyPlanProgressFeedback = planFeedback
            };
        }

        public static string GenerateEquivalentMatchReason(Question question, string rawUserAnswer)
        {
            var user = (rawUserAnswer ?? "").Trim();
            var correct = (question.CorrectAnswer ?? "").Trim();

            if (question.Type == QuestionType.SingleChoice)
            {
                return $"选项映射等价：已将您的作答 [{user}] 自动规范识别为标准选项 [{correct}]";
            }
            if (question.Type == QuestionType.MultipleChoice)
            {
                return $"多选格式等价：已自动识别选项组合与无序表达，对应标准选项 [{correct}]";
            }
            if (question.Type == QuestionType.FillInBlank)
            {
                if (TryNormalizeJudgement(user, out _) && TryNormalizeJudgement(correct, out _))
                {
                    return "判断题判定等价：已自动识别对错真假判定逻辑与标准答案一致";
                }

                var normU = user.Replace(" ", "").ToLowerInvariant();
                var normC = correct.Replace(" ", "").ToLowerInvariant();

                // 化学离子符号书写与上下标、电荷次序等价 (如 Fe^{3+} vs Fe3+ vs Fe^+3, SO_4^{2-} vs SO42- vs SO4^2-)
                if (CheckChemicalIonEquivalence(user, correct) || CheckChemicalIonEquivalence(normU, normC))
                {
                    return $"化学离子式表达等价：已自动识别离子电荷数、正负符号次序（如 3+ 与 +3）及上下标书写方式，对应标准离子式 [{correct}]";
                }

                // 微积分不定积分常数 C 加法交换与大小写等价 (如 x^2 + C vs C + x^2 vs x^2 + c)
                if (CheckIndefiniteIntegralConstantCMatch(user, correct) || CheckIndefiniteIntegralConstantCMatch(normU, normC))
                {
                    return $"微积分不定积分常数等价：已自动识别原函数与积分任意常数 C 的加法交换律与大小写表达，对应标准答案 [{correct}]";
                }

                // 代数因式分解因子乘积交换律等价
                if (CheckPolynomialFactorProductCommutativeMatch(user, correct) ||
                    CheckPolynomialFactorProductCommutativeMatch(normU, normC))
                {
                    return $"因式分解因子交换律等价：已自动识别多项式因式乘积因子的交换律无序性，对应标准因式分解 [{correct}]";
                }

                // 向量范数与模长记号等价
                if (user.Contains("\\|") || user.Contains("\\Vert") || user.Contains("\\lVert") || user.Contains("‖") ||
                    correct.Contains("\\|") || correct.Contains("\\Vert") || correct.Contains("\\lVert") || correct.Contains("‖"))
                {
                    return $"向量范数与模长记号等价：已自动识别向量/复数模长符号（\\|...\\| 与 |...|）数学等价性，对应标准答案 [{correct}]";
                }

                // 化学热化学方程式与焓变 ΔH 等价识别 (优先于普通 /mol 复合单位判定)
                if (CheckThermochemicalEquationMatch(user, correct) || CheckThermochemicalEquationMatch(normU, normC))
                {
                    return $"化学热化学方程式等价：已自动识别化学反应物与生成物化学计量、聚集状态及焓变 ΔH（单位与数值）的科学等价性，对应标准方程式 [{correct}]";
                }

                // 化学可逆反应与反应式等价
                if (user.Contains("⇌") || correct.Contains("⇌") || user.Contains("\\rightleftharpoons") || correct.Contains("\\rightleftharpoons") ||
                    user.Contains("<=>") || correct.Contains("<=>") || user.Contains("<->") || correct.Contains("<->"))
                {
                    return $"化学反应方程式/可逆平衡等价：已自动识别化学反应方程式与可逆符号（⇌/<=>）等价性，对应标准反应式 [{correct}]";
                }

                // 理化复合单位等价 (摩尔质量/摩尔体积等)
                if (user.Contains("/mol") || correct.Contains("/mol") || user.Contains("mol^-1") || correct.Contains("mol^-1") || user.Contains("摩尔") || correct.Contains("摩尔"))
                {
                    return $"理化复合单位智能对齐等价：已自动识别摩尔质量/摩尔体积等理化单位，对应标准答案 [{correct}]";
                }

                // 对数底数、真数与记号等价 (\ln(x) vs \log_e(x), \lg(x) vs \log_{10}(x), \log_2(x) 等)
                if ((normU.Contains("log") || normU.Contains("ln") || normU.Contains("lg")) &&
                    (normC.Contains("log") || normC.Contains("ln") || normC.Contains("lg")))
                {
                    return $"对数记号与底数等价：已自动识别以 e 为底的自然对数 (\\ln)、以 10 为底的常用对数 (\\lg) 及对数 \\log_b(x) 的符号等价性，对应标准答案 [{correct}]";
                }

                // 速度、加速度、密度、压强、功率等理科复合单位智能对齐等价
                if (normU.Contains("m/s") || normC.Contains("m/s") || normU.Contains("m·s") || normC.Contains("m·s") || normU.Contains("m*s") || normC.Contains("m*s") ||
                    normU.Contains("米/秒") || normC.Contains("米/秒") || normU.Contains("米每秒") || normC.Contains("米每秒") ||
                    normU.Contains("kg/m") || normC.Contains("kg/m") || normU.Contains("千克/立方米") || normC.Contains("千克/立方米") || normU.Contains("千克每立方米") || normC.Contains("千克每立方米") ||
                    normU.Contains("pa") || normC.Contains("pa") || normU.Contains("帕") || normC.Contains("帕") ||
                    normU.Contains("j/s") || normC.Contains("j/s") || normU.Contains("焦/秒") || normC.Contains("焦/秒") || normU.Contains("焦每秒") || normC.Contains("焦每秒"))
                {
                    return $"物理/科学单位智能对齐等价（理科复合单位）：已自动识别物理量数值并对齐复合单位（如速度、加速度、密度、压强或功率等），对应标准答案 [{correct}]";
                }

                // 三角函数幂次记号等价 (如 \sin^2 x vs (\sin x)^2)
                if ((normU.Contains("sin") || normU.Contains("cos") || normU.Contains("tan") || normU.Contains("cot") || normU.Contains("sec") || normU.Contains("csc") ||
                     normC.Contains("sin") || normC.Contains("cos") || normC.Contains("tan") || normC.Contains("cot") || normC.Contains("sec") || normC.Contains("csc")) &&
                    (user.Contains("^") || correct.Contains("^") || normU.Contains("^") || normC.Contains("^")))
                {
                    return $"三角函数幂次记号等价：已自动识别 sin^2(x) 与 (sin x)^2 的代数等价性，对应标准答案 [{correct}]";
                }

                // 三角函数与倒数记法等价 (如 tg x vs tan x, ctg x vs cot x, 1/tan x vs cot x)
                if ((normU.Contains("tg") || normU.Contains("tan") || normU.Contains("cot") || normU.Contains("sec") || normU.Contains("csc")) &&
                    (normC.Contains("tg") || normC.Contains("tan") || normC.Contains("cot") || normC.Contains("sec") || normC.Contains("csc")))
                {
                    return $"三角函数与倒数函数等价：已自动识别正切/余切/正割/余割（如 tg 与 tan、ctg 与 cot、1/tan 与 cot、1/cos 与 sec 等）的等价恒等变换，对应标准答案 [{correct}]";
                }

                // 指数幂与根式等价表达 (如 x^-1 vs 1/x, x^(1/2) vs sqrt(x), e^x vs exp(x))
                if ((normU.Contains("exp") || normU.Contains("e^") || normU.Contains("^-") || user.Contains("^(1/") || correct.Contains("^(1/") || user.Contains("cbrt") || correct.Contains("cbrt")) &&
                    (normC.Contains("exp") || normC.Contains("e^") || normC.Contains("^-") || normC.Contains("sqrt") || normC.Contains("/") || user.Contains("^-") || correct.Contains("^-")))
                {
                    return $"指数幂与根式等价表达：已自动识别负幂、分数指数幂或自然底数指数函数（如 x^-1 与 1/x、x^(1/2) 与 \\sqrt{{x}}、e^x 与 exp(x)）数学等价性，对应标准答案 [{correct}]";
                }

                // 中文区间范围等价 (如 2到5, 2至5, x大于等于2且小于等于5)
                if (normU.Contains("到") || normC.Contains("到") || normU.Contains("至") || normC.Contains("至") ||
                    normU.Contains("大于") || normC.Contains("大于") || normU.Contains("小于") || normC.Contains("小于"))
                {
                    return $"数学解集与区间范围等价：已自动识别中文区间/不等式范围（如闭区间、半开半闭区间）表达，对应标准答案 [{correct}]";
                }

                bool isDisjU = user.Contains("或") || user.Contains("或者");
                bool isDisjC = correct.Contains("或") || correct.Contains("或者");
                if (isDisjU || isDisjC)
                {
                    return $"不等式或关系与区间并集等价：已识别多段不等式（逻辑或）与区间并集 \\cup 的数学等价性，对应标准解集 [{correct}]";
                }

                if ((user.StartsWith("{") && user.Contains("|")) || (correct.StartsWith("{") && correct.Contains("|")))
                {
                    return $"集合描述法与解集等价：已识别集合描述法 {{x | ...}} 与对应数轴区间/不等式的数学等价性，对应标准答案 [{correct}]";
                }

                bool isUnionU = user.Contains("\\cup") || user.Contains("∪") || user.Contains("并") || System.Text.RegularExpressions.Regex.IsMatch(user, @"[\]\)]\s*[uU]\s*[\[\(]");
                bool isUnionC = correct.Contains("\\cup") || correct.Contains("∪") || correct.Contains("并") || System.Text.RegularExpressions.Regex.IsMatch(correct, @"[\]\)]\s*[uU]\s*[\[\(]");
                if (isUnionU || isUnionC)
                {
                    return $"区间/集合并集等价：已自动识别多区间并集的集合无序性与等价范围，标准书写建议使用 LaTeX \\cup 符号 [{correct}]";
                }

                // 直线方程代数形式与移项等价 (一般式 Ax+By+C=0、斜截式 y=kx+b 等)
                if (TryNormalizeLinearEquation(normU, out var luA, out var luB, out var luC) &&
                    TryNormalizeLinearEquation(normC, out var lcA, out var lcB, out var lcC) &&
                    Math.Abs(luA - lcA) < 1e-5 && Math.Abs(luB - lcB) < 1e-5 && Math.Abs(luC - lcC) < 1e-5)
                {
                    return $"解析几何直线方程等价：已自动识别直线方程（一般式 Ax+By+C=0、斜截式 y=kx+b 与移项变形）的代数等价性，对应标准方程 [{correct}]";
                }

                // 解析几何圆的方程等价 (标准方程 (x-a)^2+(y-b)^2=r^2、一般方程 x^2+y^2+Dx+Ey+F=0、平方项次序对调与移项等价)
                if (TryNormalizeCircleEquation(normU, out var curCirX, out var curCirY, out var curCirR2) &&
                    TryNormalizeCircleEquation(normC, out var corCirX, out var corCirY, out var corCirR2) &&
                    Math.Abs(curCirX - corCirX) < 1e-4 && Math.Abs(curCirY - corCirY) < 1e-4 && Math.Abs(curCirR2 - corCirR2) < 1e-4)
                {
                    return $"解析几何圆的方程等价：已自动识别圆的标准方程与一般方程代数展开/移项等价（圆心 ({curCirX}, {curCirY})，半径²={curCirR2}），对应标准方程 [{correct}]";
                }

                // 解析几何圆锥曲线（椭圆）标准方程等价
                if (TryNormalizeEllipseEquation(normU, out var curElX, out var curElY, out var curElA2, out var curElB2) &&
                    TryNormalizeEllipseEquation(normC, out var corElX, out var corElY, out var corElA2, out var corElB2) &&
                    Math.Abs(curElX - corElX) < 1e-4 && Math.Abs(curElY - corElY) < 1e-4 &&
                    Math.Abs(curElA2 - corElA2) < 1e-4 && Math.Abs(curElB2 - corElB2) < 1e-4)
                {
                    return $"解析几何圆锥曲线等价：已自动识别椭圆标准方程各项加法交换律与代数表达等价性（中心 ({curElX}, {curElY})，a²={curElA2}, b²={curElB2}），对应标准方程 [{correct}]";
                }

                // 解析几何圆锥曲线（双曲线）标准方程与移项展开等价
                if (TryNormalizeHyperbolaEquation(normU, out var curHypOri, out var curHypX, out var curHypY, out var curHypA2, out var curHypB2) &&
                    TryNormalizeHyperbolaEquation(normC, out var corHypOri, out var corHypX, out var corHypY, out var corHypA2, out var corHypB2) &&
                    curHypOri == corHypOri &&
                    Math.Abs(curHypX - corHypX) < 1e-4 && Math.Abs(curHypY - corHypY) < 1e-4 &&
                    Math.Abs(curHypA2 - corHypA2) < 1e-4 && Math.Abs(curHypB2 - corHypB2) < 1e-4)
                {
                    string axisDesc = curHypOri == HyperbolaOrientation.Horizontal ? "焦点在 x 轴" : "焦点在 y 轴";
                    return $"解析几何双曲线方程等价：已自动识别双曲线标准方程与一般展开式的代数等价性（{axisDesc}，中心 ({curHypX}, {curHypY})，a²={curHypA2}, b²={curHypB2}），对应标准方程 [{correct}]";
                }

                // 解析几何圆锥曲线（抛物线）标准方程与函数展开等价
                if (TryNormalizeParabolaEquation(normU, out var curParOri, out var curParX, out var curParY, out var curPar2P) &&
                    TryNormalizeParabolaEquation(normC, out var corParOri, out var corParX, out var corParY, out var corPar2P) &&
                    curParOri == corParOri &&
                    Math.Abs(curParX - corParX) < 1e-4 && Math.Abs(curParY - corParY) < 1e-4 &&
                    Math.Abs(curPar2P - corPar2P) < 1e-4)
                {
                    string axisDesc = curParOri == ParabolaOrientation.Horizontal ? "对称轴平行于 x 轴" : "对称轴平行于 y 轴";
                    return $"解析几何抛物线方程等价：已自动识别抛物线标准方程与函数/一般展开式的代数等价性（{axisDesc}，顶点 ({curParX}, {curParY})，焦准距 2p={curPar2P}），对应标准方程 [{correct}]";
                }

                // 空间/平面向量列矩阵与坐标表达等价 (\begin{pmatrix} 2 \\ -3 \end{pmatrix} vs (2,-3))
                if ((user.Contains("pmatrix") || user.Contains("bmatrix") || correct.Contains("pmatrix") || correct.Contains("bmatrix")) &&
                    (user.Contains("(") || correct.Contains("(") || user.Contains(",") || correct.Contains(",")))
                {
                    return $"空间/平面向量矩阵与坐标表达等价：已自动识别列向量/矩阵形式与坐标形式 (x, y) 的数学等价性，对应标准答案 [{correct}]";
                }

                // 数理区间/点坐标中文分号分隔符等价 ([-1; 2] vs [-1, 2], [-2; 3) vs [-2, 3))
                if ((user.Contains(";") || user.Contains("；") || correct.Contains(";") || correct.Contains("；")) &&
                    (((user.Contains("[") || user.Contains("(")) && (user.Contains("]") || user.Contains(")"))) ||
                     ((correct.Contains("[") || correct.Contains("(")) && (correct.Contains("]") || correct.Contains(")")))))
                {
                    return $"数理区间/点坐标分隔符等价：已自动识别中文教材中分号 ';' 与标准逗号 ',' 分隔的数学等价性，对应标准表达 [{correct}]";
                }

                // 空间/平面向量基底与坐标表达等价 (2\vec{i}+3\vec{j} vs (2,3))
                if ((user.Contains("i") || user.Contains("j") || correct.Contains("i") || correct.Contains("j")) &&
                    (user.Contains("(") || correct.Contains("(")) &&
                    (CheckCoordinateEquationMatch(normU, normC) || CheckCoordinateEquationMatch(normC, normU)))
                {
                    return $"空间/平面向量基底与坐标表达等价：已自动识别向量正交基分解（如 a=xi+yj）与坐标表达 (x,y) 的代数等价性，对应标准答案 [{correct}]";
                }

                // 平面向量点乘/数量积交换律与模长等价
                if ((user.Contains("\\vec") || correct.Contains("\\vec") || user.Contains("·") || correct.Contains("·") || user.Contains("*") || correct.Contains("*") || user.Contains("|") || correct.Contains("|")) &&
                    (user.Contains("a") || user.Contains("b") || correct.Contains("a") || correct.Contains("b") || user.Contains("v") || correct.Contains("v")))
                {
                    if (user.Contains("|") || correct.Contains("|"))
                    {
                        return $"平面向量模长等价：已自动识别向量模长记号 (|a|) 与标量数值表达，对应标准答案 [{correct}]";
                    }
                    if (user.Contains("=") || correct.Contains("="))
                    {
                        return $"平面向量数量积/点乘等价：已自动识别向量数量积交换律 (a·b = b·a) 与向量记号表达，对应标准答案 [{correct}]";
                    }
                }

                // 热化学方程式焓变与物理电功能量单位换算等价
                if (user.Contains("delta") || correct.Contains("delta") || user.Contains("Δ") || correct.Contains("Δ") ||
                    user.Contains("kj/mol") || correct.Contains("kj/mol") || user.Contains("千焦") || correct.Contains("千焦") ||
                    user.Contains("kwh") || correct.Contains("kwh") || user.Contains("千瓦时") || correct.Contains("千瓦时") || user.Contains("度"))
                {
                    return $"热化学与物理能量单位等价：已自动识别焓变 ΔH、千焦每摩尔 (kJ/mol) 与电功能量单位 (1 kW·h = 3.6×10^6 J = 1 度) 规范对应，对应标准答案 [{correct}]";
                }

                // 三角特殊角角度制与弧度制等价 (\pi/6 = 30°, \pi/4 = 45°, \pi/3 = 60° 等)
                if (CheckAngleAndRadianEquivalence(user, correct) || CheckAngleAndRadianEquivalence(normU, normC))
                {
                    return $"三角角度与弧度制等价：已自动识别角度制 (如 30°、45°、90°) 与弧度制 (如 \\pi/6、\\pi/4、\\pi/2) 的精确数理等价对应，对应标准答案 [{correct}]";
                }

                // 国际单位制科学词头换算等价 (如 A与mA、kWh与度与J、h与min与s、kHz与Hz、kJ与J、kΩ与Ω、L与mL、mol/L与mmol/L等) 与物理工程量纲
                if (CheckScientificUnitMultiplierEquivalence(user, correct) || CheckScientificUnitMultiplierEquivalence(normU, normC))
                {
                    return $"国际单位制科学词头换算等价（物理与工程量纲、化学量纲智能换算）：已自动对齐体积容积（L/mL/m³/dm³）、物质的量浓度（mol/L与mmol/L）、磁通量（Wb/Mx）、电感（H/mH）、电导（S/mS）、电流（A/mA/μA）、电能度数（kWh/度/J）、时间（h/min/s）、频率（Hz/kHz/MHz）、电容（F/uF/nF/pF）、速度（km/h与m/s）、功率（W/kW/MW）、压强（Pa/kPa/MPa）、电压、阻抗或力学等理化工程量纲智能换算，对应标准答案 [{correct}]";
                }


                // 复数代数形式等价 (z = a + bi, bi + a, 0 + bi 等，需包含虚数单位 i)
                if ((normU.Contains("i") || normC.Contains("i")) &&
                    TryParseComplex(normU, out var cruR, out var cruI) &&
                    TryParseComplex(normC, out var crcR, out var crcI) &&
                    AreNumbersClose(cruR, crcR) && AreNumbersClose(cruI, crcI) && normU != normC)
                {
                    return $"复数代数形式等价：已自动识别复数 z=a+bi 的实部、虚部与加法交换律表达，对应标准答案 [{correct}]";
                }

                // 空间直角坐标/向量等价 ((x,y,z) vs 方程组 vs 向量)
                if ((user.Contains("\\vec") || correct.Contains("\\vec") || normU.Count(c => c == ',') == 2 || normC.Count(c => c == ',') == 2) &&
                    (CheckCoordinateEquationMatch(normU, normC) || CheckCoordinateEquationMatch(normC, normU)))
                {
                    return $"空间直角坐标/向量等价：已自动识别空间直角坐标 (x,y,z) 与方程组/向量记号的几何等价性，对应标准答案 [{correct}]";
                }

                // 1. 方程组与多元解集等价 (包含逗号或换行分隔的多元方程、cases环境、或二维坐标点)
                bool isCoordU = System.Text.RegularExpressions.Regex.IsMatch(user.Trim(), @"^\([+-]?\d+(?:\.\d+)?\s*,\s*[+-]?\d+(?:\.\d+)?\)$") || user.Contains("(x,") || user.Contains("(x,y)");
                bool isCoordC = System.Text.RegularExpressions.Regex.IsMatch(correct.Trim(), @"^\([+-]?\d+(?:\.\d+)?\s*,\s*[+-]?\d+(?:\.\d+)?\)$") || correct.Contains("(x,") || correct.Contains("(x,y)");
                bool hasMultiVarsU = (normU.Contains("x") && normU.Contains("y")) || (normU.Contains("y") && normU.Contains("z")) || user.Contains("cases") || isCoordU;
                bool hasMultiVarsC = (normC.Contains("x") && normC.Contains("y")) || (normC.Contains("y") && normC.Contains("z")) || correct.Contains("cases") || isCoordC;
                bool isEqSystemU = (user.Contains(",") && user.Contains("=") && hasMultiVarsU) || user.Contains("cases") || isCoordU;
                bool isEqSystemC = (correct.Contains(",") && correct.Contains("=")) || correct.Contains("cases") || isCoordC;
                if ((isEqSystemU || isEqSystemC) && (hasMultiVarsU || hasMultiVarsC))
                {
                    return $"方程组与多元解集等价：已识别未知数解集的无序/坐标表达，对应标准解 [{correct}]";
                }

                // 方程多根解集表达等价 (如 x_1=1, x_2=-2 与 {1, -2}、x=1或x=-2 等)
                bool hasRootVarU = normU.Contains("x_1") || normU.Contains("x_2") || normU.Contains("x=");
                bool hasRootVarC = normC.Contains("x_1") || normC.Contains("x_2") || normC.Contains("x=");
                if ((hasRootVarU || hasRootVarC) && !normU.Contains("|") && !normC.Contains("|") && !user.Contains("u") && !user.Contains("∪"))
                {
                    return $"方程根解集等价：已识别方程各根的集合/或关系表达与根下标无序对应，对应标准解 [{correct}]";
                }

                // 2. 电极反应半反应式电子转移移项等价 (优先于普通方程式匹配，提供精准教学反馈)
                if ((normU.Contains("e-") || normC.Contains("e-") || normU.Contains("e^-") || normC.Contains("e^-") || normU.Contains("电子")) &&
                    (normU.Contains("=") || normU.Contains("->") || normC.Contains("=") || normC.Contains("->")))
                {
                    return $"电极反应式与电子转移等价：已识别电极反应半反应式中的电子转移项移项等价性，对应标准反应式 [{correct}]";
                }

                // 3. 化学方程式反应项等价 (需包含反应物生成物加号或特征反应箭头)
                if (((normU.Contains("=") && normU.Contains("+")) || normU.Contains("->") || normU.Contains("<=>")) &&
                    ((normC.Contains("=") && normC.Contains("+")) || normC.Contains("->") || normC.Contains("<=>")))
                {
                    if (user.Contains("overset") || correct.Contains("overset") || user.Contains("stackrel") || correct.Contains("stackrel") || user.Contains("点燃") || user.Contains("加热") || user.Contains("高温") || user.Contains("催化剂") || user.Contains("通电") || user.Contains("电解") || user.Contains("高压"))
                    {
                        return $"化学反应方程式条件等价：已自动识别反应条件标注（如点燃、加热、催化剂、通电、电解等）与标准方程式等价对应 [{correct}]";
                    }
                    return $"化学方程式反应项等价：已识别反应物与生成物项的无序书写，对应标准方程式 [{correct}]";
                }

                // 4. 有机化学结构简式与分子式等价
                if (IsOrganicStructureEquivalent(user, correct) || IsOrganicStructureEquivalent(normU, normC))
                {
                    return $"有机化学结构简式与分子式等价：已识别结构简式、示性式与分子式（如 CH2=CH2 与 C2H4，CH3CH2OH 与 C2H5OH）的化学等价性，对应标准答案 [{correct}]";
                }

                // 5. 比例与比值形式等价 (如 3:4 vs 3比4 vs 3/4)
                if ((user.Contains(':') || correct.Contains(':') || user.Contains("比") || correct.Contains("比") || user.Contains('：') || correct.Contains('：')) &&
                    (CheckRatioFractionEquivalence(user, correct) || CheckRatioFractionEquivalence(normU, normC)))
                {
                    return $"比值与比率智能等价：已识别比例书写形式（如 A:B、A比B 与最简分数）的数学等价性，对应标准答案 [{correct}]";
                }

                bool hasIneqU = user.Contains("<") || user.Contains(">") || user.Contains("≤") || user.Contains("≥");
                bool hasIneqC = correct.Contains("<") || correct.Contains(">") || correct.Contains("≤") || correct.Contains("≥");
                if ((hasIneqU || hasIneqC) && (user.Contains("[") || user.Contains("(") || correct.Contains("[") || correct.Contains("(")))
                {
                    return $"不等式与实数区间解集等价：已识别不等式与区间的相互转化表达，对应标准表达 [{correct}]";
                }

                if (StripCommonUnits(normU) == StripCommonUnits(normC) && normU != normC)
                {
                    return $"物理/科学单位智能对齐等价：已自动识别数值并对齐复合单位，对应标准答案 [{correct}]";
                }

                // 三角函数幂次记号等价 (如 \sin^2 x vs (\sin x)^2)
                if ((normU.Contains("sin") || normU.Contains("cos") || normU.Contains("tan") || normU.Contains("cot") || normC.Contains("sin") || normC.Contains("cos") || normC.Contains("tan")) &&
                    (normU.Contains("^") || normC.Contains("^") || user.Contains("^") || correct.Contains("^")))
                {
                    return $"三角函数幂次记号等价：已自动识别 sin^2(x) 与 (sin x)^2 的代数等价性，对应标准答案 [{correct}]";
                }

                // 命题逻辑充分必要条件等价
                if (normC == "充要条件" || normC == "充分不必要条件" || normC == "必要不充分条件" || normC == "既不充分也不必要条件" ||
                    normU == "充要条件" || normU == "充分不必要条件" || normU == "必要不充分条件" || normU == "既不充分也不必要条件")
                {
                    return $"命题充分必要条件等价：已自动识别逻辑命题充分必要条件的规范等价性，对应标准答案 [{correct}]";
                }

                // 函数单调性与奇偶性表述等价
                if (normC == "单调递增" || normC == "单调递减" || normC == "偶函数" || normC == "奇函数" ||
                    normU == "单调递增" || normU == "单调递减" || normU == "偶函数" || normU == "奇函数")
                {
                    return $"函数单调性/奇偶性表述等价：已自动识别函数增减性或奇偶性质的数学等价性，对应标准答案 [{correct}]";
                }

                // 方程正负号 ± 复合解等价
                if ((normU.Contains("+-") || normC.Contains("+-") || user.Contains("±") || correct.Contains("±")) &&
                    (normU.Contains("x_") || normC.Contains("x_") || normU.Contains("x=") || normC.Contains("x=") || normU.Contains("{") || normC.Contains("{") || normU.Contains("或") || normC.Contains("或") || normU.Contains(",") || normC.Contains(",")))
                {
                    return $"方程正负解/共轭根等价：已自动识别 ± 正负号复合表达式与两实根列表的数学等价性，对应标准解 [{correct}]";
                }

                // 化学式、可逆符号与离子电荷等价
                if ((user.Contains("3+") || user.Contains("2+") || user.Contains("3-") || user.Contains("2-") || user.Contains("³⁺") || user.Contains("²⁺") || user.Contains("⁻") || user.Contains("⁺") || user.Contains("⇌") || correct.Contains("⇌")) &&
                    (correct.Contains("3+") || correct.Contains("2+") || correct.Contains("3-") || correct.Contains("2-") || correct.Contains("³⁺") || correct.Contains("²⁺") || correct.Contains("⁻") || correct.Contains("⁺") || correct.Contains("<=>") || user.Contains("<=>")))
                {
                    return $"化学式与离子电荷等价：已自动识别化学式角标、可逆符号与离子化合价符号等价性，对应标准答案 [{correct}]";
                }

                if (normU.StartsWith("{") && normU.EndsWith("}") && normC.StartsWith("{") && normC.EndsWith("}"))
                {
                    return $"有限集合元素等价：已识别集合元素的无序等价性，标准表达建议按元素升序书写 [{correct}]";
                }

                if (user.Contains("10^") || correct.Contains("10^") || user.Contains("e") || correct.Contains("e") || user.Contains("E") || correct.Contains("E") || user.Contains("×") || user.Contains("\\times"))
                {
                    return $"科学记数法等价：您输入的 [{user}] 与标准答案数值一致，考试建议书写标准 a×10^n 形式 [{correct}]";
                }

                if (IsChemicalNomenclatureEquivalent(user, correct))
                {
                    return $"化学品名称与化学式智能等价：已自动识别化学品俗名/中文名与标准化学式对应关系 [{user}] 对应 [{correct}]";
                }

                if ((normU == "r" || normU == "全体实数" || normU == "实数集") && (normC == "r" || normC == "全体实数" || normC == "实数集" || normC.Contains("inf")))
                {
                    return $"全实数集合域等价：已识别全体实数、实数集与实数域 \\mathbb{{R}} / (-∞, +∞) 等价，对应标准答案 [{correct}]";
                }

                if ((normU == "∅" || normU == "空集" || normU == "无解" || normU == "不存在") && (normC == "∅" || normC == "空集" || normC == "无解" || normC == "不存在" || normC.Contains("emptyset")))
                {
                    return $"解集与空集等价：已识别无解/无实数解与空集 \\emptyset 的集合论等价性，对应标准答案 [{correct}]";
                }

                if ((user.Contains("sqrt") || correct.Contains("sqrt") || user.Contains("\\sqrt") || correct.Contains("\\sqrt")) &&
                    (user.Contains("/") || correct.Contains("/") || user.Contains("\\frac") || correct.Contains("\\frac")))
                {
                    return $"分母有理化与根式数值等价：您输入的 [{user}] 与标准答案 [{correct}] 数值完全等价，已通过精准数学求值判定";
                }

                if (user.Contains('/') || correct.Contains('/') || user.Contains("\\frac") || correct.Contains("\\frac") || user.Contains('.') || correct.Contains('.'))
                {
                    return $"数值运算等价：您输入的 [{user}] 与标准答案 [{correct}] 数值完全一致，数学考试建议优先书写最简分数";
                }

                if (user.Contains("±") || correct.Contains("±") || user.Contains("+-") || correct.Contains("+-") || user.Contains("或") || correct.Contains("或"))
                {
                    return $"方程解集展开等价：已识别多解枚举/正负号展开逻辑与标准答案一致";
                }

                return $"智能表达式规范化等价：已自动对齐符号、排版与规范格式，对应标准答案 [{correct}]";
            }

            return $"智能等价认可：已自动识别并采纳您的作答表达";
        }

        public async Task<WholePaperSubmissionResult> SubmitBatchPaperAsync(
            List<(Question Question, string UserAnswer, int TimeTakenSeconds)> submissions,
            int initialCombo,
            Guid? targetUserId = null,
            CancellationToken cancellationToken = default)
        {
            var currentUserId = targetUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id ?? Guid.Empty;
            var result = new WholePaperSubmissionResult
            {
                TotalQuestions = submissions?.Count ?? 0
            };

            if (submissions == null || submissions.Count == 0)
            {
                return result;
            }

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            // 架构保障：使用显式事务保障整卷提交的 ACID 原子性
            // 无论是多道题逐题保存还是最后提交，一旦中途出现未捕获异常或取消，全量事务回滚，杜绝脏数据
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
            if (ctx.Database.IsRelational() && ctx.Database.CurrentTransaction == null)
            {
                transaction = await ctx.Database.BeginTransactionAsync(cancellationToken);
            }

            try
            {
                int runningCombo = initialCombo;
                int maxComboAchieved = initialCombo;
                int totalExp = 0;
                int totalCoins = 0;
                int correctCount = 0;

                for (int i = 0; i < submissions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = submissions[i];
                    int itemIndex = i + 1;
                    var q = item.Question;
                    if (q == null) continue;
                    var userAns = item.UserAnswer ?? string.Empty;
                    var timeTaken = Math.Max(0, item.TimeTakenSeconds);

                    var singleResult = await SubmitAnswerInternalAsync(ctx, q, userAns, timeTaken, runningCombo, currentUserId, cancellationToken);
                    result.ItemResults[itemIndex] = singleResult;

                    if (singleResult.IsCorrect)
                    {
                        correctCount++;
                        totalExp += singleResult.Reward.EarnedExp;
                        totalCoins += singleResult.Reward.EarnedCoins;
                        runningCombo = singleResult.Reward.CurrentCombo;
                        maxComboAchieved = Math.Max(maxComboAchieved, runningCombo);
                    }
                    else
                    {
                        totalCoins += singleResult.Reward.EarnedCoins;
                        runningCombo = singleResult.Reward.CurrentCombo;
                    }
                }

                result.CorrectCount = correctCount;
                result.TotalEarnedExp = totalExp;
                result.TotalEarnedCoins = totalCoins;
                result.MaxComboAchieved = maxComboAchieved;

                // 认知科学架构分析：四象限作答速度与正确率归因分类 (敏捷精通/稳健深思/急躁粗心/攻坚盲区)
                int agileCount = 0;
                int steadyCount = 0;
                int carelessCount = 0;
                int strugglingCount = 0;
                int totalDuration = submissions.Sum(s => Math.Max(0, s.TimeTakenSeconds));
                double avgDuration = submissions.Count > 0 ? (double)totalDuration / submissions.Count : 0.0;
                double speedThreshold = Math.Max(10.0, avgDuration * 0.75);

                for (int i = 0; i < submissions.Count; i++)
                {
                    int itemIndex = i + 1;
                    if (result.ItemResults.TryGetValue(itemIndex, out var res))
                    {
                        var timeTaken = Math.Max(0, submissions[i].TimeTakenSeconds);
                        bool isFast = timeTaken <= speedThreshold;
                        if (res.IsCorrect)
                        {
                            if (isFast)
                            {
                                agileCount++;
                                res.CognitiveClassification = "AgileMastery";
                                res.CognitiveBadgeText = "⚡ 敏捷秒杀";
                            }
                            else
                            {
                                steadyCount++;
                                res.CognitiveClassification = "SteadyMastery";
                                res.CognitiveBadgeText = "🧘 稳健深思";
                            }
                        }
                        else
                        {
                            if (isFast)
                            {
                                carelessCount++;
                                res.CognitiveClassification = "Careless";
                                res.CognitiveBadgeText = "⚠️ 急躁粗心";
                            }
                            else
                            {
                                strugglingCount++;
                                res.CognitiveClassification = "Struggling";
                                res.CognitiveBadgeText = "🧩 攻坚盲区";
                            }
                        }
                    }
                }

                result.AgileMasteryCount = agileCount;
                result.SteadyMasteryCount = steadyCount;
                result.CarelessCount = carelessCount;
                result.StrugglingCount = strugglingCount;
                result.AverageTimePerQuestionSeconds = Math.Round(avgDuration, 1);

                if (carelessCount > 0 && carelessCount >= strugglingCount)
                {
                    result.CognitivePaceAdvice = "💡 检测到部分题目答题过快导致粗心失分，建议审题时放慢节奏，仔细圈画题干关键限制词！";
                }
                else if (strugglingCount > 0 && strugglingCount > carelessCount)
                {
                    result.CognitivePaceAdvice = "💡 检测到部分题目思考耗时较长仍未攻克，属于典型知识盲区，建议查阅试题详解并一键加入错题本！";
                }
                else if (agileCount > 0 && carelessCount == 0 && strugglingCount == 0)
                {
                    result.CognitivePaceAdvice = "🌟 敏捷度与准确度双双拉满！知识点掌握炉火纯青，保持极佳答题状态！";
                }
                else if (steadyCount > 0 && carelessCount == 0)
                {
                    result.CognitivePaceAdvice = "🛡️ 沉稳缜密，思路清晰严谨，每一步推导都扎实有效，保持节奏！";
                }
                else
                {
                    result.CognitivePaceAdvice = "🎯 保持专注作答节奏，循序渐进突破薄弱板块，继续加油！";
                }

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return result;
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                throw;
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }
        }

        public static bool CheckAnswerCorrectness(Question question, string userAnswer)
        {
            if (string.IsNullOrWhiteSpace(userAnswer)) return false;

            var cleanUser = userAnswer.Trim();
            var cleanCorrect = (question.CorrectAnswer ?? "").Trim();

            static char NormalizeChoiceKey(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return ' ';
                string trimmed = raw.Trim();
                string extracted = Northtropic.Helpers.QuestionShuffleHelper.ExtractChoicePrefix(trimmed);
                if (!string.IsNullOrEmpty(extracted) && extracted.Length == 1 && char.IsLetter(extracted[0]))
                {
                    return char.ToUpperInvariant(extracted[0]);
                }
                char c = char.ToUpperInvariant(trimmed[0]);
                if (trimmed.Length == 1 && c >= '1' && c <= '9')
                {
                    return (char)('A' + (c - '1'));
                }
                return c;
            }

            if (question.Type == QuestionType.SingleChoice)
            {
                // 1. 选项前缀键比对 (支持 1->A, 2->B 映射以及 (A), ①, [A] 规范化映射)
                if (cleanUser.Length > 0 && cleanCorrect.Length > 0)
                {
                    char uKey = NormalizeChoiceKey(cleanUser);
                    char cKey = NormalizeChoiceKey(cleanCorrect);
                    if (uKey >= 'A' && uKey <= 'Z' && cKey >= 'A' && cKey <= 'Z' && uKey == cKey)
                    {
                        return true;
                    }
                }

                // 2. 完整字符串忽略大小写比对
                if (string.Equals(cleanUser, cleanCorrect, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 3. 题干选项文本回退比对 (例如用户输入了具体选项内容，或 CorrectAnswer 为具体选项文本)
                if (!string.IsNullOrWhiteSpace(question.OptionsJson))
                {
                    try
                    {
                        var options = System.Text.Json.JsonSerializer.Deserialize<List<string>>(question.OptionsJson);
                        if (options != null && options.Count > 0)
                        {
                            int correctIdx = -1;
                            if (cleanCorrect.Length > 0)
                            {
                                char cKey = NormalizeChoiceKey(cleanCorrect);
                                if (cKey >= 'A' && cKey <= 'Z')
                                {
                                    correctIdx = cKey - 'A';
                                }
                            }
                            if (correctIdx == -1)
                            {
                                correctIdx = options.FindIndex(o => string.Equals(o.Trim(), cleanCorrect, StringComparison.OrdinalIgnoreCase) || o.Trim().StartsWith(cleanCorrect, StringComparison.OrdinalIgnoreCase));
                            }

                            if (correctIdx >= 0 && correctIdx < options.Count)
                            {
                                var correctOptText = options[correctIdx].Trim();
                                if (string.Equals(cleanUser, correctOptText, StringComparison.OrdinalIgnoreCase)) return true;
                                var strippedOpt = System.Text.RegularExpressions.Regex.Replace(correctOptText, @"^[A-Za-z0-9][\.\、\s]+", "").Trim();
                                var strippedUser = System.Text.RegularExpressions.Regex.Replace(cleanUser, @"^[A-Za-z0-9][\.\、\s]+", "").Trim();
                                if (!string.IsNullOrEmpty(strippedOpt) && string.Equals(strippedUser, strippedOpt, StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                    catch { }
                }

                return false;
            }
            if (question.Type == QuestionType.MultipleChoice)
            {
                // 多选题支持组合逗号/空格分隔比较，以及直接键入连写字母 (如 "ABD" 或 "124")
                static IEnumerable<char> ExtractChoiceLetters(string raw)
                {
                    var tokens = raw.ToUpperInvariant()
                        .Split(new[] { ',', ' ', '、', '，', ';', '；', '|' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrEmpty(t));

                    foreach (var token in tokens)
                    {
                        if (token.Length > 1 && token.All(c => (c >= 'A' && c <= 'Z') || (c >= '1' && c <= '9')))
                        {
                            foreach (var c in token)
                            {
                                char norm = (c >= '1' && c <= '9') ? (char)('A' + (c - '1')) : c;
                                yield return norm;
                            }
                        }
                        else
                        {
                            yield return NormalizeChoiceKey(token);
                        }
                    }
                }

                var userKeys = ExtractChoiceLetters(cleanUser).Where(c => c >= 'A' && c <= 'Z').OrderBy(k => k).Distinct();
                var correctKeys = ExtractChoiceLetters(cleanCorrect).Where(c => c >= 'A' && c <= 'Z').OrderBy(k => k).Distinct();

                return string.Join("", userKeys) == string.Join("", correctKeys);
            }
            if (question.Type == QuestionType.FillInBlank)
            {
                return CheckFillInBlankMatch(cleanUser, cleanCorrect);
            }

            // 主观题只要用户提交非空内容即视为完成（后续由 AI 进行深度智能评分）
            return cleanUser.Length >= 5;
        }

        public static bool TryNormalizeJudgement(string s, out bool val)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                val = false;
                return false;
            }
            s = s.Trim();
            // 去除常见的选项编号前缀，如 "A. 正确", "A、对", "(A) 正确", "[B] 错误", "B: 错" 等
            if (s.Length >= 3 && ((s[0] >= 'a' && s[0] <= 'e') || (s[0] >= 'A' && s[0] <= 'E')))
            {
                char sep = s[1];
                if (sep == '.' || sep == '、' || sep == ':' || sep == '：' || sep == ' ' || sep == ')' || sep == '）' || sep == ']' || sep == '】')
                {
                    s = s.Substring(2).Trim();
                }
            }
            else if (s.Length >= 4 && (s[0] == '(' || s[0] == '（' || s[0] == '[' || s[0] == '【') && ((s[1] >= 'a' && s[1] <= 'e') || (s[1] >= 'A' && s[1] <= 'E')))
            {
                char sep = s[2];
                if (sep == ')' || sep == '）' || sep == ']' || sep == '】' || sep == '.' || sep == ' ')
                {
                    s = s.Substring(3).Trim();
                }
            }

            s = s.ToLowerInvariant().Trim('.', '。', '!', '！', ' ', '(', ')', '（', '）', '[', ']', '【', '】');
            if (s == "对" || s == "正确" || s == "√" || s == "v" || s == "true" || s == "t" || s == "yes" || s == "y" || s == "是" || s == "right" || s == "r" || s == "1")
            {
                val = true;
                return true;
            }
            if (s == "错" || s == "错误" || s == "×" || s == "x" || s == "false" || s == "f" || s == "no" || s == "n" || s == "否" || s == "wrong" || s == "w" || s == "0")
            {
                val = false;
                return true;
            }
            val = false;
            return false;
        }

        public static bool AreNumbersClose(double a, double b)
        {
            if (double.IsNaN(a) || double.IsNaN(b)) return false;
            if (double.IsInfinity(a) || double.IsInfinity(b)) return a == b;
            if (a == b) return true;
            double diff = Math.Abs(a - b);
            if (a == 0 || b == 0)
            {
                return diff < 1e-6;
            }
            double maxVal = Math.Max(Math.Abs(a), Math.Abs(b));
            return (diff / maxVal) < 1e-4;
        }

        public static bool TryParseFractionOrDouble(string s, out double val)
        {
            val = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Trim('(', ')').Replace("−", "-").Replace("－", "-").Replace("＋", "+").Replace("％", "%").Replace("\\%", "%");
            if (string.IsNullOrEmpty(s)) return false;

            // 1. 百分比智能识别 (例如 25% -> 0.25, 0.5% -> 0.005)
            if (s.EndsWith("%"))
            {
                var pctPart = s.Substring(0, s.Length - 1).Trim();
                if (TryParseFractionOrDouble(pctPart, out var pctVal))
                {
                    val = pctVal / 100.0;
                    return true;
                }
            }

            // 2. LaTeX 分式解析 (支持 \frac{a}{b}, \dfrac{a}{b}, \tfrac{a}{b} 以及带符号如 -\frac{1}{2})
            var fracMatch = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?)\s*\\(?:frac|dfrac|tfrac)\s*\{([^}]+)\}\s*\{([^}]+)\}$");
            if (fracMatch.Success)
            {
                double sign = fracMatch.Groups[1].Value == "-" ? -1.0 : 1.0;
                var numStr = fracMatch.Groups[2].Value.Trim();
                var denStr = fracMatch.Groups[3].Value.Trim();
                if (TryParseFractionOrDouble(numStr, out double num) &&
                    TryParseFractionOrDouble(denStr, out double den) &&
                    Math.Abs(den) > 1e-9)
                {
                    val = sign * (num / den);
                    return true;
                }
            }

            // 3. 常规浮点数解析
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val))
            {
                return true;
            }

            // 4. 标准斜杠分式解析 (如 a/b, 3/4, -1/2)
            var slashIdx = s.IndexOf('/');
            if (slashIdx > 0 && slashIdx < s.Length - 1)
            {
                var numStr = s.Substring(0, slashIdx).Trim().Trim('(', ')');
                var denStr = s.Substring(slashIdx + 1).Trim().Trim('(', ')');
                if (double.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double num) &&
                    double.TryParse(denStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double den) &&
                    Math.Abs(den) > 1e-9)
                {
                    val = num / den;
                    return true;
                }
            }

            return false;
        }

        private static (char v1, char v2) DetectVariables(string eq)
        {
            var letters = new List<char>();
            foreach (char ch in eq)
            {
                if (char.IsLetter(ch))
                {
                    char lower = char.ToLowerInvariant(ch);
                    if (lower != 'e' && lower != 'i' && !letters.Contains(lower))
                    {
                        letters.Add(lower);
                    }
                }
            }

            // 若包含平面直角坐标常用变量 x 或 y，则基底变量严格固定为 (x, y)，保证 x 与 y 不被混淆
            if (letters.Contains('x') || letters.Contains('y') || letters.Count == 0)
            {
                return ('x', 'y');
            }

            char v1 = letters[0];
            char v2 = letters.Count > 1 ? letters[1] : (v1 == 'a' ? 'b' : 'a');
            return (v1, v2);
        }

        private static bool TryParseLinearExpression(string expr, char var1, char var2, out double a, out double b, out double c)
        {
            a = 0; b = 0; c = 0;
            if (string.IsNullOrWhiteSpace(expr)) return false;

            expr = expr.Trim().Replace(" ", "").Replace("（", "(").Replace("）", ")");
            expr = System.Text.RegularExpressions.Regex.Replace(expr, @"\\frac\{([^}]+)\}\{([^}]+)\}", "(($1)/($2))");

            // 规范化截距式负分母/负分子 (例如 x/(-4) 或 +y/(-5) 或 -y/(-5))
            expr = System.Text.RegularExpressions.Regex.Replace(expr, @"(?<=^|[+])\s*\(?([a-zA-Z0-9]+)\)?/\(\s*-\s*(\d+(?:\.\d+)?)\s*\)", "-$1/$2");
            expr = System.Text.RegularExpressions.Regex.Replace(expr, @"-\s*\(?([a-zA-Z0-9]+)\)?/\(\s*-\s*(\d+(?:\.\d+)?)\s*\)", "+$1/$2");
            expr = System.Text.RegularExpressions.Regex.Replace(expr, @"(?<=^|[+])\s*\(\s*-\s*([a-zA-Z0-9]+)\s*\)/(\d+(?:\.\d+)?)", "-$1/$2");
            expr = System.Text.RegularExpressions.Regex.Replace(expr, @"-\s*\(\s*-\s*([a-zA-Z0-9]+)\s*\)/(\d+(?:\.\d+)?)", "+$1/$2");

            expr = System.Text.RegularExpressions.Regex.Replace(expr, @"(?<=^|[+-])-\(([^()]+)\)", "-1*($1)");
            expr = System.Text.RegularExpressions.Regex.Replace(expr, @"(?<=^|[+-])\+\(([^()]+)\)", "+1*($1)");
            expr = System.Text.RegularExpressions.Regex.Replace(expr, @"(?<=^|[+-])\(([^()]+)\)", "1*($1)");

            var parenPattern = @"([+-]?\d+(?:\.\d+)?)\s*\*?\s*\(\s*([^()]+)\s*\)";
            int maxUnfold = 5;
            while (maxUnfold-- > 0 && System.Text.RegularExpressions.Regex.IsMatch(expr, parenPattern))
            {
                expr = System.Text.RegularExpressions.Regex.Replace(expr, parenPattern, match =>
                {
                    double factor = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    string inner = match.Groups[2].Value;
                    var innerMatches = System.Text.RegularExpressions.Regex.Matches(inner, @"([+-]?)([^+-]+)");
                    var expanded = new System.Text.StringBuilder();
                    foreach (System.Text.RegularExpressions.Match im in innerMatches)
                    {
                        double sign = im.Groups[1].Value == "-" ? -1.0 : 1.0;
                        string term = im.Groups[2].Value.Trim();
                        if (string.IsNullOrEmpty(term)) continue;

                        if (term.Contains(var1))
                        {
                            string rest = term.Replace(var1.ToString(), "").Replace("*", "");
                            double coef = string.IsNullOrEmpty(rest) ? 1.0 : (double.TryParse(rest, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cv) ? cv : 1.0);
                            double res = factor * sign * coef;
                            expanded.Append(res >= 0 ? $"+{res}{var1}" : $"{res}{var1}");
                        }
                        else if (term.Contains(var2))
                        {
                            string rest = term.Replace(var2.ToString(), "").Replace("*", "");
                            double coef = string.IsNullOrEmpty(rest) ? 1.0 : (double.TryParse(rest, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cv) ? cv : 1.0);
                            double res = factor * sign * coef;
                            expanded.Append(res >= 0 ? $"+{res}{var2}" : $"{res}{var2}");
                        }
                        else
                        {
                            if (double.TryParse(term, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var constVal))
                            {
                                double res = factor * sign * constVal;
                                expanded.Append(res >= 0 ? $"+{res}" : $"{res}");
                            }
                        }
                    }
                    return expanded.ToString();
                });
            }

            var matches = System.Text.RegularExpressions.Regex.Matches(expr, @"([+-]?)([^+-]+)");
            if (matches.Count == 0) return false;

            double sumA = 0;
            double sumB = 0;
            double sumC = 0;

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var signStr = m.Groups[1].Value;
                double sign = signStr == "-" ? -1.0 : 1.0;
                var term = m.Groups[2].Value.Trim();
                if (string.IsNullOrEmpty(term)) continue;

                if (var1 != var2 && term.Contains(var1) && term.Contains(var2))
                {
                    return false;
                }

                if (term.Contains(var1))
                {
                    if (term.Contains("^")) return false;
                    string rest = term.Replace(var1.ToString(), "").Replace("*", "").Replace("(", "").Replace(")", "").Trim();
                    double coef;
                    if (string.IsNullOrEmpty(rest))
                    {
                        coef = 1.0;
                    }
                    else if (rest.StartsWith("/"))
                    {
                        if (!TryParseFractionOrDouble(rest.Substring(1), out double denom) || Math.Abs(denom) < 1e-9)
                            return false;
                        coef = 1.0 / denom;
                    }
                    else if (!TryParseFractionOrDouble(rest, out coef))
                    {
                        return false;
                    }
                    sumA += sign * coef;
                }
                else if (term.Contains(var2))
                {
                    if (term.Contains("^")) return false;
                    string rest = term.Replace(var2.ToString(), "").Replace("*", "").Replace("(", "").Replace(")", "").Trim();
                    double coef;
                    if (string.IsNullOrEmpty(rest))
                    {
                        coef = 1.0;
                    }
                    else if (rest.StartsWith("/"))
                    {
                        if (!TryParseFractionOrDouble(rest.Substring(1), out double denom) || Math.Abs(denom) < 1e-9)
                            return false;
                        coef = 1.0 / denom;
                    }
                    else if (!TryParseFractionOrDouble(rest, out coef))
                    {
                        return false;
                    }
                    sumB += sign * coef;
                }
                else
                {
                    if (!TryParseFractionOrDouble(term, out double cVal))
                    {
                        return false;
                    }
                    sumC += sign * cVal;
                }
            }

            a = sumA;
            b = sumB;
            c = sumC;
            return true;
        }

        public static bool TryNormalizeLinearEquation(string eq, out double normA, out double normB, out double normC)
        {
            normA = 0; normB = 0; normC = 0;
            if (string.IsNullOrWhiteSpace(eq)) return false;

            var parts = eq.Split('=');
            if (parts.Length != 2) return false;

            string left = parts[0].Trim();
            string right = parts[1].Trim();

            var (v1, v2) = DetectVariables(eq);

            if (!TryParseLinearExpression(left, v1, v2, out double lA, out double lB, out double lC) ||
                !TryParseLinearExpression(right, v1, v2, out double rA, out double rB, out double rC))
            {
                return false;
            }

            double A = lA - rA;
            double B = lB - rB;
            double C = lC - rC;

            if (Math.Abs(A) < 1e-7 && Math.Abs(B) < 1e-7)
            {
                return false;
            }

            if (Math.Abs(A) > 1e-7)
            {
                if (A < 0)
                {
                    A = -A;
                    B = -B;
                    C = -C;
                }
                normA = 1.0;
                normB = Math.Round(B / A, 6);
                normC = Math.Round(C / A, 6);
                if (normB == 0.0) normB = 0.0;
                if (normC == 0.0) normC = 0.0;
                return true;
            }
            else
            {
                if (B < 0)
                {
                    B = -B;
                    C = -C;
                }
                normA = 0.0;
                normB = 1.0;
                normC = Math.Round(C / B, 6);
                if (normC == 0.0) normC = 0.0;
                return true;
            }
        }

        public static bool TryNormalizeHyperbolaEquation(
            string eq,
            out HyperbolaOrientation orientation,
            out double centerX,
            out double centerY,
            out double aSquared,
            out double bSquared)
        {
            orientation = HyperbolaOrientation.Horizontal;
            centerX = 0; centerY = 0; aSquared = 0; bSquared = 0;
            if (string.IsNullOrWhiteSpace(eq)) return false;

            string clean = eq.Trim().Replace(" ", "").Replace("（", "(").Replace("）", ")")
                             .Replace("^{2}", "^2").Replace("^{02}", "^2");

            var parts = clean.Split('=');
            if (parts.Length != 2) return false;

            double cx2 = 0, cy2 = 0, cxy = 0, cx = 0, cy = 0, c0 = 0;

            if (!TryParseCircleSide(parts[0], ref cx2, ref cy2, ref cxy, ref cx, ref cy, ref c0, sign: 1.0))
                return false;

            if (!TryParseCircleSide(parts[1], ref cx2, ref cy2, ref cxy, ref cx, ref cy, ref c0, sign: -1.0))
                return false;

            // 双曲线代数特征：无交叉项 cxy == 0, 二次项系数严格异号 cx2 * cy2 < 0
            if (Math.Abs(cxy) > 1e-5) return false;
            if (cx2 * cy2 >= -1e-6) return false;

            // cx2 * (x - x0)^2 + cy2 * (y - y0)^2 = cRhs
            double x0 = -cx / (2.0 * cx2);
            double y0 = -cy / (2.0 * cy2);
            double cRhs = cx2 * x0 * x0 + cy2 * y0 * y0 - c0;

            if (Math.Abs(cRhs) <= 1e-5) return false; // 排除相交渐近线退化形式

            double qx = cRhs / cx2;
            double qy = cRhs / cy2;

            if (qx > 1e-5 && qy < -1e-5)
            {
                // (x - x0)^2 / a^2 - (y - y0)^2 / b^2 = 1 (焦点在水平轴 / 对应 x)
                orientation = HyperbolaOrientation.Horizontal;
                centerX = Math.Round(x0, 4);
                centerY = Math.Round(y0, 4);
                aSquared = Math.Round(qx, 4);
                bSquared = Math.Round(-qy, 4);
                if (centerX == 0.0) centerX = 0.0;
                if (centerY == 0.0) centerY = 0.0;
                return true;
            }
            else if (qy > 1e-5 && qx < -1e-5)
            {
                // (y - y0)^2 / a^2 - (x - x0)^2 / b^2 = 1 (焦点在垂直轴 / 对应 y)
                orientation = HyperbolaOrientation.Vertical;
                centerX = Math.Round(x0, 4);
                centerY = Math.Round(y0, 4);
                aSquared = Math.Round(qy, 4);
                bSquared = Math.Round(-qx, 4);
                if (centerX == 0.0) centerX = 0.0;
                if (centerY == 0.0) centerY = 0.0;
                return true;
            }

            return false;
        }

        public static bool TryNormalizeParabolaEquation(
            string eq,
            out ParabolaOrientation orientation,
            out double vertexX,
            out double vertexY,
            out double twoP)
        {
            orientation = ParabolaOrientation.Horizontal;
            vertexX = 0; vertexY = 0; twoP = 0;
            if (string.IsNullOrWhiteSpace(eq)) return false;

            string clean = eq.Trim().Replace(" ", "").Replace("（", "(").Replace("）", ")")
                             .Replace("^{2}", "^2").Replace("^{02}", "^2");

            var parts = clean.Split('=');
            if (parts.Length != 2) return false;

            double cx2 = 0, cy2 = 0, cxy = 0, cx = 0, cy = 0, c0 = 0;

            if (!TryParseCircleSide(parts[0], ref cx2, ref cy2, ref cxy, ref cx, ref cy, ref c0, sign: 1.0))
                return false;

            if (!TryParseCircleSide(parts[1], ref cx2, ref cy2, ref cxy, ref cx, ref cy, ref c0, sign: -1.0))
                return false;

            // 抛物线代数特征：无交叉项 cxy == 0, 且恰好只有一个二次项非零
            if (Math.Abs(cxy) > 1e-5) return false;

            bool hasY2 = Math.Abs(cy2) > 1e-5 && Math.Abs(cx2) <= 1e-5;
            bool hasX2 = Math.Abs(cx2) > 1e-5 && Math.Abs(cy2) <= 1e-5;

            if (!hasY2 && !hasX2) return false;

            if (hasY2)
            {
                // 对称轴平行于 x 轴: (y - y0)^2 = 2p * (x - x0)
                // 展开: cy2*y^2 + cy*y + cx*x + c0 = 0
                // 必须含 x 的一次项 (cx 非零)
                if (Math.Abs(cx) <= 1e-5) return false;

                double y0 = -cy / (2.0 * cy2);
                // cy2 * (y - y0)^2 = -cx * x - (c0 - cy2 * y0^2) = -cx * (x - x0)
                // 其中 x0 = (cy2 * y0^2 - c0) / cx
                double x0 = (cy2 * y0 * y0 - c0) / cx;
                double p2 = -cx / cy2;

                orientation = ParabolaOrientation.Horizontal;
                vertexX = Math.Round(x0, 4);
                vertexY = Math.Round(y0, 4);
                twoP = Math.Round(p2, 4);
                if (vertexX == 0.0) vertexX = 0.0;
                if (vertexY == 0.0) vertexY = 0.0;
                if (twoP == 0.0) twoP = 0.0;
                return true;
            }
            else
            {
                // 对称轴平行于 y 轴: (x - x0)^2 = 2p * (y - y0)
                // 展开: cx2*x^2 + cx*x + cy*y + c0 = 0
                // 必须含 y 的一次项 (cy 非零)
                if (Math.Abs(cy) <= 1e-5) return false;

                double x0 = -cx / (2.0 * cx2);
                // cx2 * (x - x0)^2 = -cy * y - (c0 - cx2 * x0^2) = -cy * (y - y0)
                // 其中 y0 = (cx2 * x0^2 - c0) / cy
                double y0 = (cx2 * x0 * x0 - c0) / cy;
                double p2 = -cy / cx2;

                orientation = ParabolaOrientation.Vertical;
                vertexX = Math.Round(x0, 4);
                vertexY = Math.Round(y0, 4);
                twoP = Math.Round(p2, 4);
                if (vertexX == 0.0) vertexX = 0.0;
                if (vertexY == 0.0) vertexY = 0.0;
                if (twoP == 0.0) twoP = 0.0;
                return true;
            }
        }

        public static bool TryNormalizeEllipseEquation(string eq, out double centerX, out double centerY, out double aSquared, out double bSquared)
        {
            centerX = 0; centerY = 0; aSquared = 0; bSquared = 0;
            if (string.IsNullOrWhiteSpace(eq)) return false;

            string clean = eq.Trim().Replace(" ", "").Replace("（", "(").Replace("）", ")")
                             .Replace("^{2}", "^2").Replace("^{02}", "^2");

            var parts = clean.Split('=');
            if (parts.Length != 2) return false;

            double cx2 = 0, cy2 = 0, cxy = 0, cx = 0, cy = 0, c0 = 0;

            if (!TryParseCircleSide(parts[0], ref cx2, ref cy2, ref cxy, ref cx, ref cy, ref c0, sign: 1.0))
                return false;

            if (!TryParseCircleSide(parts[1], ref cx2, ref cy2, ref cxy, ref cx, ref cy, ref c0, sign: -1.0))
                return false;

            // 椭圆代数特征：无交叉项 cxy == 0, 二次项系数同号严格正数 (若同负则同乘 -1)
            if (Math.Abs(cxy) > 1e-5) return false;

            if (cx2 < 0 && cy2 < 0 && c0 > 0)
            {
                cx2 = -cx2;
                cy2 = -cy2;
                cx = -cx;
                cy = -cy;
                c0 = -c0;
            }

            if (cx2 <= 1e-5 || cy2 <= 1e-5) return false;

            // cx2 * (x - x0)^2 + cy2 * (y - y0)^2 = C_rhs
            double x0 = -cx / (2.0 * cx2);
            double y0 = -cy / (2.0 * cy2);
            double cRhs = cx2 * x0 * x0 + cy2 * y0 * y0 - c0;

            if (cRhs <= 1e-5) return false;

            double aSq = cRhs / cx2;
            double bSq = cRhs / cy2;

            centerX = Math.Round(x0, 4);
            centerY = Math.Round(y0, 4);
            aSquared = Math.Round(aSq, 4);
            bSquared = Math.Round(bSq, 4);
            if (centerX == 0.0) centerX = 0.0;
            if (centerY == 0.0) centerY = 0.0;
            return true;
        }

        public static bool TryNormalizeCircleEquation(string eq, out double centerX, out double centerY, out double rSquared)
        {
            centerX = 0; centerY = 0; rSquared = 0;
            if (string.IsNullOrWhiteSpace(eq)) return false;

            // 预处理：消除空格，统一中英文括号与上标
            string clean = eq.Trim().Replace(" ", "").Replace("（", "(").Replace("）", ")")
                             .Replace("^{2}", "^2").Replace("^{02}", "^2");

            var parts = clean.Split('=');
            if (parts.Length != 2) return false;

            double cx2 = 0, cy2 = 0, cxy = 0, cx = 0, cy = 0, c0 = 0;

            if (!TryParseCircleSide(parts[0], ref cx2, ref cy2, ref cxy, ref cx, ref cy, ref c0, sign: 1.0))
                return false;

            if (!TryParseCircleSide(parts[1], ref cx2, ref cy2, ref cxy, ref cx, ref cy, ref c0, sign: -1.0))
                return false;

            // 圆方程代数特征：无交叉项 xy (cxy == 0), 且二次项系数 cx2 == cy2 > 0 (若同负则同乘 -1)
            if (Math.Abs(cxy) > 1e-5) return false;

            if (cx2 < 0 && cy2 < 0)
            {
                cx2 = -cx2;
                cy2 = -cy2;
                cx = -cx;
                cy = -cy;
                c0 = -c0;
            }

            if (cx2 <= 1e-5 || Math.Abs(cx2 - cy2) > 1e-5) return false;

            // 归一化二次项系数为 1
            double A = cx2;
            double D = cx / A;
            double E = cy / A;
            double F = c0 / A;

            // 标准圆方程: (x - x0)^2 + (y - y0)^2 = r^2
            // 展开式: x^2 + y^2 - 2x0*x - 2y0*y + (x0^2 + y0^2 - r^2) = 0
            // 对应: D = -2x0 => x0 = -D/2
            //       E = -2y0 => y0 = -E/2
            //       F = x0^2 + y0^2 - r^2 => r^2 = x0^2 + y0^2 - F
            double x0 = -D / 2.0;
            double y0 = -E / 2.0;
            double rSq = x0 * x0 + y0 * y0 - F;

            if (rSq <= 1e-5) return false; // 半径平方必须为正数（排除点圆与虚圆）

            centerX = Math.Round(x0, 4);
            centerY = Math.Round(y0, 4);
            rSquared = Math.Round(rSq, 4);
            if (centerX == 0.0) centerX = 0.0;
            if (centerY == 0.0) centerY = 0.0;
            return true;
        }

        private static bool TryParseCircleSide(string side, ref double cx2, ref double cy2, ref double cxy, ref double cx, ref double cy, ref double c0, double sign)
        {
            if (string.IsNullOrWhiteSpace(side)) return false;
            if (side == "0") return true;

            var terms = SplitCircleAlgebraicTerms(side);
            if (terms.Count == 0) return false;

            foreach (var term in terms)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;

                double termSign = 1.0;
                string t = term.Trim();
                if (t.StartsWith("+"))
                {
                    t = t.Substring(1).Trim();
                }
                else if (t.StartsWith("-"))
                {
                    termSign = -1.0;
                    t = t.Substring(1).Trim();
                }

                // 1. ( ... )^2 项, 例如 (x-1)^2, (y+2)^2, (1-x)^2, 2*(x-1)^2, (x-1)^2/9, (y+2)^2/4
                var squareBracketMatch = System.Text.RegularExpressions.Regex.Match(t, @"^(?:([+-]?\d+(?:\.\d+)?)\*?)?\(([^)]+)\)\^2(?:\/([+-]?\d+(?:\.\d+)?))?$");
                if (squareBracketMatch.Success)
                {
                    double outerCoeff = 1.0;
                    if (!string.IsNullOrEmpty(squareBracketMatch.Groups[1].Value))
                    {
                        if (!double.TryParse(squareBracketMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out outerCoeff))
                            return false;
                    }
                    if (!string.IsNullOrEmpty(squareBracketMatch.Groups[3].Value))
                    {
                        if (double.TryParse(squareBracketMatch.Groups[3].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double den) && Math.Abs(den) > 1e-9)
                        {
                            outerCoeff /= den;
                        }
                    }
                    outerCoeff *= termSign * sign;
                    string inner = squareBracketMatch.Groups[2].Value.Trim();

                    if (!TryParseCircleLinearInner(inner, out char varChar, out double aCoeff, out double bConst))
                        return false;

                    // (a*v + b)^2 = a^2 * v^2 + 2*a*b * v + b^2
                    if (varChar == 'x')
                    {
                        cx2 += outerCoeff * aCoeff * aCoeff;
                        cx += outerCoeff * 2.0 * aCoeff * bConst;
                        c0 += outerCoeff * bConst * bConst;
                    }
                    else if (varChar == 'y')
                    {
                        cy2 += outerCoeff * aCoeff * aCoeff;
                        cy += outerCoeff * 2.0 * aCoeff * bConst;
                        c0 += outerCoeff * bConst * bConst;
                    }
                    else
                    {
                        return false;
                    }
                    continue;
                }

                // 1.5 一次带括号项, 例如 4(x+2), -2(y-1), (x+2), 4*(x+2), (x-3)/2
                var linearBracketMatch = System.Text.RegularExpressions.Regex.Match(t, @"^(?:([+-]?\d+(?:\.\d+)?)\*?)?\(([^)]+)\)(?:\/([+-]?\d+(?:\.\d+)?))?$");
                if (linearBracketMatch.Success && !t.Contains("^"))
                {
                    double outerCoeff = 1.0;
                    if (!string.IsNullOrEmpty(linearBracketMatch.Groups[1].Value))
                    {
                        if (!double.TryParse(linearBracketMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out outerCoeff))
                            return false;
                    }
                    if (!string.IsNullOrEmpty(linearBracketMatch.Groups[3].Value))
                    {
                        if (double.TryParse(linearBracketMatch.Groups[3].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double den) && Math.Abs(den) > 1e-9)
                        {
                            outerCoeff /= den;
                        }
                    }
                    outerCoeff *= termSign * sign;
                    string inner = linearBracketMatch.Groups[2].Value.Trim();

                    if (!TryParseCircleLinearInner(inner, out char varChar, out double aCoeff, out double bConst))
                        return false;

                    // outerCoeff * (a*v + b) = outerCoeff * a * v + outerCoeff * b
                    if (varChar == 'x')
                    {
                        cx += outerCoeff * aCoeff;
                        c0 += outerCoeff * bConst;
                    }
                    else if (varChar == 'y')
                    {
                        cy += outerCoeff * aCoeff;
                        c0 += outerCoeff * bConst;
                    }
                    else
                    {
                        return false;
                    }
                    continue;
                }

                // 2. 二次单项: k*x^2 或 k*y^2 或 x^2/9 或 y^2/4
                var quadMatch = System.Text.RegularExpressions.Regex.Match(t, @"^(?:([+-]?\d+(?:\.\d+)?(?:\/\d+)?)\*?)?([xy])\^2(?:\/([+-]?\d+(?:\.\d+)?))?$");
                if (quadMatch.Success)
                {
                    double coeff = 1.0;
                    string cStr = quadMatch.Groups[1].Value;
                    if (!string.IsNullOrEmpty(cStr))
                    {
                        if (!TryParseFractionOrDouble(cStr, out coeff)) return false;
                    }
                    if (!string.IsNullOrEmpty(quadMatch.Groups[3].Value))
                    {
                        if (double.TryParse(quadMatch.Groups[3].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double den) && Math.Abs(den) > 1e-9)
                        {
                            coeff /= den;
                        }
                    }
                    coeff *= termSign * sign;
                    char v = quadMatch.Groups[2].Value[0];
                    if (v == 'x') cx2 += coeff;
                    else if (v == 'y') cy2 += coeff;
                    continue;
                }

                // 3. 一次单项: k*x 或 k*y
                var linearMatch = System.Text.RegularExpressions.Regex.Match(t, @"^(?:([+-]?\d+(?:\.\d+)?(?:\/\d+)?)\*?)?([xy])$");
                if (linearMatch.Success)
                {
                    double coeff = 1.0;
                    string cStr = linearMatch.Groups[1].Value;
                    if (!string.IsNullOrEmpty(cStr))
                    {
                        if (!TryParseFractionOrDouble(cStr, out coeff)) return false;
                    }
                    coeff *= termSign * sign;
                    char v = linearMatch.Groups[2].Value[0];
                    if (v == 'x') cx += coeff;
                    else if (v == 'y') cy += coeff;
                    continue;
                }

                // 4. 常数项平方 (如 3^2, 4^2)
                var constSquareMatch = System.Text.RegularExpressions.Regex.Match(t, @"^([+-]?\d+(?:\.\d+)?)\^2$");
                if (constSquareMatch.Success)
                {
                    if (double.TryParse(constSquareMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double baseVal))
                    {
                        c0 += baseVal * baseVal * termSign * sign;
                        continue;
                    }
                }

                // 5. 普通数值或分数常数 (如 9, 4, 16, 25/4)
                if (TryParseFractionOrDouble(t, out double constVal))
                {
                    c0 += constVal * termSign * sign;
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool TryParseCircleLinearInner(string inner, out char varChar, out double aCoeff, out double bConst)
        {
            varChar = ' '; aCoeff = 0; bConst = 0;
            if (string.IsNullOrWhiteSpace(inner)) return false;

            inner = inner.Replace(" ", "");
            var terms = SplitCircleAlgebraicTerms(inner);
            if (terms.Count == 0 || terms.Count > 2) return false;

            foreach (var term in terms)
            {
                double sign = 1.0;
                string t = term.Trim();
                if (t.StartsWith("+")) t = t.Substring(1).Trim();
                else if (t.StartsWith("-")) { sign = -1.0; t = t.Substring(1).Trim(); }

                if (t == "x" || t == "y")
                {
                    varChar = t[0];
                    aCoeff += 1.0 * sign;
                }
                else if (t.EndsWith("x") || t.EndsWith("y"))
                {
                    char v = t[t.Length - 1];
                    string numPart = t.Substring(0, t.Length - 1).Trim('*');
                    if (double.TryParse(numPart, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double numVal))
                    {
                        varChar = v;
                        aCoeff += numVal * sign;
                    }
                    else return false;
                }
                else if (TryParseFractionOrDouble(t, out double cVal))
                {
                    bConst += cVal * sign;
                }
                else return false;
            }

            return varChar == 'x' || varChar == 'y';
        }

        private static List<string> SplitCircleAlgebraicTerms(string s)
        {
            var terms = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0 && i > 0 && (c == '+' || c == '-'))
                {
                    char prev = s[i - 1];
                    if (prev != '+' && prev != '-' && prev != '*' && prev != '/' && prev != '^')
                    {
                        var term = s.Substring(start, i - start).Trim();
                        if (!string.IsNullOrEmpty(term)) terms.Add(term);
                        start = i;
                    }
                }
            }
            if (start < s.Length)
            {
                var term = s.Substring(start).Trim();
                if (!string.IsNullOrEmpty(term)) terms.Add(term);
            }
            return terms;
        }

        public static bool TryParseComplex(string s, out double real, out double imag)
        {
            real = 0; imag = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            s = s.Trim().Replace(" ", "").Replace("（", "(").Replace("）", ")");
            if (s.Contains('=') || s.Contains('[') || s.Contains(']') || s.Contains('{') || s.Contains('}'))
            {
                return false;
            }

            bool hasI = System.Text.RegularExpressions.Regex.IsMatch(s, @"(?<![a-zA-Z])i(?![a-zA-Z])");

            if (!hasI)
            {
                if (TryParseFractionOrDouble(s, out double rVal))
                {
                    real = rVal;
                    imag = 0;
                    return true;
                }
                return false;
            }

            s = s.Replace("i*", "i").Replace("*i", "i");
            var matches = System.Text.RegularExpressions.Regex.Matches(s, @"([+-]?)([^+-]+)");
            if (matches.Count == 0) return false;

            double sumReal = 0;
            double sumImag = 0;
            bool foundAny = false;

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var signStr = m.Groups[1].Value;
                double sign = signStr == "-" ? -1.0 : 1.0;
                var term = m.Groups[2].Value.Trim();
                if (string.IsNullOrEmpty(term)) continue;

                if (term.Contains("i"))
                {
                    string rest = term.Replace("i", "").Trim('(', ')');
                    double coef;
                    if (string.IsNullOrEmpty(rest))
                    {
                        coef = 1.0;
                    }
                    else if (!TryParseFractionOrDouble(rest, out coef))
                    {
                        return false;
                    }
                    sumImag += sign * coef;
                    foundAny = true;
                }
                else
                {
                    string rest = term.Trim('(', ')');
                    if (!TryParseFractionOrDouble(rest, out double rPart))
                    {
                        return false;
                    }
                    sumReal += sign * rPart;
                    foundAny = true;
                }
            }

            if (!foundAny) return false;

            real = Math.Round(sumReal, 6);
            imag = Math.Round(sumImag, 6);
            if (real == 0.0) real = 0.0;
            if (imag == 0.0) imag = 0.0;
            return true;
        }

        public static bool CheckThermochemicalEquationMatch(string u, string c)
        {
            if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(c)) return false;

            // 快速前置过滤：热化学方程式必须包含焓变记号
            bool hasDeltaU = u.Contains("Delta", StringComparison.OrdinalIgnoreCase) || u.Contains("Δ") || u.Contains("delta", StringComparison.OrdinalIgnoreCase);
            bool hasDeltaC = c.Contains("Delta", StringComparison.OrdinalIgnoreCase) || c.Contains("Δ") || c.Contains("delta", StringComparison.OrdinalIgnoreCase);
            if (!hasDeltaU || !hasDeltaC) return false;

            static bool TryParseThermochemical(string input, out string reaction, out double deltaH, out string unit)
            {
                reaction = string.Empty;
                deltaH = 0;
                unit = string.Empty;
                if (string.IsNullOrWhiteSpace(input)) return false;

                // 匹配热化学焓变特征: \Delta H / ΔH / deltaH / \delta H / delta H
                // 允许标态上标如 ^\theta, ^\circ, ^o, ^0, 或下标如 \Delta_r H_m 等
                var pattern = @"(?i)(?:\\?(?:Delta|delta)|Δ)(?:_[a-zA-Z0-9]+)?\s*[hH](?:(?:\^|_)?(?:\\circ|\\theta|\theta|0|o|θ|[a-zA-Z0-9]+))?\s*=\s*([+-]?\s*\d+(?:\.\d+)?)\s*([a-zA-Z0-9·*^/\\{}\s+-]*)";
                var match = System.Text.RegularExpressions.Regex.Match(input, pattern);
                if (!match.Success) return false;

                string valStr = match.Groups[1].Value.Replace(" ", "");
                if (!double.TryParse(valStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out deltaH))
                {
                    return false;
                }

                unit = match.Groups[2].Value.Trim();

                // 提取反应方程式主体部分 (支持 ΔH 在末尾，或在开头通过逗号/分号隔开)
                if (match.Index > 0)
                {
                    reaction = input.Substring(0, match.Index).Trim().TrimEnd(';', '；', ',', '，');
                }
                else
                {
                    reaction = input.Substring(match.Index + match.Length).Trim().TrimStart(';', '；', ',', '，');
                }

                return !string.IsNullOrWhiteSpace(reaction);
            }

            if (!TryParseThermochemical(u, out var uRxn, out var uDeltaH, out var uUnit) ||
                !TryParseThermochemical(c, out var cRxn, out var cDeltaH, out var cUnit))
            {
                return false;
            }

            // 1. 检验反应方程式主体
            bool rxnMatch = CheckFillInBlankMatch(uRxn, cRxn);

            if (!rxnMatch) return false;

            // 2. 检验焓变单位与数值
            static string NormalizeDeltaHUnit(string rawUnit)
            {
                rawUnit = rawUnit.ToLowerInvariant().Replace(" ", "").Replace("~", "").Replace("$", "")
                    .Replace("\\text{", "").Replace("\\mathrm{", "").Replace("}", "")
                    .Replace("\\cdot", "").Replace("·", "").Replace("*", "");

                if (rawUnit.Contains("kj") && rawUnit.Contains("mol")) return "kj/mol";
                if (rawUnit.Contains("j") && rawUnit.Contains("mol")) return "j/mol";
                return rawUnit;
            }

            string normUUnit = NormalizeDeltaHUnit(uUnit);
            string normCUnit = NormalizeDeltaHUnit(cUnit);

            if (normUUnit == normCUnit || string.IsNullOrEmpty(normUUnit) || string.IsNullOrEmpty(normCUnit))
            {
                return AreNumbersClose(uDeltaH, cDeltaH);
            }
            else if (normUUnit == "j/mol" && normCUnit == "kj/mol")
            {
                return AreNumbersClose(uDeltaH, cDeltaH * 1000.0);
            }
            else if (normUUnit == "kj/mol" && normCUnit == "j/mol")
            {
                return AreNumbersClose(uDeltaH * 1000.0, cDeltaH);
            }

            return false;
        }

        public static bool CheckCoordinateEquationMatch(string u, string c)
        {
            if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(c)) return false;

            static string StripPrefix(string s)
            {
                s = s.Trim();
                s = System.Text.RegularExpressions.Regex.Replace(s, @"^(?:点\s*[a-zA-Z]?\s*|[a-zA-Z]\s*(?=\()|\\vec\{[a-zA-Z]\}\s*=\s*|[a-zA-Z]\s*=\s*(?=\()|\([xyzXYZ]\s*,\s*[xyzXYZ](?:\s*,\s*[xyzXYZ])?\)\s*=\s*)", "");
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^(?:点\s*)?[a-zA-Z]?\s*\("))
                {
                    int openParen = s.IndexOf('(');
                    if (openParen >= 0)
                    {
                        s = s.Substring(openParen).Trim();
                    }
                }
                return s.Trim();
            }

            u = StripPrefix(u);
            c = StripPrefix(c);

            // 排除含有方括号的区间表示或包含集合并集运算符的表达式
            if (u.Contains("[") || u.Contains("]") || c.Contains("[") || c.Contains("]")) return false;
            if (u.Contains("∪") || u.Contains("\\cup") || u.Contains("并") || c.Contains("∪") || c.Contains("\\cup") || c.Contains("并")) return false;

            // 至少有一方必须是坐标元组形式 (a, b) 或 (a, b, c)
            bool uIsTuple = u.StartsWith("(") && u.EndsWith(")");
            bool cIsTuple = c.StartsWith("(") && c.EndsWith(")");
            if (!uIsTuple && !cIsTuple) return false;

            if (string.Equals(u, c, StringComparison.OrdinalIgnoreCase)) return true;

            // 1. 2D 坐标与基底向量或变量方程匹配
            bool TryMatch2D(string tupleStr, string otherStr)
            {
                var m = System.Text.RegularExpressions.Regex.Match(tupleStr, @"^\(\s*([^,()]+)\s*,\s*([^,()]+)\s*\)$");
                if (!m.Success) return false;
                string a = m.Groups[1].Value.Trim();
                string b = m.Groups[2].Value.Trim();

                var otherM = System.Text.RegularExpressions.Regex.Match(otherStr, @"^\(\s*([^,()]+)\s*,\s*([^,()]+)\s*\)$");
                if (otherM.Success)
                {
                    // 双方均为二维元组，严格保序比对各分量
                    string oA = otherM.Groups[1].Value.Trim();
                    string oB = otherM.Groups[2].Value.Trim();
                    if ((a == oA || CheckFillInBlankMatch(a, oA)) && (b == oB || CheckFillInBlankMatch(b, oB)))
                        return true;
                    if (TryParseFractionOrDouble(a, out double valA) && TryParseFractionOrDouble(b, out double valB) &&
                        TryParseFractionOrDouble(oA, out double valOA) && TryParseFractionOrDouble(oB, out double valOB))
                    {
                        if (AreNumbersClose(valA, valOA) && AreNumbersClose(valB, valOB)) return true;
                    }
                    return false;
                }

                if (CheckFillInBlankMatch($"x={a},y={b}", otherStr) || CheckFillInBlankMatch($"y={b},x={a}", otherStr))
                {
                    return true;
                }

                if (TryParseBasisVector(otherStr, out double oX, out double oY))
                {
                    if (TryParseFractionOrDouble(a, out double uA) && TryParseFractionOrDouble(b, out double uB))
                    {
                        if (AreNumbersClose(uA, oX) && AreNumbersClose(uB, oY)) return true;
                    }
                }

                return false;
            }

            // 2. 3D 空间坐标与三维基底向量或变量方程匹配
            bool TryMatch3D(string tupleStr, string otherStr)
            {
                var m = System.Text.RegularExpressions.Regex.Match(tupleStr, @"^\(\s*([^,()]+)\s*,\s*([^,()]+)\s*,\s*([^,()]+)\s*\)$");
                if (!m.Success) return false;
                string a = m.Groups[1].Value.Trim();
                string b = m.Groups[2].Value.Trim();
                string d = m.Groups[3].Value.Trim();

                var otherM = System.Text.RegularExpressions.Regex.Match(otherStr, @"^\(\s*([^,()]+)\s*,\s*([^,()]+)\s*,\s*([^,()]+)\s*\)$");
                if (otherM.Success)
                {
                    // 双方均为三维空间元组，严格保序比对各分量
                    string oA = otherM.Groups[1].Value.Trim();
                    string oB = otherM.Groups[2].Value.Trim();
                    string oD = otherM.Groups[3].Value.Trim();
                    if ((a == oA || CheckFillInBlankMatch(a, oA)) &&
                        (b == oB || CheckFillInBlankMatch(b, oB)) &&
                        (d == oD || CheckFillInBlankMatch(d, oD)))
                        return true;
                    if (TryParseFractionOrDouble(a, out double valA) && TryParseFractionOrDouble(b, out double valB) && TryParseFractionOrDouble(d, out double valD) &&
                        TryParseFractionOrDouble(oA, out double valOA) && TryParseFractionOrDouble(oB, out double valOB) && TryParseFractionOrDouble(oD, out double valOD))
                    {
                        if (AreNumbersClose(valA, valOA) && AreNumbersClose(valB, valOB) && AreNumbersClose(valD, valOD)) return true;
                    }
                    return false;
                }

                if (CheckFillInBlankMatch($"x={a},y={b},z={d}", otherStr))
                {
                    return true;
                }

                if (TryParseBasisVector3D(otherStr, out double oX, out double oY, out double oZ))
                {
                    if (TryParseFractionOrDouble(a, out double uA) &&
                        TryParseFractionOrDouble(b, out double uB) &&
                        TryParseFractionOrDouble(d, out double uD))
                    {
                        if (AreNumbersClose(uA, oX) && AreNumbersClose(uB, oY) && AreNumbersClose(uD, oZ)) return true;
                    }
                }

                return false;
            }

            if (TryMatch2D(u, c) || TryMatch2D(c, u)) return true;
            if (TryMatch3D(u, c) || TryMatch3D(c, u)) return true;

            return false;
        }

        public static bool TryParseBasisVector(string s, out double x, out double y)
        {
            if (TryParseBasisVectorGeneral(s, out x, out y, out double z) && Math.Abs(z) < 1e-9)
            {
                return true;
            }
            return false;
        }

        public static bool TryParseBasisVector3D(string s, out double x, out double y, out double z)
        {
            return TryParseBasisVectorGeneral(s, out x, out y, out z);
        }

        private static bool TryParseBasisVectorGeneral(string s, out double x, out double y, out double z)
        {
            x = 0; y = 0; z = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^(?:\\vec\{[a-zA-Z]\}\s*=\s*|[a-zA-Z]\s*=\s*)", "").Trim();
            s = s.Replace("\\vec{i}", "i").Replace("\\vec{j}", "j").Replace("\\vec{k}", "k")
                 .Replace("\\mathbf{i}", "i").Replace("\\mathbf{j}", "j").Replace("\\mathbf{k}", "k")
                 .Replace("\\vec i", "i").Replace("\\vec j", "j").Replace("\\vec k", "k")
                 .Replace(" ", "");

            if (!s.Contains("i") && !s.Contains("j") && !s.Contains("k")) return false;

            s = s.Replace("i*", "i").Replace("*i", "i")
                 .Replace("j*", "j").Replace("*j", "j")
                 .Replace("k*", "k").Replace("*k", "k");

            var matches = System.Text.RegularExpressions.Regex.Matches(s, @"([+-]?)([^+-]+)");
            if (matches.Count == 0) return false;

            double sumX = 0, sumY = 0, sumZ = 0;
            bool foundAny = false;

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var signStr = m.Groups[1].Value;
                double sign = signStr == "-" ? -1.0 : 1.0;
                var term = m.Groups[2].Value.Trim();
                if (string.IsNullOrEmpty(term)) continue;

                if (term.EndsWith("i"))
                {
                    string rest = term.Substring(0, term.Length - 1).Trim('(', ')');
                    double coef = 1.0;
                    if (string.IsNullOrEmpty(rest) || rest == "+") coef = 1.0;
                    else if (rest == "-") coef = -1.0;
                    else if (!TryParseFractionOrDouble(rest, out coef)) return false;
                    sumX += sign * coef;
                    foundAny = true;
                }
                else if (term.EndsWith("j"))
                {
                    string rest = term.Substring(0, term.Length - 1).Trim('(', ')');
                    double coef = 1.0;
                    if (string.IsNullOrEmpty(rest) || rest == "+") coef = 1.0;
                    else if (rest == "-") coef = -1.0;
                    else if (!TryParseFractionOrDouble(rest, out coef)) return false;
                    sumY += sign * coef;
                    foundAny = true;
                }
                else if (term.EndsWith("k"))
                {
                    string rest = term.Substring(0, term.Length - 1).Trim('(', ')');
                    double coef = 1.0;
                    if (string.IsNullOrEmpty(rest) || rest == "+") coef = 1.0;
                    else if (rest == "-") coef = -1.0;
                    else if (!TryParseFractionOrDouble(rest, out coef)) return false;
                    sumZ += sign * coef;
                    foundAny = true;
                }
                else
                {
                    return false;
                }
            }

            if (!foundAny) return false;
            x = Math.Round(sumX, 6);
            y = Math.Round(sumY, 6);
            z = Math.Round(sumZ, 6);
            return true;
        }

        public static string StripCommonUnits(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Trim();
            var unitPattern = @"(?<=\d|\d\.\d+|\})\s*(\\mu m|\\mu s|\\mu a|\\mu c|\\mu f|\\mu v|\\mu h|\\mu t|\\mu mol|mu m|mu s|mu a|mu c|mu f|mu v|mu h|mu t|mu mol|mum|mus|mua|muc|muf|muv|muh|mut|mumol|μm|μs|μa|μc|μf|μv|μh|μt|μmol|um|us|ua|uc|uf|uv|uh|ut|umol|nm|pm|ns|ps|pf|nf|mt|mh|kj/mol|j/mol|kj\*mol\^\{-1\}|kj\*mol\^-1|kj·mol\^-1|kj·mol\^\{-1\}|j\*mol\^\{-1\}|j\*mol\^-1|j·mol\^-1|千焦/摩尔|千焦每摩尔|焦/摩尔|焦每摩尔|g/mol|g\*mol\^\{-1\}|g\*mol\^-1|g·mol\^-1|g·mol\^\{-1\}|l/mol|l\*mol\^\{-1\}|l\*mol\^-1|l·mol\^-1|l·mol\^\{-1\}|mol\^\{-1\}|mol\^-1|/mol|克/摩尔|克每摩尔|升/摩尔|升每摩尔|mol/l|mol·l\^\{-1\}|mol\*l\^\{-1\}|mol·l\^-1|mol\*l\^-1|mol·l\{-1\}|mol/L|摩尔/升|摩尔每升|g/ml|g/l|g/mL|克/毫升|克/升|克每升|克每毫升|m\*s\^\{-?[12]\}|m\*s\^-?[12]|m·s\^\{-?[12]\}|m·s\^-?[12]|m/s\^2|m/s²|m/s2|m/s|km/h|km\*h\^\{-1\}|km\*h\^-1|km/时|公里/小时|公里每小时|千米/小时|千米每小时|千米/时|mol|cm\^3|m\^3|dm\^3|mm\^3|cm³|m³|dm³|mm³|立方厘米|立方分米|立方毫米|立方米|cm\^2|m\^2|dm\^2|mm\^2|km\^2|cm²|m²|dm²|mm²|km²|平方厘米|平方分米|平方毫米|平方米|平方千米|平方公里|kg\*m/s\^2|n\*m|n·m|牛·米|牛\*米|牛顿·米|牛顿\*米|牛米|牛顿米|g/cm\^3|g/cm³|g/cm3|g·cm\^\{-?3\}|g·cm\^-?3|g\*cm\^\{-?3\}|g\*cm\^-?3|kg/m\^3|kg/m³|kg/m3|kg·m\^\{-?3\}|kg·m\^-?3|kg\*m\^\{-?3\}|kg\*m\^-?3|kw\*h|kw·h|kwh|j/\(kg\*℃\)|j/\(kg·℃\)|j/\(kg\*c\)|j/\(kg·c\)|j/\(kg\*k\)|j/\(kg·k\)|j/kg\*k|j/kg\*c|n/kg|n\*kg\^\{-1\}|n\*kg\^-1|n/m\^2|n/m²|n/m2|n\*m\^\{-?2\}|n\*m\^-?2|n·m\^\{-?2\}|n·m\^-?2|j/s|j\*s\^\{-?1\}|j\*s\^-?1|j·s\^\{-?1\}|j·s\^-?1|v/m|n/c|pa\*s|pa·s|pa|kpa|mpa|hpa|千帕|兆帕|百帕|atm|mmhg|hz|khz|mhz|ghz|kg|mg|cm|mm|dm|km|t|ml|v|kv|mv|a|ma|w|kw|mw|gw|千瓦|兆瓦|j|kj|mj|gj|千焦|兆焦|kn|n|c|ev|kev|mev|gev|wb|h|komega|momega|gomega|kohm|mohm|gohm|kω|mω|gω|ω|kΩ|mΩ|gΩ|Ω|千欧|兆欧|ohm|omega|bar|mbar|°c|deg|l|g|kb|mb|gb|tb|rad/s|rad|db|微米|纳米|皮米|微秒|纳秒|毫秒|微安|毫安|微法|纳法|皮法|微库|毫库|微伏|毫伏|毫特|微特|毫亨|微亨|牛顿?|焦耳?/\(千克[\*·]?(?:摄氏度|℃|度)\)|焦/\(千克[\*·]?(?:摄氏度|℃|度)\)|焦耳?/\(千克·摄氏度\)|焦/\(千克·摄氏度\)|焦/\(千克·度\)|焦/\(千克·℃\)|焦/\(千克\*℃\)|焦每千克摄氏度|焦耳?|瓦特?|帕斯卡?|帕·秒|帕\*秒|帕秒|帕|牛/千克|牛每千克|牛顿每千克|牛/平方米|牛每平方米|焦/秒|焦每秒|伏/米|伏每米|牛/库仑?|牛每库仑?|标准大气压|毫米汞柱|米/秒²|米/秒\^2|米每秒二次方|米每二次方秒|米每秒的平方|米/秒|米每秒|千瓦时|千瓦·时|千瓦\*时|度|摄氏度|℃|开尔文|k|厘米|毫米|分米|千米|米|克/立方厘米|克每立方厘米|千克/立方米|千克每立方米|克|千克|公斤|吨|升|毫升|摩尔|伏特?|伏|安培?|安|欧姆|欧|库仑?|库|特斯拉?|韦伯?|亨利?|电子伏特?|电子伏|字节|弧度|分贝|种|个|条|类|只|支|组|份|位|次|倍|对|双|根|颗|粒|株|块|幅|门|项|节|题|道|把|套)$";
            return System.Text.RegularExpressions.Regex.Replace(s, unitPattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        }

        public static readonly Dictionary<string, string> ChemicalSynonymMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // 氧化物与水
            ["水"] = "h2o", ["水蒸气"] = "h2o", ["冰"] = "h2o",
            ["二氧化碳"] = "co2", ["一氧化碳"] = "co", ["干冰"] = "co2",
            ["二氧化硫"] = "so2", ["三氧化硫"] = "so3",
            ["一氧化氮"] = "no", ["二氧化氮"] = "no2",
            ["氧化钙"] = "cao", ["生石灰"] = "cao",
            ["氧化铜"] = "cuo", ["氧化亚铜"] = "cu2o",
            ["氧化铁"] = "fe2o3", ["三氧化二铁"] = "fe2o3", ["铁锈"] = "fe2o3", ["赤铁矿"] = "fe2o3",
            ["四氧化三铁"] = "fe3o4", ["磁性氧化铁"] = "fe3o4", ["磁铁矿"] = "fe3o4",
            ["氧化镁"] = "mgo", ["氧化铝"] = "al2o3", ["氧化锌"] = "zno",
            ["二氧化锰"] = "mno2", ["二氧化硅"] = "sio2",
            ["过氧化氢"] = "h2o2", ["双氧水"] = "h2o2",
            ["过氧化钠"] = "na2o2",

            // 常见酸
            ["盐酸"] = "hcl", ["氯化氢"] = "hcl",
            ["次氯酸"] = "hclo",
            ["硫酸"] = "h2so4",
            ["硝酸"] = "hno3",
            ["碳酸"] = "h2co3",
            ["乙酸"] = "ch3cooh", ["醋酸"] = "ch3cooh",

            // 常见碱与碱性气体
            ["氨气"] = "nh3",
            ["氢氧化钠"] = "naoh", ["烧碱"] = "naoh", ["火碱"] = "naoh", ["苛性钠"] = "naoh",
            ["氢氧化钙"] = "ca(oh)2", ["熟石灰"] = "ca(oh)2", ["消石灰"] = "ca(oh)2", ["石灰水"] = "ca(oh)2",
            ["氢氧化钾"] = "koh", ["苛性钾"] = "koh",
            ["氢氧化钡"] = "ba(oh)2",
            ["氢氧化铜"] = "cu(oh)2",
            ["氢氧化铁"] = "fe(oh)3",
            ["氢氧化铝"] = "al(oh)3",
            ["氨水"] = "nh3*h2o", ["一水合氨"] = "nh3*h2o",

            // 常见盐
            ["氯化钠"] = "nacl", ["食盐"] = "nacl",
            ["次氯酸钠"] = "naclo",
            ["碳酸钙"] = "caco3", ["石灰石"] = "caco3", ["大理石"] = "caco3", ["方解石"] = "caco3",
            ["碳酸钠"] = "na2co3", ["纯碱"] = "na2co3", ["苏打"] = "na2co3",
            ["碳酸氢钠"] = "nahco3", ["小苏打"] = "nahco3",
            ["硫酸氢钠"] = "nahso4",
            ["碳酸氢钙"] = "ca(hco3)2",
            ["硫酸钠"] = "na2so4", ["硝酸钠"] = "nano3",
            ["氯化钙"] = "cacl2", ["硫酸钙"] = "caso4",
            ["硫酸铜"] = "cuso4",
            ["硫酸钡"] = "baso4",
            ["氯化银"] = "agcl",
            ["硫酸亚铁"] = "feso4",
            ["硫酸铁"] = "fe2(so4)3",
            ["氯化铁"] = "fecl3",
            ["氯化亚铁"] = "fecl2",
            ["硫酸铝"] = "al2(so4)3",
            ["高锰酸钾"] = "kmno4",
            ["锰酸钾"] = "k2mno4",
            ["氯酸钾"] = "kclo3",
            ["氯化钾"] = "kcl",
            ["硝酸钾"] = "kno3",
            ["硝酸银"] = "agno3",
            ["氯化铵"] = "nh4cl",
            ["硫酸铵"] = "(nh4)2so4",
            ["硝酸铵"] = "nh4no3",
            ["碳酸铵"] = "(nh4)2co3",
            ["碳酸氢铵"] = "nh4hco3",
            ["硫酸锌"] = "znso4",
            ["重铬酸钾"] = "k2cr2o7",
            ["草酸"] = "h2c2o4",
            ["碘化钾"] = "ki",
            ["溴化银"] = "agbr",
            ["碘化银"] = "agi",
            ["氯化钡"] = "bacl2",
            ["硅酸钠"] = "na2sio3", ["水玻璃"] = "na2sio3",
            ["葡萄糖"] = "c6h12o6",
            ["蔗糖"] = "c12h22o11",
            ["苯"] = "c6h6",
            ["甲苯"] = "c7h8",
            ["乙酸乙酯"] = "ch3cooc2h5",

            // 常见原子团与离子
            ["铵根"] = "nh4+", ["铵根离子"] = "nh4+",
            ["氢氧根"] = "oh-", ["氢氧根离子"] = "oh-",
            ["碳酸根"] = "co3^2-", ["碳酸根离子"] = "co3^2-",
            ["碳酸氢根"] = "hco3-", ["碳酸氢根离子"] = "hco3-",
            ["硫酸根"] = "so4^2-", ["硫酸根离子"] = "so4^2-",
            ["亚硫酸根"] = "so3^2-", ["亚硫酸根离子"] = "so3^2-",
            ["硝酸根"] = "no3-", ["硝酸根离子"] = "no3-",
            ["高锰酸根"] = "mno4-", ["高锰酸根离子"] = "mno4-",
            ["锰酸根"] = "mno4^2-", ["锰酸根离子"] = "mno4^2-",
            ["磷酸根"] = "po4^3-", ["磷酸根离子"] = "po4^3-",
            ["重铬酸根"] = "cr2o7^2-", ["重铬酸根离子"] = "cr2o7^2-",

            // 常见单质
            ["氧气"] = "o2", ["氢气"] = "h2", ["氮气"] = "n2", ["氯气"] = "cl2", ["臭氧"] = "o3",
            ["铁"] = "fe", ["铁粉"] = "fe", ["铜"] = "cu", ["铝"] = "al", ["锌"] = "zn", ["镁"] = "mg",
            ["银"] = "ag", ["金"] = "au", ["水银"] = "hg", ["汞"] = "hg",
            ["碳"] = "c", ["木炭"] = "c", ["焦炭"] = "c", ["金刚石"] = "c", ["石墨"] = "c",
            ["硫"] = "s", ["硫磺"] = "s",
            ["磷"] = "p", ["红磷"] = "p", ["白磷"] = "p",

            // 常见有机物
            ["甲烷"] = "ch4", ["天然气"] = "ch4", ["沼气"] = "ch4", ["瓦斯"] = "ch4",
            ["乙烷"] = "c2h6",
            ["乙烯"] = "c2h4",
            ["乙炔"] = "c2h2",
            ["丙烷"] = "c3h8",
            ["丁烷"] = "c4h10",
            ["乙醇"] = "c2h5oh", ["酒精"] = "c2h5oh",
            ["甲醇"] = "ch3oh",
            ["乙酸"] = "ch3cooh", ["醋酸"] = "ch3cooh",
            ["甲酸"] = "hcooh", ["蚁酸"] = "hcooh",
            ["乙醛"] = "ch3cho",
            ["甲醛"] = "hcho",
            ["乙醚"] = "c2h5oc2h5",
            ["葡萄糖"] = "c6h12o6",
            ["蔗糖"] = "c12h22o11",
            ["淀粉"] = "(c6h10o5)n",
            ["纤维素"] = "(c6h10o5)n",
            ["苯"] = "c6h6",
            ["甲苯"] = "c7h8",
            ["乙酸乙酯"] = "ch3cooch2ch3",

            // 结晶水合物与矿物俗名
            ["胆矾"] = "cuso4*5h2o", ["蓝矾"] = "cuso4*5h2o", ["五水硫酸铜"] = "cuso4*5h2o",
            ["绿矾"] = "feso4*7h2o", ["七水硫酸亚铁"] = "feso4*7h2o",
            ["明矾"] = "kal(so4)2*12h2o", ["白矾"] = "kal(so4)2*12h2o", ["十二水合硫酸铝钾"] = "kal(so4)2*12h2o",
            ["熟石膏"] = "2caso4*h2o", ["半水硫酸钙"] = "2caso4*h2o", ["(caso4)2*h2o"] = "2caso4*h2o",
            ["生石膏"] = "caso4*2h2o", ["二水硫酸钙"] = "caso4*2h2o",
            ["皓矾"] = "znso4*7h2o",
            ["芒硝"] = "na2so4*10h2o", ["十水合硫酸钠"] = "na2so4*10h2o",
            ["大苏打"] = "na2s2o3", ["海波"] = "na2s2o3", ["硫代硫酸钠"] = "na2s2o3",
            ["重晶石"] = "baso4",
            ["石英"] = "sio2", ["硅石"] = "sio2", ["水晶"] = "sio2",
            ["金刚砂"] = "sic",
            ["萤石"] = "caf2", ["荧石"] = "caf2", ["氟化钙"] = "caf2",
            ["孔雀石"] = "cu2(oh)2co3", ["碱式碳酸铜"] = "cu2(oh)2co3",
            ["菱铁矿"] = "feco3", ["碳酸亚铁"] = "feco3",
            ["黄铁矿"] = "fes2", ["二硫化亚铁"] = "fes2"
        };

        public static readonly Dictionary<string, string> OrganicCondensedMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ch2=ch2"] = "c2h4",
            ["h2c=ch2"] = "c2h4",
            ["ch2ch2"] = "c2h4",
            ["c2h4"] = "c2h4",
            ["ch≡ch"] = "c2h2",
            ["hc≡ch"] = "c2h2",
            ["chch"] = "c2h2",
            ["c2h2"] = "c2h2",
            ["ch3ch2oh"] = "c2h5oh",
            ["c2h5oh"] = "c2h5oh",
            ["c2h6o"] = "c2h5oh",
            ["ch3cooh"] = "ch3cooh",
            ["c2h4o2"] = "ch3cooh",
            ["hac"] = "ch3cooh",
            ["ch3cho"] = "c2h4o",
            ["c2h4o"] = "c2h4o",
            ["hcho"] = "ch2o",
            ["ch2o"] = "ch2o",
            ["hcooh"] = "ch2o2",
            ["ch2o2"] = "ch2o2",
            ["ch3cooch2ch3"] = "ch3cooc2h5",
            ["ch3cooc2h5"] = "ch3cooc2h5",
            ["c4h8o2"] = "ch3cooc2h5",
            ["ch3och3"] = "c2h6o"
        };

        public static bool IsOrganicStructureEquivalent(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            var normA = a.Trim();
            var normB = b.Trim();
            if (string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase)) return true;

            var mapA = OrganicCondensedMap.TryGetValue(normA, out var mA) ? mA : normA;
            var mapB = OrganicCondensedMap.TryGetValue(normB, out var mB) ? mB : normB;
            if (string.Equals(mapA, mapB, StringComparison.OrdinalIgnoreCase)) return true;

            static string? ComputeMolecularFormula(string formula)
            {
                string clean = formula.Replace("=", "").Replace("-", "").Replace("≡", "").Replace("~", "").Replace(" ", "");
                clean = clean.Replace("(", "").Replace(")", "").Replace("[", "").Replace("]", "");
                if (string.IsNullOrWhiteSpace(clean)) return null;

                var matches = System.Text.RegularExpressions.Regex.Matches(clean, @"([A-Z][a-z]?)(\d*)");
                if (matches.Count == 0) return null;

                int matchedLength = matches.Cast<System.Text.RegularExpressions.Match>().Sum(m => m.Length);
                if (matchedLength != clean.Length) return null;

                var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    string element = m.Groups[1].Value;
                    int count = string.IsNullOrEmpty(m.Groups[2].Value) ? 1 : int.Parse(m.Groups[2].Value);
                    counts[element] = counts.GetValueOrDefault(element, 0) + count;
                }

                var sb = new System.Text.StringBuilder();
                if (counts.TryGetValue("C", out int cCount))
                {
                    sb.Append("C").Append(cCount > 1 ? cCount.ToString() : "");
                    counts.Remove("C");
                    if (counts.TryGetValue("H", out int hCount))
                    {
                        sb.Append("H").Append(hCount > 1 ? hCount.ToString() : "");
                        counts.Remove("H");
                    }
                }
                foreach (var kvp in counts)
                {
                    sb.Append(kvp.Key).Append(kvp.Value > 1 ? kvp.Value.ToString() : "");
                }
                return sb.ToString();
            }

            var fA = ComputeMolecularFormula(normA);
            var fB = ComputeMolecularFormula(normB);
            return fA != null && fB != null && string.Equals(fA, fB, StringComparison.OrdinalIgnoreCase);
        }

        public static bool CheckRatioFractionEquivalence(string u, string c)
        {
            if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(c)) return false;
            u = u.Replace("比", ":").Replace(" ", "").ToLowerInvariant();
            c = c.Replace("比", ":").Replace(" ", "").ToLowerInvariant();
            if (u == c) return true;

            // 多项比例: 如 1:2:3 vs 2:4:6
            if (u.Contains(':') && c.Contains(':'))
            {
                var uParts = u.Split(':');
                var cParts = c.Split(':');
                if (uParts.Length == cParts.Length && uParts.Length >= 2)
                {
                    var uVals = new double[uParts.Length];
                    var cVals = new double[cParts.Length];
                    bool valid = true;
                    for (int i = 0; i < uParts.Length; i++)
                    {
                        if (!double.TryParse(uParts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out uVals[i]) ||
                            !double.TryParse(cParts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cVals[i]) ||
                            Math.Abs(cVals[i]) < 1e-9)
                        {
                            valid = false;
                            break;
                        }
                    }
                    if (valid)
                    {
                        double ratio = uVals[0] / cVals[0];
                        if (Math.Abs(ratio) > 1e-9 && uVals.Select((v, idx) => Math.Abs((v / cVals[idx]) - ratio)).All(diff => diff < 1e-6))
                        {
                            return true;
                        }
                    }
                }
            }

            // 单项比值与分数互转: 如 3:4 vs 3/4, 3:4 vs 0.75
            static bool TryParseRatioOrFraction(string s, out double val)
            {
                val = 0;
                var parts = s.Split(new[] { ':', '/' });
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num) &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var den) &&
                    Math.Abs(den) > 1e-9)
                {
                    val = num / den;
                    return true;
                }
                return false;
            }

            if (TryParseRatioOrFraction(u, out var vU) && TryParseRatioOrFraction(c, out var vC))
            {
                if (Math.Abs(vU - vC) < 1e-6) return true;
            }
            if (TryParseRatioOrFraction(u, out var vU2) && double.TryParse(c, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var decC))
            {
                if (Math.Abs(vU2 - decC) < 1e-6) return true;
            }
            if (TryParseRatioOrFraction(c, out var vC2) && double.TryParse(u, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var decU))
            {
                if (Math.Abs(vC2 - decU) < 1e-6) return true;
            }

            return false;
        }

        public static string NormalizeElectrochemicalReaction(string eq)
        {
            if (string.IsNullOrWhiteSpace(eq)) return string.Empty;
            string s = eq;
            // 剥离上标反应条件: \stackrel{...}{=} / \overset{...}{=} / \xlongequal{...} / \xrightleftharpoons{...}
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:stackrel|overset)\s*\{[^}]*\}\s*\{?=?\}?", "=");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:xlongequal|xrightleftharpoons|xrightarrow)(?:\[[^\]]*\]|\{[^}]*\})*", "=");
            s = s.Replace("\\rightleftharpoons", "=")
                 .Replace("\\longrightarrow", "=")
                 .Replace("\\rightarrow", "=")
                 .Replace("⇌", "=")
                 .Replace("⇄", "=")
                 .Replace("<->", "=")
                 .Replace("↔", "=")
                 .Replace("==", "=")
                 .Replace("->", "=")
                 .Replace("<=>", "=");
            var parts = s.Split('=', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return s;

            var lhs = parts[0].Trim();
            var rhs = parts[1].Trim();

            // 匹配负项移项: 例如 "Zn - 2e- = Zn2+" 或 "2Cl- - 2e- = Cl2"
            // 移项规则: LHS 的减项移至 RHS 变为加项；RHS 的减项移至 LHS 变为加项
            static (string side, List<string> movedTerms) ExtractSubtractedTerms(string side)
            {
                var moved = new List<string>();
                var electronPattern = @"(?<=[a-zA-Z0-9\)\]\}\+-])\s*-\s*(\d*\s*(?:e\^?-?|e\^\{-[0-9]*\}|e|个?电子))(?=[^a-zA-Z0-9]|$)";
                var matches = System.Text.RegularExpressions.Regex.Matches(side, electronPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (matches.Count > 0)
                {
                    foreach (System.Text.RegularExpressions.Match m in matches)
                    {
                        var term = m.Groups[1].Value.Replace("个", "").Replace("电子", "e-").Replace(" ", "");
                        if (term == "e") term = "e-";
                        moved.Add(term);
                    }
                    side = System.Text.RegularExpressions.Regex.Replace(side, electronPattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                return (side.Trim(), moved);
            }

            var (newLhs, fromLhs) = ExtractSubtractedTerms(lhs);
            var (newRhs, fromRhs) = ExtractSubtractedTerms(rhs);

            if (fromLhs.Count > 0)
            {
                foreach (var t in fromLhs)
                {
                    newRhs = $"{newRhs} + {t}";
                }
            }
            if (fromRhs.Count > 0)
            {
                foreach (var t in fromRhs)
                {
                    newLhs = $"{newLhs} + {t}";
                }
            }

            return $"{newLhs} = {newRhs}";
        }

        public static bool IsChemicalNomenclatureEquivalent(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            var normA = a.Trim().ToLowerInvariant();
            var normB = b.Trim().ToLowerInvariant();
            if (normA == normB) return true;

            static string CleanChem(string s)
            {
                s = s.Replace("↑", "").Replace("↓", "").Replace("\\uparrow", "").Replace("\\downarrow", "");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"[\(（](?:s|l|g|aq|固|液|气|水)[\)）]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"_\{?(\d+)\}?", "$1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\^\{?(\d*[+-])\}?", "$1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\^([0-9]*[+-])", "$1");
                s = s.Replace("·", "*").Replace("×", "*").Replace("•", "*");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[a-zA-Z0-9\)])\.(?=\d+[a-zA-Z])", "*");
                return s.ToLowerInvariant().Trim();
            }

            var cleanNormA = CleanChem(normA);
            var cleanNormB = CleanChem(normB);

            var formulaA = ChemicalSynonymMap.TryGetValue(normA, out var fA) ? fA : (ChemicalSynonymMap.TryGetValue(cleanNormA, out var fA2) ? fA2 : cleanNormA);
            var formulaB = ChemicalSynonymMap.TryGetValue(normB, out var fB) ? fB : (ChemicalSynonymMap.TryGetValue(cleanNormB, out var fB2) ? fB2 : cleanNormB);

            return CleanChem(formulaA) == CleanChem(formulaB);
        }

        // 初高中代数因式分解乘积因子交换律等价匹配 (如 (x+1)(x-1) 与 (x-1)(x+1), 2(x+3)(x-4) 与 2(x-4)(x+3), -(x-1)(x-2) 与 -(x-2)(x-1), x(x-2) 与 (x-2)x)
        public static bool CheckPolynomialFactorProductCommutativeMatch(string u, string c)
        {
            if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(c)) return false;
            if (u.Equals(c, StringComparison.OrdinalIgnoreCase)) return true;

            if (!TryParsePolynomialFactorProduct(u, out var uScalar, out var uFactors) ||
                !TryParsePolynomialFactorProduct(c, out var cScalar, out var cFactors))
            {
                return false;
            }

            if (Math.Abs(uScalar - cScalar) > 1e-6) return false;
            if (uFactors.Count != cFactors.Count) return false;

            var remainingC = new List<string>(cFactors);
            foreach (var uf in uFactors)
            {
                int matchedIdx = remainingC.FindIndex(cf => ArePolynomialFactorsEquivalent(uf, cf));
                if (matchedIdx >= 0)
                {
                    remainingC.RemoveAt(matchedIdx);
                }
                else
                {
                    return false;
                }
            }

            return remainingC.Count == 0;
        }

        public static bool TryParsePolynomialFactorProduct(string s, out double scalarCoeff, out List<string> factors)
        {
            scalarCoeff = 1.0;
            factors = new List<string>();
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Replace(" ", "");

            // 排除方程、不等式、区间与集合
            if (s.Contains('=') || s.Contains('<') || s.Contains('>') || s.Contains(',') || s.Contains(';') ||
                s.StartsWith("[") || s.StartsWith("{"))
            {
                return false;
            }

            int idx = 0;
            // 提取可选前导符号或纯数标量系数 (如 "-", "+", "2", "-3", "1/2", "0.5")
            var scalarMatch = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?(?:(?:\d+(?:\.\d+)?(?:/\d+(?:\.\d+)?)?)|\d+))\s*\*?\s*(?=[a-zA-Z\(])");
            if (scalarMatch.Success)
            {
                string rawScalar = scalarMatch.Groups[1].Value.Trim();
                if (rawScalar == "+" || string.IsNullOrEmpty(rawScalar)) scalarCoeff = 1.0;
                else if (rawScalar == "-") scalarCoeff = -1.0;
                else
                {
                    if (rawScalar.Contains('/'))
                    {
                        var fParts = rawScalar.Split('/');
                        if (double.TryParse(fParts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n) &&
                            double.TryParse(fParts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) && Math.Abs(d) > 1e-9)
                        {
                            scalarCoeff = n / d;
                        }
                        else return false;
                    }
                    else if (double.TryParse(rawScalar, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedScalar))
                    {
                        scalarCoeff = parsedScalar;
                    }
                    else return false;
                }
                idx = scalarMatch.Length;
            }
            else if (s.StartsWith("-") && s.Length > 1 && (s[1] == '(' || char.IsLetter(s[1])))
            {
                scalarCoeff = -1.0;
                idx = 1;
            }
            else if (s.StartsWith("+") && s.Length > 1 && (s[1] == '(' || char.IsLetter(s[1])))
            {
                scalarCoeff = 1.0;
                idx = 1;
            }

            string remainder = s.Substring(idx).Trim();
            if (string.IsNullOrWhiteSpace(remainder)) return false;

            int pos = 0;
            while (pos < remainder.Length)
            {
                while (pos < remainder.Length && (remainder[pos] == '*' || char.IsWhiteSpace(remainder[pos]))) pos++;
                if (pos >= remainder.Length) break;

                if (remainder[pos] == '(')
                {
                    int depth = 0;
                    int startP = pos;
                    bool closed = false;
                    while (pos < remainder.Length)
                    {
                        if (remainder[pos] == '(') depth++;
                        else if (remainder[pos] == ')')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                pos++;
                                closed = true;
                                break;
                            }
                        }
                        pos++;
                    }
                    if (!closed) return false;

                    // 检查指数幂次: 如 ^2, ^{2}, ^(2)
                    if (pos < remainder.Length && remainder[pos] == '^')
                    {
                        pos++;
                        if (pos < remainder.Length && (remainder[pos] == '{' || remainder[pos] == '('))
                        {
                            char closeChar = remainder[pos] == '{' ? '}' : ')';
                            pos++;
                            while (pos < remainder.Length && remainder[pos] != closeChar) pos++;
                            if (pos < remainder.Length) pos++;
                        }
                        else
                        {
                            while (pos < remainder.Length && char.IsDigit(remainder[pos])) pos++;
                        }
                    }
                    string factorStr = remainder.Substring(startP, pos - startP).Trim();
                    factors.Add(factorStr);
                }
                else if (char.IsLetter(remainder[pos]))
                {
                    int startV = pos;
                    while (pos < remainder.Length && (char.IsLetterOrDigit(remainder[pos]) || remainder[pos] == '_')) pos++;
                    if (pos < remainder.Length && remainder[pos] == '^')
                    {
                        pos++;
                        if (pos < remainder.Length && (remainder[pos] == '{' || remainder[pos] == '('))
                        {
                            char closeChar = remainder[pos] == '{' ? '}' : ')';
                            pos++;
                            while (pos < remainder.Length && remainder[pos] != closeChar) pos++;
                            if (pos < remainder.Length) pos++;
                        }
                        else
                        {
                            while (pos < remainder.Length && char.IsDigit(remainder[pos])) pos++;
                        }
                    }
                    string varFactor = remainder.Substring(startV, pos - startV).Trim();
                    factors.Add(varFactor);
                }
                else
                {
                    return false;
                }
            }

            return factors.Count >= 2 || (factors.Count == 1 && (Math.Abs(scalarCoeff - 1.0) > 1e-9 || s.StartsWith("-") || s.StartsWith("+")));
        }

        public static bool ArePolynomialFactorsEquivalent(string f1, string f2)
        {
            if (string.Equals(f1, f2, StringComparison.OrdinalIgnoreCase)) return true;

            // 检查幂次是否相同: 如 (x+1)^2 vs (1+x)^2
            string base1 = f1;
            string base2 = f2;
            string exp1 = "1";
            string exp2 = "1";

            var m1 = System.Text.RegularExpressions.Regex.Match(f1, @"^(.+?)\^[\{\(]?([0-9]+)[\}\)]?$");
            if (m1.Success)
            {
                base1 = m1.Groups[1].Value;
                exp1 = m1.Groups[2].Value;
            }
            var m2 = System.Text.RegularExpressions.Regex.Match(f2, @"^(.+?)\^[\{\(]?([0-9]+)[\}\)]?$");
            if (m2.Success)
            {
                base2 = m2.Groups[1].Value;
                exp2 = m2.Groups[2].Value;
            }

            if (exp1 != exp2) return false;

            // 剥离外层括号
            if (base1.StartsWith("(") && base1.EndsWith(")")) base1 = base1.Substring(1, base1.Length - 2);
            if (base2.StartsWith("(") && base2.EndsWith(")")) base2 = base2.Substring(1, base2.Length - 2);

            if (string.Equals(base1, base2, StringComparison.OrdinalIgnoreCase)) return true;

            // 检查加法项交换律: 如 x+1 vs 1+x, 2x-3 vs -3+2x
            return CheckPolynomialAdditiveTermsEquivalent(base1, base2);
        }

        public static bool CheckPolynomialAdditiveTermsEquivalent(string expr1, string expr2)
        {
            static List<string> SplitAdditiveTerms(string expr)
            {
                var terms = new List<string>();
                expr = expr.Trim().Replace(" ", "");
                if (string.IsNullOrEmpty(expr)) return terms;

                int start = 0;
                int depth = 0;
                for (int i = 0; i < expr.Length; i++)
                {
                    if (expr[i] == '(') depth++;
                    else if (expr[i] == ')') depth--;
                    else if (depth == 0 && (expr[i] == '+' || expr[i] == '-') && i > start)
                    {
                        var t = expr.Substring(start, i - start).Trim();
                        if (!string.IsNullOrEmpty(t)) terms.Add(t.StartsWith("+") ? t.Substring(1) : t);
                        start = i;
                    }
                }
                if (start < expr.Length)
                {
                    var t = expr.Substring(start).Trim();
                    if (!string.IsNullOrEmpty(t)) terms.Add(t.StartsWith("+") ? t.Substring(1) : t);
                }
                return terms.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            }

            var terms1 = SplitAdditiveTerms(expr1);
            var terms2 = SplitAdditiveTerms(expr2);
            if (terms1.Count == 0 || terms2.Count == 0 || terms1.Count != terms2.Count) return false;

            return terms1.SequenceEqual(terms2, StringComparer.OrdinalIgnoreCase);
        }

        public static bool CheckFillInBlankMatch(string user, string correct)
        {
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(correct)) return false;
            if (string.Equals(user, correct, StringComparison.OrdinalIgnoreCase)) return true;

            // 0. 判断题/真假判定双向归一 (如 "对" vs "正确" vs "√" vs "v" vs "True" vs "是" vs "right"; "错" vs "错误" vs "×" vs "x" vs "False" vs "否" vs "wrong")
            if (TryNormalizeJudgement(user, out var uJudg) && TryNormalizeJudgement(correct, out var cJudg))
            {
                return uJudg == cJudg;
            }

            static string Normalize(string s)
            {
                // 零宽不可见字符与非断行空格消除
                s = s.Replace("\u200B", "").Replace("\uFEFF", "").Replace("\u200C", "").Replace("\u200D", "").Replace("\u00A0", " ");
                s = s.Trim().Trim('$', '￥');
                s = s.TrimEnd('。', '；', ';');
                if (s.EndsWith(".") && !s.EndsWith(".."))
                {
                    s = s.Substring(0, s.Length - 1).Trim();
                }
                // Unicode 负号与减号归一
                s = s.Replace("−", "-").Replace("－", "-");
                // 英语常用缩略语归一 (人称代词、助动词、否定式等)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bcan't\b", "cannot", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bwon't\b", "will not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bdon't\b", "do not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bdoesn't\b", "does not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bdidn't\b", "did not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bisn't\b", "is not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\baren't\b", "are not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bwasn't\b", "was not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bweren't\b", "were not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bcouldn't\b", "could not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bshouldn't\b", "should not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bwouldn't\b", "would not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bmustn't\b", "must not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bhaven't\b", "have not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bhasn't\b", "has not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bhadn't\b", "had not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bI'm\b", "I am", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\byou're\b", "you are", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bwe're\b", "we are", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bthey're\b", "they are", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bthat's\b", "that is", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bwhat's\b", "what is", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bthere's\b", "there is", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bhere's\b", "here is", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bit's\b", "it is", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bI'll\b", "I will", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\byou'll\b", "you will", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bwe'll\b", "we will", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bthey'll\b", "they will", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bhe'll\b", "he will", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bshe'll\b", "she will", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bI've\b", "I have", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\byou've\b", "you have", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bwe've\b", "we have", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bthey've\b", "they have", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\blet's\b", "let us", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bwho's\b", "who is", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bwhere's\b", "where is", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bhow's\b", "how is", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bwhen's\b", "when is", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bwhy's\b", "why is", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\bshan't\b", "shall not", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // 省略前导零的小数补齐: 如 .5 -> 0.5, -.75 -> -0.75, +.25 -> +0.25, x = .5 -> x = 0.5
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=^|[^\d\.])\.(\d+)", "0.$1");
                // 全角数字转换为半角数字
                for (int i = 0; i < 10; i++)
                {
                    s = s.Replace((char)('０' + i), (char)('0' + i));
                }
                // Unicode 下标数字与正负号转换为标准字符 (₀₁₂₃₄₅₆₇₈₉ ₊ ₋)
                s = s.Replace('₀', '0').Replace('₁', '1').Replace('₂', '2').Replace('₃', '3').Replace('₄', '4')
                     .Replace('₅', '5').Replace('₆', '6').Replace('₇', '7').Replace('₈', '8').Replace('₉', '9')
                     .Replace('₊', '+').Replace('₋', '-');

                // Unicode 上标数字与正负号转换为 ^... 指数与电荷 (⁰¹²³⁴⁵⁶⁷⁸⁹ ⁺ ⁻)
                s = s.Replace("³⁺", "^3+").Replace("²⁺", "^2+").Replace("⁴⁺", "^4+")
                     .Replace("³⁻", "^3-").Replace("²⁻", "^2-").Replace("⁴⁻", "^4-")
                     .Replace("⁺", "^+").Replace("⁻", "^-");
                s = s.Replace("⁰", "^0").Replace("¹", "^1").Replace("²", "^2").Replace("³", "^3")
                     .Replace("⁴", "^4").Replace("⁵", "^5").Replace("⁶", "^6").Replace("⁷", "^7")
                     .Replace("⁸", "^8").Replace("⁹", "^9");
                s = s.Replace("^^", "^");

                s = s.Replace("（", "(").Replace("）", ")").Replace("，", ",").Replace("：", ":");
                // 全角方括号、花括号与六角括号归一 (如 【1, 2】 -> [1, 2], ［1, 2］ -> [1, 2], ｛1, 2｝ -> {1, 2})
                s = s.Replace("【", "[").Replace("】", "]")
                     .Replace("［", "[").Replace("］", "]")
                     .Replace("｛", "{").Replace("｝", "}")
                     .Replace("〖", "[").Replace("〗", "]");

                // 中文教材分号区间与点坐标智能对齐 (如 [-1; 2] -> [-1, 2], (3; 4) -> (3, 4))
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[\[\(][^\]\)]*?)[;；](?=[^\[\)]*?[\]\)])", ",");
                // 全角数学运算符与符号归一
                s = s.Replace("％", "%").Replace("＋", "+").Replace("－", "-").Replace("＊", "*").Replace("／", "/").Replace("＝", "=");
                // LaTeX 百分比转义符解构
                s = s.Replace("\\%", "%");
                // 范围连接符 ~ (如 2~5, -3~7) 在消除 LaTeX 空格 ~ 前先行保护归一为 "到"
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[+-]?\d+(?:\.\d+)?)\s*~\s*(?=[+-]?\d)", "到");
                // LaTeX 空白与间距符消除
                s = s.Replace("\\,", " ").Replace("\\;", " ").Replace("\\:", " ").Replace("\\quad", " ").Replace("\\qquad", " ").Replace("\\enspace", " ").Replace("~", " ");
                // LaTeX 常用定界符与范数/模长解构 (\left[, \right], \left|, \right|, \left\{, \right\}, \|, \Vert, \lVert, \rVert, ‖)
                s = s.Replace("\\left(", "(").Replace("\\right)", ")")
                     .Replace("\\left[", "[").Replace("\\right]", "]")
                     .Replace("\\left\\{", "{").Replace("\\right\\}", "}")
                     .Replace("\\left\\|", "|").Replace("\\right\\|", "|")
                     .Replace("\\left|", "|").Replace("\\right|", "|")
                     .Replace("\\left.", "").Replace("\\right.", "")
                     .Replace("\\vert", "|")
                     .Replace("\\|", "|").Replace("\\Vert", "|").Replace("\\lVert", "|").Replace("\\rVert", "|").Replace("‖", "|")
                     .Replace("$", "");
                // 范数与双竖线模长解构: ||a|| -> |a|
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\|\|\s*([^|]+?)\s*\|\|", "|$1|");
                // LaTeX 矩阵列向量解构 (支持 \begin{pmatrix} a \\ b \end{pmatrix}, \begin{bmatrix} a \\ b \end{bmatrix}, 3维列向量等映射为标准坐标 (a, b) / (a, b, c))
                s = System.Text.RegularExpressions.Regex.Replace(s,
                    @"\\begin\{(?:pmatrix|bmatrix|vmatrix|matrix)\}\s*([^\\]+?)\s*\\\\\s*([^\\]+?)(?:\s*\\\\\s*([^\\]+?))?\s*\\end\{(?:pmatrix|bmatrix|vmatrix|matrix)\}",
                    m =>
                    {
                        var v1 = m.Groups[1].Value.Trim();
                        var v2 = m.Groups[2].Value.Trim();
                        if (m.Groups[3].Success && !string.IsNullOrWhiteSpace(m.Groups[3].Value))
                        {
                            var v3 = m.Groups[3].Value.Trim();
                            return $"({v1},{v2},{v3})";
                        }
                        return $"({v1},{v2})";
                    }, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                // 几何弧记号优先规范化 (支持 \overset{\frown}{AB}, \widehat{AB}, \overgroup{AB}, \overarc{AB} -> 弧AB)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:overset\{\\frown\}|widehat|overgroup|overarc)\s*\{([^}]+)\}", "弧$1");
                // 化学反应扩展箭头与反应条件优先解构 (支持 \xrightarrow[\Delta]{MnO2}, \xrightarrow{加热}, \xlongequal, \xrightleftharpoons, \stackrel{点燃}{=}, \overset{...}{=} 等)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:stackrel|overset)\s*\{[^}]*\}\s*(?:\{=+\}|={1,2})", "=");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:xrightarrow|xlongequal|xrightleftharpoons)(?:\[[^\]]*\]|\{[^}]*\})*", "->");
                s = s.Replace("\\rightleftharpoons", "=").Replace("\\longrightarrow", "->").Replace("⇌", "=").Replace("⇄", "=");
                // 剥离气体与沉淀箭头 (支持 ↑, ↓, \uparrow, \downarrow)
                s = s.Replace("↑", "").Replace("↓", "").Replace("\\uparrow", "").Replace("\\downarrow", "");
                // 剥离化学物态标注: (s), (l), (g), (aq), (固), (液), (气), (水)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"[\(（](?:s|l|g|aq|固|液|气|水)[\)）]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                // LaTeX 根号解构 (支持 \sqrt{x} 与无大括号 \sqrt2 映射为 sqrt(x), 三次方根与任意次方根 \sqrt[n]{x} 映射为 root(n,x))
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\cbrt\{\s*([^}]+?)\s*\}", "root(3,$1)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\sqrt\[(\d+)\]\{\s*([^}]+?)\s*\}", "root($1,$2)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\sqrt\{\s*([^}]+?)\s*\}", "sqrt($1)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\sqrt\s*(\d)", "sqrt($1)");

                // LaTeX 分数全面解构 (支持标准 \frac 以及中高考极常用的 \dfrac, \tfrac；若分子/分母含加减复合项，自动外包圆括号保持代数结构一致)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:frac|dfrac|tfrac)\s*\{([^}]+)\}\s*\{([^}]+)\}", m =>
                {
                    var num = m.Groups[1].Value.Trim();
                    var den = m.Groups[2].Value.Trim();
                    if ((num.Contains('+') || num.Contains('-') || num.Contains('±')) && !num.StartsWith("(") && !num.EndsWith(")"))
                    {
                        num = $"({num})";
                    }
                    if ((den.Contains('+') || den.Contains('-') || den.Contains('±') || den.Contains('*') || den.Contains('/')) && !den.StartsWith("(") && !den.EndsWith(")"))
                    {
                        den = $"({den})";
                    }
                    return $"{num}/{den}";
                });

                // 隐式乘法补全：系数紧跟根号或 pi (如 2sqrt(3) -> 2*sqrt(3), 2\pi -> 2*pi)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=\d)\s*sqrt\(", "*sqrt(");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=\d)\s*(\\pi|π|pi)\b", "*pi");
                // 根号有理化与等价分母规范化: 1/sqrt(2) -> sqrt(2)/2, 1/sqrt(3) -> sqrt(3)/3, 2/sqrt(2) -> sqrt(2), 3/sqrt(3) -> sqrt(3)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![a-zA-Z0-9])1\s*/\s*sqrt\((\d+)\)", "sqrt($1)/$1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![a-zA-Z0-9])(\d+)\s*/\s*sqrt\(\1\)", "sqrt($1)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![a-zA-Z0-9])-\s*1\s*/\s*sqrt\((\d+)\)", "-sqrt($1)/$1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![a-zA-Z0-9])-\s*(\d+)\s*/\s*sqrt\(\1\)", "-sqrt($1)");
                // 复数共轭解构: \overline{a+bi} / \bar{a+bi} -> a-bi, \overline{a-bi} -> a+bi
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:overline|bar)\s*\{([^}]+)\}", m =>
                {
                    var inner = m.Groups[1].Value.Trim();
                    if (inner.Contains("i"))
                    {
                        if (inner.Contains("+")) return inner.Replace("+", "-");
                        if (inner.Contains("-")) return inner.Replace("-", "+");
                    }
                    return inner;
                });
                // 虚数单位幂次解构: i^4 -> 1, i^3 -> -i, i^2 -> -1, i^1 -> i, i^0 -> 1
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![a-zA-Z])i\^4\b", "1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![a-zA-Z])i\^3\b", "-i");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![a-zA-Z])i\^2\b", "-1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![a-zA-Z])i\^1\b", "i");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![a-zA-Z])i\^0\b", "1");

                // 向量与线段标记解构: \vec{a} -> a, \overrightarrow{AB} -> AB, \overline{AB} -> AB, \vec a -> a, 向量AB -> AB, AB^\rightarrow -> AB
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:vec|overrightarrow|overline)\s*\{([^}]+)\}", "$1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:vec|overrightarrow|overline)\s+([a-zA-Z])\b", "$1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=^|[^a-zA-Z0-9\u4e00-\u9fa5])向量\s*([a-zA-Z]+)", "$1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"([a-zA-Z]+)\^\\rightarrow|([a-zA-Z]+)\^\{\\rightarrow\}", "$1$2");
                // 粗体/黑板粗体数学符号解构: \mathbb{R} -> R, \mathbf{a} -> a, \boldsymbol{a} -> a, \bm{a} -> a
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:mathbb|mathbf|boldsymbol|bm|mathrm|mathit)\s*\{([^}]+)\}", "$1");
                // 反三角函数与高阶三角记法预解构 (在负幂展开前处理，防止 \sin^{-1} 误解构为 1/sin 或 csc)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\?(?:arcsin|asin)\b|\\?sin\^\{\s*-1\s*\}|\\?sin\^-1\b", "arcsin", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\?(?:arccos|acos)\b|\\?cos\^\{\s*-1\s*\}|\\?cos\^-1\b", "arccos", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\?(?:arctan|atan)\b|\\?tan\^\{\s*-1\s*\}|\\?tan\^-1\b", "arctan", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\?(?:arccot|acot)\b|\\?cot\^\{\s*-1\s*\}|\\?cot\^-1\b", "arccot", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // LaTeX 指数花括号规范化: x^{2} -> x^2, 10^{-3} -> 10^-3
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\^\{([^}]+)\}", "^$1");
                // 负幂与分数指数幂规范化: x^-1 / x^(-1) -> 1/x, x^-2 / x^(-2) -> 1/x^2, x^(1/2) -> sqrt(x), x^(1/3) -> root(3,x)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\b([a-zA-Z0-9]+)\^(?:\(-1\)|-1(?!\d))", "1/$1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\b([a-zA-Z0-9]+)\^(?:\(-2\)|-2(?!\d))", "1/$1^2");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\b([a-zA-Z0-9]+)\^(?:\(1/2\)|1/2(?!\d))", "sqrt($1)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\b([a-zA-Z0-9]+)\^(?:\(1/3\)|1/3(?!\d))", "root(3,$1)");
                // 乘号接倒数归一为除号 (如理化复合单位与代数表达式: m * 1/s -> m/s, kJ * 1/mol -> kJ/mol, 2mol·1/L -> 2mol/L, 9.8 m*1/s^2 -> 9.8 m/s^2)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*[\*·]\s*1\s*/\s*", "/");
                // 自然底数指数函数: e^x -> exp(x), e^(2x) -> exp(2x) (仅当指数含字母变量时转换，保留 e^2, e^3 常数幂给对数求值)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\be\^([a-zA-Z][a-zA-Z0-9]*)\b", "exp($1)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\be\^\(([a-zA-Z0-9]*[a-zA-Z][a-zA-Z0-9]*)\)", "exp($1)");

                // 中苏传统三角记法归一: \tg -> tan, tg -> tan, \ctg -> cot, ctg -> cot (需先替换 ctg 防止 tg 误伤 ctg)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![a-zA-Z])\\?ctg\b", "cot");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![a-zA-Z])\\?tg\b", "tan");
                // 三角函数与对数函数前缀反斜杠剥离与自适应空白 (如 \cos\theta -> cos theta, \sin x -> sin x, \ln 2 -> ln 2, \log_2 8 -> log_2 8)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(sin|cos|tan|cot|sec|csc|arcsin|arccos|arctan|ln|lg|log|exp)(?=[a-zA-Z\\(])", "$1 ");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(sin|cos|tan|cot|sec|csc|arcsin|arccos|arctan|ln|lg|log|exp)(?=[^a-zA-Z]|$)", "$1");
                // 倒数三角等价记法解构: 1/tan x -> cot x, 1/cot x -> tan x, 1/cos x -> sec x, 1/sin x -> csc x
                s = System.Text.RegularExpressions.Regex.Replace(s, @"1\s*/\s*tan\s*(?:\(\s*([a-zA-Z0-9]+)\s*\)|([a-zA-Z0-9]+))", "cot($1$2)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"1\s*/\s*cot\s*(?:\(\s*([a-zA-Z0-9]+)\s*\)|([a-zA-Z0-9]+))", "tan($1$2)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"1\s*/\s*cos\s*(?:\(\s*([a-zA-Z0-9]+)\s*\)|([a-zA-Z0-9]+))", "sec($1$2)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"1\s*/\s*sin\s*(?:\(\s*([a-zA-Z0-9]+)\s*\)|([a-zA-Z0-9]+))", "csc($1$2)");
                // 对数底数与真数 LaTeX 格式规范化:
                // 1. 特殊对数底数映射: \log_e -> ln, \log_{10} -> lg
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\?log_\{?e\}?\s*", "ln ");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\?log_\{?10\}?\s*", "lg ");
                // 2. 通用对数: \log_{2}{x} / \log_2{x} / \log_2(x) / \log_2 x / \log_2 1/4 / \log_2 sqrt(2) -> log(2,x)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\?log_\{?([a-zA-Z0-9\.\/]+)\}?\s*\{([^}]+)\}", "log($1,$2)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\?log_\{?([a-zA-Z0-9\.\/]+)\}?\s*\(\s*(.+?)\s*\)", "log($1,$2)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\?log_\{?([a-zA-Z0-9\.\/]+)\}?\s+(sqrt\([^)]+\)|[a-zA-Z0-9\.\/]+)", "log($1,$2)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\blog\s*\(\s*([a-zA-Z0-9\.\/]+)\s*,\s*(.+?)\s*\)", "log($1,$2)");
                // 3. lg 与 ln 花括号规范化
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\?lg\s*\{([a-zA-Z0-9\+\-\*\/\^]+)\}", "lg($1)");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\?ln\s*\{([a-zA-Z0-9\+\-\*\/\^]+)\}", "ln($1)");
                // 4. 三角与对数函数单项括号等价规范: 如 sin(x) -> sin x, ln(2) -> ln 2, lg(x) -> lg x, arcsin(x) -> arcsin x
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\b(sin|cos|tan|cot|sec|csc|arcsin|arccos|arctan|arccot|asin|acos|atan|acot|ln|lg|exp)\s*\(\s*([a-zA-Z0-9]+)\s*\)", "$1 $2");
                // 常用希腊字母与物理常数反斜杠剥离与统一转录: \theta / θ -> theta, \eta / η -> eta, \nu / ν -> nu, \Delta / Δ -> delta 等
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(theta|alpha|beta|gamma|lambda|mu|rho|omega|phi|sigma|delta|tau|eta|nu|epsilon|Delta|Omega|Phi)\b", "$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = s.Replace("θ", "theta").Replace("α", "alpha").Replace("β", "beta").Replace("γ", "gamma")
                     .Replace("λ", "lambda").Replace("μ", "mu").Replace("ρ", "rho").Replace("ω", "omega")
                     .Replace("φ", "phi").Replace("ϕ", "phi").Replace("σ", "sigma").Replace("δ", "delta")
                     .Replace("Δ", "delta").Replace("τ", "tau").Replace("η", "eta").Replace("ν", "nu")
                     .Replace("ε", "epsilon").Replace("Ω", "omega").Replace("Φ", "phi");
                // 希腊字母与理科物理量变量名间的冗余空白消除 (如 \Delta E -> deltaE 等价于 ΔE; \omega t -> omegat 等价于 ωt)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\b(theta|alpha|beta|gamma|lambda|mu|rho|omega|phi|sigma|delta|tau|eta|nu|epsilon)\s+(?=[a-zA-Z0-9])", "$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                // 摄氏度与热力学温度规范化 (在剥离角度"度"前先行保护归一)
                s = s.Replace("摄氏度", "℃").Replace("摄氏", "℃");
                // 弧度单位保护 (防止"度"字被剥离)
                s = s.Replace("弧度/秒", "rad/s").Replace("弧度每秒", "rad/s").Replace("弧度", "rad");
                // 角度与度数符号等价规范: ^\circ, °, 度, \circ, \text{°}
                s = s.Replace("^\\circ", "").Replace("^{\\circ}", "").Replace("\\text{°}", "").Replace("°", "").Replace("度", "").Replace("\\circ", "");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=\d)\s*(?:deg|度|°|\^\{\\circ\}|\^\\circ|\\circ)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                // 圆周率符号与角度几何符号归一
                s = s.Replace("\\pi", "pi").Replace("π", "pi");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?:\\angle|∠)\s*", "∠");
                 s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=^|[^a-zA-Z0-9\u4e00-\u9fa5])角\s*([A-Za-z0-9]+)", "∠$1");
                // 几何弧记号规范化: \overset{\frown}{AB}, \widehat{AB}, \overgroup{AB}, 弧AB -> 弧AB
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:overset\{\\frown\}|widehat|overgroup)\s*\{([^}]+)\}", "弧$1");
                // 几何平行与垂直符号归一
                s = s.Replace("\\parallel", "//").Replace("平行于", "//").Replace("平行", "//").Replace("∥", "//");
                s = s.Replace("\\perp", "⊥").Replace("\\bot", "⊥").Replace("垂直于", "⊥").Replace("垂直", "⊥");
                // 几何全等与相似符号归一
                s = s.Replace("\\triangle", "△").Replace("三角形", "△");
                s = s.Replace("\\cong", "≌").Replace("全等于", "≌").Replace("全等", "≌");
                s = s.Replace("\\sim", "∽").Replace("相似于", "∽");
                // 集合包含关系与补集符号归一 (真包含于必须排在包含于之前以防词缀短截)
                s = s.Replace("\\subsetneqq", "⫋").Replace("\\subsetneq", "⫋").Replace("真包含于", "⫋").Replace("真包含", "⫋");
                s = s.Replace("\\subseteq", "⊆").Replace("包含于", "⊆").Replace("包含", "⊆");
                s = s.Replace("\\subset", "⊂");
                s = s.Replace("\\complement_U", "∁").Replace("\\complement", "∁").Replace("∁_U", "∁").Replace("C_U", "∁").Replace("补集", "∁");
                // LaTeX 运算符与关系符解构
                s = s.Replace("\\times", "*").Replace("\\cdot", "*").Replace("\\div", "/").Replace("×", "*").Replace("·", "*").Replace("•", "*").Replace("∙", "*");
                s = s.Replace("\\leq", "<=").Replace("\\le", "<=").Replace("\\geq", ">=").Replace("\\ge", ">=");
                s = s.Replace("≤", "<=").Replace("≥", ">=");
                s = s.Replace("\\neq", "!=").Replace("≠", "!=").Replace("≈", "~");
                s = s.Replace("\\pm", "+-").Replace("±", "+-");
                s = s.Replace("\\{", "{").Replace("\\}", "}").Replace("\\mid", "|");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\+-\s*", "+-");
                // 集合构建符号 {x | x > 0} 与 {x : x > 0} 冒号/竖线统一规范化
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=\{[^{}]+?)\s*[:：]\s*(?=[^{}]+?\})", "|");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\|\s*", "|");
                // 比例与比值符号规范化 (如 3:4, 3 : 4, 3：4, 3比4, 1比2比3)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=\d)\s*比\s*(?=\d)", ":");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=\d)\s*[:：]\s*(?=\d)", ":");
                // 数学集合与区间常用无穷、空集等价规范 (容错符号与无穷符号之间的间距，如 - \infty -> -inf)
                s = s.Replace("正无穷", "+inf").Replace("负无穷", "-inf").Replace("无穷", "inf");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"([+\-])\s*\\infty", "$1inf");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"([+\-])\s*∞", "$1inf");
                s = s.Replace("+\\infty", "+inf").Replace("-\\infty", "-inf").Replace("\\infty", "inf");
                s = s.Replace("+∞", "+inf").Replace("-∞", "-inf").Replace("∞", "inf");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[\(\[,])\s*inf\b", "+inf");
                s = s.Replace("\\emptyset", "∅").Replace("\\varnothing", "∅").Replace("\\empty", "∅").Replace("空集", "∅").Replace("{}", "∅").Replace("Ø", "∅").Replace("ø", "∅");
                s = s.Replace("\\cup", "u").Replace("∪", "u").Replace("\\cap", "∩").Replace("交集", "∩");
                // 区间与集合交并集连词解构 (如 (-inf, 1] U [3, +inf), (-inf, 1]并[3, +inf), (-inf, 1]或[3, +inf), [1, 2] 与 [3, 4])
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[\]\)\}])\s*(?:\\cup|∪|U|并集?|或者?|与|及|并且|和)\s*(?=[\[\(\{])", "u");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[\]\)\}])\s*(?:\\cap|∩|交集?)\s*(?=[\[\(\{])", "∩");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[\]\)\}A-Za-z0-9])\s*交\s*(?=[\[\(\{A-Za-z0-9])", "∩");
                // 数学集合隶属度符号归一: \in, ∈, 属于 -> in; \notin, ∉, 不属于 -> !in
                s = s.Replace("\\notin", " !in ").Replace("∉", " !in ").Replace("不属于", " !in ");
                s = s.Replace("\\in", " in ").Replace("∈", " in ").Replace("属于", " in ");
                // 常见数集专有名词中文与正负号归一 (全体实数/实数集 -> R, 全体整数/整数集 -> Z, 自然数集 -> N, 正整数集 -> N*, 有理数集 -> Q, 复数集 -> C)
                s = s.Replace("全体实数集", "R").Replace("全体实数", "R").Replace("实数集", "R");
                s = s.Replace("全体整数集", "Z").Replace("全体整数", "Z").Replace("整数集", "Z");
                s = s.Replace("自然数集", "N");
                s = s.Replace("正整数集", "N*");
                s = s.Replace("有理数集", "Q");
                s = s.Replace("复数集", "C");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\b([RNZQC])\^[\*\+]", "$1*");
                // 汉字指数幂解构 (如 10的8次方 -> 10^8, 10的-3次方 -> 10^-3)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=^|[^\d])10的([+-]?\d+)次(?:方)?", "10^$1");

                // 科学记数法工程简写解构: 3e8 -> 3*10^8, 1.6e-19 -> 1.6*10^-19, 6.02E23 -> 6.02*10^23
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[0-9])[eE]([+-]?[0-9]+)\b", "*10^$1");

                // 中文百分比与汉字分数智能解构 (如 "百分之二十五" -> "25%", "百分之50" -> "50%", "二分之一" -> "1/2", "三分之二" -> "2/3", "四分之三" -> "3/4")
                static bool TryParseSimpleChineseNumber(string cn, out double num)
                {
                    num = 0;
                    if (string.IsNullOrWhiteSpace(cn)) return false;
                    cn = cn.Trim();
                    if (double.TryParse(cn, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out num)) return true;

                    var pointIdx = cn.IndexOf('点');
                    if (pointIdx >= 0)
                    {
                        var intPartStr = cn.Substring(0, pointIdx);
                        var fracPartStr = cn.Substring(pointIdx + 1);
                        double intVal = 0;
                        if (!string.IsNullOrEmpty(intPartStr) && !TryParseSimpleChineseNumber(intPartStr, out intVal)) return false;
                        double fracVal = 0;
                        double factor = 0.1;
                        foreach (char ch in fracPartStr)
                        {
                            int d = ch switch { '零' => 0, '一' => 1, '二' => 2, '两' => 2, '三' => 3, '四' => 4, '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9, _ => -1 };
                            if (d < 0) return false;
                            fracVal += d * factor;
                            factor *= 0.1;
                        }
                        num = intVal + fracVal;
                        return true;
                    }

                    if (cn == "零") { num = 0; return true; }
                    if (cn == "半" || cn == "一半") { num = 0.5; return true; }

                    int total = 0;
                    int current = 0;
                    for (int i = 0; i < cn.Length; i++)
                    {
                        char c = cn[i];
                        int val = c switch
                        {
                            '零' => 0, '一' => 1, '二' => 2, '两' => 2, '三' => 3, '四' => 4,
                            '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9, _ => -1
                        };
                        if (val >= 0)
                        {
                            current = val;
                            if (i == cn.Length - 1) total += current;
                        }
                        else if (c == '十')
                        {
                            if (current == 0 && i == 0) current = 1;
                            total += current * 10;
                            current = 0;
                        }
                        else if (c == '百')
                        {
                            if (current == 0 && i == 0) current = 1;
                            total += current * 100;
                            current = 0;
                        }
                        else return false;
                    }
                    num = total;
                    return true;
                }

                s = System.Text.RegularExpressions.Regex.Replace(s, @"百分之([0-9a-zA-Z\u4e00-\u9fa5\.]+)", m =>
                {
                    var raw = m.Groups[1].Value.Trim();
                    if (TryParseSimpleChineseNumber(raw, out var val))
                    {
                        return $"{val}%";
                    }
                    return m.Value;
                });

                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=(?:^|[^\u4e00-\u9fa5a-zA-Z0-9]))([0-9\u4e00-\u9fa5]+)分之([0-9\u4e00-\u9fa5]+)(?=(?:$|[^\u4e00-\u9fa5a-zA-Z0-9]))", m =>
                {
                    var denStr = m.Groups[1].Value.Trim();
                    var numStr = m.Groups[2].Value.Trim();
                    if (TryParseSimpleChineseNumber(denStr, out var den) && TryParseSimpleChineseNumber(numStr, out var num) && Math.Abs(den) > 1e-9)
                    {
                        return $"{num}/{den}";
                    }
                    return m.Value;
                });
                // 化学反应箭头与可逆反应符号规范化: \rightarrow, \longrightarrow, -->, \rightleftharpoons, ⇌, ⇄
                s = s.Replace("\\longrightarrow", "->")
                     .Replace("\\rightarrow", "->")
                     .Replace("\\to", "->")
                     .Replace("-->", "->")
                     .Replace("===", "=")
                     .Replace("==", "=");
                s = s.Replace("\\rightleftharpoons", "<=>")
                     .Replace("⇌", "<=>")
                     .Replace("⇄", "<=>")
                     .Replace("<==>", "<=>")
                     .Replace("<-->", "<=>")
                     .Replace("<->", "<=>");
                // 剥离 LaTeX 样式外壳: \text{...}, \mathrm{...}, \rm{...}
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(text|mathrm|rm)\{([^}]+)\}", "$2");
                // 剥离化学反应条件标注 (如 \stackrel{点燃}{=}, \overset{点燃}{=}, \underset{加热}{=}, \xrightarrow{加热}, [点燃], (催化剂), △ 等)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:overset|underset|stackrel)\{(?:[^{}]*|\{[^{}]*\})*\}\{(=|->|<=>)\}", "$1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:overset|underset|stackrel)\{(?:[^{}]*|\{[^{}]*\})*\}\{\s*\}", "");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\stackrel\{[^}]*\}\{(=|->)\}", "$1");
                // 如果反应条件单独充当连接符（两端是化学式或分子而无等号箭头），如 "2KClO3 [催化剂] 2KCl" 或 "Cu (加热) CuO"，将其归一为 "="
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[a-zA-Z0-9\)])\s*[\[\(\{]?(?:点燃|加热|催化剂|高温|通电|电解|高压|光照|△|\\Delta)[\]\)\}]?\s*(?=[a-zA-Z0-9\(])", "=");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"[\[\(\{]?(?:点燃|加热|催化剂|高温|通电|电解|高压|光照|△|\\Delta)[\]\)\}]?", "");
                s = s.Replace("“", "\"").Replace("”", "\"").Replace("‘", "'").Replace("’", "'");
                // LaTeX 方程组环境与换行解构
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\begin\{cases\}|\\end\{cases\}|\\begin\{aligned\}|\\end\{aligned\}|\\begin\{array\}|\\end\{array\}", "");
                s = s.Replace("\\\\", ",");
                // 坐标表达式规范化: (x, y) = (a, b) -> (a, b)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"^\([a-zA-Z\s,]+\)\s*=\s*\(([^)]+)\)$", "($1)");
                // 结晶水合物点规范化: CuSO4·5H2O, CuSO4*5H2O, CuSO4.5H2O
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[a-zA-Z0-9\)])\s*[\.·*]\s*(?=\d+\s*[hH]2[oO])", "*");
                s = s.Replace("·", "*").Replace("×", "*");
                // 去除运算符与反应符号周围多余空白
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*<=>\s*", "<=>");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*->\s*", "=");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*=\s*", "=");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*(<=|>=|!=|<|>)\s*", "$1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*/\s*", "/");
                // 仅在字母/数字或定界括号两端压缩数学运算符空白，保护离子电荷与反应物加号不被误黏连 (如 Ag+ + Cl- 不会误变为 Ag++Cl-)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[a-zA-Z0-9\)\]\}])\s*([+\-*])\s*(?=[a-zA-Z0-9\(\[\{])", "$1");
                // 压缩定界符开头的正负一元符号空白 (如 (- inf, 2] -> (-inf, 2])
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[\(\[\{])\s*([+\-])\s*", "$1");
                // 统一区间/坐标/集合/绝对值逗号与定界符间多余空白: 如 [1, 5) vs [1,5), { 1, 2 } vs {1,2}, | x | vs |x|
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*,\s*", ",");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*([\[\]\(\)\|\{\}])\s*", "$1");

                // 中文序数词与小写数字规范化 (如 "第二周期" -> "第2周期", "第一宇宙速度" -> "第1宇宙速度")
                s = s.Replace("第一", "第1").Replace("第二", "第2").Replace("第三", "第3").Replace("第四", "第4").Replace("第五", "第5")
                     .Replace("第六", "第6").Replace("第七", "第7").Replace("第八", "第8").Replace("第九", "第9").Replace("第十", "第10");

                // 常用数量词中数词归一 (如 "两种" -> "2种", "三个" -> "3个", "一对" -> "1对", "四条" -> "4条")
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=(?:^|[^\u4e00-\u9fa5]))(?:两|二)(?=[个种条类只支张头顶瓶组台份本层轮位段步次名度角倍对双根颗粒株块幅门项节题道把套])", "2");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=(?:^|[^\u4e00-\u9fa5]))一(?=[个种条类只支张头顶瓶组台份本层轮位段步次名度角倍对双根颗粒株块幅门项节题道把套])", "1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=(?:^|[^\u4e00-\u9fa5]))三(?=[个种条类只支张头顶瓶组台份本层轮位段步次名度角倍对双根颗粒株块幅门项节题道把套])", "3");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=(?:^|[^\u4e00-\u9fa5]))四(?=[个种条类只支张头顶瓶组台份本层轮位段步次名度角倍对双根颗粒株块幅门项节题道把套])", "4");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=(?:^|[^\u4e00-\u9fa5]))五(?=[个种条类只支张头顶瓶组台份本层轮位段步次名度角倍对双根颗粒株块幅门项节题道把套])", "5");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=(?:^|[^\u4e00-\u9fa5]))六(?=[个种条类只支张头顶瓶组台份本层轮位段步次名度角倍对双根颗粒株块幅门项节题道把套])", "6");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=(?:^|[^\u4e00-\u9fa5]))七(?=[个种条类只支张头顶瓶组台份本层轮位段步次名度角倍对双根颗粒株块幅门项节题道把套])", "7");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=(?:^|[^\u4e00-\u9fa5]))八(?=[个种条类只支张头顶瓶组台份本层轮位段步次名度角倍对双根颗粒株块幅门项节题道把套])", "8");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=(?:^|[^\u4e00-\u9fa5]))九(?=[个种条类只支张头顶瓶组台份本层轮位段步次名度角倍对双根颗粒株块幅门项节题道把套])", "9");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=(?:^|[^\u4e00-\u9fa5]))十(?=[个种条类只支张头顶瓶组台份本层轮位段步次名度角倍对双根颗粒株块幅门项节题道把套])", "10");

                // 单个纯中文数字转换为阿拉伯数字 (如 "两" -> "2", "二" -> "2", "三" -> "3")
                if (s == "零") s = "0";
                else if (s == "一") s = "1";
                else if (s == "二" || s == "两") s = "2";
                else if (s == "三") s = "3";
                else if (s == "四") s = "4";
                else if (s == "五") s = "5";
                else if (s == "六") s = "6";
                else if (s == "七") s = "7";
                else if (s == "八") s = "8";
                else if (s == "九") s = "9";
                else if (s == "十") s = "10";

                s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
                var sLower = s.ToLowerInvariant();

                // 物理速度、频率与阻抗复合单位归一
                sLower = sLower.Replace("千米/小时", "km/h").Replace("千米每小时", "km/h").Replace("千米/时", "km/h");
                sLower = sLower.Replace("赫兹", "hz").Replace("千赫", "khz").Replace("兆赫", "mhz").Replace("吉赫", "ghz");
                sLower = sLower.Replace("千欧姆", "komega").Replace("千欧", "komega").Replace("兆欧", "momega").Replace("欧姆", "omega");
                // 流体动力学动力黏度与运动黏度优先规范化 (在单字“帕”和“平方米”归一前处理，防止词缀被提前拆解)
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bm\^?2\s*(?:/\s*s|[每/]?\s*秒)\b|平方米[每/]?秒", "m^2/s");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\b(?:pa|帕)\s*[\*·•]?\s*(?:s|秒)\b|(?:pa|帕)[·\*•]?(?:s|秒)|帕乘秒", "pa*s");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bn\s*[\*·•]?\s*s\s*/\s*m\^?2\b|\bn\s*[\*·•]?\s*s\s*[\*·•]?\s*m\^?-2\b|牛[顿]?[·\*•]?秒[每/]?平方米", "pa*s");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bkg\s*/\s*\(?\s*m\s*[\*·•]?\s*s\s*\)?|千克[每/]?\(?米[·\*•]?秒\)?", "pa*s");

                // 物理面积与体积中文字符归一
                sLower = sLower.Replace("平方米", "m^2").Replace("平方分米", "dm^2").Replace("平方厘米", "cm^2");
                sLower = sLower.Replace("立方米", "m^3").Replace("立方分米", "dm^3").Replace("立方厘米", "cm^3");
                // 物理高频电磁、气压与同位素归一
                sLower = sLower.Replace("特斯拉", "t").Replace("毫特", "mt").Replace("韦伯", "wb");
                sLower = sLower.Replace("亨利", "h").Replace("法拉", "f").Replace("微法", "uf").Replace("皮法", "pf");
                sLower = sLower.Replace("标准大气压", "atm").Replace("毫米汞柱", "mmhg");
                sLower = sLower.Replace("千帕", "kpa").Replace("兆帕", "mpa").Replace("百帕", "hpa");
                sLower = sLower.Replace("千瓦时", "kwh").Replace("度电", "kwh");
                sLower = sLower.Replace("电子伏特", "ev").Replace("电子伏", "ev").Replace("兆电子伏", "mev");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bm\s*[\*·]?\s*s\^?-2\b|m/s²|\bm\s+s\^-2\b", "m/s^2");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bn\s*[\*·]\s*m\b|\bn\s+m\b", "n*m");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bt\s*[\*·]?\s*m\^?2\b|\bt\s+m\^?2\b|(?:t|特斯拉)[·\*]?(?:m\^?2|平方米)", "wb");

                // 物理动量与冲量单位等价: kg·m/s <=> N·s
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bkg\s*[\*·•]?\s*m\s*(?:/\s*s|[\*·•]?\s*s\^?-1)\b|\bkg\s+m\s*(?:/\s*s|\s+s\^-1)\b|千克[·\*•]?米[每/]?秒", "n*s");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bn\s*[\*·•]?\s*s\b|\bn\s+s\b|牛[·\*•]?秒|牛乘秒", "n*s");

                // 物理功率与能率单位等价: J/s <=> W
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bj\s*(?:/\s*s|[\*·]?\s*s\^?-1)\b|\bj\s+s\^-1\b|焦[耳]?[每/]?秒", "w");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*(?:瓦特|瓦)\b", "w");

                // 电场强度单位等价: V/m <=> N/C
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bv\s*(?:/\s*m|[\*·]?\s*m\^?-1)\b|\bv\s+m\^-1\b|伏[特]?[每/]?米", "v/m");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bn\s*(?:/\s*c|[\*·]?\s*c\^?-1)\b|\bn\s+c\^-1\b|牛[顿]?[每/]?库[仑]?", "v/m");

                // 电流强度与电荷流速等价: C/s <=> A
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bc\s*(?:/\s*s|[\*·]?\s*s\^?-1)\b|\bc\s+s\^-1\b|库[仑]?[每/]?秒", "a");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*(?:安培|安)\b", "a");

                // 角速度与转速单位等价: rad/s <=> rad*s^-1; r/min <=> rpm; r/s
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\brad\s*(?:/\s*s|[\*·]?\s*s\^?-1)\b|\brad\s+s\^-1\b|弧度[每/]?秒", "rad/s");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\b(?:r\s*/\s*min|rpm)\b|转[每/]?分(?:钟)?", "r/min");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\br\s*/\s*s\b|转[每/]?秒", "r/s");

                // 电能与功单位: kW·h <=> 千瓦时 <=> 度
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bkw\s*[\*·]?\s*h\b|\bkw\s+h\b", "kwh");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*度\b", "kwh");

                // 物理压强单位等价: Pa <=> N/m^2 <=> N*m^-2 <=> 帕斯卡 <=> 帕
                sLower = sLower.Replace("帕斯卡", "pa");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*帕\b", "pa");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bn\s*(?:/\s*m\^?2|[\*·]?\s*m\^?-2)\b|\bn\s+m\^-2\b|牛[顿]?[每/]?m\^2|牛[顿]?[每/]?平方米", "pa");

                // 电容单位等价: C/V <=> F <=> 法拉 <=> 库/伏
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bc\s*(?:/\s*v|[\*·]?\s*v\^?-1)\b|\bc\s+v\^-1\b|库[仑]?[每/]?伏[特]?", "f");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*法拉\b", "f");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*法\b", "f");

                // 自感/互感单位等价: H <=> Wb/A <=> \Omega·s <=> 亨利 <=> 韦伯每安培
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bwb\s*(?:/\s*a|[\*·]?\s*a\^?-1|[每/]?\s*安[培]?)\b|韦伯[每/]?安[培]?", "h");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\b(?:omega|komega)\s*[\*·]?\s*s\b", "h");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*亨利\b", "h");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*亨\b", "h");

                // 磁通量单位等价扩展: Wb <=> V·s <=> T·m^2 <=> 韦伯 <=> 伏·秒
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bv\s*[\*·]?\s*s\b|\bv\s+s\b|伏[特]?[·\*]?秒", "wb");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bt\s*[\*·]?\s*m\^?2\b|\bt\s+m\^?2\b|(?:t|特斯拉)[·\*]?(?:m\^?2|平方米)", "wb");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*韦伯\b", "wb");

                // 比热容单位等价: J/(kg·℃) <=> J/(kg·K) <=> J·kg^-1·K^-1 <=> 焦每千克摄氏度
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bj\s*/\s*\(?\s*kg\s*[\*·]?\s*(?:℃|c|k)\s*\)?|\bj\s*[\*·]?\s*kg\^?-1\s*[\*·]?\s*(?:℃|c|k)\^?-1|焦[耳]?[每/]?\(?千克[·\*]?(?:摄氏度|开尔文|℃|k)\)?", "j/(kg*k)");

                // 物质的量浓度单位等价: mol/L <=> mol/dm^3 <=> mol·L^-1 <=> 摩尔每升
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bmol\s*(?:/\s*(?:l|dm\^?3)|[\*·]?\s*(?:l|dm\^3)\^?-1)\b|摩尔[每/]?升|摩[每/]?升", "mol/l");

                // 磁感应强度单位等价: T <=> N/(A*m) <=> Wb/m^2 <=> 特斯拉 <=> 牛每安米
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bn\s*[每/]\s*\(?\s*a\s*[\*·]?\s*m\s*\)?|\bn\s*[\*·]?\s*a\^?-1\s*[\*·]?\s*m\^?-1|牛[顿]?[每/]?\(?安[培]?[·\*]?米\)?", "t");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\b(?:wb|韦伯)\s*(?:[每/]\s*m\^?2|[\*·]?\s*m\^?-2)\b|\b(?:wb|韦伯)\s+m\^-2\b|(?:wb|韦伯)[每/]?m\^2|(?:wb|韦伯)[每/]?平方米", "t");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*(?:tesla|特斯拉|t)\b", "t");

                // 电阻率单位等价: Ω·m <=> \Omega*m <=> 欧姆米 <=> 欧·米 <=> 欧米
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?:\\?omega|ohm|欧[姆]?)\s*[\*·]?\s*(?:m|米)\b|(?:\\?omega|ohm|欧[姆]?)[·\*]?(?:m|米)", "omega*m");

                // 电导单位等价: S <=> 西门子 <=> 1/Ω <=> A/V <=> 安每伏
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\ba\s*[每/]\s*v\b|\ba\s*[\*·]?\s*v\^?-1\b|安[培]?[每/]?伏[特]?", "siemens");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"1\s*/\s*(?:\\?omega|ohm|欧[姆]?)|(?:\\?omega|ohm|欧[姆]?)\^?-1", "siemens");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*(?:西门子|siemens|s)\b", "siemens");

                // 导热系数/热导率单位等价: W/(m·K) <=> W/(m*K) <=> W/(m·℃) <=> 瓦每米开尔文
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bw\s*[每/]\s*\(?\s*m\s*[\*·]?\s*(?:k|℃|c)\s*\)?|\bw\s*[\*·]?\s*m\^?-1\s*[\*·]?\s*(?:k|℃|c)\^?-1|瓦[特]?[每/]?\(?米[·\*]?(?:开尔文|摄氏度|k|℃)\)?", "w/(m*k)");

                // 摩尔气体常数/摩尔熵单位等价: J/(mol·K) <=> J/(mol*K) <=> 焦每摩尔开尔文
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bj\s*[每/]\s*\(?\s*mol\s*[\*·]?\s*k\s*\)?|\bj\s*[\*·]?\s*mol\^?-1\s*[\*·]?\s*k\^?-1|焦[耳]?[每/]?\(?摩[尔]?[·\*]?(?:开尔文|开|k)\)?", "j/(mol*k)");

                // 表面张力系数单位等价: N/m <=> N·m^-1 <=> 牛每米 <=> 牛/米 <=> J/m^2 <=> 焦每平方米
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bn\s*(?:/\s*m|[\*·]?\s*m\^?-1)\b|牛[顿]?[每/]?米", "n/m");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bj\s*(?:/\s*m\^?2|[\*·]?\s*m\^?-2)\b|焦[耳]?[每/]?\(?(?:平方米|m\^?2)\)?", "n/m");

                // 光度学照度与光通量单位等价: lx <=> lm/m^2 <=> 勒克斯 <=> 流明每平方米; lm <=> 流明
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\b(?:lm\s*(?:/\s*m\^?2|[\*·]?\s*m\^?-2))\b|流明[每/]?\(?(?:平方米|m\^?2)\)?|(?<=\d)\s*(?:lx|lux|勒克斯)\b", "lx");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*(?:lm|lumen|流明)\b", "lm");


                // 化学反应焓变与摩尔生成热单位等价: kJ/mol <=> 千焦每摩尔 <=> 千焦/摩尔 <=> 10^3 J/mol
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bkj\s*/\s*mol\b|\bkj\s*[\*·]?\s*mol\^?-1\b|千焦[耳]?[每/]?摩[尔]?", "kj/mol");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bj\s*/\s*mol\b|\bj\s*[\*·]?\s*mol\^?-1\b|焦[耳]?[每/]?摩[尔]?", "j/mol");

                // 潜热/比燃烧热单位等价: kJ/kg <=> 千焦每千克; J/kg <=> 焦每千克
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bkj\s*/\s*kg\b|\bkj\s*[\*·]?\s*kg\^?-1\b|千焦[耳]?[每/]?千克", "kj/kg");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bj\s*/\s*kg\b|\bj\s*[\*·]?\s*kg\^?-1\b|焦[耳]?[每/]?千克", "j/kg");

                // 摩尔质量单位等价: g/mol <=> 克每摩尔
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bg\s*/\s*mol\b|\bg\s*[\*·]?\s*mol\^?-1\b|克[每/]?摩[尔]?", "g/mol");

                // 物理密度单位等价: g/cm^3 <=> g/mL <=> 克每立方厘米 <=> 克每毫升
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bg\s*/\s*(?:cm\^?3|ml)\b|\bg\s*[\*·]?\s*(?:cm\^3|ml)\^?-1\b|克[每/]?\(?(?:立方厘米|毫升|cm\^3)\)?", "g/cm^3");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\bkg\s*/\s*m\^?3\b|\bkg\s*[\*·]?\s*m\^?-3\b|千克[每/]?\(?(?:立方米|m\^3)\)?", "kg/m^3");

                // 声压级与声音强度: dB <=> 分贝
                sLower = sLower.Replace("分贝", "db");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*(?:db|分贝)\b", "db");

                // 发光强度: cd <=> 坎德拉 <=> 坎
                sLower = sLower.Replace("坎德拉", "cd");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*(?:cd|坎德拉|坎)\b", "cd");

                // 微观长度与波长单位: nm <=> 纳米; um <=> 微米; pm <=> 皮米
                sLower = sLower.Replace("纳米", "nm").Replace("微米", "um").Replace("皮米", "pm").Replace("μm", "um");

                // 放射性核素活度与辐射剂量: Bq <=> 贝克勒尔 <=> 贝克; Gy <=> 戈瑞; Sv <=> 希沃特
                sLower = sLower.Replace("贝克勒尔", "bq").Replace("贝克", "bq").Replace("戈瑞", "gy").Replace("希沃特", "sv");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*(?:bq|贝克勒尔|贝克)\b", "bq");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*(?:gy|戈瑞)\b", "gy");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?<=\d)\s*(?:sv|希沃特)\b", "sv");

                // 反三角函数规范化: 如 \arcsin, \sin^{-1}, asin -> arcsin; \arccos, \cos^{-1}, acos -> arccos; \arctan, \tan^{-1}, atan -> arctan
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\\?(?:arcsin|asin)\b|\\?sin\^\{\s*-1\s*\}|\\?sin\^-1\b", "arcsin");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\\?(?:arccos|acos)\b|\\?cos\^\{\s*-1\s*\}|\\?cos\^-1\b", "arccos");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\\?(?:arctan|atan)\b|\\?tan\^\{\s*-1\s*\}|\\?tan\^-1\b", "arctan");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\\?(?:arccot|acot)\b|\\?cot\^\{\s*-1\s*\}|\\?cot\^-1\b", "arccot");

                // 化学同位素规范化: 如 ^{14}c, ^{14}_{6}c, c-14, 碳-14, 碳14 -> c-14
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\^\{?(\d+)\}?(?:_\{?\d+\}?)?([a-z]+)", "$2-$1");
                sLower = sLower.Replace("碳-14", "c-14").Replace("碳14", "c-14")
                               .Replace("铀-235", "u-235").Replace("铀235", "u-235")
                               .Replace("氢-1", "h-1").Replace("氢-2", "h-2").Replace("氢-3", "h-3")
                               .Replace("氧-16", "o-16").Replace("氧-18", "o-18");

                // 三角函数幂次规范化: 如 \sin^2(x) / sin^2 x / sin^2(x) -> (sin(x))^2, (\sin x)^2 -> (sin(x))^2
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"(?:\\)?\b(sin|cos|tan|cot|sec|csc)\^(\d+)\s*(?:\(\s*([a-zA-Z0-9]+)\s*\)|([a-zA-Z0-9]+))", "($1($3$4))^$2");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\((?:\\)?(sin|cos|tan|cot|sec|csc)\s+([a-zA-Z0-9]+)\)\^(\d+)", "($1($2))^$3");
                sLower = System.Text.RegularExpressions.Regex.Replace(sLower, @"\((?:\\)?(sin|cos|tan|cot|sec|csc)\(([a-zA-Z0-9]+)\)\)\^(\d+)", "($1($2))^$3");

                // 命题逻辑充分必要条件规范化
                if (sLower == "充分必要条件" || sLower == "充要条件" || sLower == "充分且必要条件" || sLower == "充要" ||
                    sLower == "当且仅当" || sLower == "<=>" || sLower == "\\iff" || sLower == "iff")
                {
                    return "充要条件";
                }
                if (sLower == "充分不必要条件" || sLower == "充分非必要条件" || sLower == "充分不必要")
                {
                    return "充分不必要条件";
                }
                if (sLower == "必要不充分条件" || sLower == "必要非充分条件" || sLower == "必要不充分")
                {
                    return "必要不充分条件";
                }
                if (sLower == "既不充分也不必要条件" || sLower == "既非充分又非必要条件" || sLower == "既不充分又非必要条件" || sLower == "既非充分也不必要条件" || sLower == "既不充分也不必要")
                {
                    return "既不充分也不必要条件";
                }

                // 函数单调性与奇偶性规范化
                if (sLower == "单调递增" || sLower == "单调增加" || sLower == "单调增" || sLower == "递增")
                {
                    return "单调递增";
                }
                if (sLower == "单调递减" || sLower == "单调减少" || sLower == "单调减" || sLower == "递减")
                {
                    return "单调递减";
                }
                if (sLower == "偶函数" || sLower == "是偶函数" || sLower == "为偶函数")
                {
                    return "偶函数";
                }
                if (sLower == "奇函数" || sLower == "是奇函数" || sLower == "为奇函数")
                {
                    return "奇函数";
                }

                // 全体实数 / 实数集与实数域双向等价归一为 "r"
                if (sLower == "全体实数" || sLower == "实数集" || sLower == "全体实数集" || 
                    sLower == "(-inf,+inf)" || sLower == "(-inf,inf)" || sLower == "(-inf, +inf)" || 
                    sLower == "\\mathbb{r}" || sLower == "\\mathbf{r}" || sLower == "r" ||
                    sLower == "x属于r" || sLower == "x∈r" || sLower == "x\\in\\mathbb{r}" || sLower == "x\\in r" ||
                    sLower == "x in r" || sLower == "xinr" ||
                    System.Text.RegularExpressions.Regex.IsMatch(sLower, @"^[a-z]\s*(?:in|属于|∈|\\in)\s*r$"))
                {
                    return "r";
                }
                // 空集 / 无解 / 不存在等价归一为 "∅"
                if (sLower == "空集" || sLower == "无解" || sLower == "不存在" || 
                    sLower == "无实数解" || sLower == "无实数根" || sLower == "无实根" ||
                    sLower == "\\emptyset" || sLower == "\\varnothing" || sLower == "\\empty" || 
                    sLower == "\\phi" || sLower == "phi" || sLower == "empty" || sLower == "empty_set" ||
                    sLower == "{}" || sLower == "∅" || sLower == "ø")
                {
                    return "∅";
                }

                return sLower;
            }

            static string NormalizeChemical(string s)
            {
                var trimmed = s.Trim().ToLowerInvariant();
                if (ChemicalSynonymMap.TryGetValue(trimmed, out var mappedFormula))
                {
                    s = mappedFormula;
                }
                // 统一 Unicode 离子电荷与角标
                s = s.Replace("³⁺", "3+").Replace("²⁺", "2+").Replace("⁴⁺", "4+").Replace("⁺", "+")
                     .Replace("³⁻", "3-").Replace("²⁻", "2-").Replace("⁴⁻", "4-").Replace("⁻", "-");
                s = s.Replace('₀', '0').Replace('₁', '1').Replace('₂', '2').Replace('₃', '3').Replace('₄', '4')
                     .Replace('₅', '5').Replace('₆', '6').Replace('₇', '7').Replace('₈', '8').Replace('₉', '9');
                // 统一可逆反应符号
                s = s.Replace("⇌", "<=>").Replace("⇄", "<=>").Replace("\\rightleftharpoons", "<=>").Replace("<==>", "<=>").Replace("<-->", "<=>");
                // 剥离气体与沉淀箭头：↑, ↓, \uparrow, \downarrow, ^
                s = s.Replace("↑", "").Replace("↓", "").Replace("\\uparrow", "").Replace("\\downarrow", "");
                // 剥离化学物态标注: (s), (l), (g), (aq), (固), (液), (气), (水)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"[\(（](?:s|l|g|aq|固|液|气|水)[\)）]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                // Ca(OH)_2 -> ca(oh)2, H_2O -> h2o, CO_2 -> co2
                s = System.Text.RegularExpressions.Regex.Replace(s, @"_\{?(\d+)\}?", "$1");
                // Fe^{3+} -> fe3+, Fe^3+ -> fe3+, SO4^{2-} -> so42-
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\^\{?(\d*[+-])\}?", "$1");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\^([0-9]*[+-])", "$1");
                // 离子电荷多加号/多减号容错: Fe+++ -> fe3+, SO4-- -> so42-
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\+{2,}", m => $"{m.Length}+");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\-{2,}", m => $"{m.Length}-");
                return s.ToLowerInvariant().Trim();
            }

            var normUser = Normalize(user);
            var normCorrect = Normalize(correct);

            if (normUser == normCorrect) return true;

            // 化学品名称与化学式双向映射智能匹配 (如 "硫酸铜" vs "CuSO4", "NaOH" vs "氢氧化钠")
            if (IsChemicalNomenclatureEquivalent(user, correct) ||
                IsChemicalNomenclatureEquivalent(normUser, normCorrect))
            {
                return true;
            }

            // 化学反应方程式等号、可逆平衡与箭头等价匹配: 如 2H2+O2=2H2O 与 2H2+O2->2H2O 与 N2+3H2<=>2NH3
            if (normUser.Replace("->", "=").Replace("<=>", "=") == normCorrect.Replace("->", "=").Replace("<=>", "=")) return true;

            // 化学式分子与离子角标规范化匹配
            var chemUser = NormalizeChemical(normUser);
            var chemCorrect = NormalizeChemical(normCorrect);
            if (chemUser == chemCorrect) return true;
            if (chemUser.Replace("->", "=").Replace("<=>", "=") == chemCorrect.Replace("->", "=").Replace("<=>", "=")) return true;

            // 化学反应方程式反应物/生成物次序对易等价匹配及电极反应移项: 如 2NaOH + CuSO4 = Cu(OH)2 + Na2SO4 与 CuSO4 + 2NaOH = Na2SO4 + Cu(OH)2, Zn - 2e- = Zn2+ 与 Zn = Zn2+ + 2e-
            static bool CheckChemicalReactionCommutativeMatch(string u, string c)
            {
                string uNorm = NormalizeElectrochemicalReaction(u);
                string cNorm = NormalizeElectrochemicalReaction(c);
                var uParts = uNorm.Split('=', StringSplitOptions.RemoveEmptyEntries);
                var cParts = cNorm.Split('=', StringSplitOptions.RemoveEmptyEntries);
                if (uParts.Length == 2 && cParts.Length == 2)
                {
                    static List<string> ExtractReactionTerms(string side)
                    {
                        side = side.Trim();
                        side = System.Text.RegularExpressions.Regex.Replace(side, @"([0-9]?[+-])\+", "$1 + ");
                        side = System.Text.RegularExpressions.Regex.Replace(side, @"(?<=[a-zA-Z\)])\+(?=[0-9a-zA-Z])", " + ");
                        side = System.Text.RegularExpressions.Regex.Replace(side, @"(?<=\d[+-])(?=[0-9a-zA-Z])", " + ");

                        var rawTerms = System.Text.RegularExpressions.Regex.Split(side, @"\s+\+\s+")
                            .Select(t => t.Trim())
                            .Where(t => !string.IsNullOrEmpty(t))
                            .ToList();

                        if (rawTerms.Count == 1 && side.Contains('+') && !side.EndsWith("+"))
                        {
                            rawTerms = side.Split('+', StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => t.Trim())
                                .Where(t => !string.IsNullOrEmpty(t))
                                .ToList();
                        }

                        static string MapReactionTerm(string rawTerm)
                        {
                            rawTerm = rawTerm.Trim();
                            var m = System.Text.RegularExpressions.Regex.Match(rawTerm, @"^(\d+)?\s*(.+)$");
                            if (m.Success)
                            {
                                var coeff = m.Groups[1].Value;
                                var substance = m.Groups[2].Value.Trim();
                                if (ChemicalSynonymMap.TryGetValue(substance, out var mapped))
                                {
                                    return coeff + mapped;
                                }
                            }
                            return rawTerm;
                        }

                        return rawTerms
                                   .Select(t => NormalizeChemical(MapReactionTerm(t)))
                                   .Where(t => !string.IsNullOrEmpty(t))
                                   .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                                   .ToList();
                    }

                    var uLhs = ExtractReactionTerms(uParts[0]);
                    var uRhs = ExtractReactionTerms(uParts[1]);
                    var cLhs = ExtractReactionTerms(cParts[0]);
                    var cRhs = ExtractReactionTerms(cParts[1]);

                    if (uLhs.Count > 0 && cLhs.Count > 0 &&
                        uLhs.SequenceEqual(cLhs, StringComparer.OrdinalIgnoreCase) &&
                        uRhs.SequenceEqual(cRhs, StringComparer.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }

            if (CheckChemicalReactionCommutativeMatch(normUser, normCorrect) ||
                CheckChemicalReactionCommutativeMatch(user, correct) ||
                CheckChemicalReactionCommutativeMatch(chemUser, chemCorrect))
            {
                return true;
            }

            // 化学热化学方程式与焓变 ΔH 等价识别 (反应物/生成物、聚集状态及焓变数值单位)
            if (CheckThermochemicalEquationMatch(user, correct) ||
                CheckThermochemicalEquationMatch(normUser, normCorrect) ||
                CheckThermochemicalEquationMatch(chemUser, chemCorrect))
            {
                return true;
            }

            // 有机化学结构简式、示性式与分子式智能等价识别 (如 CH2=CH2 vs C2H4, CH3CH2OH vs C2H5OH)
            if (IsOrganicStructureEquivalent(user, correct) || 
                IsOrganicStructureEquivalent(normUser, normCorrect) ||
                IsOrganicStructureEquivalent(chemUser, chemCorrect))
            {
                return true;
            }

            // 比例与比值/最简分数智能等价 (如 3:4 vs 3比4, 3:4 vs 3/4, 1:2:3 vs 2:4:6)
            if (CheckRatioFractionEquivalence(normUser, normCorrect) ||
                CheckRatioFractionEquivalence(user, correct))
            {
                return true;
            }

            // 实数区间与一元不等式双向等价映射: 如 x >= 3 <=> [3, +inf), x < 2 <=> (-inf, 2), 1 <= x <= 5 <=> [1, 5], x < -1 或 x > 1 <=> (-inf,-1)u(1,+inf)
            static string NormalizeIntervalOrInequality(string s)
            {
                s = s.Trim();

                // 空集及无解归一
                var sEmptyCheck = s.ToLowerInvariant().Trim();
                if (sEmptyCheck == "∅" || sEmptyCheck == "ø" || sEmptyCheck == "\\emptyset" || sEmptyCheck == "\\varnothing" ||
                    sEmptyCheck == "\\empty" || sEmptyCheck == "\\phi" || sEmptyCheck == "phi" ||
                    sEmptyCheck == "empty" || sEmptyCheck == "empty_set" || sEmptyCheck == "{}" ||
                    sEmptyCheck == "空集" || sEmptyCheck == "无解" || sEmptyCheck == "无实数解" || sEmptyCheck == "无实数根")
                {
                    return "∅";
                }

                // 二次因式分解不等式规范化: (x - a)(x - b) < 0, (x - a)(x - b) <= 0, (x - a)(x - b) > 0, (x - a)(x - b) >= 0
                var factorMatch = System.Text.RegularExpressions.Regex.Match(s, @"^\(\s*([a-zA-Z])\s*([+-])\s*(\d+(?:\.\d+)?)\s*\)\s*(?:\*|·)?\s*\(\s*\1\s*([+-])\s*(\d+(?:\.\d+)?)\s*\)\s*(<=|<|>=|>)\s*0$");
                if (factorMatch.Success)
                {
                    string sign1 = factorMatch.Groups[2].Value;
                    double num1 = double.Parse(factorMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                    double root1 = sign1 == "-" ? num1 : -num1;

                    string sign2 = factorMatch.Groups[4].Value;
                    double num2 = double.Parse(factorMatch.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture);
                    double root2 = sign2 == "-" ? num2 : -num2;

                    string op = factorMatch.Groups[6].Value;

                    if (root1 > root2)
                    {
                        var temp = root1; root1 = root2; root2 = temp;
                    }

                    string r1Str = (root1 % 1 == 0) ? ((long)root1).ToString() : root1.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string r2Str = (root2 % 1 == 0) ? ((long)root2).ToString() : root2.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    return op switch
                    {
                        "<" => $"({r1Str},{r2Str})",
                        "<=" => $"[{r1Str},{r2Str}]",
                        ">" => $"(-inf,{r1Str})u({r2Str},+inf)",
                        ">=" => $"(-inf,{r1Str}]u[{r2Str},+inf)",
                        _ => s
                    };
                }

                // 包含单个自变量因式的二次分解: x(x-a) < 0, (x)(x-a) <= 0 等
                var factorMatch2 = System.Text.RegularExpressions.Regex.Match(s, @"^(?:\(\s*([a-zA-Z])\s*\)|([a-zA-Z]))\s*(?:\*|·)?\s*\(\s*(?:\1|\2)\s*([+-])\s*(\d+(?:\.\d+)?)\s*\)\s*(<=|<|>=|>)\s*0$");
                if (factorMatch2.Success)
                {
                    double root1 = 0;
                    string sign2 = factorMatch2.Groups[3].Value;
                    double num2 = double.Parse(factorMatch2.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                    double root2 = sign2 == "-" ? num2 : -num2;
                    string op = factorMatch2.Groups[5].Value;

                    if (root1 > root2)
                    {
                        var temp = root1; root1 = root2; root2 = temp;
                    }

                    string r1Str = (root1 % 1 == 0) ? ((long)root1).ToString() : root1.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string r2Str = (root2 % 1 == 0) ? ((long)root2).ToString() : root2.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    return op switch
                    {
                        "<" => $"({r1Str},{r2Str})",
                        "<=" => $"[{r1Str},{r2Str}]",
                        ">" => $"(-inf,{r1Str})u({r2Str},+inf)",
                        ">=" => $"(-inf,{r1Str}]u[{r2Str},+inf)",
                        _ => s
                    };
                }

                // 剥离集合描述法外壳: 如 {x | x > 2} 或 {x \in R | x <= 5} -> x > 2 / x <= 5
                var setBuilderMatch = System.Text.RegularExpressions.Regex.Match(s, @"^\{\s*[a-zA-Z](?:\s*(?:\\in|∈|\bin\b|in|属于)\s*[a-zA-Z\\]+)?\s*\|\s*(.+)\s*\}$");
                if (setBuilderMatch.Success)
                {
                    s = setBuilderMatch.Groups[1].Value.Trim();
                }
                s = System.Text.RegularExpressions.Regex.Replace(s, @"^[a-zA-Z]\s*(?:\\in|∈|\bin\b|in|属于)\s*", "");

                // 中文闭区间规范化: "2到5" / "2至5" / "2~5" -> "[2,5]"
                var rangeMatch = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?\d+(?:\.\d+)?)\s*(?:到|至|~)\s*([+-]?\d+(?:\.\d+)?)$");
                if (rangeMatch.Success)
                {
                    return $"[{rangeMatch.Groups[1].Value},{rangeMatch.Groups[2].Value}]";
                }

                // 中文双侧不等式解构: "x大于等于2且小于等于5" -> "2<=x<=5"
                var cnDoubleIneq = System.Text.RegularExpressions.Regex.Match(s, @"^([a-zA-Z])\s*(大于等于|大于)\s*([^且，,;]+)\s*(?:且|，|,)\s*(小于等于|小于)\s*([^且，,;]+)$");
                if (cnDoubleIneq.Success)
                {
                    string varName = cnDoubleIneq.Groups[1].Value;
                    string leftOp = cnDoubleIneq.Groups[2].Value == "大于等于" ? "<=" : "<";
                    string leftVal = cnDoubleIneq.Groups[3].Value.Trim();
                    string rightOp = cnDoubleIneq.Groups[4].Value == "小于等于" ? "<=" : "<";
                    string rightVal = cnDoubleIneq.Groups[5].Value.Trim();
                    s = $"{leftVal}{leftOp}{varName}{rightOp}{rightVal}";
                }

                // 中文单侧不等式解构: "x大于等于2", "x小于5"
                s = System.Text.RegularExpressions.Regex.Replace(s, @"^([a-zA-Z])\s*大于等于\s*(.+)$", "$1>=$2");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"^([a-zA-Z])\s*小于等于\s*(.+)$", "$1<=$2");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"^([a-zA-Z])\s*大于\s*(.+)$", "$1>$2");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"^([a-zA-Z])\s*小于\s*(.+)$", "$1<$2");

                // 绝对值不等式解构: |x| <= a, |x| < a, |x| >= a, |x| > a
                var absIneq = System.Text.RegularExpressions.Regex.Match(s, @"^\|([a-zA-Z])\|\s*(<=|<|>=|>)\s*([^<>=,;，；或]+)$");
                if (absIneq.Success)
                {
                    string op = absIneq.Groups[2].Value;
                    string val = absIneq.Groups[3].Value.Trim();
                    string negVal = val.StartsWith("-") ? val.Substring(1) : $"-{val}";
                    return op switch
                    {
                        "<=" => $"[{negVal},{val}]",
                        "<" => $"({negVal},{val})",
                        ">=" => $"(-inf,{negVal}]u[{val},+inf)",
                        ">" => $"(-inf,{negVal})u({val},+inf)",
                        _ => s
                    };
                }

                // 逻辑与连词复合不等式: 如 "x>1 且 x<5", "x>=2 and x<=6", "1<x 且 x<5"
                var andMatch = System.Text.RegularExpressions.Regex.Match(s, @"^(.+?)\s*(?:且|and|&&)\s*(.+?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (andMatch.Success)
                {
                    string leftPart = andMatch.Groups[1].Value.Trim();
                    string rightPart = andMatch.Groups[2].Value.Trim();
                    var p1 = NormalizeIntervalOrInequality(leftPart);
                    var p2 = NormalizeIntervalOrInequality(rightPart);
                    var intervalMatch1 = System.Text.RegularExpressions.Regex.Match(p1, @"^([\[\(])([^,]+),([^\]\)]+)([\]\)])$");
                    var intervalMatch2 = System.Text.RegularExpressions.Regex.Match(p2, @"^([\[\(])([^,]+),([^\]\)]+)([\]\)])$");
                    if (intervalMatch1.Success && intervalMatch2.Success)
                    {
                        string b1Left = intervalMatch1.Groups[1].Value;
                        string v1Left = intervalMatch1.Groups[2].Value;
                        string v1Right = intervalMatch1.Groups[3].Value;
                        string b1Right = intervalMatch1.Groups[4].Value;

                        string b2Left = intervalMatch2.Groups[1].Value;
                        string v2Left = intervalMatch2.Groups[2].Value;
                        string v2Right = intervalMatch2.Groups[3].Value;
                        string b2Right = intervalMatch2.Groups[4].Value;

                        if (v1Right == "+inf" && v2Left == "-inf")
                        {
                            return $"{b1Left}{v1Left},{v2Right}{b2Right}";
                        }
                        if (v1Left == "-inf" && v2Right == "+inf")
                        {
                            return $"{b2Left}{v2Left},{v1Right}{b1Right}";
                        }
                    }
                }

                // 如果包含复合 "或者" / "或" / "并" / "与" / "及" / "并且" / "u" / "∪" 连接的多段不等式或区间 (如 x < -1 或 x > 1, (-inf, -1) u (1, +inf))
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"\b(?:或者|或|并|与|及|并且|和)\b|(?<=\d|\))\s*(?:或者|或|并|与|及|并且|和)\s*(?=[a-zA-Z\(])") || s.Contains('u') || s.Contains('∪') || s.Contains("\\cup"))
                {
                    var parts = System.Text.RegularExpressions.Regex.Split(s, @"\s*(?:或者|或|并|与|及|并且|和|\\cup|∪|u)\s*")
                        .Select(p => NormalizeIntervalOrInequality(p.Trim()))
                        .Where(p => !string.IsNullOrEmpty(p))
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .ToList();
                    if (parts.Count > 1)
                    {
                        return string.Join("u", parts);
                    }
                }

                // 规范无穷符号与右端点规范: \infty, +∞, -∞, 以及区间右端点省略正号的 inf
                s = s.Replace("+\\infty", "+inf").Replace("-\\infty", "-inf").Replace("\\infty", "+inf")
                     .Replace("+∞", "+inf").Replace("-∞", "-inf").Replace("∞", "+inf");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[\(\[,])\s*inf\b", "+inf");

                // 双侧复合不等式 (支持分式/根式端点): a <= x <= b, a < x < b, a <= x < b, a < x <= b
                var doubleIneq = System.Text.RegularExpressions.Regex.Match(s, @"^([^<>=,;，；或]+)\s*(<=|<)\s*([a-zA-Z])\s*(<=|<)\s*([^<>=,;，；或]+)$");
                if (doubleIneq.Success)
                {
                    string leftVal = doubleIneq.Groups[1].Value.Trim();
                    string leftOp = doubleIneq.Groups[2].Value;
                    string rightOp = doubleIneq.Groups[4].Value;
                    string rightVal = doubleIneq.Groups[5].Value.Trim();
                    char leftBracket = leftOp == "<=" ? '[' : '(';
                    char rightBracket = rightOp == "<=" ? ']' : ')';
                    return $"{leftBracket}{leftVal},{rightVal}{rightBracket}";
                }

                // 双侧反向复合不等式: b >= x >= a, b > x > a, b >= x > a, b > x >= a
                var doubleIneqRev = System.Text.RegularExpressions.Regex.Match(s, @"^([^<>=,;，；或]+)\s*(>=|>)\s*([a-zA-Z])\s*(>=|>)\s*([^<>=,;，；或]+)$");
                if (doubleIneqRev.Success)
                {
                    string rightVal = doubleIneqRev.Groups[1].Value.Trim();
                    string rightOp = doubleIneqRev.Groups[2].Value;
                    string leftOp = doubleIneqRev.Groups[4].Value;
                    string leftVal = doubleIneqRev.Groups[5].Value.Trim();
                    char rightBracket = rightOp == ">=" ? ']' : ')';
                    char leftBracket = leftOp == ">=" ? '[' : '(';
                    return $"{leftBracket}{leftVal},{rightVal}{rightBracket}";
                }

                // 单侧不等式 (变量在左侧): x >= a, x > a, x <= b, x < b (支持分式/根式端点)
                var singleIneq = System.Text.RegularExpressions.Regex.Match(s, @"^[a-zA-Z]\s*(>=|>|<=|<)\s*([^<>=,;，；或]+)$");
                if (singleIneq.Success)
                {
                    string op = singleIneq.Groups[1].Value;
                    string val = singleIneq.Groups[2].Value.Trim();
                    return op switch
                    {
                        ">=" => $"[{val},+inf)",
                        ">" => $"({val},+inf)",
                        "<=" => $"(-inf,{val}]",
                        "<" => $"(-inf,{val})",
                        _ => s
                    };
                }

                // 单侧反向不等式 (变量在右侧): a <= x, a < x, b >= x, b > x (支持分式/根式端点)
                var singleIneqRev = System.Text.RegularExpressions.Regex.Match(s, @"^([^<>=,;，；或]+)\s*(<=|<|>=|>)\s*([a-zA-Z])$");
                if (singleIneqRev.Success)
                {
                    string val = singleIneqRev.Groups[1].Value.Trim();
                    string op = singleIneqRev.Groups[2].Value;
                    return op switch
                    {
                        "<=" => $"[{val},+inf)",
                        "<" => $"({val},+inf)",
                        ">=" => $"(-inf,{val}]",
                        ">" => $"(-inf,{val})",
                        _ => s
                    };
                }

                return s;
            }

            var ineqUser = NormalizeIntervalOrInequality(normUser);
            var ineqCorrect = NormalizeIntervalOrInequality(normCorrect);
            if (ineqUser == ineqCorrect) return true;

            // 实数区间端点精准等价容错 (如 [1/2, 3/2] 与 [0.5, 1.5], [1/2, +inf) 与 [0.5, +inf))
            static bool AreIntervalsEquivalent(string i1, string i2)
            {
                if (i1 == i2) return true;
                var m1 = System.Text.RegularExpressions.Regex.Match(i1, @"^([\[\(])([^,]+),([^\]\)]+)([\]\)])$");
                var m2 = System.Text.RegularExpressions.Regex.Match(i2, @"^([\[\(])([^,]+),([^\]\)]+)([\]\)])$");
                if (m1.Success && m2.Success)
                {
                    if (m1.Groups[1].Value != m2.Groups[1].Value || m1.Groups[4].Value != m2.Groups[4].Value)
                        return false;
                    var left1 = m1.Groups[2].Value.Trim();
                    var right1 = m1.Groups[3].Value.Trim();
                    var left2 = m2.Groups[2].Value.Trim();
                    var right2 = m2.Groups[3].Value.Trim();

                    bool leftMatch = left1 == left2 || CheckFillInBlankMatch(left1, left2);
                    bool rightMatch = right1 == right2 || CheckFillInBlankMatch(right1, right2);
                    return leftMatch && rightMatch;
                }
                return false;
            }

            if (AreIntervalsEquivalent(ineqUser, ineqCorrect)) return true;

            // 多区间并集无序与排列等价匹配 (如 (-inf,-1)u(1,+inf) 与 (1,+inf)u(-inf,-1))
            static bool AreIntervalUnionsEquivalent(string u, string c)
            {
                if (u == c) return true;
                if (!u.Contains('u') || !c.Contains('u')) return false;

                var uParts = u.Split('u', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
                var cParts = c.Split('u', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();

                if (uParts.Count != cParts.Count || uParts.Count < 2) return false;

                var remainingC = new List<string>(cParts);
                foreach (var up in uParts)
                {
                    var idx = remainingC.FindIndex(cp => cp == up || AreIntervalsEquivalent(up, cp));
                    if (idx >= 0)
                    {
                        remainingC.RemoveAt(idx);
                    }
                    else
                    {
                        return false;
                    }
                }
                return remainingC.Count == 0;
            }

            if (AreIntervalUnionsEquivalent(ineqUser, ineqCorrect)) return true;

            // 方程多根/解集无序与表达形式等价匹配 (如 {1, -2} 与 {-2, 1}, x_1=1, x_2=-2 与 x_1=-2, x_2=1, x=1或x=-2 与 1, -2)
            static bool TryExtractEquationRoots(string s, out List<string> roots)
            {
                roots = new List<string>();
                s = s.Trim();
                if (string.IsNullOrEmpty(s)) return false;

                // 排除闭区间与开区间形如 [a, b], (a, b)
                if ((s.StartsWith("(") && s.EndsWith(")")) || (s.StartsWith("[") && s.EndsWith("]")))
                {
                    return false;
                }

                // 1. 集合表示 {v1, v2, ...} (无集合描述法竖线或冒号)
                if (s.StartsWith("{") && s.EndsWith("}") && !s.Contains("|") && !s.Contains(":"))
                {
                    var inner = s.Trim('{', '}');
                    var parts = inner.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        foreach (var p in parts)
                        {
                            var trimmed = p.Trim();
                            if (TryExtractEquationValue(trimmed, out _, out var val))
                                roots.Add(val);
                            else
                                roots.Add(trimmed);
                        }
                        return roots.Count >= 2;
                    }
                }

                // 2. 逻辑或/和连接: 如 "x=1 或 x=-2", "1或-2", "x=1 或者 x=-2", "x_1=1且x_2=-2"
                if (s.Contains("或") || s.Contains("或者") || s.Contains("和") || s.Contains("且"))
                {
                    var parts = System.Text.RegularExpressions.Regex.Split(s, @"\s*(?:或者|或|和|且)\s*")
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .ToList();
                    if (parts.Count >= 2)
                    {
                        foreach (var p in parts)
                        {
                            var trimmed = p.Trim();
                            if (TryExtractEquationValue(trimmed, out _, out var val))
                                roots.Add(val);
                            else
                                roots.Add(trimmed);
                        }
                        return roots.Count >= 2;
                    }
                }

                // 3. 逗号/分号连接的同名变量解: 如 "x_1=1, x_2=-2", "x=1, x=-2"
                if ((s.Contains(',') || s.Contains('，') || s.Contains(';') || s.Contains('；')) && s.Contains('='))
                {
                    var parts = s.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var tempRoots = new List<string>();
                        string? baseVar = null;
                        bool allSameBaseVar = true;
                        foreach (var p in parts)
                        {
                            var trimmed = p.Trim();
                            if (TryExtractEquationValue(trimmed, out var vName, out var val))
                            {
                                var normV = NormalizeVarName(vName);
                                var m = System.Text.RegularExpressions.Regex.Match(normV, @"^([a-zA-Z]+)_?(\d+)?$");
                                var currBase = m.Success ? m.Groups[1].Value : normV;
                                if (baseVar == null) baseVar = currBase;
                                else if (!string.Equals(baseVar, currBase, StringComparison.OrdinalIgnoreCase))
                                {
                                    allSameBaseVar = false;
                                    break;
                                }
                                tempRoots.Add(val);
                            }
                            else
                            {
                                allSameBaseVar = false;
                                break;
                            }
                        }
                        if (allSameBaseVar && tempRoots.Count >= 2)
                        {
                            roots = tempRoots;
                            return true;
                        }
                    }
                }

                // 4. 无括号逗号/分号/顿号连接的纯标量列表 (如 "1, -2", "2; 1", "1、2")
                // 严密排除方程组(含 '=')、有序区间与空间点坐标
                if (!s.Contains('=') &&
                    (s.Contains(',') || s.Contains('，') || s.Contains(';') || s.Contains('；') || s.Contains('、')) &&
                    !s.StartsWith("(") && !s.EndsWith(")") && !s.StartsWith("[") && !s.EndsWith("]"))
                {
                    var parts = s.Split(new[] { ',', '，', ';', '；', '、' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var tempRoots = new List<string>();
                        bool allValid = true;
                        foreach (var p in parts)
                        {
                            var trimmed = p.Trim();
                            if (string.IsNullOrWhiteSpace(trimmed)) { allValid = false; break; }
                            tempRoots.Add(trimmed);
                        }
                        if (allValid && tempRoots.Count >= 2)
                        {
                            roots = tempRoots;
                            return true;
                        }
                    }
                }

                // 5. 正负号综合表达式展开: 如 "x = 1 +- \sqrt{3}", "1 +- sqrt(3)", "x = +-2", "+-3"
                if (s.Contains("+-") || s.Contains("±"))
                {
                    var sClean = s.Replace("±", "+-");
                    sClean = System.Text.RegularExpressions.Regex.Replace(sClean, @"^[a-zA-Z]+(?:_[a-zA-Z0-9,]+)?\s*=\s*", "");
                    var splitIdx = sClean.IndexOf("+-", StringComparison.Ordinal);
                    if (splitIdx > 0)
                    {
                        var prefix = sClean.Substring(0, splitIdx).Trim();
                        var suffix = sClean.Substring(splitIdx + 2).Trim();
                        if (!string.IsNullOrEmpty(suffix))
                        {
                            if (double.TryParse(prefix, out double pVal) && double.TryParse(suffix, out double sVal))
                            {
                                roots = new List<string> { (pVal + sVal).ToString("G"), (pVal - sVal).ToString("G") };
                            }
                            else
                            {
                                roots = new List<string> { $"{prefix}+{suffix}", $"{prefix}-{suffix}" };
                            }
                            return true;
                        }
                    }
                    else if (splitIdx == 0)
                    {
                        var suffix = sClean.Substring(2).Trim();
                        if (!string.IsNullOrEmpty(suffix))
                        {
                            roots = new List<string> { suffix, $"-{suffix}" };
                            return true;
                        }
                    }
                }

                return false;
            }

            // 根解集多向匹配比对
            bool uHasRoots = TryExtractEquationRoots(normUser, out var userRoots);
            bool cHasRoots = TryExtractEquationRoots(normCorrect, out var corrRoots);

            if (uHasRoots && cHasRoots)
            {
                if (userRoots.Count == corrRoots.Count && userRoots.Count >= 2)
                {
                    var remainingC = new List<string>(corrRoots);
                    bool allMatched = true;
                    foreach (var ur in userRoots)
                    {
                        var idx = remainingC.FindIndex(cr => cr == ur || CheckFillInBlankMatch(ur, cr));
                        if (idx >= 0) remainingC.RemoveAt(idx);
                        else { allMatched = false; break; }
                    }
                    if (allMatched && remainingC.Count == 0) return true;
                }
            }
            else if (uHasRoots && !cHasRoots &&
                     !normCorrect.StartsWith("(") && !normCorrect.EndsWith(")") &&
                     !normCorrect.StartsWith("[") && !normCorrect.EndsWith("]") &&
                     (normCorrect.Contains(',') || normCorrect.Contains('，') || normCorrect.Contains(';') || normCorrect.Contains('；') || normCorrect.Contains('、')))
            {
                var cParts = normCorrect.Split(new[] { ',', '，', ';', '；', '、' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
                if (cParts.Count == userRoots.Count && userRoots.Count >= 2)
                {
                    var remainingC = new List<string>(cParts);
                    bool allMatched = true;
                    foreach (var ur in userRoots)
                    {
                        var idx = remainingC.FindIndex(cr => cr == ur || CheckFillInBlankMatch(ur, cr));
                        if (idx >= 0) remainingC.RemoveAt(idx);
                        else { allMatched = false; break; }
                    }
                    if (allMatched && remainingC.Count == 0) return true;
                }
            }
            else if (!uHasRoots && cHasRoots &&
                     !normUser.StartsWith("(") && !normUser.EndsWith(")") &&
                     !normUser.StartsWith("[") && !normUser.EndsWith("]") &&
                     (normUser.Contains(',') || normUser.Contains('，') || normUser.Contains(';') || normUser.Contains('；') || normUser.Contains('、')))
            {
                var uParts = normUser.Split(new[] { ',', '，', ';', '；', '、' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
                if (uParts.Count == corrRoots.Count && corrRoots.Count >= 2)
                {
                    var remainingC = new List<string>(corrRoots);
                    bool allMatched = true;
                    foreach (var up in uParts)
                    {
                        var idx = remainingC.FindIndex(cr => cr == up || CheckFillInBlankMatch(up, cr));
                        if (idx >= 0) remainingC.RemoveAt(idx);
                        else { allMatched = false; break; }
                    }
                    if (allMatched && remainingC.Count == 0) return true;
                }
            }

            // 二维及三维直角空间坐标与方程组/向量双向等价容错 (如 (1,2) vs x=1,y=2; (1,2,3) vs x=1,y=2,z=3; \vec{a}=(1,2,3) 等)
            if (CheckCoordinateEquationMatch(normUser, normCorrect) || CheckCoordinateEquationMatch(normCorrect, normUser))
            {
                return true;
            }

            // 直线方程代数形式与移项等价匹配 (一般式 Ax+By+C=0、斜截式 y=kx+b、截距式、移项等)
            if (TryNormalizeLinearEquation(normUser, out var uLA, out var uLB, out var uLC) &&
                TryNormalizeLinearEquation(normCorrect, out var cLA, out var cLB, out var cLC))
            {
                if (Math.Abs(uLA - cLA) < 1e-5 && Math.Abs(uLB - cLB) < 1e-5 && Math.Abs(uLC - cLC) < 1e-5)
                {
                    return true;
                }
            }

            // 解析几何圆的方程等价匹配 (标准方程 (x-a)^2+(y-b)^2=r^2、一般方程 x^2+y^2+Dx+Ey+F=0、平方项次序对调与移项等价)
            if (TryNormalizeCircleEquation(normUser, out var uCirX, out var uCirY, out var uCirR2) &&
                TryNormalizeCircleEquation(normCorrect, out var cCirX, out var cCirY, out var cCirR2))
            {
                if (Math.Abs(uCirX - cCirX) < 1e-4 && Math.Abs(uCirY - cCirY) < 1e-4 && Math.Abs(uCirR2 - cCirR2) < 1e-4)
                {
                    return true;
                }
            }

            // 解析几何圆锥曲线（椭圆）标准方程等价匹配 (标准方程 x^2/a^2 + y^2/b^2 = 1、加法项对调、一般式 4x^2+9y^2=36 等)
            if (TryNormalizeEllipseEquation(normUser, out var uElX, out var uElY, out var uElA2, out var uElB2) &&
                TryNormalizeEllipseEquation(normCorrect, out var cElX, out var cElY, out var cElA2, out var cElB2))
            {
                if (Math.Abs(uElX - cElX) < 1e-4 && Math.Abs(uElY - cElY) < 1e-4 &&
                    Math.Abs(uElA2 - cElA2) < 1e-4 && Math.Abs(uElB2 - cElB2) < 1e-4)
                {
                    return true;
                }
            }

            // 解析几何圆锥曲线（双曲线）标准方程等价匹配 (水平/垂直标准方程、去分母展开、加减项对调、中心平移等)
            if (TryNormalizeHyperbolaEquation(normUser, out var uHypOri, out var uHypX, out var uHypY, out var uHypA2, out var uHypB2) &&
                TryNormalizeHyperbolaEquation(normCorrect, out var cHypOri, out var cHypX, out var cHypY, out var cHypA2, out var cHypB2))
            {
                if (uHypOri == cHypOri &&
                    Math.Abs(uHypX - cHypX) < 1e-4 && Math.Abs(uHypY - cHypY) < 1e-4 &&
                    Math.Abs(uHypA2 - cHypA2) < 1e-4 && Math.Abs(uHypB2 - cHypB2) < 1e-4)
                {
                    return true;
                }
            }

            // 解析几何圆锥曲线（抛物线）标准方程等价匹配 (标准式 y^2=2px/x^2=2py、显式函数 x=y^2/4、一般式 y^2-4x=0 等)
            if (TryNormalizeParabolaEquation(normUser, out var uParOri, out var uParX, out var uParY, out var uPar2P) &&
                TryNormalizeParabolaEquation(normCorrect, out var cParOri, out var cParX, out var cParY, out var cPar2P))
            {
                if (uParOri == cParOri &&
                    Math.Abs(uParX - cParX) < 1e-4 && Math.Abs(uParY - cParY) < 1e-4 &&
                    Math.Abs(uPar2P - cPar2P) < 1e-4)
                {
                    return true;
                }
            }

            // 平面向量点乘/数量积交换律等价匹配 (如 \vec{a}\cdot\vec{b} = -3 与 \vec{b}\cdot\vec{a} = -3 或 a*b=-3 与 -3)
            static bool TryParseVectorDotProduct(string s, out string v1, out string v2, out string val)
            {
                v1 = string.Empty; v2 = string.Empty; val = string.Empty;
                if (string.IsNullOrWhiteSpace(s)) return false;
                var m = System.Text.RegularExpressions.Regex.Match(s.Trim(), @"^([a-zA-Z]+(?:_[a-zA-Z0-9]+)?)\s*\*\s*([a-zA-Z]+(?:_[a-zA-Z0-9]+)?)\s*=\s*(.+)$");
                if (m.Success)
                {
                    v1 = m.Groups[1].Value.Trim();
                    v2 = m.Groups[2].Value.Trim();
                    val = m.Groups[3].Value.Trim();
                    return true;
                }
                return false;
            }

            if (TryParseVectorDotProduct(normUser, out var uV1, out var uV2, out var uVal) &&
                TryParseVectorDotProduct(normCorrect, out var cV1, out var cV2, out var cVal))
            {
                bool varsCommutative = (uV1.Equals(cV1, StringComparison.OrdinalIgnoreCase) && uV2.Equals(cV2, StringComparison.OrdinalIgnoreCase)) ||
                                      (uV1.Equals(cV2, StringComparison.OrdinalIgnoreCase) && uV2.Equals(cV1, StringComparison.OrdinalIgnoreCase));
                if (varsCommutative && (uVal == cVal || CheckFillInBlankMatch(uVal, cVal)))
                {
                    return true;
                }
            }
            else if (TryParseVectorDotProduct(normUser, out _, out _, out var uValOnly))
            {
                if (uValOnly == normCorrect || CheckFillInBlankMatch(uValOnly, normCorrect))
                {
                    return true;
                }
            }
            else if (TryParseVectorDotProduct(normCorrect, out _, out _, out var cValOnly))
            {
                if (normUser == cValOnly || CheckFillInBlankMatch(normUser, cValOnly))
                {
                    return true;
                }
            }

            // 向量模长等价匹配 (如 |\vec{a}| = 2 与 |a| = 2 或 2)
            static bool TryParseVectorModulus(string s, out string vName, out string val)
            {
                vName = string.Empty; val = string.Empty;
                if (string.IsNullOrWhiteSpace(s)) return false;
                var m = System.Text.RegularExpressions.Regex.Match(s.Trim(), @"^\|([a-zA-Z]+(?:_[a-zA-Z0-9]+)?)\|\s*=\s*(.+)$");
                if (m.Success)
                {
                    vName = m.Groups[1].Value.Trim();
                    val = m.Groups[2].Value.Trim();
                    return true;
                }
                return false;
            }

            if (TryParseVectorModulus(normUser, out var uModV, out var uModVal) &&
                TryParseVectorModulus(normCorrect, out var cModV, out var cModVal))
            {
                if (uModV.Equals(cModV, StringComparison.OrdinalIgnoreCase) &&
                    (uModVal == cModVal || CheckFillInBlankMatch(uModVal, cModVal)))
                {
                    return true;
                }
            }
            else if (TryParseVectorModulus(normUser, out _, out var uModValOnly))
            {
                if (uModValOnly == normCorrect || CheckFillInBlankMatch(uModValOnly, normCorrect))
                {
                    return true;
                }
            }
            else if (TryParseVectorModulus(normCorrect, out _, out var cModValOnly))
            {
                if (normUser == cModValOnly || CheckFillInBlankMatch(normUser, cModValOnly))
                {
                    return true;
                }
            }

            // 初高中代数因式分解乘积因子交换律等价匹配: 如 (x+1)(x-1) 与 (x-1)(x+1), 2(x+3)(x-4) 与 2(x-4)(x+3), -(x-1)(x-2) 与 -(x-2)(x-1), x(x-2) 与 (x-2)x
            if (CheckPolynomialFactorProductCommutativeMatch(normUser, normCorrect) ||
                CheckPolynomialFactorProductCommutativeMatch(user, correct))
            {
                return true;
            }

            // 物理电功/能量单位换算等价: 1 kW*h = 3.6*10^6 J = 1 度
            static bool CheckEnergyWorkEquivalence(string s1, string s2)
            {
                static bool TryExtractJoules(string s, out double joules)
                {
                    joules = 0;
                    if (string.IsNullOrWhiteSpace(s)) return false;
                    s = s.Trim().ToLowerInvariant().Replace(" ", "");

                    var kwhMatch = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?\d+(?:\.\d+)?)\s*(?:kw\*h|kw·h|kwh|千瓦时|千瓦·时|度(?:电)?)$");
                    if (kwhMatch.Success && double.TryParse(kwhMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double kwhVal))
                    {
                        joules = kwhVal * 3.6e6;
                        return true;
                    }
                    if (s == "1度" || s == "1度电" || s == "1kwh" || s == "1kw*h")
                    {
                        joules = 3.6e6;
                        return true;
                    }

                    var jMatch = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+|\*10\^[+-]?\d+)?)\s*(?:j|焦耳?)$");
                    if (jMatch.Success && TryParseScientificOrNumber(jMatch.Groups[1].Value, out double jVal))
                    {
                        joules = jVal;
                        return true;
                    }

                    var kjMatch = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+|\*10\^[+-]?\d+)?)\s*(?:kj|千焦)$");
                    if (kjMatch.Success && TryParseScientificOrNumber(kjMatch.Groups[1].Value, out double kjVal))
                    {
                        joules = kjVal * 1000.0;
                        return true;
                    }

                    return false;
                }

                if (TryExtractJoules(s1, out double j1) && TryExtractJoules(s2, out double j2))
                {
                    return Math.Abs(j1 - j2) < 1e-3 || Math.Abs(j1 - j2) / Math.Max(Math.Abs(j1), Math.Abs(j2)) < 1e-4;
                }
                return false;
            }

            if (CheckEnergyWorkEquivalence(user, correct) || CheckEnergyWorkEquivalence(normUser, normCorrect))
            {
                return true;
            }

            // 物理与工程单位多量纲换算智能匹配:
            // 1. 频率: 1 kHz = 1000 Hz, 1 MHz = 10^6 Hz, 1 GHz = 10^9 Hz
            // 2. 电容: 1 uF = 10^-6 F, 1 nF = 10^-9 F, 1 pF = 10^-12 F
            // 3. 速度: 36 km/h = 10 m/s (v_ms = v_kmh / 3.6)
            // 4. 功率: 1 kW = 1000 W, 1 MW = 10^6 W
            // 5. 压强: 100 kPa = 100000 Pa = 10^5 Pa, 1 MPa = 10^6 Pa
            static bool CheckScientificPhysicalQuantityEquivalence(string s1, string s2)
            {
                if (string.IsNullOrWhiteSpace(s1) || string.IsNullOrWhiteSpace(s2)) return false;
                s1 = s1.Trim().ToLowerInvariant().Replace(" ", "");
                s2 = s2.Trim().ToLowerInvariant().Replace(" ", "");

                // A. 频率 (以 Hz 为基底)
                static bool TryExtractHz(string s, out double hz)
                {
                    hz = 0;
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+|\*10\^[+-]?\d+)?)\s*(hz|khz|mhz|ghz|赫兹|千赫|兆赫|吉赫)$");
                    if (m.Success && TryParseScientificOrNumber(m.Groups[1].Value, out double val))
                    {
                        string unit = m.Groups[2].Value;
                        hz = unit switch
                        {
                            "ghz" or "吉赫" => val * 1e9,
                            "mhz" or "兆赫" => val * 1e6,
                            "khz" or "千赫" => val * 1e3,
                            _ => val
                        };
                        return true;
                    }
                    return false;
                }

                if (TryExtractHz(s1, out double hz1) && TryExtractHz(s2, out double hz2))
                {
                    return Math.Abs(hz1 - hz2) < 1e-4 || Math.Abs(hz1 - hz2) / Math.Max(Math.Abs(hz1), Math.Abs(hz2)) < 1e-4;
                }

                // B. 电容 (以 F 为基底)
                static bool TryExtractFarad(string s, out double farad)
                {
                    farad = 0;
                    s = s.Replace("μ", "u").Replace("\\mu", "u");
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+|\*10\^[+-]?\d+)?)\s*(f|uf|nf|pf|法拉|微法|纳法|皮法)$");
                    if (m.Success && TryParseScientificOrNumber(m.Groups[1].Value, out double val))
                    {
                        string unit = m.Groups[2].Value;
                        farad = unit switch
                        {
                            "pf" or "皮法" => val * 1e-12,
                            "nf" or "纳法" => val * 1e-9,
                            "uf" or "微法" => val * 1e-6,
                            _ => val
                        };
                        return true;
                    }
                    return false;
                }

                if (TryExtractFarad(s1, out double f1) && TryExtractFarad(s2, out double f2))
                {
                    return Math.Abs(f1 - f2) < 1e-15 || Math.Abs(f1 - f2) / Math.Max(Math.Abs(f1), Math.Abs(f2)) < 1e-4;
                }

                // C. 速度 (以 m/s 为基底)
                static bool TryExtractSpeedMs(string s, out double ms)
                {
                    ms = 0;
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+|\*10\^[+-]?\d+)?)\s*(m/s|米/秒|米每秒|km/h|千米/小时|千米每小时|公里/小时)$");
                    if (m.Success && TryParseScientificOrNumber(m.Groups[1].Value, out double val))
                    {
                        string unit = m.Groups[2].Value;
                        ms = (unit == "km/h" || unit.Contains("千米") || unit.Contains("公里")) ? (val / 3.6) : val;
                        return true;
                    }
                    return false;
                }

                if (TryExtractSpeedMs(s1, out double v1) && TryExtractSpeedMs(s2, out double v2))
                {
                    return Math.Abs(v1 - v2) < 1e-3 || Math.Abs(v1 - v2) / Math.Max(Math.Abs(v1), Math.Abs(v2)) < 1e-4;
                }

                // D. 功率 (以 W 为基底)
                static bool TryExtractWatt(string s, out double watt)
                {
                    watt = 0;
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?\d+(?:\.\d+)?(?:e[+-]?\d+|\*10\^[+-]?\d+)?)\s*(w|kw|mw|瓦特?|千瓦|兆瓦)$");
                    if (m.Success && TryParseScientificOrNumber(m.Groups[1].Value, out double val))
                    {
                        string unit = m.Groups[2].Value;
                        watt = unit switch
                        {
                            "mw" or "兆瓦" => val * 1e6,
                            "kw" or "千瓦" => val * 1e3,
                            _ => val
                        };
                        return true;
                    }
                    return false;
                }

                if (TryExtractWatt(s1, out double w1) && TryExtractWatt(s2, out double w2))
                {
                    return Math.Abs(w1 - w2) < 1e-3 || Math.Abs(w1 - w2) / Math.Max(Math.Abs(w1), Math.Abs(w2)) < 1e-4;
                }

                // E. 压强 (以 Pa 为基底, 1 Pa = 1 N/m^2)
                static bool TryExtractPascal(string s, out double pa)
                {
                    pa = 0;
                    s = s.Replace("n/m²", "n/m^2").Replace("牛/米^2", "n/m^2").Replace("牛/米²", "n/m^2").Replace("牛/平方米", "n/m^2").Replace("牛每平方米", "n/m^2");
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?(?:(?:\d+(?:\.\d+)?|\.\d+)(?:e[+-]?\d+|\*10\^[+-]?\d+)?|10\^[+-]?\d+))\s*(pa|kpa|mpa|帕(?:斯卡)?|千帕|兆帕|n/m\^2)$");
                    if (m.Success && TryParseScientificOrNumber(m.Groups[1].Value, out double val))
                    {
                        string unit = m.Groups[2].Value;
                        pa = unit switch
                        {
                            "mpa" or "兆帕" => val * 1e6,
                            "kpa" or "千帕" => val * 1e3,
                            _ => val
                        };
                        return true;
                    }
                    return false;
                }

                if (TryExtractPascal(s1, out double pa1) && TryExtractPascal(s2, out double pa2))
                {
                    return Math.Abs(pa1 - pa2) < 1e-3 || Math.Abs(pa1 - pa2) / Math.Max(Math.Abs(pa1), Math.Abs(pa2)) < 1e-4;
                }

                // F. 重力加速度/引力场强 (以 N/kg 为基底, 1 N/kg = 1 m/s^2)
                static bool TryExtractGravitationalFieldOrAcc(string s, out double g)
                {
                    g = 0;
                    s = s.Replace("m/s²", "m/s^2").Replace("牛/千克", "n/kg").Replace("牛每千克", "n/kg").Replace("米/秒^2", "m/s^2").Replace("米每二次方秒", "m/s^2");
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?(?:(?:\d+(?:\.\d+)?|\.\d+)(?:e[+-]?\d+|\*10\^[+-]?\d+)?|10\^[+-]?\d+))\s*(n/kg|m/s\^2)$");
                    if (m.Success && TryParseScientificOrNumber(m.Groups[1].Value, out double val))
                    {
                        g = val;
                        return true;
                    }
                    return false;
                }

                if (TryExtractGravitationalFieldOrAcc(s1, out double g1) && TryExtractGravitationalFieldOrAcc(s2, out double g2))
                {
                    return Math.Abs(g1 - g2) < 1e-3 || Math.Abs(g1 - g2) / Math.Max(Math.Abs(g1), Math.Abs(g2)) < 1e-4;
                }

                // G. 密度 (以 kg/m^3 为基底, 1 g/cm^3 = 1000 kg/m^3)
                static bool TryExtractDensity(string s, out double density)
                {
                    density = 0;
                    s = s.Replace("kg/m³", "kg/m^3").Replace("g/cm³", "g/cm^3")
                         .Replace("千克/立方米", "kg/m^3").Replace("千克每立方米", "kg/m^3")
                         .Replace("克/立方厘米", "g/cm^3").Replace("克每立方厘米", "g/cm^3");
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?(?:(?:\d+(?:\.\d+)?|\.\d+)(?:e[+-]?\d+|\*10\^[+-]?\d+)?|10\^[+-]?\d+))\s*(kg/m\^3|g/cm\^3)$");
                    if (m.Success && TryParseScientificOrNumber(m.Groups[1].Value, out double val))
                    {
                        string unit = m.Groups[2].Value;
                        density = unit == "g/cm^3" ? (val * 1000.0) : val;
                        return true;
                    }
                    return false;
                }

                if (TryExtractDensity(s1, out double rho1) && TryExtractDensity(s2, out double rho2))
                {
                    return Math.Abs(rho1 - rho2) < 1e-3 || Math.Abs(rho1 - rho2) / Math.Max(Math.Abs(rho1), Math.Abs(rho2)) < 1e-4;
                }

                // H. 能量与功 (以 J 为基底, 1 kJ = 1000 J, 1 MJ = 10^6 J, 1 kW·h = 3.6×10^6 J, 1 eV = 1.602×10^-19 J)
                static bool TryExtractJoule(string s, out double joule)
                {
                    joule = 0;
                    s = s.Replace("kw·h", "kwh").Replace("kw*h", "kwh").Replace("kw·时", "kwh")
                         .Replace("千瓦·时", "kwh").Replace("千瓦时", "kwh").Replace("千瓦*时", "kwh")
                         .Replace("w·s", "ws").Replace("w*s", "ws")
                         .Replace("度", "kwh");
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?(?:(?:\d+(?:\.\d+)?|\.\d+)(?:e[+-]?\d+|\*10\^[+-]?\d+)?|10\^[+-]?\d+))\s*(j|kj|mj|gj|焦耳?|千焦|兆焦|吉焦|kwh|ws|ev|kev|mev|电子伏特?|千电子伏|兆电子伏)$");
                    if (m.Success && TryParseScientificOrNumber(m.Groups[1].Value, out double val))
                    {
                        string unit = m.Groups[2].Value;
                        joule = unit switch
                        {
                            "kwh" => val * 3.6e6,
                            "gj" or "吉焦" => val * 1e9,
                            "mj" or "兆焦" => val * 1e6,
                            "kj" or "千焦" => val * 1e3,
                            "ev" or "电子伏" or "电子伏特" => val * 1.602e-19,
                            "kev" or "千电子伏" => val * 1.602e-16,
                            "mev" or "兆电子伏" => val * 1.602e-13,
                            _ => val
                        };
                        return true;
                    }
                    return false;
                }

                if (TryExtractJoule(s1, out double j1) && TryExtractJoule(s2, out double j2))
                {
                    return Math.Abs(j1 - j2) < 1e-3 || Math.Abs(j1 - j2) / Math.Max(Math.Abs(j1), Math.Abs(j2)) < 1e-4;
                }

                // I. 电压与电势差 (以 V 为基底, 1 kV = 1000 V, 1 mV = 10^-3 V, 1 μV = 10^-6 V)
                static bool TryExtractVolt(string s, out double volt)
                {
                    volt = 0;
                    s = s.Replace("μ", "u").Replace("\\mu", "u");
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?(?:(?:\d+(?:\.\d+)?|\.\d+)(?:e[+-]?\d+|\*10\^[+-]?\d+)?|10\^[+-]?\d+))\s*(v|kv|mv|uv|伏特?|千伏|毫伏|微伏)$");
                    if (m.Success && TryParseScientificOrNumber(m.Groups[1].Value, out double val))
                    {
                        string unit = m.Groups[2].Value;
                        volt = unit switch
                        {
                            "kv" or "千伏" => val * 1e3,
                            "mv" or "毫伏" => val * 1e-3,
                            "uv" or "微伏" => val * 1e-6,
                            _ => val
                        };
                        return true;
                    }
                    return false;
                }

                if (TryExtractVolt(s1, out double volt1) && TryExtractVolt(s2, out double volt2))
                {
                    return Math.Abs(volt1 - volt2) < 1e-4 || Math.Abs(volt1 - volt2) / Math.Max(Math.Abs(volt1), Math.Abs(volt2)) < 1e-4;
                }

                // J. 电阻 (以 Ω 为基底, 1 kΩ = 1000 Ω, 1 MΩ = 10^6 Ω)
                static bool TryExtractOhm(string s, out double ohm)
                {
                    ohm = 0;
                    s = s.Replace("komega", "kω").Replace("momega", "mω").Replace("gomega", "gω").Replace("omega", "ω")
                         .Replace("kohm", "kω").Replace("mohm", "mω").Replace("ohm", "ω")
                         .Replace("千欧", "kω").Replace("兆欧", "mω").Replace("欧姆", "ω").Replace("欧", "ω");
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?(?:(?:\d+(?:\.\d+)?|\.\d+)(?:e[+-]?\d+|\*10\^[+-]?\d+)?|10\^[+-]?\d+))\s*(ω|kω|mω|gω)$");
                    if (m.Success && TryParseScientificOrNumber(m.Groups[1].Value, out double val))
                    {
                        string unit = m.Groups[2].Value;
                        ohm = unit switch
                        {
                            "gω" => val * 1e9,
                            "mω" => val * 1e6,
                            "kω" => val * 1e3,
                            _ => val
                        };
                        return true;
                    }
                    return false;
                }

                if (TryExtractOhm(s1, out double r1) && TryExtractOhm(s2, out double r2))
                {
                    return Math.Abs(r1 - r2) < 1e-3 || Math.Abs(r1 - r2) / Math.Max(Math.Abs(r1), Math.Abs(r2)) < 1e-4;
                }

                // K. 电流 (以 A 为基底, 1 kA = 1000 A, 1 mA = 10^-3 A, 1 μA = 10^-6 A)
                static bool TryExtractAmpere(string s, out double amp)
                {
                    amp = 0;
                    s = s.Replace("μ", "u").Replace("\\mu", "u");
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?(?:(?:\d+(?:\.\d+)?|\.\d+)(?:e[+-]?\d+|\*10\^[+-]?\d+)?|10\^[+-]?\d+))\s*(a|ka|ma|ua|安培?|千安|毫安|微安)$");
                    if (m.Success && TryParseScientificOrNumber(m.Groups[1].Value, out double val))
                    {
                        string unit = m.Groups[2].Value;
                        amp = unit switch
                        {
                            "ka" or "千安" => val * 1e3,
                            "ma" or "毫安" => val * 1e-3,
                            "ua" or "微安" => val * 1e-6,
                            _ => val
                        };
                        return true;
                    }
                    return false;
                }

                if (TryExtractAmpere(s1, out double i1) && TryExtractAmpere(s2, out double i2))
                {
                    return Math.Abs(i1 - i2) < 1e-5 || Math.Abs(i1 - i2) / Math.Max(Math.Abs(i1), Math.Abs(i2)) < 1e-4;
                }

                return false;
            }

            if (CheckScientificPhysicalQuantityEquivalence(user, correct) || CheckScientificPhysicalQuantityEquivalence(normUser, normCorrect))
            {
                return true;
            }

            // 特殊角角度制与弧度制等价匹配 (如 30° / 30 与 \pi/6, 45° 与 \pi/4, 60° 与 \pi/3, 90° 与 \pi/2, 180° 与 \pi)
            static bool CheckAngleRadianEquivalence(string a1, string a2)
            {
                static bool TryExtractAngleInDegrees(string s, out double deg)
                {
                    deg = 0;
                    if (string.IsNullOrWhiteSpace(s)) return false;
                    s = s.Trim().ToLowerInvariant().Replace(" ", "");

                    if (s.Contains("pi"))
                    {
                        if (s == "pi") { deg = 180.0; return true; }
                        if (s == "2pi" || s == "2*pi") { deg = 360.0; return true; }
                        if (s == "pi/2" || s == "1/2pi" || s == "(1/2)*pi") { deg = 90.0; return true; }
                        if (s == "pi/3" || s == "1/3pi" || s == "(1/3)*pi") { deg = 60.0; return true; }
                        if (s == "pi/4" || s == "1/4pi" || s == "(1/4)*pi") { deg = 45.0; return true; }
                        if (s == "pi/6" || s == "1/6pi" || s == "(1/6)*pi") { deg = 30.0; return true; }
                        if (s == "2pi/3" || s == "2/3pi" || s == "(2/3)*pi") { deg = 120.0; return true; }
                        if (s == "3pi/4" || s == "3/4pi" || s == "(3/4)*pi") { deg = 135.0; return true; }
                        if (s == "5pi/6" || s == "5/6pi" || s == "(5/6)*pi") { deg = 150.0; return true; }
                        if (s == "3pi/2" || s == "3/2pi" || s == "(3/2)*pi") { deg = 270.0; return true; }
                    }

                    if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double dVal))
                    {
                        deg = dVal;
                        return true;
                    }
                    return false;
                }

                if (TryExtractAngleInDegrees(a1, out double d1) && TryExtractAngleInDegrees(a2, out double d2))
                {
                    if ((a1.Contains("pi") || a2.Contains("pi")) && Math.Abs(d1 - d2) < 1e-4)
                    {
                        return true;
                    }
                }
                return false;
            }

            if (CheckAngleRadianEquivalence(user, correct) || CheckAngleRadianEquivalence(normUser, normCorrect))
            {
                return true;
            }

            // 复数代数形式加法交换律与纯虚数等价匹配 (z = a + bi, bi + a, 0 + bi 等，需包含虚数单位 i)
            if ((normUser.Contains('i') || normCorrect.Contains('i')) &&
                TryParseComplex(normUser, out var uCplxR, out var uCplxI) &&
                TryParseComplex(normCorrect, out var cCplxR, out var cCplxI))
            {
                if (AreNumbersClose(uCplxR, cCplxR) && AreNumbersClose(uCplxI, cCplxI))
                {
                    return true;
                }
            }

            // 规范化多元方程解集无序置换等价匹配: 如 "y=3,x=2" 与 "x=2,y=3"
            if (normUser.Contains(',') && normUser.Contains('=') && normCorrect.Contains(',') && normCorrect.Contains('='))
            {
                var uEqs = normUser.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).ToList();
                var cEqs = normCorrect.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).ToList();
                if (uEqs.Count > 1 && uEqs.Count == cEqs.Count)
                {
                    var remainingC = new List<string>(cEqs);
                    bool allMatch = true;
                    foreach (var ue in uEqs)
                    {
                        var idx = remainingC.FindIndex(ce => ce == ue || CheckFillInBlankMatch(ue, ce));
                        if (idx >= 0) remainingC.RemoveAt(idx);
                        else { allMatch = false; break; }
                    }
                    if (allMatch && remainingC.Count == 0) return true;
                }
            }

            var strippedUser = StripCommonUnits(normUser);
            var strippedCorrect = StripCommonUnits(normCorrect);
            if (!string.IsNullOrEmpty(strippedUser) && strippedUser == strippedCorrect) return true;

            // 有限集合元素无序置换等价匹配: 如 {1, 2} 与 {2, 1}, {-3, 0, 5} 与 {5, -3, 0}
            if (normUser.StartsWith("{") && normUser.EndsWith("}") && !normUser.Contains("|") &&
                normCorrect.StartsWith("{") && normCorrect.EndsWith("}") && !normCorrect.Contains("|"))
            {
                var uElems = normUser.Trim('{', '}').Split(',', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).ToList();
                var cElems = normCorrect.Trim('{', '}').Split(',', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).ToList();
                if (uElems.Count == cElems.Count && uElems.Count > 0)
                {
                    var remainingC = new List<string>(cElems);
                    bool allMatched = true;
                    foreach (var ue in uElems)
                    {
                        var idx = remainingC.FindIndex(ce => ce == ue || CheckFillInBlankMatch(ue, ce));
                        if (idx >= 0)
                        {
                            remainingC.RemoveAt(idx);
                        }
                        else
                        {
                            allMatched = false;
                            break;
                        }
                    }
                    if (allMatched && remainingC.Count == 0) return true;
                }
            }

            // 区间并集组合等价匹配: 如 (-inf, 1]u[3, +inf) 与 [3, +inf)u(-inf, 1]
            if (normUser.Contains('u') && normCorrect.Contains('u'))
            {
                var uIntervals = normUser.Split('u', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                var cIntervals = normCorrect.Split('u', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                if (uIntervals.Count > 1 && uIntervals.Count == cIntervals.Count)
                {
                    var remainingC = new List<string>(cIntervals);
                    bool allMatched = true;
                    foreach (var ui in uIntervals)
                    {
                        var idx = remainingC.FindIndex(ci => ci == ui || CheckFillInBlankMatch(ui, ci));
                        if (idx >= 0)
                        {
                            remainingC.RemoveAt(idx);
                        }
                        else
                        {
                            allMatched = false;
                            break;
                        }
                    }
                    if (allMatched && remainingC.Count == 0) return true;
                }
            }

            // 分割多答案备选项：注意仅在非数字间使用 '/' 进行拆分，保护数学最简分数（如 2/3 不被切碎）
            var rawDelimiters = new[] { ';', '；', '|', '或' };
            var candidateList = new List<string>();
            foreach (var segment in correct.Split(rawDelimiters, StringSplitOptions.RemoveEmptyEntries))
            {
                var subParts = System.Text.RegularExpressions.Regex.Split(segment, @"(?<!\d)\s*/\s*|\s*/\s*(?!\d)");
                foreach (var sp in subParts)
                {
                    if (!string.IsNullOrWhiteSpace(sp)) candidateList.Add(Normalize(sp));
                }
            }

            if (candidateList.Any(a => a == normUser || NormalizeChemical(a) == chemUser)) return true;

            // 针对正负号解的扩展容错 (如 \pm 2 或 +-2 与 "2或-2", "2,-2", "-2或2", "-2,2", "2和-2")
            var pmMatch = System.Text.RegularExpressions.Regex.Match(normCorrect, @"^(?:[a-zA-Z]=\s*)?\+-(.+)$");
            if (pmMatch.Success)
            {
                var baseVal = pmMatch.Groups[1].Value.Trim();
                candidateList.Add($"{baseVal}或-{baseVal}");
                candidateList.Add($"-{baseVal}或{baseVal}");
                candidateList.Add($"{baseVal},-{baseVal}");
                candidateList.Add($"-{baseVal},{baseVal}");
                candidateList.Add($"{baseVal}和-{baseVal}");
                candidateList.Add($"-{baseVal}和{baseVal}");
            }
            var userPmMatch = System.Text.RegularExpressions.Regex.Match(normUser, @"^(?:[a-zA-Z]=\s*)?\+-(.+)$");
            if (userPmMatch.Success)
            {
                var baseVal = userPmMatch.Groups[1].Value.Trim();
                if (normCorrect == $"{baseVal}或-{baseVal}" || normCorrect == $"-{baseVal}或{baseVal}" ||
                    normCorrect == $"{baseVal},-{baseVal}" || normCorrect == $"-{baseVal},{baseVal}" ||
                    normCorrect == $"{baseVal}和-{baseVal}" || normCorrect == $"-{baseVal}和{baseVal}")
                {
                    return true;
                }
            }
            if (candidateList.Any(a => a == normUser || NormalizeChemical(a) == chemUser)) return true;

            // 复合多空题 (有序与无序多空位智能切分与比对: 如 "(1) 动能 (2) 势能" vs "动能; 势能", "① 3 ② 5" vs "(1) 3 (2) 5", "1. 动能 2. 势能" vs "动能; 势能", "(I) 3 (II) 5", "[1] A [2] B")
            static List<string> ExtractBlanks(string s)
            {
                s = s.Trim();
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\begin\{cases\}|\\end\{cases\}|\\begin\{aligned\}|\\end\{aligned\}", "");
                s = s.Replace(@"\\", ",");
                // 1. 带圈序号 ①-⑳
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳]"))
                {
                    var parts = System.Text.RegularExpressions.Regex.Split(s, @"[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳]")
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
                    if (parts.Count > 1) return parts;
                }
                // 2. 阿拉伯数字括号 (1) 或 （1）
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"(?:\(\d+\)|（\d+）)"))
                {
                    var parts = System.Text.RegularExpressions.Regex.Split(s, @"(?:\(\d+\)|（\d+）)")
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
                    if (parts.Count > 1) return parts;
                }
                // 3. 罗马数字序号 (I), (ii), I., ii.
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"(?:\([ivxIVX]+\)|（[ivxIVX]+）|\b[ivxIVX]+[.、])"))
                {
                    var parts = System.Text.RegularExpressions.Regex.Split(s, @"(?:\([ivxIVX]+\)|（[ivxIVX]+）|\b[ivxIVX]+[.、])")
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
                    if (parts.Count > 1) return parts;
                }
                // 4. 中括号/方头括号序号 [1], [2], 【1】, 【2】
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"(?:\[\d+\]|【\d+】)"))
                {
                    var parts = System.Text.RegularExpressions.Regex.Split(s, @"(?:\[\d+\]|【\d+】)")
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
                    if (parts.Count > 1) return parts;
                }
                // 5. 阿拉伯数字序号前缀 1. 2. 1、 2、 (仅在空白或开头后引导选项，排除等号或代数式中的数值)
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"(?<=(?:^|\s))\d+[.、]\s*"))
                {
                    var parts = System.Text.RegularExpressions.Regex.Split(s, @"(?<=(?:^|\s))\d+[.、]\s*")
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
                    if (parts.Count > 1) return parts;
                }
                // 6. 中文大写数字 一、 二、
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"(?:[一二三四五六七八九十]+、)"))
                {
                    var parts = System.Text.RegularExpressions.Regex.Split(s, @"(?:[一二三四五六七八九十]+、)")
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
                    if (parts.Count > 1) return parts;
                }
                // 7. 分号、逗号、顿号顶层分隔 (自动保护括号内的数学区间、坐标与集合，如 [1, 2] 不会被截断)
                if (s.Contains(';') || s.Contains('；') || s.Contains('，') || s.Contains('、') || s.Contains(','))
                {
                    static List<string> SplitTopLevel(string text, char[] delimiters)
                    {
                        var list = new List<string>();
                        int paren = 0, bracket = 0, brace = 0;
                        int start = 0;
                        for (int i = 0; i < text.Length; i++)
                        {
                            char c = text[i];
                            if (c == '(' || c == '（') paren++;
                            else if (c == ')' || c == '）') { if (paren > 0) paren--; }
                            else if (c == '[' || c == '【') bracket++;
                            else if (c == ']' || c == '】') { if (bracket > 0) bracket--; }
                            else if (c == '{') brace++;
                            else if (c == '}') { if (brace > 0) brace--; }
                            else if (paren == 0 && bracket == 0 && brace == 0)
                            {
                                if (delimiters.Contains(c))
                                {
                                    // 排除千分位纯数字逗号 (如 1,000)
                                    if (c == ',' && i > 0 && i < text.Length - 1 && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))
                                    {
                                        continue;
                                    }
                                    var part = text.Substring(start, i - start).Trim();
                                    if (!string.IsNullOrEmpty(part)) list.Add(part);
                                    start = i + 1;
                                }
                            }
                        }
                        if (start < text.Length)
                        {
                            var part = text.Substring(start).Trim();
                            if (!string.IsNullOrEmpty(part)) list.Add(part);
                        }
                        return list;
                    }

                    var parts = SplitTopLevel(s, new[] { ';', '；', '，', '、', ',' });
                    if (parts.Count > 1) return parts;
                }
                // 8. 中文词语间空白或连续空白/制表符分隔 (如 "动能 势能" 或 "增大  减小")
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"\s{2,}|\t|(?<=[\u4e00-\u9fa5])\s+(?=[\u4e00-\u9fa5])"))
                {
                    var parts = System.Text.RegularExpressions.Regex.Split(s, @"\s{2,}|\t|(?<=[\u4e00-\u9fa5])\s+(?=[\u4e00-\u9fa5])")
                        .Select(p => p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
                    if (parts.Count > 1) return parts;
                }
                return new List<string> { s };
            }

            var userBlanks = ExtractBlanks(user);
            var correctBlanks = ExtractBlanks(correct);
            if (userBlanks.Count > 1 && correctBlanks.Count > 1 && userBlanks.Count == correctBlanks.Count)
            {
                bool allOrderedMatch = true;
                for (int i = 0; i < userBlanks.Count; i++)
                {
                    if (!CheckFillInBlankMatch(userBlanks[i], correctBlanks[i]))
                    {
                        allOrderedMatch = false;
                        break;
                    }
                }
                if (allOrderedMatch) return true;

                // 无序多空容错：两边集合元素能够形成一对一匹配
                var remainingCorrect = new List<string>(correctBlanks);
                bool allSetMatch = true;
                foreach (var ub in userBlanks)
                {
                    var matchedIndex = remainingCorrect.FindIndex(cb => CheckFillInBlankMatch(ub, cb));
                    if (matchedIndex >= 0)
                    {
                        remainingCorrect.RemoveAt(matchedIndex);
                    }
                    else
                    {
                        allSetMatch = false;
                        break;
                    }
                }
                if (allSetMatch && remainingCorrect.Count == 0) return true;
            }

            // 广义单变量方程解形式容错：如 "y=6", "t=10", "v_0=5", "a=2", "x=6" 与 "6", 
            // 以及希腊字母变量 "\lambda=500", "\theta=45", "\rho=1.0", "\omega=10" 与多字符角标 "x_1=3", "v_{max}=20"
            static string NormalizeVarName(string v) => v.TrimStart('\\').Replace("{", "").Replace("}", "").Trim().ToLowerInvariant();

            static bool AreRootVariablesEquivalent(string v1, string v2)
            {
                var norm1 = NormalizeVarName(v1);
                var norm2 = NormalizeVarName(v2);
                if (string.Equals(norm1, norm2, StringComparison.OrdinalIgnoreCase)) return true;
                // 检查是否为同名带数字下标的解集变量 (如 x1 vs x2, x_1 vs x_2, y1 vs y2)
                var m1 = System.Text.RegularExpressions.Regex.Match(norm1, @"^([a-zA-Z]+)_?(\d+)$");
                var m2 = System.Text.RegularExpressions.Regex.Match(norm2, @"^([a-zA-Z]+)_?(\d+)$");
                if (m1.Success && m2.Success && string.Equals(m1.Groups[1].Value, m2.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                return false;
            }

            static bool TryExtractEquationValue(string s, out string varName, out string valPart)
            {
                var match = System.Text.RegularExpressions.Regex.Match(s, @"^(\\[a-zA-Z]+|[a-zA-Z]+(?:_\{?[a-zA-Z0-9\u4e00-\u9fa5]+\}?|\d+)?)=([^=,;，；、|]+)$");
                if (match.Success)
                {
                    varName = match.Groups[1].Value.Trim();
                    valPart = match.Groups[2].Value.Trim();
                    return true;
                }
                varName = string.Empty;
                valPart = string.Empty;
                return false;
            }

            if (TryExtractEquationValue(normCorrect, out var corrVar, out var corrVal))
            {
                if (!TryExtractEquationValue(normUser, out _, out _))
                {
                    if (normUser == corrVal || CheckFillInBlankMatch(normUser, corrVal)) return true;
                }
                else if (TryExtractEquationValue(normUser, out var userVar, out var userVal))
                {
                    if (AreRootVariablesEquivalent(corrVar, userVar))
                    {
                        if (userVal == corrVal || CheckFillInBlankMatch(userVal, corrVal)) return true;
                    }
                }
            }
            else if (TryExtractEquationValue(normUser, out _, out var userVal))
            {
                if (userVal == normCorrect || CheckFillInBlankMatch(userVal, normCorrect)) return true;
            }

            // 纯数值、百分比、分数与科学记数法智能等价容错
            // (如 3.0*10^8 vs 3e8 vs 3.0 \times 10^8, 1.6*10^-19 vs 1.6e-19, 0.5 vs 0.50, 1/2 vs 0.5, 50% vs 0.5)
            static bool TryParseScientificOrNumber(string s, out double val)
            {
                s = s.Trim();
                if (s.StartsWith("+") && s.Length > 1)
                {
                    s = s.Substring(1).Trim();
                }

                if (s.EndsWith("%"))
                {
                    var pctPart = s.Substring(0, s.Length - 1).Trim().TrimEnd('\\');
                    if (double.TryParse(pctPart, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pctVal))
                    {
                        val = pctVal / 100.0;
                        return true;
                    }
                }

                // 匹配纯 10 的次幂 (如 10^5, 10^-3, 10^{5}, 10^{-3}, 10^(5), -10^5)
                var purePow10Match = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?)\s*10\^[\{\(]?([+-]?\d+)[\}\)]?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (purePow10Match.Success)
                {
                    double sign = purePow10Match.Groups[1].Value == "-" ? -1.0 : 1.0;
                    if (int.TryParse(purePow10Match.Groups[2].Value, out var exp))
                    {
                        val = sign * Math.Pow(10, exp);
                        return true;
                    }
                }

                // 匹配形如 3.0*10^8, 3.0x10^8, 3.0×10^8, 3.0·10^8, 3*10^{8}, 3*10^(8), 1.6*10^-19, 1.6*10^{-19}, 1.6*10^(-19) 以及 .5*10^3
                var sciMatch = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+))\s*(?:\*|x|×|·|•|∙|\\times|\\cdot)\s*10\^[\{\(]?([+-]?\d+)[\}\)]?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (sciMatch.Success)
                {
                    if (double.TryParse(sciMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var mantissa) &&
                        int.TryParse(sciMatch.Groups[2].Value, out var exp))
                    {
                        val = mantissa * Math.Pow(10, exp);
                        return true;
                    }
                }

                // 辅助函数：解析带符号单项数值、纯小数、带系数根式或带系数圆周率 (如 2, 0.5, sqrt(2), 2*sqrt(3), -sqrt(2), root(3,8), pi, 2*pi)
                static bool TryParseSingleTerm(string t, out double termVal)
                {
                    termVal = 0;
                    if (string.IsNullOrWhiteSpace(t)) return false;
                    t = t.Trim();
                    if (t.StartsWith("(") && t.EndsWith(")"))
                    {
                        t = t.Substring(1, t.Length - 2).Trim();
                    }
                    if (double.TryParse(t, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out termVal)) return true;

                    // 简易带符号分数与根式分式求值 (如 1/4, -1/2, 3/4, sqrt(2)/2)
                    if (t.Contains('/') && !t.Contains("log") && !t.Contains("ln") && !t.Contains("lg") && !t.Contains("sin") && !t.Contains("cos") && !t.Contains("tan"))
                    {
                        var fracParts = t.Split('/');
                        if (fracParts.Length == 2 &&
                            TryParseSingleTerm(fracParts[0].Trim(), out var numVal) &&
                            TryParseSingleTerm(fracParts[1].Trim(), out var denVal) &&
                            Math.Abs(denVal) > 1e-9)
                        {
                            termVal = numVal / denVal;
                            return true;
                        }
                    }

                    // 匹配单项根号与带系数根号 (如 sqrt(2), 2*sqrt(3), -sqrt(2), 3sqrt(5), +sqrt(3))
                    var sqrtMatch = System.Text.RegularExpressions.Regex.Match(t, @"^([+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)?)\s*\*?\s*sqrt\(([0-9]+(?:\.[0-9]+)?|\.[0-9]+)\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (sqrtMatch.Success)
                    {
                        var coeffStr = sqrtMatch.Groups[1].Value.Trim();
                        double coeff = 1.0;
                        if (coeffStr == "-") coeff = -1.0;
                        else if (coeffStr == "+") coeff = 1.0;
                        else if (!string.IsNullOrEmpty(coeffStr))
                        {
                            if (!double.TryParse(coeffStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out coeff))
                            {
                                coeff = 1.0;
                            }
                        }
                        if (double.TryParse(sqrtMatch.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var radicand) && radicand >= 0)
                        {
                            termVal = coeff * Math.Sqrt(radicand);
                            return true;
                        }
                    }

                    // 任意次根号 root(degree, radicand)
                    var rootMatch = System.Text.RegularExpressions.Regex.Match(t, @"^([+-]?)\s*root\((\d+),([0-9]+(?:\.[0-9]+)?|\.[0-9]+)\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (rootMatch.Success)
                    {
                        double sign = rootMatch.Groups[1].Value == "-" ? -1.0 : 1.0;
                        if (int.TryParse(rootMatch.Groups[2].Value, out var deg) && deg > 0 &&
                            double.TryParse(rootMatch.Groups[3].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var rootRadicand))
                        {
                            termVal = sign * Math.Pow(rootRadicand, 1.0 / deg);
                            return true;
                        }
                    }

                    // 圆周率项 pi / π (如 pi, -pi, 2*pi, 0.5*pi)
                    var piMatch = System.Text.RegularExpressions.Regex.Match(t, @"^([+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)?)\s*\*?\s*(?:pi|π)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (piMatch.Success)
                    {
                        var coeffStr = piMatch.Groups[1].Value.Trim();
                        double coeff = 1.0;
                        if (coeffStr == "-") coeff = -1.0;
                        else if (coeffStr == "+") coeff = 1.0;
                        else if (!string.IsNullOrEmpty(coeffStr))
                        {
                            if (!double.TryParse(coeffStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out coeff))
                            {
                                coeff = 1.0;
                            }
                        }
                        termVal = coeff * Math.PI;
                        return true;
                    }

                    // 自然对数 ln (如 ln(e), ln(e^2), ln(1), ln(2), -ln(3))
                    var lnMatch = System.Text.RegularExpressions.Regex.Match(t, @"^([+-]?)\s*ln\s*\(?\s*([0-9]+(?:\.[0-9]+)?|\.[0-9]+|e(?:\^(\d+))?)\s*\)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (lnMatch.Success)
                    {
                        double sign = lnMatch.Groups[1].Value == "-" ? -1.0 : 1.0;
                        if (lnMatch.Groups[3].Success && int.TryParse(lnMatch.Groups[3].Value, out var expP))
                        {
                            termVal = sign * expP;
                            return true;
                        }
                        var argStr = lnMatch.Groups[2].Value.Trim().ToLowerInvariant();
                        double arg = argStr == "e" ? Math.E : double.Parse(argStr, System.Globalization.CultureInfo.InvariantCulture);
                        if (arg > 0)
                        {
                            termVal = sign * Math.Log(arg);
                            return true;
                        }
                    }

                    // 常用对数 lg (如 lg(10), lg(100), lg 100, lg(1), -lg(1000))
                    var lgMatch = System.Text.RegularExpressions.Regex.Match(t, @"^([+-]?)\s*lg\s*\(?\s*([0-9]+(?:\.[0-9]+)?|\.[0-9]+)\s*\)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (lgMatch.Success)
                    {
                        double sign = lgMatch.Groups[1].Value == "-" ? -1.0 : 1.0;
                        if (double.TryParse(lgMatch.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var arg) && arg > 0)
                        {
                            termVal = sign * Math.Log10(arg);
                            return true;
                        }
                    }

                    // 任意底对数 log_b(x) / log(b, x) / log2(8) / log_2 8 / log(10) / log(2, 1/4) / log(1/2, 4) / log(2, sqrt(2))
                    string logTarget = t;
                    double logSign = 1.0;
                    if (logTarget.StartsWith("-"))
                    {
                        logSign = -1.0;
                        logTarget = logTarget.Substring(1).Trim();
                    }
                    else if (logTarget.StartsWith("+"))
                    {
                        logTarget = logTarget.Substring(1).Trim();
                    }

                    if (logTarget.StartsWith("log", StringComparison.OrdinalIgnoreCase))
                    {
                        var afterLog = logTarget.Substring(3).Trim();
                        double? explicitBase = null;

                        if (!afterLog.StartsWith("("))
                        {
                            var bPrefixMatch = System.Text.RegularExpressions.Regex.Match(afterLog, @"^_{?([0-9]+(?:\.[0-9]+)?|\d+\/\d+)}?(.*)$");
                            if (bPrefixMatch.Success)
                            {
                                if (TryParseSingleTerm(bPrefixMatch.Groups[1].Value, out var parsedB))
                                {
                                    explicitBase = parsedB;
                                }
                                afterLog = bPrefixMatch.Groups[2].Value.Trim();
                            }
                            else
                            {
                                var digitPrefixMatch = System.Text.RegularExpressions.Regex.Match(afterLog, @"^([0-9]+(?:\.[0-9]+)?)(.*)$");
                                if (digitPrefixMatch.Success)
                                {
                                    if (TryParseSingleTerm(digitPrefixMatch.Groups[1].Value, out var parsedB))
                                    {
                                        explicitBase = parsedB;
                                    }
                                    afterLog = digitPrefixMatch.Groups[2].Value.Trim();
                                }
                            }
                        }

                        if (afterLog.StartsWith("(") && afterLog.EndsWith(")"))
                        {
                            string inner = afterLog.Substring(1, afterLog.Length - 2).Trim();
                            int depth = 0;
                            int commaIdx = -1;
                            for (int ci = 0; ci < inner.Length; ci++)
                            {
                                if (inner[ci] == '(') depth++;
                                else if (inner[ci] == ')') depth--;
                                else if (inner[ci] == ',' && depth == 0)
                                {
                                    commaIdx = ci;
                                    break;
                                }
                            }

                            if (commaIdx >= 0)
                            {
                                string bStr = inner.Substring(0, commaIdx).Trim();
                                string argStr = inner.Substring(commaIdx + 1).Trim();
                                if (TryParseSingleTerm(bStr, out var bVal) && TryParseSingleTerm(argStr, out var argVal))
                                {
                                    if (bVal > 0 && Math.Abs(bVal - 1.0) > 1e-6 && argVal > 0)
                                    {
                                        termVal = logSign * (Math.Log(argVal) / Math.Log(bVal));
                                        return true;
                                    }
                                }
                            }
                            else
                            {
                                double bVal = explicitBase ?? 10.0;
                                if (TryParseSingleTerm(inner, out var argVal))
                                {
                                    if (bVal > 0 && Math.Abs(bVal - 1.0) > 1e-6 && argVal > 0)
                                    {
                                        termVal = logSign * (Math.Log(argVal) / Math.Log(bVal));
                                        return true;
                                    }
                                }
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(afterLog))
                        {
                            double bVal = explicitBase ?? 10.0;
                            if (TryParseSingleTerm(afterLog, out var argVal))
                            {
                                if (bVal > 0 && Math.Abs(bVal - 1.0) > 1e-6 && argVal > 0)
                                {
                                    termVal = logSign * (Math.Log(argVal) / Math.Log(bVal));
                                    return true;
                                }
                            }
                        }
                    }

                    // 三角函数精确特殊角与弧度制/度数求值 (如 sin(30), sin(30°), sin(pi/6), cos(60°), cos(pi/3), tan(45°), -sin(pi/2))
                    var trigMatch = System.Text.RegularExpressions.Regex.Match(t, @"^([+-]?)\s*(sin|cos|tan)\s*\(?\s*([^()]+?)\s*\)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (trigMatch.Success)
                    {
                        double sign = trigMatch.Groups[1].Value == "-" ? -1.0 : 1.0;
                        string fn = trigMatch.Groups[2].Value.ToLowerInvariant();
                        string arg = trigMatch.Groups[3].Value.Trim().ToLowerInvariant();

                        if (TryEvaluateStandardAngle(arg, out double radAngle))
                        {
                            double calculated = fn switch
                            {
                                "sin" => Math.Sin(radAngle),
                                "cos" => Math.Cos(radAngle),
                                "tan" => Math.Tan(radAngle),
                                _ => double.NaN
                            };
                            if (!double.IsNaN(calculated) && !double.IsInfinity(calculated))
                            {
                                if (Math.Abs(calculated) < 1e-9) calculated = 0.0;
                                termVal = sign * calculated;
                                return true;
                            }
                        }
                    }

                    static bool TryEvaluateStandardAngle(string arg, out double radAngle)
                    {
                        radAngle = 0;
                        if (string.IsNullOrWhiteSpace(arg)) return false;
                        arg = arg.Replace(" ", "").Replace("°", "").Replace("度", "").Replace("deg", "").Replace("\\circ", "");
                        arg = arg.Replace("π", "pi");

                        // 1. 弧度制表达式 (含 pi)
                        if (arg.Contains("pi"))
                        {
                            if (arg == "pi" || arg == "+pi") { radAngle = Math.PI; return true; }
                            if (arg == "-pi") { radAngle = -Math.PI; return true; }
                            var mRadian = System.Text.RegularExpressions.Regex.Match(arg, @"^([+-]?(?:\d+(?:\.\d+)?)?)?\*?pi(?:/(\d+(?:\.\d+)?))?$");
                            if (mRadian.Success)
                            {
                                string cStr = mRadian.Groups[1].Value;
                                double coeff = 1.0;
                                if (cStr == "-") coeff = -1.0;
                                else if (!string.IsNullOrEmpty(cStr) && cStr != "+")
                                {
                                    if (!double.TryParse(cStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out coeff)) return false;
                                }
                                double den = 1.0;
                                if (mRadian.Groups[2].Success)
                                {
                                    if (!double.TryParse(mRadian.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out den) || Math.Abs(den) < 1e-9) return false;
                                }
                                radAngle = coeff * Math.PI / den;
                                return true;
                            }
                        }

                        // 2. 角度制度数 (如 30, 45, 60, 90, 120, 135, 150, 180, 270, 360)
                        if (double.TryParse(arg, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double deg))
                        {
                            radAngle = deg * Math.PI / 180.0;
                            return true;
                        }

                        return false;
                    }

                    return false;
                }

                // 若单项可直接求值（如 0.5, sqrt(4), 2*sqrt(3), pi）
                if (TryParseSingleTerm(s, out val)) return true;

                // 乘法因数算式 (如 2*3, 2.5*4, 1.5*2)
                var prodParts = s.Split('*');
                if (prodParts.Length == 2 &&
                    TryParseSingleTerm(prodParts[0].Trim(), out var f1) &&
                    TryParseSingleTerm(prodParts[1].Trim(), out var f2))
                {
                    val = f1 * f2;
                    return true;
                }

                // 简易分数、比例及分母有理化根式分数 (如 1/2, 2/3, 3:4, sqrt(2)/2, 1/sqrt(2), sqrt(3)/3, (-sqrt(2))/2, -1/sqrt(2))
                var cleanFraction = s.Trim();
                bool isOuterNeg = false;
                if (cleanFraction.StartsWith("-(") && cleanFraction.EndsWith(")"))
                {
                    isOuterNeg = true;
                    cleanFraction = cleanFraction.Substring(2, cleanFraction.Length - 3).Trim();
                }
                var parts = cleanFraction.Split(new[] { '/', ':' });
                if (parts.Length == 2)
                {
                    var p0 = parts[0].Trim();
                    if (p0.StartsWith("(") && p0.EndsWith(")")) p0 = p0.Substring(1, p0.Length - 2).Trim();
                    var p1 = parts[1].Trim();
                    if (p1.StartsWith("(") && p1.EndsWith(")")) p1 = p1.Substring(1, p1.Length - 2).Trim();
                    if (TryParseSingleTerm(p0, out var num) && 
                        TryParseSingleTerm(p1, out var den) && 
                        Math.Abs(den) > 1e-9)
                    {
                        val = (num / den) * (isOuterNeg ? -1.0 : 1.0);
                        return true;
                    }
                }
                val = 0;
                return false;
            }

            static bool AreNumbersClose(double a, double b)
            {
                if (double.IsNaN(a) || double.IsNaN(b)) return false;
                if (double.IsInfinity(a) || double.IsInfinity(b)) return a == b;
                if (a == b) return true;
                double diff = Math.Abs(a - b);
                if (a == 0 || b == 0)
                {
                    return diff < 1e-6;
                }
                double maxVal = Math.Max(Math.Abs(a), Math.Abs(b));
                return (diff / maxVal) < 1e-4;
            }

            var numUserStr = TryParseScientificOrNumber(normUser, out _) ? normUser : strippedUser;
            var numCorrStr = TryParseScientificOrNumber(normCorrect, out _) ? normCorrect : strippedCorrect;

            if (TryParseScientificOrNumber(numUserStr, out var userNumber))
            {
                if (TryParseScientificOrNumber(numCorrStr, out var corrNumber))
                {
                    if (AreNumbersClose(userNumber, corrNumber)) return true;
                }
                foreach (var cand in candidateList)
                {
                    var numCandStr = TryParseScientificOrNumber(cand, out _) ? cand : StripCommonUnits(cand);
                    if (TryParseScientificOrNumber(numCandStr, out var candNumber) && AreNumbersClose(userNumber, candNumber))
                    {
                        return true;
                    }
                }
            }
            else if (TryParseScientificOrNumber(numCorrStr, out var corrNumber2))
            {
                if (TryParseScientificOrNumber(numUserStr, out var userNumber2) && AreNumbersClose(userNumber2, corrNumber2))
                {
                    return true;
                }
            }

            // 中学与大学三角特殊角角度制与弧度制双向等价 (如 30° vs \pi/6, 45° vs \pi/4, 90° vs \pi/2, 180° vs \pi, 360° vs 2\pi)
            if (CheckAngleAndRadianEquivalence(user, correct) || CheckAngleAndRadianEquivalence(normUser, normCorrect)) return true;

            // 国际单位制常用科学词头换算等价 (如 kHz 与 Hz、MHz 与 Hz、kJ 与 J、kV 与 V、kΩ 与 Ω、L 与 mL、mol/L 与 mmol/L 等)
            if (CheckScientificUnitMultiplierEquivalence(user, correct) || CheckScientificUnitMultiplierEquivalence(normUser, normCorrect)) return true;

            // 化学离子符号书写与上下标、电荷次序等价 (如 Fe^{3+} vs Fe3+ vs Fe^+3, SO_4^{2-} vs SO42- vs SO4^2-)
            if (CheckChemicalIonEquivalence(user, correct) || CheckChemicalIonEquivalence(normUser, normCorrect)) return true;

            // 微积分不定积分常数 C 加法交换与大小写等价 (如 x^2 + C vs C + x^2 vs x^2 + c)
            if (CheckIndefiniteIntegralConstantCMatch(user, correct) || CheckIndefiniteIntegralConstantCMatch(normUser, normCorrect)) return true;

            return false;
        }

        public static bool CheckAngleAndRadianEquivalence(string u, string c)
        {
            if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(c)) return false;
            u = u.Trim();
            c = c.Trim();

            static bool TryExtractAngleDegrees(string s, out double deg)
            {
                deg = 0;
                if (string.IsNullOrWhiteSpace(s)) return false;
                s = s.Trim().Replace(" ", "").Replace("°", "").Replace("度", "").Replace("deg", "").Replace("\\circ", "").Replace("^{\\circ}", "").Replace("^\\circ", "");
                return double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out deg);
            }

            static bool TryExtractAngleRadians(string s, out double rad)
            {
                rad = 0;
                if (string.IsNullOrWhiteSpace(s)) return false;
                s = s.Trim().Replace(" ", "").Replace("π", "pi").Replace("\\pi", "pi").Replace("rad", "").Replace("弧度", "");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:frac|dfrac|tfrac)\s*\{([^}]+)\}\s*\{([^}]+)\}", "$1/$2");
                s = s.Trim('(', ')');
                if (s == "pi" || s == "+pi") { rad = Math.PI; return true; }
                if (s == "-pi") { rad = -Math.PI; return true; }
                if (s == "2pi" || s == "2*pi") { rad = 2 * Math.PI; return true; }

                var mRadian = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?(?:\d+(?:\.\d+)?)?)?\*?pi(?:/(\d+(?:\.\d+)?))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (mRadian.Success)
                {
                    string cStr = mRadian.Groups[1].Value;
                    double coeff = 1.0;
                    if (cStr == "-") coeff = -1.0;
                    else if (!string.IsNullOrEmpty(cStr) && cStr != "+")
                    {
                        if (!double.TryParse(cStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out coeff)) return false;
                    }
                    double den = 1.0;
                    if (mRadian.Groups[2].Success)
                    {
                        if (!double.TryParse(mRadian.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out den) || Math.Abs(den) < 1e-9) return false;
                    }
                    rad = coeff * Math.PI / den;
                    return true;
                }

                if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out rad))
                {
                    return true;
                }
                return false;
            }

            bool uHasDegMarker = u.Contains("°") || u.Contains("度") || u.Contains("deg") || u.Contains("\\circ");
            bool cHasDegMarker = c.Contains("°") || c.Contains("度") || c.Contains("deg") || c.Contains("\\circ");
            bool uHasRadMarker = u.Contains("pi") || u.Contains("π") || u.Contains("\\pi") || u.Contains("rad") || u.Contains("弧度");
            bool cHasRadMarker = c.Contains("pi") || c.Contains("π") || c.Contains("\\pi") || c.Contains("rad") || c.Contains("弧度");

            // 至少一方需要包含角度或弧度特征（如 °、度、deg、\circ、pi、π、\pi、rad、弧度）
            if (!uHasDegMarker && !cHasDegMarker && !uHasRadMarker && !cHasRadMarker)
            {
                return false;
            }

            // 情况一：一边是角度标识（或显式数值），另一边是弧度标识（或纯弧度表达式）
            if ((uHasDegMarker || (!uHasRadMarker && cHasRadMarker)) && TryExtractAngleDegrees(u, out var degU) && TryExtractAngleRadians(c, out var radC))
            {
                double expectedRad = degU * Math.PI / 180.0;
                if (Math.Abs(expectedRad - radC) < 1e-4) return true;
            }

            if ((cHasDegMarker || (!cHasRadMarker && uHasRadMarker)) && TryExtractAngleDegrees(c, out var degC) && TryExtractAngleRadians(u, out var radU))
            {
                double expectedRad = degC * Math.PI / 180.0;
                if (Math.Abs(expectedRad - radU) < 1e-4) return true;
            }

            // 情况二：若双方都带有角度标识 (如 30° vs 30度)
            if (uHasDegMarker && cHasDegMarker)
            {
                if (TryExtractAngleDegrees(u, out var d1) && TryExtractAngleDegrees(c, out var d2))
                {
                    if (Math.Abs(d1 - d2) < 1e-4) return true;
                }
            }

            // 情况三：若双方都带有弧度标识 (如 \frac{\pi}{6} vs pi/6)
            if (uHasRadMarker && cHasRadMarker)
            {
                if (TryExtractAngleRadians(u, out var r1) && TryExtractAngleRadians(c, out var r2))
                {
                    if (Math.Abs(r1 - r2) < 1e-4) return true;
                }
            }

            return false;
        }

        public static bool CheckScientificUnitMultiplierEquivalence(string u, string c)
        {
            if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(c)) return false;

            // 电学电感亨利 (H) 与电导西门子 (S) 语境预处理，避免与时间单位 (h, s) 混淆
            bool isInductanceContext = System.Text.RegularExpressions.Regex.IsMatch(u, @"(?:亨|毫亨|微亨|纳亨|mH|μH|uH|nH|\bH\b)") ||
                                       System.Text.RegularExpressions.Regex.IsMatch(c, @"(?:亨|毫亨|微亨|纳亨|mH|μH|uH|nH|\bH\b)");
            if (isInductanceContext)
            {
                u = System.Text.RegularExpressions.Regex.Replace(u, @"(?<=\d)\s*H\b", "亨");
                c = System.Text.RegularExpressions.Regex.Replace(c, @"(?<=\d)\s*H\b", "亨");
            }

            bool isConductanceContext = System.Text.RegularExpressions.Regex.IsMatch(u, @"(?:西门子|西|毫西|微西|mS|μS|uS|\bS\b)") ||
                                        System.Text.RegularExpressions.Regex.IsMatch(c, @"(?:西门子|西|毫西|微西|mS|μS|uS|\bS\b)");
            if (isConductanceContext)
            {
                u = System.Text.RegularExpressions.Regex.Replace(u, @"(?<=\d)\s*mS\b", "毫西");
                c = System.Text.RegularExpressions.Regex.Replace(c, @"(?<=\d)\s*mS\b", "毫西");
                u = System.Text.RegularExpressions.Regex.Replace(u, @"(?<=\d)\s*(?:uS|μS)\b", "微西");
                c = System.Text.RegularExpressions.Regex.Replace(c, @"(?<=\d)\s*(?:uS|μS)\b", "微西");
                u = System.Text.RegularExpressions.Regex.Replace(u, @"(?<=\d)\s*S\b", "西门子");
                c = System.Text.RegularExpressions.Regex.Replace(c, @"(?<=\d)\s*S\b", "西门子");
            }

            static bool TryParseUnitNumber(string s, out double num)
            {
                num = 0;
                if (string.IsNullOrWhiteSpace(s)) return false;
                s = s.Trim().Replace(" ", "");
                if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out num)) return true;

                // 科学记数法: 如 3.0*10^8, 1.5×10^-3, 6.02E23
                var sciMatch = System.Text.RegularExpressions.Regex.Match(s, @"^([+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+))\s*(?:\*|x|×|·|•|\\times|\\cdot)?\s*(?:10\^[\{\(]?([+-]?\d+)[\}\)]?|[eE]([+-]?\d+))$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (sciMatch.Success &&
                    double.TryParse(sciMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var baseNum))
                {
                    string expStr = sciMatch.Groups[2].Success ? sciMatch.Groups[2].Value : sciMatch.Groups[3].Value;
                    if (int.TryParse(expStr, out var exp))
                    {
                        num = baseNum * Math.Pow(10, exp);
                        return true;
                    }
                }

                // 简易分数: 如 1/2, 3/4
                var fracParts = s.Split('/');
                if (fracParts.Length == 2 &&
                    double.TryParse(fracParts[0].Trim('(', ')'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n) &&
                    double.TryParse(fracParts[1].Trim('(', ')'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) &&
                    Math.Abs(d) > 1e-9)
                {
                    num = n / d;
                    return true;
                }

                return false;
            }

            static bool TryParsePrefixedQuantity(string s, out double baseValue, out string family)
            {
                baseValue = 0;
                family = string.Empty;
                if (string.IsNullOrWhiteSpace(s)) return false;
                s = s.Trim().ToLowerInvariant();
                s = s.Replace("~", "").Replace("\\,", "").Replace("\\text{", "").Replace("\\mathrm{", "").Replace("}", "").Replace("$", "");
                s = s.Replace("×", "*").Replace("·", "*").Replace("•", "*").Replace("\\times", "*").Replace("\\cdot", "*");

                var prefixUnits = new (string pattern, double multiplier, string fam)[]
                {
                    ("ghz|吉赫", 1e9, "freq"),
                    ("mhz|兆赫", 1e6, "freq"),
                    ("khz|千赫", 1e3, "freq"),
                    ("hz|赫兹|赫", 1.0, "freq"),

                    // 电能与功实用单位 (1 kWh = 1 度 = 3.6e6 J)
                    (@"mwh|mw·h|mw\*h|兆瓦时", 3.6e9, "energy"),
                    (@"kwh|kw·h|kw\*h|kw\s*h|千瓦·时|千瓦\*时|千瓦时|度", 3.6e6, "energy"),
                    (@"wh|w·h|w\*h|瓦时", 3600.0, "energy"),
                    (@"ws|w·s|w\*s|瓦秒", 1.0, "energy"),
                    ("gj|吉焦", 1e9, "energy"),
                    ("mj|兆焦", 1e6, "energy"),
                    ("kj|千焦", 1e3, "energy"),
                    ("j|焦耳|焦", 1.0, "energy"),

                    // 电学电阻
                    (@"g\\omega|gω|gΩ|gomega", 1e9, "res"),
                    (@"m\\omega|mω|mΩ|momega|兆欧", 1e6, "res"),
                    (@"k\\omega|kω|kΩ|komega|千欧", 1e3, "res"),
                    (@"\\omega|ω|Ω|omega|ohm|欧姆|欧", 1.0, "res"),

                    // 电学电压
                    ("kv|千伏", 1e3, "volt"),
                    ("mv|毫伏", 1e-3, "volt"),
                    ("v|伏特|伏", 1.0, "volt"),

                    // 电学电流 (A, mA, μA, kA)
                    ("ka|千安", 1e3, "curr"),
                    ("ma|毫安", 1e-3, "curr"),
                    (@"ua|μa|\\mu\s*a|微安", 1e-6, "curr"),
                    ("a|安培|安", 1.0, "curr"),

                    // 电学电容 (F, mF, μF, nF, pF)
                    ("mf|毫法", 1e-3, "cap"),
                    (@"uf|μf|\\mu\s*f|微法", 1e-6, "cap"),
                    ("nf|纳法", 1e-9, "cap"),
                    ("pf|皮法", 1e-12, "cap"),
                    ("f|法拉|法", 1.0, "cap"),

                    // 速度 (m/s, km/h)
                    (@"km/h|千米/小时|公里/小时|千米/时|公里/时|km/hr|km\*h\^\{-1\}|km\*h\^-1|km·h\^-1", 1.0 / 3.6, "speed"),
                    (@"m/s|米/秒|米每秒|m\*s\^\{-1\}|m\*s\^-1|m·s\^-1", 1.0, "speed"),

                    // 动量与冲量 (N·s, kg·m/s)
                    (@"n\*s|n·s|n\s*s|牛\*秒|牛·秒|牛\s*秒|牛秒", 1.0, "momentum"),
                    (@"kg\*m/s|kg·m/s|kg\s*m/s|千克\*米/秒|千克·米/秒|千克\s*米/秒|千克米/秒|公斤\*米/秒|公斤·米/秒|公斤米/秒", 1.0, "momentum"),

                    // 磁学磁感应强度 (T, mT, Wb/m^2)
                    (@"wb/m\^2|wb/m²|wb/m2|韦伯/平方米|韦伯每平方米", 1.0, "mag"),
                    ("mt|毫特", 1e-3, "mag"),
                    ("特斯拉|特", 1.0, "mag"),

                    // 磁学磁通量 (Wb, mWb, μWb, Mx)
                    (@"mwb|毫韦伯|毫韦", 1e-3, "flux"),
                    (@"uwb|μwb|\\mu\s*wb|微韦伯|微韦", 1e-6, "flux"),
                    (@"wb|韦伯|韦", 1.0, "flux"),
                    (@"mx|麦克斯韦|麦", 1e-8, "flux"),

                    // 电学电感 (H, mH, μH, nH)
                    (@"mh|毫亨", 1e-3, "inductance"),
                    (@"uh|μh|\\mu\s*h|微亨", 1e-6, "inductance"),
                    (@"nh|纳亨", 1e-9, "inductance"),
                    (@"亨利|亨", 1.0, "inductance"),

                    // 电学电导 (S, mS, μS)
                    (@"毫西门子|毫西", 1e-3, "conductance"),
                    (@"微西门子|微西", 1e-6, "conductance"),
                    (@"西门子|西", 1.0, "conductance"),

                    // 物质的量浓度 (mol/L, mol/dm³, mmol/L, mol/m³)
                    (@"mol/l|mol/dm\^3|mol/dm³|mol/dm3|摩尔/升|摩尔每升", 1.0, "conc"),
                    (@"mmol/l|mmol/dm\^3|mmol/dm³|mmol/dm3|毫摩尔/升|毫摩尔每升", 1e-3, "conc"),
                    (@"mol/m\^3|mol/m³|mol/m3|摩尔/立方米|摩尔每立方米", 1e-3, "conc"),
                    (@"umol/l|μmol/l|\\mu\s*mol/l|微摩尔/升", 1e-6, "conc"),

                    // 体积与容积 (m³, dm³, cm³, L, mL)
                    (@"m\^3|m³|m3|立方米", 1.0, "vol"),
                    (@"dm\^3|dm³|dm3|立方分米", 1e-3, "vol"),
                    (@"cm\^3|cm³|cm3|cc|立方厘米", 1e-6, "vol"),
                    (@"ml|毫升", 1e-6, "vol"),
                    (@"l|\\ell|升|公升", 1e-3, "vol"),

                    // 时间 (h, min, s, ms)
                    (@"h|小时|时|hr|hrs", 3600.0, "time"),
                    (@"min|mins|分钟|分", 60.0, "time"),
                    ("ms|毫秒", 1e-3, "time"),
                    ("s|秒|sec|secs", 1.0, "time"),

                    // 压强 (1 atm = 101325 Pa, 1 bar = 1e5 Pa, 1 mmHg ≈ 133.322 Pa)
                    (@"atm|标准大气压", 101325.0, "press"),
                    (@"bar|巴", 1e5, "press"),
                    (@"mmhg|毫米汞柱", 133.322368, "press"),
                    ("mpa|兆帕", 1e6, "press"),
                    ("kpa|千帕", 1e3, "press"),
                    ("hpa|百帕|mbar|毫巴", 100.0, "press"),
                    ("pa|帕斯卡|帕", 1.0, "press"),

                    // 力学
                    ("kn|千牛", 1e3, "force"),
                    ("n|牛顿|牛", 1.0, "force"),

                    // 功率 (1 hp ≈ 735 W)
                    ("gw|吉瓦", 1e9, "power"),
                    ("mw|兆瓦", 1e6, "power"),
                    ("kw|千瓦", 1e3, "power"),
                    ("hp|马力", 735.0, "power"),
                    ("w|瓦特|瓦", 1.0, "power"),

                    // 长度
                    ("km|千米|公里", 1e3, "len"),
                    ("dm|分米", 0.1, "len"),
                    ("cm|厘米", 0.01, "len"),
                    ("mm|毫米", 0.001, "len"),
                    ("m|米", 1.0, "len"),

                    // 质量
                    ("t|吨", 1e3, "mass"),
                    ("kg|千克|公斤", 1.0, "mass"),
                    ("mg|毫克", 1e-6, "mass"),
                    ("g|克", 1e-3, "mass")
                };

                // 分离尾部单位 (匹配上述模式之一)
                foreach (var pu in prefixUnits)
                {
                    var regex = new System.Text.RegularExpressions.Regex(@"^(.*?)\s*(?:" + pu.pattern + @")$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var match = regex.Match(s);
                    if (match.Success)
                    {
                        var numPart = match.Groups[1].Value.Trim();
                        numPart = numPart.TrimEnd('*', ' ');
                        if (TryParseUnitNumber(numPart, out var num))
                        {
                            baseValue = num * pu.multiplier;
                            family = pu.fam;
                            return true;
                        }
                    }
                }

                return false;
            }

            static bool AreValuesClose(double a, double b, string fam = "")
            {
                if (double.IsNaN(a) || double.IsNaN(b)) return false;
                if (double.IsInfinity(a) || double.IsInfinity(b)) return a == b;
                if (a == b) return true;
                double diff = Math.Abs(a - b);
                double maxVal = Math.Max(Math.Abs(a), Math.Abs(b));
                if (maxVal < 1e-9) return diff < 1e-9;
                double relTol = fam switch
                {
                    "press" => 0.015, // 压强允许 1.5% 相对容差 (兼容 1.01x10^5 Pa / 101.3 kPa 与 101325 Pa)
                    _ => 2e-3         // 通用单位允许 0.2% 相对容差
                };
                return (diff / maxVal) <= relTol;
            }

            if (TryParsePrefixedQuantity(u, out var uBase, out var uFam) &&
                TryParsePrefixedQuantity(c, out var cBase, out var cFam))
            {
                if (uFam == cFam && AreValuesClose(uBase, cBase, uFam))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool CheckChemicalIonEquivalence(string u, string c)
        {
            if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(c)) return false;

            string normU = NormalizeChemicalIon(u);
            string normC = NormalizeChemicalIon(c);

            if (string.IsNullOrEmpty(normU) || string.IsNullOrEmpty(normC)) return false;
            return string.Equals(normU, normC, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeChemicalIon(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Trim().Replace(" ", "").Replace("$", "").Replace("￥", "");
            s = s.Replace("【", "[").Replace("】", "]");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\(?:text|mathrm|mathbf)\{([^}]*)\}", "$1");
            s = s.Replace("\\text{", "").Replace("\\mathrm{", "").Replace("\\mathbf{", "");
            s = s.Replace("\\,", "").Replace("~", "");

            // 剥离 LaTeX 下标 _4 -> 4, _{4} -> 4
            s = System.Text.RegularExpressions.Regex.Replace(s, @"_\{?(\d+)\}?", "$1");

            // Unicode 上下标数字与正负号转换为标准字符
            s = s.Replace("³⁺", "3+").Replace("²⁺", "2+").Replace("⁴⁺", "4+").Replace("⁵⁺", "5+")
                 .Replace("³⁻", "3-").Replace("²⁻", "2-").Replace("⁴⁻", "4-").Replace("⁵⁻", "5-")
                 .Replace("⁺", "+").Replace("⁻", "-");
            s = s.Replace("⁰", "0").Replace("¹", "1").Replace("²", "2").Replace("³", "3")
                 .Replace("⁴", "4").Replace("⁵", "5").Replace("⁶", "6").Replace("⁷", "7")
                 .Replace("⁸", "8").Replace("⁹", "9");

            // 提取末尾电荷部分
            // 支持多种电荷形式:
            // LaTeX: ^{3+}, ^{2-}, ^{+3}, ^{-2}, ^{+}, ^{-}, ^3+, ^2-, ^+3, ^-2, ^+, ^-
            // 括号: ^(3+), ^(2-), ^(+3), ^(-2), (3+), (2-), (+3), (-2)
            // 简写: 3+, 2+, +3, +2, +, -, 1+, 1-, +1, -1, +++, ++, ---, --
            var chargeRegex = new System.Text.RegularExpressions.Regex(@"(?:\^[\{\(]?|\(?)([1-5]?[+-]|[+-][1-5]?|\+{1,4}|-{1,4})[\}\)]?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var match = chargeRegex.Match(s);
            if (!match.Success) return string.Empty;

            string rawCharge = match.Groups[1].Value;
            string basePart = s.Substring(0, match.Index).Trim();
            if (string.IsNullOrEmpty(basePart)) return string.Empty;

            int chargeSign = 1;
            int chargeVal = 1;

            if (rawCharge.All(ch => ch == '+'))
            {
                chargeSign = 1;
                chargeVal = rawCharge.Length;
            }
            else if (rawCharge.All(ch => ch == '-'))
            {
                chargeSign = -1;
                chargeVal = rawCharge.Length;
            }
            else
            {
                if (rawCharge.Contains("-")) chargeSign = -1;
                else chargeSign = 1;

                string digits = new string(rawCharge.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out int val) && val > 0)
                {
                    chargeVal = val;
                }
                else
                {
                    chargeVal = 1;
                }
            }

            string baseNorm = basePart.ToLowerInvariant()
                .Replace("(", "").Replace(")", "")
                .Replace("[", "").Replace("]", "");

            return $"{baseNorm}__ch__{(chargeSign > 0 ? "+" : "-")}{chargeVal}";
        }

        public static bool CheckIndefiniteIntegralConstantCMatch(string u, string c)
        {
            if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(c)) return false;

            static bool TryExtractConstantC(string s, out string integrand)
            {
                integrand = string.Empty;
                if (string.IsNullOrWhiteSpace(s)) return false;
                s = s.Trim().Replace(" ", "");

                // 检查是否包含常数项 +C, +c, C+, c+, +常数
                var matchTrailing = System.Text.RegularExpressions.Regex.Match(s, @"^(.*)[+](?:C|c|常数)$");
                if (matchTrailing.Success)
                {
                    integrand = matchTrailing.Groups[1].Value.Trim();
                    return !string.IsNullOrEmpty(integrand);
                }

                var matchLeading = System.Text.RegularExpressions.Regex.Match(s, @"^(?:C|c|常数)[+](.*)$");
                if (matchLeading.Success)
                {
                    integrand = matchLeading.Groups[1].Value.Trim();
                    return !string.IsNullOrEmpty(integrand);
                }

                return false;
            }

            if (TryExtractConstantC(u, out var userIntegrand) && TryExtractConstantC(c, out var corrIntegrand))
            {
                if (string.Equals(userIntegrand, corrIntegrand, StringComparison.OrdinalIgnoreCase)) return true;
                if (CheckFillInBlankMatch(userIntegrand, corrIntegrand)) return true;
            }

            return false;
        }

        public async Task<List<LlmGenerationLog>> GetLlmGenerationLogsAsync(Guid userId)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            return await ctx.LlmGenerationLogs
                .AsNoTracking()
                .Include(l => l.Question)
                .Where(l => l.UserId == userId)
                .OrderByDescending(l => l.GeneratedAt)
                .ToListAsync();
        }

        private async Task<bool> IsAuthorizedToAccessStudentDataAsync(Guid callerUserId, Guid studentUserId, AppDbContext db)
        {
            if (callerUserId == studentUserId) return true;
            var caller = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == callerUserId);
            if (caller != null && (caller.Role == UserRole.SuperAdmin || caller.Role == UserRole.Teacher))
            {
                return true;
            }

            var hasBinding = await db.StudentParentBindings
                .AsNoTracking()
                .AnyAsync(b => b.ParentUserId == callerUserId && b.StudentUserId == studentUserId);

            return hasBinding;
        }

        public async Task<PracticeAnalyticsDto> GetPracticeAnalyticsAsync(Guid userId, string? subject = null, bool? isCorrect = null, Guid? requestorUserId = null)
        {
            if (userId == Guid.Empty) return new PracticeAnalyticsDto();

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            // 防越权 (IDOR) 鉴权：若查询目标非本人，必须验证调用者权限 (超级管理员/教师/合法绑定的监护人家长)
            var callerId = requestorUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (callerId.HasValue && callerId.Value != userId)
            {
                var isAuthorized = await IsAuthorizedToAccessStudentDataAsync(callerId.Value, userId, ctx);
                if (!isAuthorized)
                {
                    return new PracticeAnalyticsDto();
                }
            }

            var query = ctx.PracticeRecords
                .AsNoTracking()
                .Include(r => r.Question)
                .Where(r => r.UserId == userId);

            if (!string.IsNullOrWhiteSpace(subject) && subject != "全部分科")
            {
                query = query.Where(r => r.Question != null && r.Question.Subject == subject);
            }
            if (isCorrect.HasValue)
            {
                query = query.Where(r => r.IsCorrect == isCorrect.Value);
            }

            var records = await query.OrderByDescending(r => r.AnsweredAt).ToListAsync();

            var dto = new PracticeAnalyticsDto
            {
                TotalAnswered = records.Count,
                TotalCorrect = records.Count(r => r.IsCorrect),
                TotalTimeSpentSeconds = records.Sum(r => r.TimeTakenSeconds),
                TotalEarnedExp = records.Sum(r => r.EarnedExp),
                TotalEarnedCoins = records.Sum(r => r.EarnedCoins),
                RecentRecords = records
            };

            // 按学科统计
            var subjectGroups = records
                .Where(r => r.Question != null)
                .GroupBy(r => r.Question!.Subject);

            foreach (var group in subjectGroups)
            {
                var total = group.Count();
                var correct = group.Count(r => r.IsCorrect);
                var avgTime = total > 0 ? group.Average(r => r.TimeTakenSeconds) : 0;

                dto.SubjectStats.Add(new SubjectAnalyticsDto
                {
                    Subject = group.Key,
                    TotalAnswered = total,
                    CorrectCount = correct,
                    AverageTimeSeconds = Math.Round(avgTime, 1)
                });
            }

            // 认知科学架构分析：四象限速度与准确率分布
            if (records.Count > 0)
            {
                double avgTime = records.Average(r => r.TimeTakenSeconds);
                double speedThreshold = Math.Max(10.0, avgTime * 0.75);

                int agile = 0;
                int steady = 0;
                int careless = 0;
                int struggling = 0;

                foreach (var r in records)
                {
                    bool isFast = r.TimeTakenSeconds <= speedThreshold;
                    if (r.IsCorrect)
                    {
                        if (isFast) agile++;
                        else steady++;
                    }
                    else
                    {
                        if (isFast) careless++;
                        else struggling++;
                    }
                }

                dto.AgileMasteryCount = agile;
                dto.SteadyMasteryCount = steady;
                dto.CarelessCount = careless;
                dto.StrugglingCount = struggling;

                if (careless > 0 && careless >= struggling)
                {
                    dto.CognitivePaceAdvice = "💡 学情诊断提示：部分题目作答过急出现粗心失误，建议审题时放慢节奏，圈画题干限定条件与关键设问！";
                }
                else if (struggling > 0 && struggling > careless)
                {
                    dto.CognitivePaceAdvice = "💡 学情诊断提示：存在耗时较长仍未攻克的重难点，属于关键知识盲区，建议针对薄弱模块进行错题清零！";
                }
                else if (agile > 0 && careless == 0 && struggling == 0)
                {
                    dto.CognitivePaceAdvice = "🌟 学情诊断提示：敏捷度与准确度双双卓越，当前知识体系构建扎实，可适度挑战高难度综合题！";
                }
                else
                {
                    dto.CognitivePaceAdvice = "🎯 学情诊断提示：作答稳健踏实，持续巩固常考题型，保持良好的思考沉淀！";
                }
            }

            dto.SubjectStats = dto.SubjectStats.OrderByDescending(s => s.TotalAnswered).ToList();
            return dto;
        }

        public async Task<bool> ToggleFavoriteAsync(Guid userId, Guid questionId, string? note = null)
        {
            if (userId == Guid.Empty || questionId == Guid.Empty) return false;

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var callerId = _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (callerId.HasValue && callerId.Value != userId)
            {
                var caller = await ctx.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == callerId.Value);
                if (caller == null || (caller.Role != UserRole.SuperAdmin && caller.Role != UserRole.Teacher))
                {
                    bool isBoundParent = await ctx.StudentParentBindings.AsNoTracking()
                        .AnyAsync(b => b.ParentUserId == callerId.Value && b.StudentUserId == userId);
                    if (!isBoundParent)
                    {
                        return false;
                    }
                }
            }

            var existing = await ctx.UserFavorites.FirstOrDefaultAsync(f => f.UserId == userId && f.QuestionId == questionId);
            if (existing != null)
            {
                ctx.UserFavorites.Remove(existing);
                await ctx.SaveChangesAsync();
                return false;
            }
            else
            {
                ctx.UserFavorites.Add(new UserFavorite
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    QuestionId = questionId,
                    Note = note ?? string.Empty,
                    CreatedAt = DateTime.Now
                });
                await ctx.SaveChangesAsync();

                int count = await ctx.UserFavorites.CountAsync(f => f.UserId == userId);
                if (count >= 3)
                {
                    await _gamificationService.UnlockAchievementAsync("FAVORITE_MASTER", userId, ctx);
                }

                return true;
            }
        }

        public async Task<bool> IsFavoriteAsync(Guid userId, Guid questionId, Guid? requestorUserId = null)
        {
            if (userId == Guid.Empty || questionId == Guid.Empty) return false;

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var callerId = requestorUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (callerId.HasValue && callerId.Value != userId)
            {
                var isAuthorized = await IsAuthorizedToAccessStudentDataAsync(callerId.Value, userId, ctx);
                if (!isAuthorized)
                {
                    return false;
                }
            }

            return await ctx.UserFavorites.AnyAsync(f => f.UserId == userId && f.QuestionId == questionId);
        }

        public async Task<List<Question>> GetFavoriteQuestionsAsync(Guid userId, Guid? requestorUserId = null)
        {
            if (userId == Guid.Empty) return new List<Question>();

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var callerId = requestorUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (callerId.HasValue && callerId.Value != userId)
            {
                var isAuthorized = await IsAuthorizedToAccessStudentDataAsync(callerId.Value, userId, ctx);
                if (!isAuthorized)
                {
                    return new List<Question>();
                }
            }

            var favs = await ctx.UserFavorites
                .Include(f => f.Question)
                .Where(f => f.UserId == userId && f.Question != null)
                .OrderByDescending(f => f.CreatedAt)
                .Select(f => f.Question!)
                .ToListAsync();

            return favs;
        }

        public async Task<List<Question>> GetSprintQuestionsFromErrorsAsync(Guid userId, string? subject = null, int count = 10, Guid? requestorUserId = null)
        {
            if (userId == Guid.Empty) return new List<Question>();

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var callerId = requestorUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (callerId.HasValue && callerId.Value != userId)
            {
                var isAuthorized = await IsAuthorizedToAccessStudentDataAsync(callerId.Value, userId, ctx);
                if (!isAuthorized)
                {
                    return new List<Question>();
                }
            }

            var query = ctx.ErrorItems
                .Include(e => e.Question)
                .Where(e => e.UserId == userId && !e.IsMastered && e.Question != null);

            if (!string.IsNullOrWhiteSpace(subject) && subject != "全部" && subject != "全部分科" && subject != "通用学科")
            {
                query = query.Where(e => e.Question!.Subject == subject);
            }

            var errors = await query
                .AsNoTracking()
                .OrderByDescending(e => e.RevisionCount)
                .ThenByDescending(e => e.CreatedAt)
                .Take(count)
                .Select(e => e.Question!)
                .ToListAsync();

            return errors.Select(q => Northtropic.Helpers.QuestionShuffleHelper.ShuffleQuestionOptions(q)).ToList();
        }

        public async Task<List<Question>> GetQuestionsByIdsAsync(List<Guid> questionIds)
        {
            if (questionIds == null || questionIds.Count == 0) return new List<Question>();

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var questions = await ctx.Questions
                .AsNoTracking()
                .Where(q => questionIds.Contains(q.Id))
                .ToListAsync();

            // 保持与传入 ID 列表相同的顺序
            var ordered = questionIds
                .Select(id => questions.FirstOrDefault(q => q.Id == id))
                .Where(q => q != null)
                .Select(q => q!)
                .ToList();

            return ordered;
        }

        public async Task<HomeworkAssignment> CreateHomeworkAssignmentAsync(
            Guid creatorUserId,
            Guid studentUserId,
            string title,
            string subject,
            string category,
            int questionCount,
            int difficulty,
            DateTime? deadline,
            string note)
        {
            if (creatorUserId == Guid.Empty || studentUserId == Guid.Empty)
            {
                throw new ArgumentException("创建人或目标学生ID无效！");
            }

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var creator = await ctx.Users.FindAsync(creatorUserId);
            if (creator == null)
            {
                throw new UnauthorizedAccessException("创建人用户不存在！");
            }
            if (creator.Role == UserRole.Student)
            {
                throw new UnauthorizedAccessException("学生账号无权布置专属作业！");
            }

            var student = await ctx.Users.FindAsync(studentUserId);
            if (student == null)
            {
                throw new InvalidOperationException("指定的学生账号不存在！");
            }

            // 防越权鉴权校验：若布置人为家长，必须确认其与该学员之间存在合法的监护绑定关系
            if (creator.Role == UserRole.Parent)
            {
                bool isBound = await ctx.StudentParentBindings
                    .AnyAsync(b => b.ParentUserId == creatorUserId && b.StudentUserId == studentUserId);
                if (!isBound)
                {
                    throw new UnauthorizedAccessException("越权拦截：您只能为已成功绑定的学员布置专属作业！");
                }
            }

            int clampedCount = Math.Clamp(questionCount, 1, 100);
            int clampedDifficulty = Math.Clamp(difficulty, 1, 5);

            var assignment = new HomeworkAssignment
            {
                Id = Guid.NewGuid(),
                CreatorUserId = creatorUserId,
                StudentUserId = studentUserId,
                Title = string.IsNullOrWhiteSpace(title) ? $"{subject} 靶向强化作业" : title.Trim(),
                Subject = subject,
                Category = category,
                QuestionCount = clampedCount,
                TargetDifficulty = clampedDifficulty,
                Deadline = deadline,
                ParentNote = note ?? string.Empty,
                CreatedAt = DateTime.Now,
                IsCompleted = false
            };

            ctx.HomeworkAssignments.Add(assignment);
            await ctx.SaveChangesAsync();
            return assignment;
        }

        public async Task<HomeworkAssignment?> GetHomeworkAssignmentByIdAsync(Guid assignmentId)
        {
            if (assignmentId == Guid.Empty) return null;

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            return await ctx.HomeworkAssignments
                .Include(a => a.CreatorUser)
                .Include(a => a.StudentUser)
                .FirstOrDefaultAsync(a => a.Id == assignmentId);
        }

        public async Task<List<HomeworkAssignment>> GetHomeworkAssignmentsByStudentAsync(Guid studentUserId, Guid? requestorUserId = null)
        {
            if (studentUserId == Guid.Empty) return new List<HomeworkAssignment>();

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            // 防越权 (IDOR) 鉴权：若查询目标非本人，必须验证调用者权限 (超级管理员/教师/合法绑定的监护人家长)
            var callerId = requestorUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (callerId.HasValue && callerId.Value != studentUserId)
            {
                var isAuthorized = await IsAuthorizedToAccessStudentDataAsync(callerId.Value, studentUserId, ctx);
                if (!isAuthorized)
                {
                    return new List<HomeworkAssignment>();
                }
            }

            return await ctx.HomeworkAssignments
                .Include(a => a.CreatorUser)
                .Where(a => a.StudentUserId == studentUserId)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();
        }

        public async Task<List<HomeworkAssignment>> GetHomeworkAssignmentsByCreatorAsync(Guid creatorUserId, Guid? requestorUserId = null)
        {
            if (creatorUserId == Guid.Empty) return new List<HomeworkAssignment>();

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            // 防越权 (IDOR) 鉴权：若查询布置者非本人，必须验证调用者权限 (超级管理员/教师)
            var callerId = requestorUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (callerId.HasValue && callerId.Value != creatorUserId)
            {
                var caller = await ctx.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == callerId.Value);
                if (caller == null || (caller.Role != UserRole.SuperAdmin && caller.Role != UserRole.Teacher))
                {
                    return new List<HomeworkAssignment>();
                }
            }

            return await ctx.HomeworkAssignments
                .Include(a => a.StudentUser)
                .Where(a => a.CreatorUserId == creatorUserId)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();
        }

        public async Task<bool> CompleteHomeworkAssignmentAsync(Guid assignmentId, int correctCount, int totalAnswered, int score, Guid? studentUserId = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var assignment = await ctx.HomeworkAssignments.FindAsync(assignmentId);
            if (assignment == null) return false;

            // 校验调用者身份：若未显式传 studentUserId，则取会话用户；非作业所属学生且非管理员时予以拦截，杜绝越权伪造完成
            var callerId = studentUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (callerId.HasValue && callerId.Value != Guid.Empty && callerId.Value != assignment.StudentUserId)
            {
                var caller = await ctx.Users.FindAsync(callerId.Value);
                if (caller?.Role != UserRole.SuperAdmin)
                {
                    return false;
                }
            }

            // 幂等防重保障：若作业已处于完成状态，直接返回成功，避免重复派发激励与污染完成时间戳
            if (assignment.IsCompleted)
            {
                return true;
            }

            int validTotal = Math.Max(0, totalAnswered);
            int validCorrect = Math.Clamp(correctCount, 0, validTotal);
            int validScore = Math.Clamp(score, 0, 100);

            assignment.IsCompleted = true;
            assignment.CorrectCount = validCorrect;
            assignment.TotalAnswered = validTotal;
            assignment.Score = validScore;
            assignment.AccuracyRate = validTotal > 0 ? (int)Math.Round((double)validCorrect / validTotal * 100) : 0;
            assignment.CompletedAt = DateTime.Now;

            // 作业结课里程碑激励：为完成任务的学生派发成就经验与金币奖励
            var student = await ctx.Users.FindAsync(assignment.StudentUserId);
            if (student != null)
            {
                int bonusExp = 50;
                int bonusCoins = 30;

                // 若正确率达到 80% 及以上，追加学霸优异奖
                if (assignment.AccuracyRate >= 80)
                {
                    bonusExp += 25;
                    bonusCoins += 15;
                }

                student.Exp += bonusExp;
                student.Coins += bonusCoins;

                GamificationService.CheckAndProcessLevelUp(student);
            }

            await ctx.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DeleteHomeworkAssignmentAsync(Guid assignmentId, Guid? requestorUserId = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var assignment = await ctx.HomeworkAssignments.FindAsync(assignmentId);
            if (assignment == null) return false;

            // 若提供了请求者 ID，校验是否为布置者本人或超级管理员，防 IDOR 越权恶意删除
            if (requestorUserId.HasValue && requestorUserId.Value != Guid.Empty)
            {
                var caller = await ctx.Users.FindAsync(requestorUserId.Value);
                bool isSuperAdmin = caller?.Role == UserRole.SuperAdmin;
                if (!isSuperAdmin && assignment.CreatorUserId != requestorUserId.Value)
                {
                    return false;
                }
            }

            ctx.HomeworkAssignments.Remove(assignment);
            await ctx.SaveChangesAsync();
            return true;
        }

        public async Task<(bool Success, string Message)> SendHomeworkReminderNudgeAsync(Guid parentId, Guid assignmentId, string? customNudge = null)
        {
            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var assignment = await ctx.HomeworkAssignments
                .Include(a => a.StudentUser)
                .FirstOrDefaultAsync(a => a.Id == assignmentId);

            if (assignment == null)
            {
                return (false, "未找到该专属作业记录。");
            }

            if (assignment.IsCompleted)
            {
                return (false, "该作业已全部完成，无需重复提醒。");
            }

            // RBAC & IDOR 防越权校验：必须为作业布置者 或 与该学生具有合法绑定关系的监护人
            bool isCreator = assignment.CreatorUserId == parentId;
            bool isBoundParent = await ctx.StudentParentBindings.AnyAsync(b => b.ParentUserId == parentId && b.StudentUserId == assignment.StudentUserId);

            if (!isCreator && !isBoundParent)
            {
                return (false, "无权对此作业发送催促提醒！");
            }

            var student = assignment.StudentUser ?? await ctx.Users.FirstOrDefaultAsync(u => u.Id == assignment.StudentUserId);
            if (student == null)
            {
                return (false, "作业关联的学生账号不存在。");
            }

            string nudgeText = !string.IsNullOrWhiteSpace(customNudge)
                ? customNudge.Trim()
                : $"【作业提醒】家长提醒你完成专属作业《{assignment.Title}》（共 {assignment.QuestionCount} 题），今日冲刺加油！";

            student.ParentEncouragementNote = nudgeText;
            student.ParentNoteUpdatedAt = DateTime.Now;

            await ctx.SaveChangesAsync();
            return (true, $"已成功向【{student.Username}】发送作业提醒寄语！");
        }

        public async Task<List<PracticeRecord>> GetUserPracticeRecordsAsync(Guid userId, int? take = null, Guid? requestorUserId = null)
        {
            if (userId == Guid.Empty) return new List<PracticeRecord>();

            await using var dbScope = await CreateDbScopeAsync();
            var ctx = dbScope.Context;

            var callerId = requestorUserId ?? _userSessionService.CurrentUserId ?? (await _userSessionService.GetActiveUserAsync())?.Id;
            if (callerId.HasValue && callerId.Value != userId)
            {
                var isAuthorized = await IsAuthorizedToAccessStudentDataAsync(callerId.Value, userId, ctx);
                if (!isAuthorized)
                {
                    return new List<PracticeRecord>();
                }
            }

            var query = ctx.PracticeRecords
                .AsNoTracking()
                .Include(r => r.Question)
                .Where(r => r.UserId == userId)
                .OrderByDescending(r => r.AnsweredAt);

            if (take.HasValue && take.Value > 0)
            {
                return await query.Take(take.Value).ToListAsync();
            }
            return await query.ToListAsync();
        }
    }
}
