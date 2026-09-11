using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Northtropic.Models;

namespace Northtropic.Helpers
{
    public static class QuestionShuffleHelper
    {
        private static readonly string[] Prefixes = { "A", "B", "C", "D", "E", "F", "G", "H" };

        public static Question ShuffleQuestionOptions(Question original)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (original.Type != QuestionType.SingleChoice && original.Type != QuestionType.MultipleChoice)
            {
                return original; // 非选择题不需要洗牌
            }

            if (string.IsNullOrWhiteSpace(original.OptionsJson))
            {
                return original;
            }

            List<string>? rawOptions;
            try
            {
                rawOptions = JsonSerializer.Deserialize<List<string>>(original.OptionsJson);
            }
            catch
            {
                return original;
            }

            if (rawOptions == null || rawOptions.Count < 2)
            {
                return original;
            }

            // 1. 剥离前缀，获取 pure text 列表
            var optionTextList = new List<string>();
            for (int i = 0; i < rawOptions.Count; i++)
            {
                string raw = rawOptions[i].Trim();
                string pureText = CleanPrefix(raw, i);
                optionTextList.Add(pureText);
            }

            // 2. 找到原正确答案对应的内容文本 (CorrectAnswer 可能是 "A" / "B" / "A. 苹果" / "苹果" 等)
            var correctTexts = FindCorrectTexts(original.CorrectAnswer, rawOptions, optionTextList);

            // 3. 产生选项对象列表包含 (PureText, IsCorrect)
            var items = new List<OptionItem>();
            for (int i = 0; i < optionTextList.Count; i++)
            {
                string text = optionTextList[i];
                bool isCorr = correctTexts.Contains(text, StringComparer.OrdinalIgnoreCase);
                items.Add(new OptionItem { Text = text, IsCorrect = isCorr });
            }

            // 4. 洗牌 (Fisher-Yates Shuffle)
            var rng = Random.Shared;
            int n = items.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                var value = items[k];
                items[k] = items[n];
                items[n] = value;
            }

            // 5. 为洗牌后的选项重新加上 A. B. C. D. 前缀，并重映射正确答案
            var shuffledRawOptions = new List<string>();
            var newCorrectAnswers = new List<string>();

            for (int i = 0; i < items.Count; i++)
            {
                string prefix = i < Prefixes.Length ? Prefixes[i] : ((char)('A' + i)).ToString();
                string newOptString = $"{prefix}. {items[i].Text}";
                shuffledRawOptions.Add(newOptString);

                if (items[i].IsCorrect)
                {
                    newCorrectAnswers.Add(prefix);
                }
            }

            // 架构安全性兜底：如果打乱后未能成功映射任何正确项（例如选项格式极端异常），保底返回原始题目对象，避免判题死锁
            if (newCorrectAnswers.Count == 0)
            {
                return original;
            }

            // 6. 产生深拷贝对象返回，绝不动 SQLite 原始记录
            var cloned = new Question
            {
                Id = original.Id,
                Stem = original.Stem,
                Type = original.Type,
                Subject = original.Subject,
                Category = original.Category,
                GradeTarget = original.GradeTarget,
                Difficulty = original.Difficulty,
                BaseExpReward = original.BaseExpReward,
                StandardAnalysis = original.StandardAnalysis,
                CreatedByUserId = original.CreatedByUserId,
                IsPublic = original.IsPublic,
                PublishStatus = original.PublishStatus,
                CreatedAt = original.CreatedAt,
                OptionsJson = JsonSerializer.Serialize(shuffledRawOptions),
                CorrectAnswer = string.Join(", ", newCorrectAnswers)
            };

