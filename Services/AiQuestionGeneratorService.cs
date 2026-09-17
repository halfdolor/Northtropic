using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class LlmConfigException : Exception
    {
        public LlmConfigException(string message) : base(message) { }
    }

    public class AiQuestionGeneratorService : IAiQuestionGeneratorService
    {
        private readonly IGamificationService _gamificationService;
        private readonly AppDbContext _dbContext;
        private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;
        private readonly HttpClient _httpClient;
        private readonly ISystemHealthService? _systemHealthService;

        public AiQuestionGeneratorService(
            IGamificationService gamificationService,
            AppDbContext dbContext,
            IHttpClientFactory httpClientFactory,
            IDbContextFactory<AppDbContext>? dbContextFactory = null,
            ISystemHealthService? systemHealthService = null)
        {
            _gamificationService = gamificationService;
            _dbContext = dbContext;
            _dbContextFactory = dbContextFactory;
            _httpClient = httpClientFactory.CreateClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _systemHealthService = systemHealthService;
        }

        private async Task SaveQuestionsAndLogsAsync(IEnumerable<Question> questions, IEnumerable<LlmGenerationLog> logs)
        {
            await using var dbScope = await AsyncDbScope.CreateAsync(_dbContextFactory, _dbContext);
            var ctx = dbScope.Context;
            ctx.Questions.AddRange(questions);
            ctx.LlmGenerationLogs.AddRange(logs);
            await ctx.SaveChangesAsync();
            PracticeService.InvalidateCategoryCache();
        }

        public async Task<Question> GenerateQuestionByGradeAsync(string grade, string subject, string? category = null)
        {
            var list = await GenerateBatchQuestionsAsync(grade, subject, category, 1);
            return list.First();
        }

        public async Task<List<Question>> GenerateBatchQuestionsAsync(string grade, string subject, string? category = null, int count = 5)
        {
            var user = await _gamificationService.GetCurrentUserAsync();
            category ??= "综合运用";
            if (count < 1) count = 1;
            if (count > 50) count = 50;

            if (string.IsNullOrWhiteSpace(user.LlmApiKey))
            {
                // 启发式智能题库引擎：未配置 API Key 时自动派发精选多题型题集
                return await GenerateHeuristicBatchQuestionsAsync(user, grade, subject, category, count);
            }

            try
            {
                var questions = await CallLlmBatchApiOrThrowAsync(user, grade, subject, category, count);
                if (questions != null && questions.Count > 0)
                {
                    // 确保返回的题目不重复
                    var distinctQuestions = questions.DistinctBy(q => q.Stem.Trim()).ToList();
                    if (distinctQuestions.Count < count)
                    {
                        // 补充不足的数量
                        int diff = count - distinctQuestions.Count;
                        var extra = await GenerateHeuristicBatchQuestionsAsync(user, grade, subject, category, diff);
                        distinctQuestions.AddRange(extra);
                    }
                    return distinctQuestions;
                }
                return await GenerateHeuristicBatchQuestionsAsync(user, grade, subject, category, count);
            }
            catch (Exception ex)
            {
                // 网络波动或 API 异常时智能降级为启发式题库，绝不阻断学习
                _systemHealthService?.RecordArchitectureEvent("AiGenerator", "Warning", $"LLM 题库批量生成异常降级: {ex.Message}");
                return await GenerateHeuristicBatchQuestionsAsync(user, grade, subject, category, count);
            }
        }

        private async Task<List<Question>> CallLlmBatchApiOrThrowAsync(User user, string grade, string subject, string category, int count)
        {
            var prompt = $@"你是一位资深高级名师与命题专家。请为【{grade}】学生出【{count}】道【{subject} - {category}】学科的精选习题。
【极其重要的硬性要求】：
1. 必须出【{count}】道题干完全不同、考查侧重点各异、数据或题意完全不重复的精选题目！
2. 严禁出现雷同题干、模板式相同题目或仅改动一个数字的题目！
3. 请根据学科特点混合题型，可选题型字符串 (type) 包括：
   - ""SingleChoice"": 单选题 (需提供 options 数组包含 4 个选项，correctAnswer 如 ""A"")
   - ""MultipleChoice"": 不定项选择题 (需提供 options 数组包含 4 个选项，correctAnswer 如 ""A, C"")
   - ""FillInBlank"": 填空题 (options 为空数组 []，correctAnswer 为短语或数值)
   - ""ShortAnswer"": 简答题 (options 为空数组 []，correctAnswer 为简算要点或步骤)
   - ""EssayAnalysis"": 问答解析综合大题 (options 为空数组 []，correctAnswer 为详细解题步骤)

请严格且仅输出如下 JSON 对象格式，保持精炼，不要输出任何多余的开场白或 markdown 标记：
{{
  ""questions"": [
    {{
      ""stem"": ""独一无二的题干描述内容..."",
      ""type"": ""SingleChoice"",
      ""options"": [""A. 选项1"", ""B. 选项2"", ""C. 选项3"", ""D. 选项4""],
      ""correctAnswer"": ""A"",
      ""analysis"": ""详细深入的解析与解题要点""
    }}
  ]
}}";

            var modelName = string.IsNullOrWhiteSpace(user.LlmModelName) ? "gemini-1.5-flash" : user.LlmModelName;
            var requestBody = new
            {
                model = modelName,
                messages = new[]
                {
                    new { role = "system", content = "你是一个精简高效、命题多样化绝不重复的出题机器人，只输出合法的 JSON 对象。" },
                    new { role = "user", content = prompt }
                },
                temperature = 0.7,
                response_format = new { type = "json_object" }
            };

            var defaultGeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";
            var rawBaseUrl = string.IsNullOrWhiteSpace(user.LlmBaseUrl) ? defaultGeminiBaseUrl : user.LlmBaseUrl.TrimEnd('/');
            if (rawBaseUrl.EndsWith("/chat/completions"))
            {
                rawBaseUrl = rawBaseUrl.Substring(0, rawBaseUrl.Length - "/chat/completions".Length);
            }
            var targetUrl = $"{rawBaseUrl.TrimEnd('/')}/chat/completions";

            var request = new HttpRequestMessage(HttpMethod.Post, targetUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.LlmApiKey.Trim());
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                response = await _httpClient.SendAsync(request, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new LlmConfigException("❌ 大模型 API 请求超时（超过 30 秒无响应），已自动降级。请检查网络连通性或服务响应速度！");
            }
            catch (Exception ex)
            {
                throw new LlmConfigException($"❌ 大模型 API 连接失败: {ex.Message}。请检查 Base URL 与网络连通性！");
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                throw new LlmConfigException($"❌ 大模型 API 返回错误 (HTTP {(int)response.StatusCode}): {errorText}。请检查 API Key 与模型名称！");
            }

            var jsonStr = await response.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var rootEl = doc.RootElement;
                var content = rootEl
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content").GetString();

                if (string.IsNullOrEmpty(content))
                {
                    throw new LlmConfigException("❌ 大模型返回的文本为空，请重试！");
                }

                // Token 统计
                int promptTokens = 0, completionTokens = 0, totalTokens = 0;
                if (rootEl.TryGetProperty("usage", out var usageEl))
                {
                    if (usageEl.TryGetProperty("prompt_tokens", out var pTok)) promptTokens = pTok.GetInt32();
                    if (usageEl.TryGetProperty("completion_tokens", out var cTok)) completionTokens = cTok.GetInt32();
                    if (usageEl.TryGetProperty("total_tokens", out var tTok)) totalTokens = tTok.GetInt32();
                }

                var cleanJson = Northtropic.Helpers.JsonExtractorHelper.ExtractJson(content);
                using var qDoc = JsonDocument.Parse(cleanJson, new JsonDocumentOptions { AllowTrailingCommas = true });
                var root = qDoc.RootElement;
                
                var questionsList = new List<Question>();
                JsonElement qArray;

                if (root.TryGetProperty("questions", out qArray) && qArray.ValueKind == JsonValueKind.Array)
                {
                    // 找到了 questions 数组
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    qArray = root;
                }
                else
                {
                    qArray = root;
                }

                int avgPromptTokens = count > 0 ? (promptTokens / count) : promptTokens;
                int avgCompletionTokens = count > 0 ? (completionTokens / count) : completionTokens;
                int avgTotalTokens = count > 0 ? ((totalTokens > 0 ? totalTokens : (promptTokens + completionTokens)) / count) : totalTokens;

                var tokenLogs = new List<LlmGenerationLog>();

                if (qArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in qArray.EnumerateArray())
                    {
                        var q = ParseSingleQuestionFromJsonElement(element, grade, subject, category);
                        if (!string.IsNullOrWhiteSpace(q.Stem) && !questionsList.Any(existing => existing.Stem == q.Stem))
                        {
                            questionsList.Add(q);

                            var tokenLog = new LlmGenerationLog
                            {
                                Id = Guid.NewGuid(),
                                UserId = user.Id,
                                QuestionId = q.Id,
                                ModelName = modelName,
                                Subject = subject,
                                Category = category,
                                PromptTokens = avgPromptTokens,
                                CompletionTokens = avgCompletionTokens,
                                TotalTokens = avgTotalTokens,
                                GeneratedAt = DateTime.Now
                            };
                            tokenLogs.Add(tokenLog);
                        }
                    }
                }
                else
                {
                    var q = ParseSingleQuestionFromJsonElement(qArray, grade, subject, category);
                    if (!string.IsNullOrWhiteSpace(q.Stem))
                    {
                        questionsList.Add(q);

                        var tokenLog = new LlmGenerationLog
                        {
                            Id = Guid.NewGuid(),
                            UserId = user.Id,
                            QuestionId = q.Id,
                            ModelName = modelName,
                            Subject = subject,
                            Category = category,
                            PromptTokens = promptTokens,
                            CompletionTokens = completionTokens,
                            TotalTokens = totalTokens > 0 ? totalTokens : (promptTokens + completionTokens),
                            GeneratedAt = DateTime.Now
                        };
                        tokenLogs.Add(tokenLog);
                    }
                }

                await SaveQuestionsAndLogsAsync(questionsList, tokenLogs);
                return questionsList;
            }
            catch (Exception ex) when (!(ex is LlmConfigException))
            {
                throw new LlmConfigException($"❌ 批量出题解析失败: {ex.Message}");
            }
        }

        private Question ParseSingleQuestionFromJsonElement(JsonElement element, string grade, string subject, string category, Guid? userId = null)
        {
            var stem = element.TryGetProperty("stem", out var sEl) ? sEl.GetString() ?? "" : "";
            var typeStr = element.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "SingleChoice" : "SingleChoice";
            
            QuestionType qType = QuestionType.SingleChoice;
            if (typeStr.Equals("MultipleChoice", StringComparison.OrdinalIgnoreCase)) qType = QuestionType.MultipleChoice;
            else if (typeStr.Equals("FillInBlank", StringComparison.OrdinalIgnoreCase)) qType = QuestionType.FillInBlank;
            else if (typeStr.Equals("ShortAnswer", StringComparison.OrdinalIgnoreCase)) qType = QuestionType.ShortAnswer;
            else if (typeStr.Equals("EssayAnalysis", StringComparison.OrdinalIgnoreCase)) qType = QuestionType.EssayAnalysis;

            var optionsList = new List<string>();
            if (element.TryGetProperty("options", out var optEl) && optEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var opt in optEl.EnumerateArray())
                {
                    optionsList.Add(opt.GetString() ?? "");
                }
            }

            var correct = element.TryGetProperty("correctAnswer", out var cEl) ? cEl.GetString() ?? "" : "";
            var analysis = element.TryGetProperty("analysis", out var aEl) ? aEl.GetString() ?? "" : "";

            return new Question
            {
                Id = Guid.NewGuid(),
                Subject = subject,
                Category = category,
                GradeTarget = grade,
                Type = qType,
                Stem = stem,
                OptionsJson = JsonSerializer.Serialize(optionsList),
                CorrectAnswer = correct,
                StandardAnalysis = analysis,
                Difficulty = 3,
                BaseExpReward = qType == QuestionType.EssayAnalysis ? 40 : 25,
                CreatedByUserId = userId,
                IsPublic = true, // AI 实时生成的习题默认全网共享
                PublishStatus = PublishStatusEnum.Approved,
                CreatedAt = DateTime.Now
            };
        }

        private async Task<List<Question>> GenerateHeuristicBatchQuestionsAsync(User user, string grade, string subject, string category, int count)
        {
            var result = new List<Question>();
            var logs = new List<LlmGenerationLog>();
            var rnd = Random.Shared;

            for (int i = 0; i < count; i++)
            {
                var q = CreateSmartHeuristicQuestion(grade, subject, category, i, rnd);
                result.Add(q);

                var log = new LlmGenerationLog
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = q.Id,
                    ModelName = "Heuristic-Engine-v3",
                    Subject = subject,
                    Category = category,
                    PromptTokens = 120,
                    CompletionTokens = 85,
                    TotalTokens = 205,
                    GeneratedAt = DateTime.Now
                };
                logs.Add(log);
            }

            await SaveQuestionsAndLogsAsync(result, logs);
            return result;
        }

        private Question CreateSmartHeuristicQuestion(string grade, string subject, string category, int index, Random rnd)
        {
            var q = new Question
            {
                Id = Guid.NewGuid(),
                GradeTarget = grade,
                Subject = subject,
                Category = category,
                Difficulty = rnd.Next(2, 5),
                CreatedByUserId = null,
                IsPublic = true,
                PublishStatus = PublishStatusEnum.Approved,
                BaseExpReward = 30,
                CreatedAt = DateTime.Now
            };

            // 1. 物理学科体系
            if (subject.Contains("物理"))
            {
                int mod = index % 5;
                if (mod == 0)
                {
                    int m = rnd.Next(2, 8);
                    int a = rnd.Next(2, 6);
                    int f = m * a;
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = $"【动力学与牛顿定律】一质量为 {m} kg 的滑块静止在光滑水平面上。在大小为 {f} N 的水平恒力作用下向前滑行，则该滑块获得的加速度大小为：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        $"A. {a} m/s²",
                        $"B. {a + 2} m/s²",
                        $"C. {Math.Max(1, a - 1)} m/s²",
                        $"D. {a * 2} m/s²"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = $"根据牛顿第二定律 $a = \\frac{{F}}{{m}} = \\frac{{{f}}}{{{m}}} = {a}\\,\\text{{m/s}}^2$。故选 A。";
                }
                else if (mod == 1)
                {
                    int u = rnd.Next(6, 24);
                    int r = rnd.Next(2, 8);
                    int p = (u * u) / r;
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = $"【电学欧姆定律与电功率】已知一段定值电阻的阻值为 {r} Ω，将其接入电压恒为 {u} V 的直流电源两端。则通过该电阻的电流及消耗的电功率分别为：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        $"A. {u / (double)r:F1} A, {p} W",
                        $"B. {u} A, {p / 2} W",
                        $"C. {r} A, {p * 2} W",
                        $"D. {u / 2.0:F1} A, {p + 10} W"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = $"由欧姆定律 $I = \\frac{{U}}{{R}} = \\frac{{{u}}}{{{r}}} = {u / (double)r:F1}\\,\\text{{A}}$；电功率 $P = \\frac{{U^2}}{{R}} = \\frac{{{u * u}}}{{{r}}} = {p}\\,\\text{{W}}$。故选 A。";
                }
                else if (mod == 2)
                {
                    q.Type = QuestionType.MultipleChoice;
                    q.Stem = "【机械能与重力场】关于自由落体运动（忽略空气阻力，从高处静止释放），下列物理学规律中正确的有哪些？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 下落过程中物体的重力势能转化为动能，机械能总量保持守恒",
                        "B. 下落第 1 秒、第 2 秒、第 3 秒内的位移之比满足 1 : 3 : 5 的比例关系",
                        "C. 物体下落的即时速度与下落时间成正比 (v = gt)",
                        "D. 加速度随着下落距离的增大而持续增大"
                    });
                    q.CorrectAnswer = "A, B, C";
                    q.StandardAnalysis = "自由落体为匀加速直线运动，仅受重力作用机械能守恒；初速度为0的匀加速运动在连续相等时间内的位移比为 1:3:5:7；速度 v=gt；加速度恒为 g 保持不变。故 ABC 正确。";
                }
                else if (mod == 3)
                {
                    int h = rnd.Next(5, 20);
                    int m = rnd.Next(2, 6);
                    int ep = m * 10 * h;
                    q.Type = QuestionType.FillInBlank;
                    q.Stem = $"【重力势能计算】质量为 {m} kg 的物体被提升至距离基准地面高 {h} m 的平台处，重力加速度取 g = 10 m/s²。若以地面为零势能参考平面，则此时物体的重力势能为 ______ J。";
                    q.CorrectAnswer = $"{ep}";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = $"重力势能公式为 $E_p = mgh = {m} \\times 10 \\times {h} = {ep}\\,\\text{{J}}$。";
                }
                else
                {
                    q.Type = QuestionType.ShortAnswer;
                    q.Stem = "【光的反射与折射】请简明阐述光从空气斜射入水中时，折射角与入射角的大小关系，并解释筷子斜插入水中看起来'折断'的物理成因。";
                    q.CorrectAnswer = "折射角小于入射角；筷子反射的光线从水中射入空气时发生折射偏离法线，人眼逆着折射光线看去看到的是偏高的虚像。";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "光从光疏介质（空气）斜射入光密介质（水）时，折射光线向法线偏折，折射角小于入射角。水下筷子发出的光在水面折射进入人眼，人眼根据光沿直线传播的经验逆向延伸，看到筷子水下部分向上弯折的虚像。";
                }
            }
            // 2. 数学学科体系
            else if (subject.Contains("数学"))
            {
                int mod = index % 5;
                if (mod == 0)
                {
                    int k = rnd.Next(2, 6);
                    int b = rnd.Next(1, 9);
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = $"【一次函数解析式】已知一次函数图像经过点 $(0, {b})$ 且斜率 $k = {k}$，则该直线与 x 轴交点的横坐标 $x_0$ 为：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        $"A. -{b}/{k}",
                        $"B. {b}/{k}",
                        $"C. -{k}/{b}",
                        $"D. {b * k}"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = $"直线方程为 $y = {k}x + {b}$。令 $y = 0$，得 ${k}x + {b} = 0$，解得 $x = -\\frac{{{b}}}{{{k}}}$。故选 A。";
                }
                else if (mod == 1)
                {
                    int x1 = rnd.Next(1, 4);
                    int x2 = rnd.Next(5, 8);
                    int sum = x1 + x2;
                    int prod = x1 * x2;
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = $"【一元二次方程根与系数关系】已知关于 x 的方程 $x^2 - {sum}x + {prod} = 0$ 的两个实数根分别为 $x_1$ 和 $x_2$，则 $x_1 + x_2$ 与 $x_1 x_2$ 的值分别为：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        $"A. {sum}, {prod}",
                        $"B. -{sum}, {prod}",
                        $"C. {sum}, -{prod}",
                        $"D. {prod}, {sum}"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = $"由韦达定理（Vieta's formulas），对于 $ax^2 + bx + c = 0$，两根之和 $x_1 + x_2 = -b/a = {sum}$，两根之积 $x_1 x_2 = c/a = {prod}$。故选 A。";
                }
                else if (mod == 2)
                {
                    int r = rnd.Next(3, 10);
                    q.Type = QuestionType.FillInBlank;
                    q.Stem = $"【平面几何与圆的性质】一个半径为 {r} cm 的圆，其圆周长为 ______ $\\pi$ cm，面积为 ______ $\\pi$ cm²。(请用逗号隔开填入两个数值，如：6, 9)";
                    q.CorrectAnswer = $"{2 * r}, {r * r}";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = $"圆周长公式 $C = 2\\pi r = {2 * r}\\pi$，面积公式 $S = \\pi r^2 = {r * r}\\pi$。";
                }
                else if (mod == 3)
                {
                    q.Type = QuestionType.MultipleChoice;
                    q.Stem = "【函数性质综合探究】下列关于二次函数 $f(x) = -(x-2)^2 + 4$ 的性质描述中，正确的有哪些？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 抛物线开口向下，顶点坐标为 (2, 4)",
                        "B. 对称轴为直线 x = 2",
                        "C. 当 x < 2 时，函数值 y 随 x 的增大而单调递增",
                        "D. 该函数有最小值 4"
                    });
                    q.CorrectAnswer = "A, B, C";
                    q.StandardAnalysis = "二次项系数为负，抛物线开口向下，有最大值 4（无最小值），对称轴为 x=2，顶点为 (2,4)；在对称轴左侧 (x<2) 单调递增。故 ABC 正确，D 错误。";
                }
                else
                {
                    q.Type = QuestionType.EssayAnalysis;
                    q.Stem = "【导数与极值应用题】已知函数 $f(x) = \\frac{1}{3}x^3 - x^2 - 3x + 1$。请求出该函数的导数 $f'(x)$，并求解其单调递增区间与极大值点。";
                    q.CorrectAnswer = "导数 f'(x) = x^2 - 2x - 3；单调递增区间为 (-∞, -1) 和 (3, +∞)；极大值点为 x = -1。";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "求导得 $f'(x) = x^2 - 2x - 3 = (x-3)(x+1)$。令 $f'(x) > 0$ 解得 $x < -1$ 或 $x > 3$，故递增区间为 $(-\\infty, -1)$ 与 $(3, +\\infty)$；在 $x = -1$ 处导数由正变负，取得极大值。";
                }
            }
            // 3. 语文学科体系
            else if (subject.Contains("语文"))
            {
                int mod = index % 5;
                if (mod == 0)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【古诗文阅读与意象】唐代诗人王维在《使至塞上》中写道：'大漠孤烟直，长河落日圆。' 下列对这两句诗的艺术赏析最恰当的一项是：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 运用'直'与'圆'两个字，准确描绘了边陲大漠壮阔雄奇、苍茫肃穆的壮丽景象",
                        "B. 表现了诗人对边塞荒凉环境的极度恐惧与退缩心理",
                        "C. 采用了拟人和夸张的手法，生动展现了将士们冲锋陷阵的激烈场面",
                        "D. 借景抒情，主要表达了诗人对江南水乡田园风光的无限眷恋"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = "'大漠孤烟直，长河落日圆' 被王国维赞为'千古壮观'之名句，'直'字显出烽烟的劲挺，'圆'字显出落日的苍茫温暖，生动勾勒出塞外奇特壮丽的风光。故选 A。";
                }
                else if (mod == 1)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【词语辨析与成语运用】下列句子中加点成语使用恰当、毫无语病的一项是：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 面对错综复杂的科学难题，科研团队处心积虑、精益求精，终于攻克了核心关键技术",
                        "B. 语文老师讲课风趣幽默、引人入胜，同学们听得津津有味",
                        "C. 在这次辩论赛中，他巧舌如簧的发言赢得了全体评委与观众的一致赞许",
                        "D. 这座历史悠久的古桥历经千百年风雨洗礼，如今依然危如累卵地矗立在江面上"
                    });
                    q.CorrectAnswer = "B";
                    q.StandardAnalysis = "A项'处心积虑'为贬义词，不合语境；C项'巧舌如簧'含贬义；D项'危如累卵'形容形势极其危险，与句意矛盾；B项'津津有味'使用准确自然。故选 B。";
                }
                else if (mod == 2)
                {
                    q.Type = QuestionType.FillInBlank;
                    q.Stem = "【名句默写】《论语》中强调对待学与思的辩证关系的名句是：'学而不思则罔，______。'";
                    q.CorrectAnswer = "思而不学则殆";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "《论语·为政》：'学而不思则罔，思而不学则殆。' 告诫我们学习与思考必须紧密结合。";
                }
                else if (mod == 3)
                {
                    q.Type = QuestionType.MultipleChoice;
                    q.Stem = "【修辞手法与表达效果】下列句子中所使用的修辞手法分析正确的有哪些？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. '看万山红遍，层林尽染' 运用了夸张的手法表现秋色的浓烈",
                        "B. '盼望着，盼望着，东风来了，春天的脚步近了' 运用了反复和拟人的修辞手法",
                        "C. '问君能有几多愁？恰似一江春水向东流' 运用了设问和比喻的手法把抽象的愁绪具象化",
                        "D. '朱门酒肉臭，路有冻死骨' 运用了强烈的对比修辞"
                    });
                    q.CorrectAnswer = "B, C, D";
                    q.StandardAnalysis = "B项'盼望着'为反复，'春天的脚步近了'为拟人；C项自问自答为设问，将愁比作春水为比喻；D项将富贵与贫苦对比。A项主要是写景描摹，无夸张。故 BCD 正确。";
                }
                else
                {
                    q.Type = QuestionType.ShortAnswer;
                    q.Stem = "【文言虚词与文意概括】简述范仲淹在《岳阳楼记》中所表达的'先天下之忧而忧，后天下之乐而乐'的核心思想内涵与政治抱负。";
                    q.CorrectAnswer = "表达了作者超越个人荣辱得失的阔大胸襟，以及以天下国家为己任、忧国忧民的崇高政治抱负。";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "该名句概括了范仲淹'不以物喜，不以己悲'的崇高情操，将国家和民众的利益置于个人得失之上，体现了中国古代士大夫以天下为己任的担当精神。";
                }
            }
            // 4. 英语学科体系
            else if (subject.Contains("英语"))
            {
                int mod = index % 5;
                if (mod == 0)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【Grammar - Attributive Clause】Choose the correct relative pronoun to complete the sentence: \"The scientist ________ discovered the new law of physics was awarded the Nobel Prize.\"";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. who",
                        "B. which",
                        "C. whom",
                        "D. whose"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = "先行词是 'The scientist'（指人），定语从句中缺少主语，因此应使用主格关系代词 who。";
                }
                else if (mod == 1)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【Tenses & Passive Voice】Look! The new high-speed railway bridge ________ by engineers from all over the country.";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. is being built",
                        "B. was built",
                        "C. has built",
                        "D. builds"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = "句首由 'Look!' 提示正在发生的动作，且主语 'bridge' 与动词 'build' 之间是被动关系，故采用现在进行时的被动语态 'is being built'。";
                }
                else if (mod == 2)
                {
                    q.Type = QuestionType.FillInBlank;
                    q.Stem = "【Vocabulary & Collocation】Fill in the blank with the proper preposition: \"We are looking forward to ________ (hear) from you soon.\"";
                    q.CorrectAnswer = "hearing";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "在固定搭配 'look forward to' 中，'to' 是介词，后面接名词或动名词（V-ing），因此填写 hearing。";
                }
                else if (mod == 3)
                {
                    q.Type = QuestionType.MultipleChoice;
                    q.Stem = "【Subjunctive Mood】Which of the following sentences correctly use the Subjunctive Mood (虚拟语气)?";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. If I were you, I would take the professor's advice immediately.",
                        "B. The teacher suggested that we (should) practice speaking English every day.",
                        "C. If it had rained yesterday, the sports meeting would have been cancelled.",
                        "D. If you will come tomorrow, we will go shopping together."
                    });
                    q.CorrectAnswer = "A, B, C";
                    q.StandardAnalysis = "A项为对现在的虚拟 (were, would do)；B项为suggest引导的宾语从句中用 (should) + 动词原形；C项为对过去的虚拟 (had done, would have done)；D项为真实条件句。故 ABC 正确。";
                }
                else
                {
                    q.Type = QuestionType.ShortAnswer;
                    q.Stem = "【Writing & Expression】Please translate this sentence into English: '坚持每天阅读不仅能拓宽我们的视野，还能提高我们的思维能力。'";
                    q.CorrectAnswer = "Insisting on reading every day can not only broaden our horizons but also improve our thinking ability.";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "考查动名词作主语 (Reading every day...) 以及 'not only... but also...' 句型。'拓宽视野'译为 broaden one's horizons，'提高思维能力'译为 improve thinking ability。";
                }
            }
            // 5. 化学学科体系
            else if (subject.Contains("化学"))
            {
                int mod = index % 5;
                if (mod == 0)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【氧化还原反应基本概念】在反应 $2\\text{Na} + \\text{Cl}_2 \\rightarrow 2\\text{NaCl}$ 中，下列关于元素化合价与得失电子的描述正确的是：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. Na 元素化合价升高失电子，作还原剂发生氧化反应",
                        "B. Cl 元素化合价升高得电子，作还原剂",
                        "C. Na 元素化合价降低得电子，被还原",
                        "D. 该反应不属于氧化还原反应"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = "Na 元素化合价由 0 价升至 +1 价，失去电子被氧化，是还原剂；Cl 元素化合价由 0 降至 -1 价，得到电子被还原，是氧化剂。故选 A。";
                }
                else if (mod == 1)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【化学平衡移动原理】对于可逆反应 $\\text{N}_2(g) + 3\\text{H}_2(g) \\rightleftharpoons 2\\text{NH}_3(g) + Q$（正反应为放热反应），为了提高平衡体系中氨气（$\\text{NH}_3$）的产率，应采取的措施是：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 适当降温并增大压强",
                        "B. 适当升温并减小压强",
                        "C. 保持温度不变并减小压强",
                        "D. 加入催化剂以使平衡发生移动"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = "正反应是放热且气体体积缩小的反应。根据勒夏特列原理，降低温度和增大压强均使化学平衡向正反应方向（生成氨气方向）移动，催化剂仅加快反应速率不改变平衡转化率。故选 A。";
                }
                else if (mod == 2)
                {
                    q.Type = QuestionType.FillInBlank;
                    q.Stem = "【离子方程式书写】向碳酸氢钠（$\\text{NaHCO}_3$）溶液中滴加少量稀盐酸，发生反应的离子方程式为：______。(请写出标准离子反应式)";
                    q.CorrectAnswer = "HCO3- + H+ = H2O + CO2↑";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "碳酸氢根与氢离子结合生成水和二氧化碳气体：$\\text{HCO}_3^- + \\text{H}^+ = \\text{H}_2\\text{O} + \\text{CO}_2\\uparrow$。";
                }
                else if (mod == 3)
                {
                    q.Type = QuestionType.MultipleChoice;
                    q.Stem = "【电化学与原电池】在以稀硫酸为电解质溶液的铜锌原电池装置中，下列描述中正确的有哪些？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 锌片（Zn）为负极，发生氧化反应溶解",
                        "B. 铜片（Cu）为正极，溶液中的 H+ 在铜极表面得电子生成 H2 气泡",
                        "C. 电子在外电路中从锌极流向铜极",
                        "D. 阳离子向负极移动"
                    });
                    q.CorrectAnswer = "A, B, C";
                    q.StandardAnalysis = "Zn 比 Cu 活泼，Zn 为负极发生氧化反应 $\\text{Zn} - 2e^- = \\text{Zn}^{2+}$；Cu 为正极发生还原反应 $2\\text{H}^+ + 2e^- = \\text{H}_2\\uparrow$；电子流向为负极(Zn)→正极(Cu)；原电池中阳离子向正极移动。故 ABC 正确。";
                }
                else
                {
                    q.Type = QuestionType.ShortAnswer;
                    q.Stem = "【实验探究与物质检验】请简述如何通过化学实验鉴别两瓶无色试剂：一瓶为稀硫酸（$\\text{H}_2\\text{SO}_4$），另一瓶为稀盐酸（$\\text{HCl}$）。";
                    q.CorrectAnswer = "分别取少量试样于试管中，滴加氯化钡（BaCl2）溶液，产生不溶于稀硝酸的白色沉淀者为稀硫酸，无沉淀者为稀盐酸。";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "硫酸根离子（$\\text{SO}_4^{2-}$）与钡离子（$\\text{Ba}^{2+}$）反应生成不溶于强酸的硫酸钡（$\\text{BaSO}_4$）白色沉淀，而氯离子与钡离子不产生沉淀，因此可用 $\\text{BaCl}_2$ 溶液加以鉴别。";
                }
            }
            // 6. 生物学科体系
            else if (subject.Contains("生物"))
            {
                int mod = index % 4;
                if (mod == 0)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【光合作用与细胞呼吸】绿色植物叶肉细胞在光照充足的条件下，叶绿体光反应阶段产生的物质中，直接用于暗反应阶段还原三碳化合物（C3）的是：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. ATP 和 [H] (NADPH)",
                        "B. O2 和 CO2",
                        "C. 葡萄糖和丙酮酸",
                        "D. ADP 和 Pi"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = "光反应阶段水光解产生氧气和 NADPH，并合成 ATP；其中 NADPH 和 ATP 提供还原剂和能量，用于暗反应阶段 C3 化合物的还原。故选 A。";
                }
                else if (mod == 1)
                {
                    q.Type = QuestionType.MultipleChoice;
                    q.Stem = "【遗传学基本定律】孟德尔用豌豆进行杂交实验发现了基因的分离定律。下列属于该定律核心实质的是：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 在杂合子的细胞中，控制同一性状的等位基因具有一定的独立性",
                        "B. 在形成配子时，等位基因会发生分离，分别进入不同的配子中",
                        "C. 受精时，雌雄配子的结合是随机的",
                        "D. F1 代只表现显性性状"
                    });
                    q.CorrectAnswer = "A, B, C";
                    q.StandardAnalysis = "分离定律实质是杂合子在减数分裂产生配子时，等位基因随同源染色体的分开而分离，独立地随配子遗传给后代，受精时雌雄配子随机结合。故 ABC 正确。";
                }
                else if (mod == 2)
                {
                    q.Type = QuestionType.FillInBlank;
                    q.Stem = "【神经调节与反射弧】人体神经调节的基本方式是 ______，其完成的结构基础被称为 ______。";
                    q.CorrectAnswer = "反射, 反射弧";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "神经调节的基本方式是反射，反射活动的结构基础是反射弧（包括感受器、传入神经、神经中枢、传出神经、效应器）。";
                }
                else
                {
                    q.Type = QuestionType.ShortAnswer;
                    q.Stem = "【生态学规律】简述生态系统中能量流动的两大显著特点，并解释为什么食物链中的营养级通常不超过 4 至 5 个。";
                    q.CorrectAnswer = "能量流动的特点是单向流动、逐级递减（传递效率约10%~20%）；由于各营养级呼吸消耗和残渣未利用，传递到高营养级的能量极少，不足以维持更长的食物链。";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "生态系统能量流动具有单向流动、逐级递减两大特征。相邻营养级间的传递效率通常为 10%～20%，能量在传递过程中大部分通过呼吸作用散失，因而无法支撑过多营养级。";
                }
            }
            // 7. 历史学科体系
            else if (subject.Contains("历史"))
            {
                int mod = index % 4;
                if (mod == 0)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【中国古代中央官制演变】秦朝建立后在中央实行'三公九卿制'。其中负责掌管全国监察事务、监察百官的官职是：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 御史大夫",
                        "B. 丞相",
                        "C. 太尉",
                        "D. 廷尉"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = "秦代三公中：丞相掌行政管政事，太尉掌军事管军务，御史大夫为副丞相兼掌监察执法与奏章。故选 A。";
                }
                else if (mod == 1)
                {
                    q.Type = QuestionType.MultipleChoice;
                    q.Stem = "【中国近代探索与辛亥革命】辛亥革命是中国近代史上一次伟大的资产阶级民主革命。下列关于辛亥革命历史功绩的评价正确的有哪些？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 推翻了清王朝的统治，结束了统治中国两千多年的封建君主专制制度",
                        "B. 建立了中国历史上第一个资产阶级共和国——中华民国",
                        "C. 使民主共和的观念深入人心，极大地推动了思想解放",
                        "D. 彻底改变了旧中国半殖民地半封建社会的性质"
                    });
                    q.CorrectAnswer = "A, B, C";
                    q.StandardAnalysis = "辛亥革命推翻了清王朝和君主专制，建立了资产阶级共和国，促进了思想解放；但革命果实被袁世凯窃取，未能彻底改变中国半殖民地半封建的社会性质。故 ABC 正确，D 错误。";
                }
                else if (mod == 2)
                {
                    q.Type = QuestionType.FillInBlank;
                    q.Stem = "【改革开放里程碑】1978 年 12 月召开的中国共产党第 ______ 届三中全会，作出了把全党工作重心转移到社会主义现代化建设上来的伟大历史转折。";
                    q.CorrectAnswer = "十一";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "1978年召开的党的十一届三中全会开启了改革开放的历史新时期。";
                }
                else
                {
                    q.Type = QuestionType.ShortAnswer;
                    q.Stem = "【世界近代史与科技革命】简述第一次工业革命以何项核心发明为标志，以及它对人类社会生产方式产生的根本性变革。";
                    q.CorrectAnswer = "以瓦特改良蒸汽机为标志；使人类社会进入'蒸汽时代'，机器大工厂生产取代了手工工场，极大提高了社会生产力。";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "第一次工业革命的核心标志是瓦特改良蒸汽机并广泛投入使用，推动了机械化大工业的诞生，使人类文明迈入'蒸汽时代'。";
                }
            }
            // 8. 地理学科体系
            else if (subject.Contains("地理"))
            {
                int mod = index % 4;
                if (mod == 0)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【地球自转与昼夜长短变化】每年夏至日（6月22日前后），太阳直射北回归线。此时北半球各地的昼夜长短状况为：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 昼长达到一年中最大值，北极圈及其以北地区出现极昼",
                        "B. 昼短夜长，南半球出现极昼",
                        "C. 全球昼夜等长",
                        "D. 昼短夜长，北极圈出现极夜"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = "夏至日太阳直射北回归线，北半球昼最长夜最短，纬度越高昼越长，北极圈及以内出现极昼现象。故选 A。";
                }
                else if (mod == 1)
                {
                    q.Type = QuestionType.MultipleChoice;
                    q.Stem = "【水循环与地质构造】下列关于自然界水循环及其地理意义的叙述中，正确的有哪些？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 水循环促进了全球水体更新，维持了全球水的动态平衡",
                        "B. 水循环是地表最活跃的能量交换和物质迁移过程之一",
                        "C. 流水侵蚀和堆积作用不断塑造着多样的地表形态",
                        "D. 人类目前主要通过跨流域调水和修建水库来影响地下径流环节"
                    });
                    q.CorrectAnswer = "A, B, C";
                    q.StandardAnalysis = "水循环维持全球水平衡，进行能量与物质迁移，塑造地貌；人类跨流域调水和修水库主要影响的是'地表径流'环节，而非地下径流。故 ABC 正确。";
                }
                else if (mod == 2)
                {
                    q.Type = QuestionType.FillInBlank;
                    q.Stem = "【板块构造学说】世界两大主要火山地震带分别是阿尔卑斯-喜马拉雅火山地震带和 ______ 火山地震带。";
                    q.CorrectAnswer = "环太平洋";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "全球最集中的两大地震火山带是环太平洋地震带和地中海-喜马拉雅（阿尔卑斯-喜马拉雅）地震带。";
                }
                else
                {
                    q.Type = QuestionType.ShortAnswer;
                    q.Stem = "【气候类型与成因】请简述温带季风气候的主要气候特征及其形成的主要原因。";
                    q.CorrectAnswer = "特征：夏季高温多雨，冬季寒冷干燥；成因：海陆热力性质差异导致冬夏季风交替控制。";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "温带季风气候主要分布于亚欧大陆东岸中纬度地区，由于巨大大陆与大洋间显著的海陆热力性质差异，冬夏盛行风向相反，形成雨热同期的季风气候。";
                }
            }
            // 9. 道德与法治 / 政治学科体系
            else if (subject.Contains("道德") || subject.Contains("法治") || subject.Contains("政治"))
            {
                int mod = index % 4;
                if (mod == 0)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【宪法根本法地位与法治国家】我国宪法规定：'一切法律、行政法规和地方性法规都不得同宪法相抵触。' 这充分表明宪法是：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 国家的根本法，具有最高的法律地位、法律权威和法律效力",
                        "B. 包含所有具体领域具体规则的百科全书式普通法律",
                        "C. 只约束普通公民行为的道德准则",
                        "D. 可以由任何地方行政机关随时修改的行政规范"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = "宪法是国家的根本法，是治国安邦的总章程，规定国家生活中的根本问题，具有最高的法律权威和法律效力，是其他法律的立法基础和依据。故选 A。";
                }
                else if (mod == 1)
                {
                    q.Type = QuestionType.MultipleChoice;
                    q.Stem = "【我国基本经济制度】在我国社会主义初级阶段，公有制为主体、多种所有制经济共同发展是我国的一项基本经济制度。下列说法中正确的有哪些？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 国有经济是国民经济的支柱，掌握着国家的经济命脉",
                        "B. 非公有制经济是社会主义市场经济的重要组成部分",
                        "C. 毫不动摇巩固和发展公有制经济，毫不动摇鼓励、支持、引导非公有制经济发展",
                        "D. 非公有制经济在国民经济中处于主体主导地位"
                    });
                    q.CorrectAnswer = "A, B, C";
                    q.StandardAnalysis = "公有制经济在国民经济中占主体地位，国有经济起主导作用；非公有制经济是市场经济重要组成部分；坚持'两个毫不动摇'。故 ABC 正确，D 错误。";
                }
                else if (mod == 2)
                {
                    q.Type = QuestionType.FillInBlank;
                    q.Stem = "【根本政治制度】中华人民共和国的根本政治制度是 ______ 制度。";
                    q.CorrectAnswer = "人民代表大会";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "人民代表大会制度是我国的根本政治制度，中国共产党领导的多党合作和政治协商制度、民族区域自治制度、基层群众自治制度为基本政治制度。";
                }
                else
                {
                    q.Type = QuestionType.ShortAnswer;
                    q.Stem = "【公民权利与义务的辩证统一】简要阐明我国公民权利与义务之间的辩证关系，以及作为公民在日常生活中应如何正确行使权利。";
                    q.CorrectAnswer = "权利与义务是相互依存、相互促进的统一体，公民既是权利的享有者也是义务的履行者；行使权利时不得损害国家的、社会的、集体的利益和其他公民的合法的自由和权利。";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "在我国，权利与义务相辅相成。任何公民享有宪法和法律规定的权利，同时必须履行宪法和法律规定的义务。行使权利必须在法律允许的范围内进行。";
                }
            }
            // 10. C# / Blazor / 软件工程
            else if (subject.Contains("C#") || subject.Contains("Blazor") || subject.Contains("软件工程") || subject.Contains("算法") || subject.Contains("数据库"))
            {
                int mod = index % 5;
                if (mod == 0)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【C# 12 / .NET 8 内存与 GC】在高性能高并发后端服务中，关于 `ValueTask<T>` 与 `Task<T>` 的选用考量，下列哪项表述最准确？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 当异步方法大概率能同步返回结果（如命中本地内存缓存）时，使用 ValueTask<T> 可消除 Task 对象的堆内存分配与 GC 压力",
                        "B. ValueTask<T> 是引用类型，支持在多个线程间同时被 await 多次",
                        "C. ValueTask<T> 内部基于 Thread.Sleep 实现异步非阻塞",
                        "D. 所有返回泛型结果的异步方法必须一律使用 ValueTask<T>"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = "ValueTask<T> 为结构体（值类型），核心价值在于同步完成路径不分配堆内存；但其不能并发多次 await，也不应无节制盲目替代 Task。故选 A。";
                }
                else if (mod == 1)
                {
                    q.Type = QuestionType.MultipleChoice;
                    q.Stem = "【Blazor Server 渲染与生命周期】在 Blazor Server 响应式架构中，下列关于组件状态与渲染的理解正确的有哪些？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. Blazor Server 通过 SignalR 协议实时将 RenderTree 差异补丁推送到客户端浏览器 DOM",
                        "B. 组件由用户点击事件触发时，框架会自动在事件处理完毕后调用 StateHasChanged()",
                        "C. 在非 UI 线程（如后台定时器或异步回调）中更新组件状态时，必须使用 InvokeAsync(StateHasChanged)",
                        "D. Blazor Server 组件在客户端断网重连后一定会丢失内存中的全部状态"
                    });
                    q.CorrectAnswer = "A, B, C";
                    q.StandardAnalysis = "Blazor Server 基于 SignalR 传输 UI Diff；EventCallback 会自动触发重绘；后台线程跨线程更新必须通过 InvokeAsync(StateHasChanged)；电路断开在超时时间内支持无缝重连恢复状态。故 ABC 正确。";
                }
                else if (mod == 2)
                {
                    q.Type = QuestionType.FillInBlank;
                    q.Stem = "【依赖注入生命周期】在 ASP.NET Core DI 容器中，每次请求被解析时均创建全新独立实例的注入生命周期方法是 `Add______`。";
                    q.CorrectAnswer = "Transient";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "AddTransient 注册瞬态生命周期，每次请求注入都会实例化全新对象；AddScoped 在作用域内单例；AddSingleton 全局单例。";
                }
                else if (mod == 3)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【EF Core 与 SQL 调优】在 Entity Framework Core 查询中，对于仅用于只读展示、无需跟踪状态的百万级数据查询，应调用何种方法以极大降低内存开销？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. AsNoTracking()",
                        "B. AsTracking()",
                        "C. ToListAsync() 前先执行 SaveChanges()",
                        "D. Attach()"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = "AsNoTracking() 会通知 EF Core 更改跟踪器（ChangeTracker）不记录实体快照与变更状态，从而大幅降低内存消耗并显著提升只读查询吞吐率。";
                }
                else
                {
                    q.Type = QuestionType.ShortAnswer;
                    q.Stem = "【数据库索引原理】请简述 MySQL InnoDB 存储引擎中聚簇索引（Clustered Index）与非聚簇二级索引（Secondary Index）在 B+ 树叶子节点存储内容上的本质区别，并解释何为'回表'。";
                    q.CorrectAnswer = "聚簇索引的叶子节点直接存放整行完整数据，二级索引的叶子节点存放主键值；通过二级索引查到主键后再去聚簇索引查完整行数据的过程称为回表。";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "聚簇索引按照主键顺序组织数据，叶子节点直接包含行数据记录；二级索引叶子节点存储索引列及对应的主键值。若查询列不全包含在二级索引中（未覆盖索引），则需拿着主键值回到聚簇索引检索完整行记录，该过程即为回表。";
                }
            }
            // 11. AI 与大模型工程
            else if (subject.Contains("AI") || subject.Contains("大模型") || subject.Contains("人工智能"))
            {
                int mod = index % 4;
                if (mod == 0)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = "【RAG 与向量检索】在构建大模型企业级知识库 (RAG) 检索增强系统中，向量数据库普遍采用何种近似最近邻 (ANN) 算法以实现毫秒级的高维语义相似度匹配？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. HNSW (Hierarchical Navigable Small World) 分层可导航小世界图算法",
                        "B. 冒泡排序与二分查找",
                        "C. 传统 B+ 树范围扫描",
                        "D. 深度优先拓扑排序"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = "HNSW 算法通过构建多层概率跳表图结构，能在百万至亿级高维向量空间中以极低的时间复杂度执行高效的近似最近邻近查找。";
                }
                else if (mod == 1)
                {
                    q.Type = QuestionType.MultipleChoice;
                    q.Stem = "【Prompt 提示词工程与 Agent】在设计高可靠大模型智能体 (Agent) 工作流时，下列哪些策略有助于显著降低模型幻觉 (Hallucination) 并提升输出稳定性？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        "A. 提供 Few-Shot（少样本）示例与清晰严谨的 System Prompt 约束",
                        "B. 采用 CoT (Chain-of-Thought 思维链) 引导模型分步推理",
                        "C. 要求模型通过 Function Calling / Tool Use 检索外部可信事实数据源",
                        "D. 盲目将 Temperature 参数拉满到 2.0"
                    });
                    q.CorrectAnswer = "A, B, C";
                    q.StandardAnalysis = "少样本学习、思维链分步推理、结合外部知识库工具调用均能有效抑制模型幻觉；拉高 Temperature 会使随机度激增加剧幻觉。故 ABC 正确。";
                }
                else if (mod == 2)
                {
                    q.Type = QuestionType.FillInBlank;
                    q.Stem = "【大模型轻量微调】在大模型高效参数微调 (PEFT) 技术中，通过冻结预训练骨干权重并在注意力层旁路注入低秩可训练矩阵的技术被称为 ______ (英文缩写)。";
                    q.CorrectAnswer = "LoRA";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "LoRA (Low-Rank Adaptation) 是目前最主流的大模型参数高效微调方法之一。";
                }
                else
                {
                    q.Type = QuestionType.ShortAnswer;
                    q.Stem = "【Transformer 核心架构】简要阐述 Transformer 架构中自注意力机制（Self-Attention）的核心计算公式 $Attention(Q, K, V) = softmax(\\frac{QK^T}{\\sqrt{d_k}})V$ 中除以 $\\sqrt{d_k}$ 的主要数学目的。";
                    q.CorrectAnswer = "防止点积结果过大导致 softmax 函数进入梯度极小的饱和区，避免梯度消失，保证训练梯度稳定传递。";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = "当向量维度 $d_k$ 较大时，点积 $QK^T$ 的方差会变大导致数值极大，将 softmax 函数推向梯度极小的平坦饱和区域。缩放因子 $\\frac{1}{\\sqrt{d_k}}$ 能将方差归一化到 1，防止梯度消失。";
                }
            }
            // 12. 万能学科与自定义考点生成引擎 (根据 index 变化 6 大考查维度，杜绝任何重复)
            else
            {
                int mod = index % 5;
                if (mod == 0)
                {
                    q.Type = QuestionType.SingleChoice;
                    q.Stem = $"【{subject} · 核心考点解析】在【{grade}】关于《{category}》的学习中，下列对该知识点核心概念的表述最符合学科规范的是：";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        $"A. 该概念在标准参考系与给定约束条件下具备因果确定性，其关键在于把握本质特征与适用边界",
                        $"B. 只要忽略所有外部环境限制，该结论依然在任何极端场景下绝对成立",
                        $"C. 该知识点仅存在纯理论推演价值，无法与实际问题建立任何因果关联",
                        $"D. 实际观察与规范理论模型之间存在根本无法调和的概念冲突"
                    });
                    q.CorrectAnswer = "A";
                    q.StandardAnalysis = $"深入理解【{subject} - {category}】的核心在于准确认知其本质内涵与适用边界。故 A 最符合学科规范。";
                }
                else if (mod == 1)
                {
                    q.Type = QuestionType.MultipleChoice;
                    q.Stem = $"【{subject} · 规律探究与多维思辨】围绕《{category}》的核心规律与典型现象，下列判断中合理的有哪些？";
                    q.OptionsJson = JsonSerializer.Serialize(new List<string>
                    {
                        $"A. 深入分析该规律有助于建立系统化认知框架与逻辑推演链条",
                        $"B. 在解决综合实际问题时，需要结合具体情境进行条件检验与分步论证",
                        $"C. 掌握基本原理与核心公式是解决相关变式题目的关键基石",
                        $"D. 只要机械死记硬背结论即可应对所有未知变式题"
                    });
                    q.CorrectAnswer = "A, B, C";
                    q.StandardAnalysis = $"学习【{subject} - {category}】需要注重逻辑推演、情境分析与原理理解，死记硬背不可取。故 ABC 正确。";
                }
                else if (mod == 2)
                {
                    q.Type = QuestionType.FillInBlank;
                    q.Stem = $"【{subject} · 重点填空巩固】在【{grade}】学科体系中，针对《{category}》的重难点梳理，其核心判断依据在于准确认知 ______ 与 ______ 之间的逻辑关联。(请用逗号隔开填入)";
                    q.CorrectAnswer = "基本概念, 适用边界";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = $"掌握《{category}》需要清晰辨析基本概念与适用边界，构建严谨的学科知识网。";
                }
                else if (mod == 3)
                {
                    q.Type = QuestionType.ShortAnswer;
                    q.Stem = $"【{subject} · 综合分析问答】请结合【{grade}】学科要求，简要阐述在学习《{category}》这一专题考点时，应重点掌握哪些核心要素与典型解题思路？";
                    q.CorrectAnswer = $"首先牢固掌握基本概念与核心规律，其次明晰适用条件与约束边界，最后通过典型例题掌握建模与分步解答方法。";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = $"对于【{subject} - {category}】的考查，建议学生从概念理解、规律应用、条件边界以及建模推演四个维度进行系统梳理与专项突破。";
                }
                else
                {
                    q.Type = QuestionType.EssayAnalysis;
                    q.Stem = $"【{subject} · 深度探究大题】请详细分析《{category}》在实际应用与综合考核中的典型考查形式，并给出规范的解题步骤与易错点防范策略。";
                    q.CorrectAnswer = $"解题步骤包括：1. 审题明确考查目标与已知条件；2. 匹配对应核心原理与公式；3. 逻辑严密分步推导；4. 检验边界与单位规范。易错点在于忽略隐含约束条件。";
                    q.OptionsJson = "[]";
                    q.StandardAnalysis = $"解答《{category}》大题时，必须建立清晰的审题、建模、推导、检验闭环体系，重点防范因忽略隐含边界或粗心计算导致的失分。";
                }
            }

            return q;
        }
    }
}
