using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;

namespace Northtropic.Services
{
    public class PrerequisiteNodeInfo
    {
        public string TargetSubject { get; set; } = string.Empty;
        public string TargetCategory { get; set; } = string.Empty;
        public string PrerequisiteSubject { get; set; } = string.Empty;
        public string PrerequisiteCategory { get; set; } = string.Empty;
        public string DependencyReason { get; set; } = string.Empty;
    }

    public class PrerequisiteTraceWarningDto
    {
        public string TargetSubject { get; set; } = string.Empty;
        public string TargetCategory { get; set; } = string.Empty;
        public string PrerequisiteSubject { get; set; } = string.Empty;
        public string PrerequisiteCategory { get; set; } = string.Empty;
        public string DependencyReason { get; set; } = string.Empty;
        public double PrerequisiteAccuracy { get; set; } = 0.0;
        public int PrerequisiteUnmasteredErrors { get; set; } = 0;
        public string ActionAdvice { get; set; } = string.Empty;
    }

    public class RootDeficiencyDiagnosisDto
    {
        public string TargetSubject { get; set; } = string.Empty;
        public string TargetCategory { get; set; } = string.Empty;
        public string RootSubject { get; set; } = string.Empty;
        public string RootCategory { get; set; } = string.Empty;
        public string RootDependencyReason { get; set; } = string.Empty;
        public int Depth { get; set; }
        public double RootAccuracy { get; set; }
        public int RootTotalPracticed { get; set; }
        public int RootUnmasteredErrors { get; set; }
        public List<string> LearningPathRoadmap { get; set; } = new();
        public string StrategicAdvice { get; set; } = string.Empty;
    }

    public static class PrerequisiteKnowledgeGraph
    {
        private static readonly List<PrerequisiteNodeInfo> GraphDependencies = new List<PrerequisiteNodeInfo>
        {
            // 物理多跳因果链: 压强与浮力 -> 力学与牛顿定律 -> 代数方程与函数 -> 四则混合运算与绝对值
            new PrerequisiteNodeInfo
            {
                TargetSubject = "物理", TargetCategory = "压强与浮力",
                PrerequisiteSubject = "物理", PrerequisiteCategory = "力学与牛顿定律",
                DependencyReason = "受力分析与重力公式 F=mg 是推导液体压强与阿基米德浮力定律的核心基础。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "物理", TargetCategory = "力学与牛顿定律",
                PrerequisiteSubject = "数学", PrerequisiteCategory = "代数方程与函数",
                DependencyReason = "匀变速直线运动规律、合力加速度公式 F=ma 与动能定理推导深度依赖二次函数与一元方程代换。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "物理", TargetCategory = "电学与电路",
                PrerequisiteSubject = "数学", PrerequisiteCategory = "代数方程与函数",
                DependencyReason = "串并联电路欧姆定律 I=U/R 与功率计算高度依赖一元二次方程组代入计算。"
            },
            // 化学多跳因果链: 酸碱盐及反应 -> 化学方程式 -> 物质构成与元素
            new PrerequisiteNodeInfo
            {
                TargetSubject = "化学", TargetCategory = "酸碱盐及反应",
                PrerequisiteSubject = "化学", PrerequisiteCategory = "化学方程式",
                DependencyReason = "中和反应与复分解离子方程式书写必须熟练掌握守恒定律与反应沉淀规则。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "化学", TargetCategory = "化学方程式",
                PrerequisiteSubject = "化学", PrerequisiteCategory = "物质构成与元素",
                DependencyReason = "化学式配平与质量守恒建立在原子构成、分子式与元素化合价记忆基础之上。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "化学", TargetCategory = "有机化学基础",
                PrerequisiteSubject = "化学", PrerequisiteCategory = "化学方程式",
                DependencyReason = "官能团反应与有机合成推断需要熟练掌握配平与质量守恒定律。"
            },
            // 数学多跳因果链: 三角函数 / 数列 -> 代数方程与函数 -> 四则混合运算与绝对值
            new PrerequisiteNodeInfo
            {
                TargetSubject = "数学", TargetCategory = "代数方程与函数",
                PrerequisiteSubject = "数学", PrerequisiteCategory = "四则混合运算与绝对值",
                DependencyReason = "字母代数式的展开因式分解与移项变形必须具备严密无误的有理数四则运算基本功。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "数学", TargetCategory = "三角函数",
                PrerequisiteSubject = "数学", PrerequisiteCategory = "代数方程与函数",
                DependencyReason = "三角函数的周期性、单调性与图像平移建立在一般函数对应法则与定义域的基础之上。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "数学", TargetCategory = "数列与极限",
                PrerequisiteSubject = "数学", PrerequisiteCategory = "代数方程与函数",
                DependencyReason = "递推数列求解与等差/等比求和通项需要极强的代数变形与函数映射能力。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "数学", TargetCategory = "立体几何",
                PrerequisiteSubject = "数学", PrerequisiteCategory = "几何图形与证明",
                DependencyReason = "线面垂直、面面平行空间定理推导完全承袭平面几何的全等证明与勾股定理。"
            },
            // 计算机 & C# & AI 多跳因果链: Agent 智能体 -> Prompt 工程 -> 大模型认知基础
            new PrerequisiteNodeInfo
            {
                TargetSubject = "C# & .NET 进阶", TargetCategory = "异步编程 async/await",
                PrerequisiteSubject = "C# & .NET 进阶", PrerequisiteCategory = "内存管理与GC",
                DependencyReason = "async/await 状态机生成、Task 对象分配与 ValueTask 优化建立在堆栈内存分配认知之上。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "C# & .NET 进阶", TargetCategory = "LINQ 高级",
                PrerequisiteSubject = "C# & .NET 进阶", PrerequisiteCategory = "依赖注入",
                DependencyReason = "LINQ 延时执行表达式树与 Lambda 闭包机制需要深刻理解委托 Func<T> 与匿名方法。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "Blazor 核心", TargetCategory = "状态管理",
                PrerequisiteSubject = "Blazor 核心", PrerequisiteCategory = "组件生命周期",
                DependencyReason = "级联参数与 StateHasChanged 异步渲染流高度依赖组件 OnInitialized/OnParametersSet 生命周期。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "算法与数据结构", TargetCategory = "动态规划",
                PrerequisiteSubject = "算法与数据结构", PrerequisiteCategory = "二分与排序算法",
                DependencyReason = "DP 状态转移方程与子问题重叠推演需要深刻掌握分治法与递归状态树拆解。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "AI & 大模型工程", TargetCategory = "Agent 智能体",
                PrerequisiteSubject = "AI & 大模型工程", PrerequisiteCategory = "Prompt 工程",
                DependencyReason = "多 Agent 协作与工具调用 Function Calling 建立在严密的 System Prompt 结构化约束之上。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "AI & 大模型工程", TargetCategory = "Prompt 工程",
                PrerequisiteSubject = "AI & 大模型工程", PrerequisiteCategory = "大模型基础认知",
                DependencyReason = "上下文窗口、Temperature 采样与思维链推导基于 Transformer 自回归生成原理。"
            },
            new PrerequisiteNodeInfo
            {
                TargetSubject = "AI & 大模型工程", TargetCategory = "RAG 检索增强",
                PrerequisiteSubject = "AI & 大模型工程", PrerequisiteCategory = "Transformer 架构",
                DependencyReason = "向量数据库分片与余弦相似度召回依赖文本 Embedding 与自注意力语义表征原理。"
            }
        };