            return cloned;
        }

        public static string ExtractChoicePrefix(string option, int fallbackIndex = -1)
        {
            if (string.IsNullOrWhiteSpace(option))
            {
                if (fallbackIndex < 0) return string.Empty;
                int safeIdx = Math.Clamp(fallbackIndex, 0, 25);
                return ((char)('A' + safeIdx)).ToString();
            }

            string opt = option.Trim();

            // 1. 匹配 "(A)", "（A）", "[A]", "【A】"
            var bracketMatch = System.Text.RegularExpressions.Regex.Match(opt, @"^[\(\[（【]\s*([A-Za-z])\s*[\)\]）】]");
            if (bracketMatch.Success)
            {
                return bracketMatch.Groups[1].Value.ToUpperInvariant();
            }

            // 2. 匹配 "①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧"
            if (opt.Length > 0)
            {
                char firstChar = opt[0];
                int circledIdx = "①②③④⑤⑥⑦⑧".IndexOf(firstChar);
                if (circledIdx >= 0)
                {
                    return ((char)('A' + circledIdx)).ToString();
                }
            }

            // 3. 匹配 "1.", "1、", "1: " 或单个数字 "1" - "8"
            var numDotMatch = System.Text.RegularExpressions.Regex.Match(opt, @"^([1-8])([\.、:．\s]|$)");
            if (numDotMatch.Success && int.TryParse(numDotMatch.Groups[1].Value, out int numVal) && numVal >= 1 && numVal <= 8)
            {
                return ((char)('A' + (numVal - 1))).ToString();
            }

            // 4. 匹配 "A. ", "A: ", "A) ", "A．", "A、", "A " 或单个字母 "A"
            if (opt.Length >= 1)
            {
                char first = char.ToUpperInvariant(opt[0]);
                if (first >= 'A' && first <= 'Z')
                {
                    if (opt.Length == 1) return first.ToString();
                    char second = opt[1];
                    if (second == '.' || second == ':' || second == ')' || second == '．' || second == ' ' || second == '、' || second == '-')
                    {
                        return first.ToString();
                    }
                }
            }

            // 5. 无显式字母前缀，若指定了合法的 fallbackIndex，回退使用列表中给定的 index
            if (fallbackIndex >= 0)
            {
                int fallback = Math.Clamp(fallbackIndex, 0, 25);
                return ((char)('A' + fallback)).ToString();
            }

            return string.Empty;
        }

        public static string CleanPrefix(string option, int index = 0)
        {
            if (string.IsNullOrWhiteSpace(option)) return string.Empty;
            string opt = option.Trim();

            // 1. 匹配 "(A)", "（A）", "[A]", "【A】" 等外包围符号及其后续空格或分隔符
            var bracketMatch = System.Text.RegularExpressions.Regex.Match(opt, @"^[\(\[（【]\s*[A-Za-z0-9]\s*[\)\]）】][\.\s、:．\-—]*");
            if (bracketMatch.Success)
            {
                return opt.Substring(bracketMatch.Length).Trim();
            }

            // 2. 匹配 "①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧" 及后续分隔符
            var circledMatch = System.Text.RegularExpressions.Regex.Match(opt, @"^[①②③④⑤⑥⑦⑧][\.\s、:．\-—]*");
            if (circledMatch.Success)
            {
                return opt.Substring(circledMatch.Length).Trim();
            }

            // 3. 匹配 "1.", "1、", "1: ", "1-", "1．" 等数字前缀
            var numMatch = System.Text.RegularExpressions.Regex.Match(opt, @"^[1-8][\.、:．\s\-—]+");
            if (numMatch.Success)
            {
                return opt.Substring(numMatch.Length).Trim();
            }

            // 4. 匹配 "A. ", "A: ", "A) ", "A．", "A、", "A-", "A " 等字母前缀
            var letterMatch = System.Text.RegularExpressions.Regex.Match(opt, @"^[A-Za-z][\.、:．\s\)\-—]+");
            if (letterMatch.Success)
            {
                return opt.Substring(letterMatch.Length).Trim();
            }

            // 5. 若只有单个字母开头且后续有空格
            if (opt.Length >= 2 && char.IsLetter(opt[0]) && char.IsWhiteSpace(opt[1]))
            {
                return opt.Substring(1).Trim();
            }

            return opt;
        }

        private static HashSet<string> FindCorrectTexts(string rawCorrectAnswer, List<string> rawOptions, List<string> pureTexts)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(rawCorrectAnswer)) return result;

            string ans = rawCorrectAnswer.Trim();

            // 1. 先检查是否与某个纯文本或原始选项完全相等 (例如 ans 就是 "苹果" 或 "A. 苹果")
            for (int i = 0; i < pureTexts.Count; i++)
            {
                if (ans.Equals(pureTexts[i], StringComparison.OrdinalIgnoreCase) ||
                    (i < rawOptions.Count && ans.Equals(rawOptions[i].Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(pureTexts[i]);
                }
            }
            if (result.Count > 0) return result;

            // 2. 将 rawCorrectAnswer 拆分为独立 token 集合（例如 "A, B", "A B", "A、C", "A; B"）
            var rawTokens = ans.Split(new[] { ',', '，', '、', ';', '；', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(t => t.Trim())
                               .Where(t => !string.IsNullOrEmpty(t))
                               .ToList();

            // 对每个 token 进行定界识别
            var letterKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in rawTokens)
            {
                // 如果 token 自身能提取出有效前缀标号 (如 "(A)", "【A】", "①", "1.", "1")
                string extractedLetter = ExtractChoicePrefix(token);
                if (!string.IsNullOrEmpty(extractedLetter) && extractedLetter.Length == 1 && char.IsLetter(extractedLetter[0]))
                {
                    if (token.Length <= 4 || token.StartsWith("(") || token.StartsWith("（") || token.StartsWith("[") || token.StartsWith("【") || "①②③④⑤⑥⑦⑧".Contains(token[0]))
                    {
                        letterKeys.Add(extractedLetter.ToUpperInvariant());
                    }
                }

                // 如果 token 是 "A"
                if (token.Length == 1 && char.IsLetter(token[0]))
                {
                    letterKeys.Add(token.ToUpperInvariant());
                }
                // 如果 token 是 "A." 或 "A、" 或 "A: "
                else if (token.Length > 1 && char.IsLetter(token[0]) && (token[1] == '.' || token[1] == '、' || token[1] == ':' || token[1] == '．' || token[1] == ')'))
                {
                    letterKeys.Add(token.Substring(0, 1).ToUpperInvariant());
                }
                else
                {
                    // 检查 token 是否能直接匹配 pureTexts 或 rawOptions
                    bool matchedText = false;
                    for (int i = 0; i < pureTexts.Count; i++)
                    {
                        if (token.Equals(pureTexts[i], StringComparison.OrdinalIgnoreCase) ||
                            (i < rawOptions.Count && token.Equals(rawOptions[i].Trim(), StringComparison.OrdinalIgnoreCase)))
                        {
                            result.Add(pureTexts[i]);
                            matchedText = true;
                        }
                    }
                    if (!matchedText)
                    {
                        string cleanedToken = CleanPrefix(token, 0);
                        if (!string.IsNullOrEmpty(cleanedToken))
                        {
                            for (int i = 0; i < pureTexts.Count; i++)
                            {
                                if (cleanedToken.Equals(pureTexts[i], StringComparison.OrdinalIgnoreCase))
                                {
                                    result.Add(pureTexts[i]);
                                }
                            }
                        }
                    }
                }
            }

            // 3. 将识别到的前缀字母映射到 pureTexts
            for (int i = 0; i < pureTexts.Count; i++)
            {
                string prefix = i < Prefixes.Length ? Prefixes[i] : ((char)('A' + i)).ToString();
                if (letterKeys.Contains(prefix))
                {
                    result.Add(pureTexts[i]);
                }
            }

            // 4. 若上述未命中且 ans 为纯连写字母（如 "ABCD", "AC"）
            if (result.Count == 0)
            {
                string upperAns = ans.ToUpperInvariant();
                bool isAllLetters = upperAns.Length <= 8 && upperAns.All(c => c >= 'A' && c <= 'Z');
                if (isAllLetters)
                {
                    foreach (char c in upperAns)
                    {
                        int index = c - 'A';
                        if (index >= 0 && index < pureTexts.Count)
                        {
                            result.Add(pureTexts[index]);
                        }
                    }
                }
            }

            // 5. 兜底策略：如果仍未查到，匹配完整包含的纯文本
            if (result.Count == 0)
            {
                foreach (var text in pureTexts)
                {
                    if (!string.IsNullOrWhiteSpace(text) && ans.Contains(text, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(text);
                    }
                }
            }

            return result;
        }

        private class OptionItem
        {
            public string Text { get; set; } = string.Empty;
            public bool IsCorrect { get; set; }
        }
    }
}
