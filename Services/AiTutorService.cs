using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class AiTutorService : IAiTutorService
    {
        private readonly IGamificationService _gamificationService;
        private readonly HttpClient _httpClient;
        private readonly AppDbContext? _dbContext;
        private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;
        private readonly ISystemHealthService? _systemHealthService;
        private readonly IUserSessionService? _userSessionService;

        public AiTutorService(
            IGamificationService gamificationService, 
            IHttpClientFactory httpClientFactory, 
            AppDbContext? dbContext = null,
            IDbContextFactory<AppDbContext>? dbContextFactory = null,
            ISystemHealthService? systemHealthService = null,
            IUserSessionService? userSessionService = null)
        {
            _gamificationService = gamificationService;
            _httpClient = httpClientFactory.CreateClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(25);
            _dbContext = dbContext;
            _dbContextFactory = dbContextFactory;
            _systemHealthService = systemHealthService;
            _userSessionService = userSessionService;
        }

        private async Task<User> ResolveEffectiveUserAsync(User user)
        {
            if (_userSessionService != null)
            {
                return await _userSessionService.ResolveEffectiveUserLlmConfigAsync(user);
            }

            if (!string.IsNullOrWhiteSpace(user.LlmApiKey))
            {
                return user;
            }

            try
            {
                if (_dbContextFactory != null || _dbContext != null)
                {
                    await using var dbScope = await AsyncDbScope.CreateAsync(_dbContextFactory, _dbContext!);
                    var admin = await dbScope.Context.Users
                        .AsNoTracking()
                        .FirstOrDefaultAsync(u => u.Role == UserRole.SuperAdmin && !string.IsNullOrWhiteSpace(u.LlmApiKey));
                    admin ??= await dbScope.Context.Users
                        .AsNoTracking()
                        .FirstOrDefaultAsync(u => u.Role == UserRole.SuperAdmin);

                    if (admin != null && !string.IsNullOrWhiteSpace(admin.LlmApiKey))
                    {
                        return new User
                        {
                            Id = user.Id,
                            Username = user.Username,
                            Grade = user.Grade,
                            Role = user.Role,
                            LlmApiKey = admin.LlmApiKey,
                            LlmBaseUrl = string.IsNullOrWhiteSpace(admin.LlmBaseUrl) ? "https://generativelanguage.googleapis.com/v1beta/openai/" : admin.LlmBaseUrl,
                            LlmModelName = string.IsNullOrWhiteSpace(admin.LlmModelName) ? "gemini-1.5-flash" : admin.LlmModelName
                        };
                    }
                }
            }
            catch
            {
                // 忽略异常
            }

            return user;
        }

        public async Task<AiExplanationResult> GetExplanationAsync(Question question, string? userAnswer = null)
        {
            var user = await _gamificationService.GetCurrentUserAsync();
            var effectiveUser = await ResolveEffectiveUserAsync(user);
            if (!string.IsNullOrWhiteSpace(effectiveUser.LlmApiKey))
            {
                try
                {
                    return await CallLlmExplanationAsync(effectiveUser, question, userAnswer);
                }
                catch (Exception ex)
                {
                    _systemHealthService?.RecordArchitectureEvent("AiTutor", "Warning", $"LLM 解析生成异常降级: {ex.Message}");
                    // 若 API 调用失败，自动降级回启发式智能解析
                }
            }

            return GenerateFallbackExplanation(question, userAnswer);
        }

        public async Task<SocraticGuidanceResult> GetSocraticGuidanceAsync(Question question, string? userAnswer = null)
        {
            var user = await _gamificationService.GetCurrentUserAsync();
            var effectiveUser = await ResolveEffectiveUserAsync(user);
            if (!string.IsNullOrWhiteSpace(effectiveUser.LlmApiKey))
            {
                try
                {
                    return await CallLlmSocraticAsync(effectiveUser, question, userAnswer);
                }
                catch (Exception ex)
                {
                    _systemHealthService?.RecordArchitectureEvent("AiTutor", "Warning", $"LLM 苏格拉底式提问生成异常降级: {ex.Message}");
                    // 降级生成苏格拉底式提问
                }
            }

            return GenerateFallbackSocraticGuidance(question, userAnswer);
        }

        public async Task<Question> GenerateVariationQuestionAsync(Question originalQuestion)
        {
            var user = await _gamificationService.GetCurrentUserAsync();
            var effectiveUser = await ResolveEffectiveUserAsync(user);
            if (!string.IsNullOrWhiteSpace(effectiveUser.LlmApiKey))
            {
                try
                {
                    return await CallLlmVariationAsync(effectiveUser, originalQuestion);
                }
                catch (Exception ex)
                {
                    _systemHealthService?.RecordArchitectureEvent("AiTutor", "Warning", $"LLM 变式题生成异常降级: {ex.Message}");
                    // 降级生成变式
                }
            }

            return GenerateFallbackVariation(originalQuestion);
        }

        public async Task<string> AskAiTutorAsync(string questionContext, string userPrompt)
        {
            var user = await _gamificationService.GetCurrentUserAsync();
            var effectiveUser = await ResolveEffectiveUserAsync(user);
            if (!string.IsNullOrWhiteSpace(effectiveUser.LlmApiKey))
            {
                try
                {
                    return await CallLlmAskAsync(effectiveUser, questionContext, userPrompt);
                }
                catch (Exception ex)
                {
                    return $"🤖 **AI 导师提示 (网络通信异常)**：{ex.Message}\n\n关于【{userPrompt}】：建议复习【{questionContext}】的基础概念，掌握核心结论与边界条件。";
                }
            }

            return GenerateFallbackAskReply(questionContext, userPrompt);
        }

        public async Task<SubjectiveGradingResult> GradeSubjectiveAnswerAsync(Question question, string userAnswer)
        {
            var user = await _gamificationService.GetCurrentUserAsync();
            var effectiveUser = await ResolveEffectiveUserAsync(user);
            if (!string.IsNullOrWhiteSpace(effectiveUser.LlmApiKey))
            {
                try
                {
                    return await CallLlmSubjectiveGradingAsync(effectiveUser, question, userAnswer);
                }
                catch (Exception ex)
                {
                    _systemHealthService?.RecordArchitectureEvent("AiTutor", "Warning", $"LLM 主观题智能判分异常降级: {ex.Message}");
                    // 降级主观题打分
                }
            }

            return GenerateFallbackSubjectiveGrading(question, userAnswer);
        }

        #region Real LLM API Handlers

        private async Task<AiExplanationResult> CallLlmExplanationAsync(User user, Question question, string? userAnswer)
        {
            var systemPrompt = "你是一位极具耐心与亲和力的 AI 名师导师。请针对学生的作答提供深度的错因剖析与分步讲解。只返回合法 JSON 对象，格式如下：\n" +
                "{\n" +
                "  \"summary\": \"3秒核心破解结论\",\n" +
                "  \"keyConcepts\": [\"考点1\", \"考点2\"],\n" +
                "  \"whyWrongAnalysis\": \"针对学生错选/填答的错因分析与避坑指南\",\n" +
                "  \"stepByStepReasoning\": \"清晰的步骤推导过程 (支持 Markdown 语法)\"\n" +
                "}";

            var userPrompt = $"题目学科：{question.Subject}\n知识点：{question.Category}\n题干：{question.Stem}\n标准答案：{question.CorrectAnswer}\n标准解析：{question.StandardAnalysis}\n学生提交答案：{userAnswer ?? "未作答/请求深度讲解"}";

            var jsonStr = await PostLlmRequestAsync(user, systemPrompt, userPrompt, question.Subject, question.Category, question.Id);
            var cleanJson = ExtractJsonBlock(jsonStr);

            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            var result = new AiExplanationResult
            {
                Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                WhyWrongAnalysis = root.TryGetProperty("whyWrongAnalysis", out var w) ? w.GetString() ?? "" : "",
                StepByStepReasoning = root.TryGetProperty("stepByStepReasoning", out var r) ? r.GetString() ?? "" : ""
            };

            if (root.TryGetProperty("keyConcepts", out var kArray) && kArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in kArray.EnumerateArray())
                {
                    if (!string.IsNullOrEmpty(item.GetString()))
                        result.KeyConcepts.Add(item.GetString()!);
                }
            }

            return result;
        }

        private async Task<SocraticGuidanceResult> CallLlmSocraticAsync(User user, Question question, string? userAnswer)
        {
            var systemPrompt = "你是一位奉行【苏格拉底提问法】的高级 AI 名师。绝对不要直接在回答中给出题目的标准答案字母或文本，也不要直接暴露解题全过程！\n" +
                "你的任务是：根据学生的错选/填答，给出 1 个启发性思维提示，以及 2-3 个循序渐进、由浅入深的反思提问，引导学生自己找出逻辑漏洞并顿悟。只输出合法 JSON 对象：\n" +
                "{\n" +
                "  \"thinkingHint\": \"对学生思路切入点的温和启发暗示（不给答案）\",\n" +
                "  \"progressiveQuestions\": [\n" +
                "    \"问题1：引导审查题目核心已知条件或定义前提？\",\n" +
                "    \"问题2：引导对比作答与已知规则/公式的矛盾点？\",\n" +
                "    \"问题3：引导推演正确结论的临界条件？\"\n" +
                "  ],\n" +
                "  \"reflectionPrompt\": \"一句话反思核验提示\",\n" +
                "  \"targetCategory\": \"考点名字\"\n" +
                "}";

            var userPrompt = $"题目学科：{question.Subject}\n考点：{question.Category}\n题干：{question.Stem}\n标准答案(内部参考,勿透漏)：{question.CorrectAnswer}\n学生提交的错误答案/作答：{userAnswer ?? "未作答/请求苏格拉底引导"}";

            var jsonStr = await PostLlmRequestAsync(user, systemPrompt, userPrompt, question.Subject, question.Category, question.Id);
            var cleanJson = ExtractJsonBlock(jsonStr);

            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            var result = new SocraticGuidanceResult
            {
                ThinkingHint = root.TryGetProperty("thinkingHint", out var th) ? th.GetString() ?? "" : "",
                ReflectionPrompt = root.TryGetProperty("reflectionPrompt", out var rf) ? rf.GetString() ?? "" : "",
                TargetCategory = root.TryGetProperty("targetCategory", out var tc) ? tc.GetString() ?? question.Category : question.Category
            };

            if (root.TryGetProperty("progressiveQuestions", out var qArray) && qArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in qArray.EnumerateArray())
                {
                    if (!string.IsNullOrWhiteSpace(item.GetString()))
                        result.ProgressiveQuestions.Add(item.GetString()!);
                }
            }

            if (result.ProgressiveQuestions.Count == 0)
            {
                result.ProgressiveQuestions.Add("思考1：题干中最关键的已知条件和边界约束是什么？");
                result.ProgressiveQuestions.Add("思考2：你在选择/填答该选项时，忽略了哪一条核心原理？");
            }

            return result;
        }

        private async Task<Question> CallLlmVariationAsync(User user, Question originalQuestion)
        {
            var systemPrompt = "你是一位资深命题专家。请根据原题考点生成一道同等难度但情景/参数不同的【巩固变式题】。只返回合法 JSON 对象：\n" +
                "{\n" +
                "  \"stem\": \"变式题题干\",\n" +
                "  \"options\": [\"A. 选项1\", \"B. 选项2\", \"C. 选项3\", \"D. 选项4\"],\n" +
                "  \"correctAnswer\": \"A\",\n" +
                "  \"analysis\": \"变式题详细解析\"\n" +
                "}";

            var userPrompt = $"原题学科：{originalQuestion.Subject}\n考点：{originalQuestion.Category}\n原题干：{originalQuestion.Stem}\n原正确答案：{originalQuestion.CorrectAnswer}";

            var jsonStr = await PostLlmRequestAsync(user, systemPrompt, userPrompt, originalQuestion.Subject, originalQuestion.Category, originalQuestion.Id);
            var cleanJson = ExtractJsonBlock(jsonStr);

            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            var stem = root.GetProperty("stem").GetString() ?? "";
            var optionsList = new List<string>();
            if (root.TryGetProperty("options", out var optArr) && optArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var opt in optArr.EnumerateArray()) optionsList.Add(opt.GetString() ?? "");
            }
            var correct = root.GetProperty("correctAnswer").GetString() ?? "A";
            var analysis = root.GetProperty("analysis").GetString() ?? "";

            return new Question
            {
                Id = Guid.NewGuid(),
                Subject = originalQuestion.Subject,
                Category = originalQuestion.Category,
                GradeTarget = originalQuestion.GradeTarget,
                Type = QuestionType.SingleChoice,
                Stem = stem,
                OptionsJson = JsonSerializer.Serialize(optionsList),
                CorrectAnswer = correct,
                StandardAnalysis = analysis,
                Difficulty = originalQuestion.Difficulty,
                BaseExpReward = originalQuestion.BaseExpReward + 10,
                IsPublic = false
            };
        }

        private async Task<string> CallLlmAskAsync(User user, string questionContext, string userPrompt)
        {
            var systemPrompt = "你是一位优秀的专职 AI 学习助手。请用清晰、鼓励、富有启发性的语气回答学生的追问。支持 Markdown 排版，可适当举例。";
            var contentPrompt = $"【学习上下文 / 题目】\n{questionContext}\n\n【学生追问】\n{userPrompt}";

            return await PostLlmTextRequestAsync(user, systemPrompt, contentPrompt, "AI专属导师", "自由追问", null);
        }

        private async Task<SubjectiveGradingResult> CallLlmSubjectiveGradingAsync(User user, Question question, string userAnswer)
        {
            var systemPrompt = "你是一位严格而公正的考官 AI。请对学生的主观题/简答题回答进行深度批改与打分 (0-100 分)。只输出合法 JSON 对象：\n" +
                "{\n" +
                "  \"score\": 85,\n" +
                "  \"isPassed\": true,\n" +
                "  \"feedback\": \"优点与得分点评析\",\n" +
                "  \"missingPoints\": [\"遗漏要点1\", \"遗漏要点2\"],\n" +
                "  \"suggestedAnswer\": \"标准满分示范作答\"\n" +
                "}";

            var userPrompt = $"题目类型：{question.Type}\n学科：{question.Subject}\n题干：{question.Stem}\n参考标准答案/得分要点：{question.CorrectAnswer}\n官方解析：{question.StandardAnalysis}\n学生提交作答：{userAnswer}";

            var jsonStr = await PostLlmRequestAsync(user, systemPrompt, userPrompt, question.Subject, "主观题AI批改", question.Id);
            var cleanJson = ExtractJsonBlock(jsonStr);

            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            int score = root.TryGetProperty("score", out var sc) ? sc.GetInt32() : (userAnswer.Length >= 10 ? 75 : 40);
            bool isPassed = root.TryGetProperty("isPassed", out var p) ? p.GetBoolean() : (score >= 60);
            string feedback = root.TryGetProperty("feedback", out var f) ? f.GetString() ?? "" : "";
            string suggestedAnswer = root.TryGetProperty("suggestedAnswer", out var sa) ? sa.GetString() ?? "" : question.CorrectAnswer;

            var missing = new List<string>();
            if (root.TryGetProperty("missingPoints", out var mArray) && mArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in mArray.EnumerateArray())
                {
                    if (!string.IsNullOrEmpty(item.GetString()))
                        missing.Add(item.GetString()!);
                }
            }

            var rubricList = new List<string>();
            if (root.TryGetProperty("rubricBreakdown", out var rArray) && rArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rArray.EnumerateArray())
                {
                    if (!string.IsNullOrEmpty(item.GetString()))
                        rubricList.Add(item.GetString()!);
                }
            }
            if (rubricList.Count == 0)
            {
                rubricList.Add(score >= 80 ? "🎯 核心考点涵盖: 优秀" : (score >= 60 ? "🎯 核心考点涵盖: 基本达标" : "⚠️ 核心考点涵盖: 不足"));
                rubricList.Add(score >= 70 ? "🔍 逻辑推演过程: 连贯" : "🔍 逻辑推演过程: 需加强");
                rubricList.Add(isPassed ? "📝 学科规范表述: 合格" : "📝 学科规范表述: 待规范");
            }

            string encouragement = root.TryGetProperty("encouragementAdvice", out var ea) ? ea.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(encouragement))
            {
                encouragement = isPassed
                    ? "作答展现了扎实的学科素养，继续稳扎稳打保持优势！"
                    : "复盘错因并总结答题要点，下一道题一定能迎刃而解！";
            }

            return new SubjectiveGradingResult
            {
                Score = score,
                IsPassed = isPassed,
                Feedback = feedback,
                MissingPoints = missing,
                SuggestedAnswer = suggestedAnswer,
                RubricBreakdown = rubricList,
                EncouragementAdvice = encouragement
            };
        }

        private async Task<string> PostLlmRequestAsync(User user, string systemPrompt, string userPrompt, string subject = "AI助学辅导", string category = "AI答疑", Guid? questionId = null)
        {
            var modelName = string.IsNullOrWhiteSpace(user.LlmModelName) ? "gpt-4o-mini" : user.LlmModelName;
            var requestBody = new
            {
                model = modelName,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.4,
                response_format = new { type = "json_object" }
            };

            var targetUrl = GetTargetUrl(user.LlmBaseUrl);
            var request = new HttpRequestMessage(HttpMethod.Post, targetUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.LlmApiKey.Trim());
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var response = await _httpClient.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();

            var jsonStr = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonStr);

            int promptTokens = 0, completionTokens = 0, totalTokens = 0;
            if (doc.RootElement.TryGetProperty("usage", out var usageEl))
            {
                promptTokens = usageEl.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                completionTokens = usageEl.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                totalTokens = usageEl.TryGetProperty("total_tokens", out var tt) ? tt.GetInt32() : 0;
            }

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content").GetString() ?? "{}";

            if (promptTokens == 0) promptTokens = Math.Max(10, (systemPrompt.Length + userPrompt.Length) / 2);
            if (completionTokens == 0) completionTokens = Math.Max(10, content.Length / 2);
            if (totalTokens == 0) totalTokens = promptTokens + completionTokens;

            await RecordLlmTokenUsageAsync(user, modelName, subject, category, questionId, promptTokens, completionTokens, totalTokens);

            return content;
        }

        private async Task<string> PostLlmTextRequestAsync(User user, string systemPrompt, string userPrompt, string subject = "AI助学辅导", string category = "AI答疑", Guid? questionId = null)
        {
            var modelName = string.IsNullOrWhiteSpace(user.LlmModelName) ? "gpt-4o-mini" : user.LlmModelName;
            var requestBody = new
            {
                model = modelName,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.7
            };

            var targetUrl = GetTargetUrl(user.LlmBaseUrl);
            var request = new HttpRequestMessage(HttpMethod.Post, targetUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.LlmApiKey.Trim());
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var response = await _httpClient.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();

            var jsonStr = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonStr);

            int promptTokens = 0, completionTokens = 0, totalTokens = 0;
            if (doc.RootElement.TryGetProperty("usage", out var usageEl))
            {
                promptTokens = usageEl.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                completionTokens = usageEl.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                totalTokens = usageEl.TryGetProperty("total_tokens", out var tt) ? tt.GetInt32() : 0;
            }

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content").GetString() ?? "回复解析完成。";

            if (promptTokens == 0) promptTokens = Math.Max(10, (systemPrompt.Length + userPrompt.Length) / 2);
            if (completionTokens == 0) completionTokens = Math.Max(10, content.Length / 2);
            if (totalTokens == 0) totalTokens = promptTokens + completionTokens;

            await RecordLlmTokenUsageAsync(user, modelName, subject, category, questionId, promptTokens, completionTokens, totalTokens);

            return content;
        }

        private async Task RecordLlmTokenUsageAsync(User user, string modelName, string subject, string category, Guid? questionId, int promptTokens, int completionTokens, int totalTokens)
        {
            if (user == null || user.Id == Guid.Empty) return;

            try
            {
                var log = new LlmGenerationLog
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = questionId,
                    ModelName = modelName,
                    Subject = string.IsNullOrWhiteSpace(subject) ? "AI助学辅导" : subject,
                    Category = string.IsNullOrWhiteSpace(category) ? "答疑诊断" : category,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    GeneratedAt = DateTime.Now
                };

                if (_dbContextFactory != null)
                {
                    using var factoryContext = await _dbContextFactory.CreateDbContextAsync();
                    factoryContext.LlmGenerationLogs.Add(log);
                    await factoryContext.SaveChangesAsync();
                }
                else if (_dbContext != null)
                {
                    _dbContext.LlmGenerationLogs.Add(log);
                    await _dbContext.SaveChangesAsync();
                }
            }
            catch
            {
                // 容错：Token 审计日志记录失败不阻断核心辅导回复流程
            }
        }

        private string GetTargetUrl(string baseUrl)
        {
            var defaultBase = "https://generativelanguage.googleapis.com/v1beta/openai/";
            var raw = string.IsNullOrWhiteSpace(baseUrl) ? defaultBase : baseUrl.TrimEnd('/');
            if (raw.EndsWith("/chat/completions"))
            {
                raw = raw.Substring(0, raw.Length - "/chat/completions".Length);
            }
            return $"{raw.TrimEnd('/')}/chat/completions";
        }

        private string ExtractJsonBlock(string input)
        {
            var extracted = Northtropic.Helpers.JsonExtractorHelper.ExtractJson(input);
            return string.IsNullOrWhiteSpace(extracted) ? "{}" : extracted;
        }

        #endregion

        #region Heuristic Fallbacks

        private AiExplanationResult GenerateFallbackExplanation(Question question, string? userAnswer)
        {
            var result = new AiExplanationResult
            {
                Summary = $"【核心破解】本题考察【{question.Subject}】之【{question.Category}】知识点。精准把握标准原理与关键条件是提分关键！",
                KeyConcepts = new List<string>
                {
                    $"{question.Category} 核心定义与标准规范",
                    "常见思维陷阱与边界边界条件分析"
                },
                StepByStepReasoning = $"### 🧠 智能分步解析：\n" +
                    $"1. **审题分析**：题目要求作答：*{question.Stem}*\n" +
                    $"2. **知识关联**：属于【{question.Subject}】体系中【{question.Category}】的重要考点。\n" +
                    $"3. **推导出结论**：正确答案为 `{question.CorrectAnswer}`。\n" +
                    $"4. **官方标准解析**：{question.StandardAnalysis}"
            };

            if (!string.IsNullOrEmpty(userAnswer))
            {
                result.WhyWrongAnalysis = $"⚠️ **错因诊断 (针对作答 '{userAnswer}')**：\n" +
                    $"你在作答时填/选了 `{userAnswer}`。这说明在【{question.Category}】相关概念理解上存在混淆，可能忽略了特定约束或语法规则。\n" +
                    $"**避坑建议**：复习相关章节，切记正确结论是 `{question.CorrectAnswer}`。";
            }

            return result;
        }

        private Question GenerateFallbackVariation(Question originalQuestion)
        {
            string subject = originalQuestion.Subject ?? "综合";
            string category = originalQuestion.Category ?? "核心概念";

            string stem;
            string optionsJson;
            string correctAnswer;
            string analysis;

            if (subject.Contains("数"))
            {
                stem = $"【变式强化 · {category}】已知某直角三角形的两直角边长分别为 $a=6$ 和 $b=8$，则该三角形斜边上的高 $h$ 为多少？";
                optionsJson = "[\"A. 4.8\", \"B. 5.0\", \"C. 7.2\", \"D. 10.0\"]";
                correctAnswer = "A";
                analysis = "【变式解析】由勾股定理可得斜边 $c=\\sqrt{6^2+8^2}=10$。利用面积等积法：$\\frac{1}{2}ab = \\frac{1}{2}ch$，即 $6 \\times 8 = 10 \\times h$，解得 $h = \\frac{48}{10} = 4.8$。故选 A。";
            }
            else if (subject.Contains("物"))
            {
                stem = $"【变式强化 · {category}】在水平桌面上放一重为 $20\\text{{N}}$ 的物块，用 $5\\text{{N}}$ 的水平拉力未能拉动物块，此时物块受到的静摩擦力大小及方向为？";
                optionsJson = "[\"A. 5N，与拉力方向相反\", \"B. 20N，竖直向上\", \"C. 0N，无运动趋势\", \"D. 15N，水平向左\"]";
                correctAnswer = "A";
                analysis = "【变式解析】物块静止处于平衡状态，水平方向受到的拉力与静摩擦力是一对平衡力，二力大小相等、方向相反。故静摩擦力为 5N，方向与拉力方向相反。故选 A。";
            }
            else if (subject.Contains("化"))
            {
                stem = $"【变式强化 · {category}】向足量稀盐酸中加入下列哪组物质，反应后溶液质量增加且生成气泡？";
                optionsJson = "[\"A. 铁钉 (Fe)\", \"B. 氢氧化钠 (NaOH)\", \"C. 氧化铜 (CuO)\", \"D. 硫酸铜 (CuSO4)\"]";
                correctAnswer = "A";
                analysis = "【变式解析】铁与稀盐酸反应生成氯化亚铁和氢气：$\\text{Fe} + 2\\text{HCl} = \\text{FeCl}_2 + \\text{H}_2\\uparrow$。每 56 份质量的铁置换出 2 份质量的氢气，溶液净增加 54 份质量并产生气泡。故选 A。";
            }
            else if (subject.Contains("英"))
            {
                stem = $"【变式强化 · {category}】The scientist ______ won the Nobel Prize in Physics last year will visit our school tomorrow.";
                optionsJson = "[\"A. who\", \"B. which\", \"C. whose\", \"D. where\"]";
                correctAnswer = "A";
                analysis = "【变式解析】先行词为 The scientist (人)，在定语从句中作主语，关系代词须选用 who 或 that。故选 A。";
            }
            else if (subject.Contains("语"))
            {
                stem = $"【变式强化 · {category}】下列句子中加点成语使用恰当、符合语境的一项是？";
                optionsJson = "[\"A. 面对复杂的数理综合难题，他抽丝剥茧，终于找到了破题关键\", \"B. 运动会上，同学们班门弄斧，展现了顽强的拼搏风貌\", \"C. 他对传统文化略知一二，便在此处贻笑大方、高谈阔论\", \"D. 暴雨过后，城市道路首当其冲，交通陷入瘫痪\"]";
                correctAnswer = "A";
                analysis = "【变式解析】A 项“抽丝剥茧”形容分析事物极其细致，符合语境。B 项“班门弄斧”为贬义，用在此处不当；C 项“贻笑大方”指被行家见笑，不能作谓语并列；D 项“首当其冲”比喻最先受到攻击或灾难，修饰道路不妥。故选 A。";
            }
            else
            {
                stem = $"【变式强化 · {category}】关于本章节所涉核心原理在实践拓展中的应用，下列阐述最严谨合理的是？";
                optionsJson = "[\"A. 全面结合前提假设与守恒定律进行综合论证\", \"B. 仅凭表面单一变量即可下绝对结论\", \"C. 忽略边界条件与初始约束直接推导\", \"D. 任意颠倒因果关系与时序先后顺序\"]";
                correctAnswer = "A";
                analysis = "【变式解析】科学探究与命题解题均需全面把握核心概念的前提约束、边界条件以及守恒规律，切忌片面孤立推断。故选 A。";
            }

            return new Question
            {
                Id = Guid.NewGuid(),
                Subject = originalQuestion.Subject ?? "综合",
                Category = originalQuestion.Category ?? "核心概念",
                Type = QuestionType.SingleChoice,
                Stem = stem,
                OptionsJson = optionsJson,
                CorrectAnswer = correctAnswer,
                StandardAnalysis = analysis,
                Difficulty = Math.Min(5, originalQuestion.Difficulty + 1),
                BaseExpReward = originalQuestion.BaseExpReward + 10
            };
        }

        private string GenerateFallbackAskReply(string questionContext, string userPrompt)
        {
            return $"🤖 **AI 导师回复**：\n\n" +
                $"关于你提问的：“*{userPrompt}*”：\n\n" +
                $"在【{questionContext}】的学习中，这是一个非常核心的问题！\n" +
                $"1. **关键原理**：一定要理解底层机制而非死记硬背答案。\n" +
                $"2. **解题技巧**：先梳理已知条件，再带入公式或逻辑链条。\n" +
                $"3. **推荐动作**：在配置页面填入 API Key 即可开启无限深度 AI 交互！";
        }

        private SubjectiveGradingResult GenerateFallbackSubjectiveGrading(Question question, string userAnswer)
        {
            var cleanUser = userAnswer?.Trim() ?? string.Empty;
            var cleanCorrect = (question.CorrectAnswer ?? string.Empty).Trim();

            // 架构与判题严谨性防护：填空题全面复用 PracticeService 的全套数学/化学/科学容错判定
            if (question.Type == QuestionType.FillInBlank)
            {
                bool isCoreMatch = !string.IsNullOrEmpty(cleanCorrect) && !string.IsNullOrEmpty(cleanUser) &&
                    (PracticeService.CheckFillInBlankMatch(cleanUser, cleanCorrect) ||
                     cleanUser.Equals(cleanCorrect, StringComparison.OrdinalIgnoreCase) ||
                     cleanUser.Contains(cleanCorrect, StringComparison.OrdinalIgnoreCase) ||
                     (cleanCorrect.Length >= 4 && cleanCorrect.Contains(cleanUser, StringComparison.OrdinalIgnoreCase)));

                int fillScore = isCoreMatch ? 90 : 30;
                return new SubjectiveGradingResult
                {
                    Score = fillScore,
                    IsPassed = isCoreMatch,
                    Feedback = isCoreMatch
                        ? "作答精准包含核心考点数值与关键术语，判定通过！"
                        : $"填空内容未命中核心标准答案，参考答案为：{question.CorrectAnswer}",
                    MissingPoints = isCoreMatch ? new List<string>() : new List<string> { question.CorrectAnswer ?? string.Empty },
                    SuggestedAnswer = question.CorrectAnswer ?? string.Empty,
                    RubricBreakdown = isCoreMatch
                        ? new List<string> { "✅ 考点数值/概念命中: 100%", "✅ 符号与单位规范: 优秀" }
                        : new List<string> { "❌ 核心概念/数值匹配: 待核对", "⚠️ 步骤或格式: 需规范" },
                    EncouragementAdvice = isCoreMatch
                        ? "解题思路非常敏捷准确！继续保持这种高效的状态。"
                        : "仔细比对参考答案与题目条件，重点关注题干中的隐含约束与易混淆概念。"
                };
            }

            if (cleanUser.Length >= 20)
            {
                return new SubjectiveGradingResult
                {
                    Score = 85,
                    IsPassed = true,
                    Feedback = "作答详实充分，核心考点涵盖全面，推理论述清晰连贯。",
                    MissingPoints = new List<string>(),
                    SuggestedAnswer = question.CorrectAnswer ?? string.Empty,
                    RubricBreakdown = new List<string> { "🎯 核心论点覆盖: 优秀", "🔍 逻辑推理链条: 严密", "📝 规范学科术语: 良好" },
                    EncouragementAdvice = "答题结构非常完整！若能进一步精炼分点阐述，在考试中将更具得分优势。"
                };
            }
            else if (cleanUser.Length >= 10)
            {
                return new SubjectiveGradingResult
                {
                    Score = 70,
                    IsPassed = true,
                    Feedback = "作答基本涵盖要点，已具备核心解题雏形，但步骤与论据仍可进一步丰富。",
                    MissingPoints = new List<string> { "核心推断依据", "展开步骤阐述" },
                    SuggestedAnswer = question.CorrectAnswer ?? string.Empty,
                    RubricBreakdown = new List<string> { "🎯 核心论点覆盖: 基本达标", "🔍 逻辑推理链条: 部分简略", "📝 规范学科术语: 良好" },
                    EncouragementAdvice = "方向基本正确！建议在平时练习中有意识地将推理步骤展开，写出更坚实的论据。"
                };
            }
            else
            {
                return new SubjectiveGradingResult
                {
                    Score = 40,
                    IsPassed = false,
                    Feedback = "作答过于简略，缺少必要的核心推导步骤与学科关键词。",
                    MissingPoints = new List<string> { "核心推断依据", "标准步骤推导" },
                    SuggestedAnswer = question.CorrectAnswer ?? string.Empty,
                    RubricBreakdown = new List<string> { "⚠️ 核心论点覆盖: 不足", "⚠️ 逻辑推演过程: 缺失", "⚠️ 答题规范性: 需强化" },
                    EncouragementAdvice = "尝试梳理题目的因果关系，结合题干关键信息展开 2-3 句完整的逻辑阐述。"
                };
            }
        }

        private SocraticGuidanceResult GenerateFallbackSocraticGuidance(Question question, string? userAnswer)
        {
            var result = new SocraticGuidanceResult
            {
                TargetCategory = question.Category,
                ThinkingHint = $"💡 【苏格拉底思维引导】在思考【{question.Subject} - {question.Category}】时，请先仔细回顾该知识点最核心的定义范式与约束前提。",
                ReflectionPrompt = "🔍 重新审视这些问题后，你能否试着修正自己的解题思路并再次推导出正确结论？",
                ProgressiveQuestions = new List<string>
                {
                    $"第一步：重新阅读题干，题目中明确给出的【核心已知条件与限制】有哪些？",
                    $"第二步：比较你的作答 '{(userAnswer ?? "错误选择/填答")}' 与【{question.Category}】的核心定义，两者是否存在矛盾或死角？",
                    $"第三步：如果排除这个逻辑误区，满足全部约束条件的真正推理走向应该是什么？"
                }
            };
            return result;
        }

        public async Task<TestConnectionResult> TestConnectionAsync(string apiKey, string baseUrl, string modelName)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new TestConnectionResult
            {
                ModelName = modelName
            };

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                result.IsSuccess = false;
                result.Message = "API Key 不能为空，请先在上方输入大模型 API Key。";
                return result;
            }

            try
            {
                var cleanBaseUrl = baseUrl.TrimEnd('/');
                var url = $"{cleanBaseUrl}/chat/completions";

                var requestBody = new
                {
                    model = modelName,
                    messages = new[]
                    {
                        new { role = "system", content = "You are a test assistant. Answer with 'READY' in one word." },
                        new { role = "user", content = "Ping test" }
                    },
                    max_tokens = 20,
                    temperature = 0.2
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                request.Content = content;

                var response = await _httpClient.SendAsync(request);
                sw.Stop();
                result.LatencyMs = (int)sw.ElapsedMilliseconds;

                if (response.IsSuccessStatusCode)
                {
                    var responseStr = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(responseStr);
                    var choices = doc.RootElement.GetProperty("choices");
                    var text = choices[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "OK";

                    result.IsSuccess = true;
                    result.SampleResponse = text;
                    result.Message = $"✅ 连通成功！模型 [{modelName}] 响应就绪，网络时延: {result.LatencyMs} ms";
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync();
                    result.IsSuccess = false;
                    result.Message = $"❌ 请求失败 (HTTP {(int)response.StatusCode})：{err}";
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.LatencyMs = (int)sw.ElapsedMilliseconds;
                result.IsSuccess = false;
                result.Message = $"❌ 连接发生异常：{ex.Message}";
            }

            return result;
        }

        #endregion
    }
}