        private static readonly object _syncLock = new object();

        public static void RegisterDependency(string targetSubject, string targetCategory, string prereqSubject, string prereqCategory, string reason)
        {
            lock (_syncLock)
            {
                if (!GraphDependencies.Any(g => g.TargetSubject == targetSubject && g.TargetCategory == targetCategory && g.PrerequisiteSubject == prereqSubject && g.PrerequisiteCategory == prereqCategory))
                {
                    GraphDependencies.Add(new PrerequisiteNodeInfo
                    {
                        TargetSubject = targetSubject,
                        TargetCategory = targetCategory,
                        PrerequisiteSubject = prereqSubject,
                        PrerequisiteCategory = prereqCategory,
                        DependencyReason = reason
                    });
                }
            }
        }

        public static string NormalizeSubject(string? subject)
        {
            if (string.IsNullOrWhiteSpace(subject)) return string.Empty;
            string s = subject.Trim();
            string[] prefixes = new[] { "小学", "初中", "高中", "大学" };
            foreach (var p in prefixes)
            {
                if (s.StartsWith(p) && s.Length > p.Length)
                {
                    s = s.Substring(p.Length).Trim();
                    break;
                }
            }
            return s;
        }

        public static bool MatchSubject(string candidate, string target)
        {
            if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase)) return true;
            string normCand = NormalizeSubject(candidate);
            string normTarget = NormalizeSubject(target);
            if (string.Equals(normCand, normTarget, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(normCand) && !string.IsNullOrEmpty(normTarget))
            {
                if (normCand.Contains(normTarget, StringComparison.OrdinalIgnoreCase) ||
                    normTarget.Contains(normCand, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool MatchCategory(string candidate, string target)
        {
            if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(target)) return false;
            
            if (candidate.Contains(target, StringComparison.OrdinalIgnoreCase) ||
                target.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 词元切分与概念语义匹配（支持如“压强与浮力”与“浮力与阿基米德原理”在“浮力”概念上的因果溯源）
            var separators = new[] { '与', '和', '及', '、', ' ', '/', '&' };
            var candTokens = candidate.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var targetTokens = target.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var ct in candTokens)
            {
                if (ct.Length < 2) continue;
                foreach (var tt in targetTokens)
                {
                    if (tt.Length < 2) continue;
                    if (ct.Contains(tt, StringComparison.OrdinalIgnoreCase) || tt.Contains(ct, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    // 特别处理包含修饰词如“牛顿运动定律”与“牛顿定律”
                    string ctClean = ct.Replace("运动", "");
                    string ttClean = tt.Replace("运动", "");
                    if (ctClean.Contains(ttClean, StringComparison.OrdinalIgnoreCase) || ttClean.Contains(ctClean, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static List<PrerequisiteNodeInfo> GetPrerequisites(string subject, string category)
        {
            lock (_syncLock)
            {
                var exact = GraphDependencies
                    .Where(g => g.TargetSubject == subject && g.TargetCategory == category)
                    .ToList();
                if (exact.Count > 0) return exact;

                return GraphDependencies
                    .Where(g => MatchSubject(g.TargetSubject, subject) && MatchCategory(g.TargetCategory, category))
                    .ToList();
            }
        }

        /// <summary>
        /// 获取目标考点的全量前置依赖链（支持多跳 DAG 拓扑传递，含环路防御检测）
        /// 返回自最底层最基础考点至直接前置考点的拓扑排序链
        /// </summary>
        public static List<PrerequisiteNodeInfo> GetFullDependencyChain(string subject, string category)
        {
            var result = new List<PrerequisiteNodeInfo>();
            var visited = new HashSet<(string Subject, string Category)>();

            void Traverse(string curSub, string curCat)
            {
                var directPrereqs = GetPrerequisites(curSub, curCat);
                foreach (var p in directPrereqs)
                {
                    var key = (p.PrerequisiteSubject, p.PrerequisiteCategory);
                    if (visited.Add(key))
                    {
                        // 递归深入查找更深层的前置依赖
                        Traverse(p.PrerequisiteSubject, p.PrerequisiteCategory);
                        result.Add(p);
                    }
                }
            }

            Traverse(subject, category);
            return result;
        }

        public static async Task<List<PrerequisiteTraceWarningDto>> TraceWeakPrerequisitesAsync(Guid userId, string subject, string category, AppDbContext dbContext)
        {
            var result = new List<PrerequisiteTraceWarningDto>();
            var prerequisites = GetPrerequisites(subject, category);

            if (!prerequisites.Any()) return result;

            foreach (var req in prerequisites)
            {
                // 获取学生在前置考点上的答题记录 (只读投影查询，消除大对象物化)
                var reqCorrectList = await dbContext.PracticeRecords
                    .AsNoTracking()
                    .Where(r => r.UserId == userId && r.Question != null && (r.Question.Subject == req.PrerequisiteSubject || r.Question.Subject.Contains(req.PrerequisiteSubject)) && (r.Question.Category == req.PrerequisiteCategory || r.Question.Category.Contains(req.PrerequisiteCategory)))
                    .Select(r => r.IsCorrect)
                    .ToListAsync();

                // 获取学生在前置考点上的未消灭错题
                int errorCount = await dbContext.ErrorItems
                    .AsNoTracking()
                    .Where(e => e.UserId == userId && !e.IsMastered && e.Question != null && (e.Question.Subject == req.PrerequisiteSubject || e.Question.Subject.Contains(req.PrerequisiteSubject)) && (e.Question.Category == req.PrerequisiteCategory || e.Question.Category.Contains(req.PrerequisiteCategory)))
                    .CountAsync();

                int total = reqCorrectList.Count;
                int correct = reqCorrectList.Count(c => c);
                double accuracy = total > 0 ? (double)correct / total * 100.0 : 0.0;

                // 若前置考点做题数少于3题、正确率低于70% 或 含有未消灭错题，判定为【前置考点薄弱】
                if (total < 3 || accuracy < 70.0 || errorCount > 0)
                {
                    string statusText = total == 0 ? "尚未建立练习基准" : $"正确率仅 {accuracy:F1}%";
                    result.Add(new PrerequisiteTraceWarningDto
                    {
                        TargetSubject = subject,
                        TargetCategory = category,
                        PrerequisiteSubject = req.PrerequisiteSubject,
                        PrerequisiteCategory = req.PrerequisiteCategory,
                        DependencyReason = req.DependencyReason,
                        PrerequisiteAccuracy = Math.Round(accuracy, 1),
                        PrerequisiteUnmasteredErrors = errorCount,
                        ActionAdvice = $"💡 **治本破壁建议**：您在【{subject} - {category}】遇到的思维瓶颈，底层源自前置考点【{req.PrerequisiteSubject} - {req.PrerequisiteCategory}】({statusText}，有 {errorCount} 道待消灭错题)。{req.DependencyReason} 建议先前往前置考点完成靶向强化！"
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// 针对深层认知障碍进行多跳根因诊断，定位整个前置依赖森林中最深层的薄弱根基
        /// </summary>
        public static async Task<RootDeficiencyDiagnosisDto?> DiagnoseRootDeficiencyAsync(Guid userId, string subject, string category, AppDbContext dbContext)
        {
            var chain = GetFullDependencyChain(subject, category);
            if (!chain.Any()) return null;

            // 存储各节点的薄弱度指标
            var weakCandidates = new List<(PrerequisiteNodeInfo Node, int Depth, double Accuracy, int Total, int Errors)>();
            var depthMap = new Dictionary<(string Sub, string Cat), int>();

            // 计算拓扑深度
            void ComputeDepth(string curSub, string curCat, int currentDepth)
            {
                var prereqs = GetPrerequisites(curSub, curCat);
                foreach (var p in prereqs)
                {
                    var key = (p.PrerequisiteSubject, p.PrerequisiteCategory);
                    if (!depthMap.TryGetValue(key, out var existingDepth) || currentDepth > existingDepth)
                    {
                        depthMap[key] = currentDepth;
                        ComputeDepth(p.PrerequisiteSubject, p.PrerequisiteCategory, currentDepth + 1);
                    }
                }
            }

            ComputeDepth(subject, category, 1);

            foreach (var node in chain)
            {
                var reqCorrectList = await dbContext.PracticeRecords
                    .AsNoTracking()
                    .Where(r => r.UserId == userId && r.Question != null && r.Question.Subject == node.PrerequisiteSubject && r.Question.Category == node.PrerequisiteCategory)
                    .Select(r => r.IsCorrect)
                    .ToListAsync();

                int errorCount = await dbContext.ErrorItems
                    .AsNoTracking()
                    .Where(e => e.UserId == userId && !e.IsMastered && e.Question != null && e.Question.Subject == node.PrerequisiteSubject && e.Question.Category == node.PrerequisiteCategory)
                    .CountAsync();

                int total = reqCorrectList.Count;
                int correct = reqCorrectList.Count(c => c);
                double accuracy = total > 0 ? (double)correct / total * 100.0 : 0.0;

                int depth = depthMap.GetValueOrDefault((node.PrerequisiteSubject, node.PrerequisiteCategory), 1);

                if (total < 3 || accuracy < 70.0 || errorCount > 0)
                {
                    weakCandidates.Add((node, depth, Math.Round(accuracy, 1), total, errorCount));
                }
            }

            if (!weakCandidates.Any()) return null;

            // 选取深度最深（最根基）、其次错误率最高的节点作为根本病灶
            var rootCandidate = weakCandidates
                .OrderByDescending(c => c.Depth)
                .ThenBy(c => c.Accuracy)
                .ThenByDescending(c => c.Errors)
                .First();

            // 生成学习路径建议 (自根本节点顺藤摸瓜返回到当前目标考点)
            var roadmap = new List<string>
            {
                $"【第 1 阶·奠基】{rootCandidate.Node.PrerequisiteSubject} · {rootCandidate.Node.PrerequisiteCategory}"
            };

            var intermediateNodes = chain
                .Where(n => n.PrerequisiteCategory != rootCandidate.Node.PrerequisiteCategory &&
                            n.TargetCategory != category)
                .Select(n => $"【第 2 阶·过渡】{n.TargetSubject} · {n.TargetCategory}")
                .Distinct()
                .ToList();

            roadmap.AddRange(intermediateNodes);
            roadmap.Add($"【第 3 阶·攻坚】{subject} · {category}");

            return new RootDeficiencyDiagnosisDto
            {
                TargetSubject = subject,
                TargetCategory = category,
                RootSubject = rootCandidate.Node.PrerequisiteSubject,
                RootCategory = rootCandidate.Node.PrerequisiteCategory,
                RootDependencyReason = rootCandidate.Node.DependencyReason,
                Depth = rootCandidate.Depth,
                RootAccuracy = rootCandidate.Accuracy,
                RootTotalPracticed = rootCandidate.Total,
                RootUnmasteredErrors = rootCandidate.Errors,
                LearningPathRoadmap = roadmap,
                StrategicAdvice = $"经过多跳知识图谱深度遍历，定位到当前【{subject} - {category}】解题障碍的底层根因是【{rootCandidate.Node.PrerequisiteSubject} - {rootCandidate.Node.PrerequisiteCategory}】(相距 {rootCandidate.Depth} 层认知跃迁，掌握度仅 {rootCandidate.Accuracy:F1}%)。{rootCandidate.Node.DependencyReason} 强烈建议遵循循序渐进认知路径完成基础强化后再攻坚当前难关！"
            };
        }
    }
}
