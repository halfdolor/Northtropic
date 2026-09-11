using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Northtropic.Helpers
{
    /// <summary>
    /// 大模型响应 JSON 鲁棒性提取与清洗辅助类
    /// 解决大语言模型常见返回格式干扰：包括 ```json 代码块包裹、<think> 思考链残留、顶层数组定界截断、前后提示语干扰等
    /// </summary>
    public static class JsonExtractorHelper
    {
        private static readonly JsonSerializerOptions DefaultSerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// 剥离 DeepSeek 等推理模型产生的 <think>...</think> 标签及其内容
        /// </summary>
        public static string StripThinkTags(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            // 移除完整的 <think>...</think> 块
            string cleaned = Regex.Replace(input, @"<think>[\s\S]*?</think>", string.Empty, RegexOptions.IgnoreCase);

            // 若输出在思考过程中断导致仅有 <think> 未闭合，清除残留
            cleaned = Regex.Replace(cleaned, @"<think>[\s\S]*$", string.Empty, RegexOptions.IgnoreCase);

            return cleaned.Trim();
        }

        /// <summary>
        /// 智能提取原始文本中的有效 JSON 字符串（自动兼容对象 {...} 与数组 [...]）
        /// </summary>
        public static string ExtractJson(string? rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText)) return string.Empty;

            // 1. 先剥离思考标签
            string text = StripThinkTags(rawText);

            // 2. 尝试从 Markdown 代码块（如 ```json ... ``` 或 ``` ... ```）中提取
            var codeBlockMatch = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
            if (codeBlockMatch.Success && !string.IsNullOrWhiteSpace(codeBlockMatch.Groups[1].Value))
            {
                text = codeBlockMatch.Groups[1].Value.Trim();
            }

            // 3. 寻找最外层定界符 { 或 [
            int firstBrace = text.IndexOf('{');
            int firstBracket = text.IndexOf('[');

            if (firstBrace == -1 && firstBracket == -1)
            {
                // 没有找到任何 JSON 定界符
                return text.Trim();
            }

            // 确定是对象还是数组优先出现
            bool isObject = false;
            if (firstBrace != -1 && firstBracket != -1)
            {
                isObject = firstBrace < firstBracket;
            }
            else
            {
                isObject = firstBrace != -1;
            }

            if (isObject)
            {
                int lastBrace = text.LastIndexOf('}');
                if (lastBrace > firstBrace)
                {
                    return text.Substring(firstBrace, lastBrace - firstBrace + 1).Trim();
                }
            }
            else
            {
                int lastBracket = text.LastIndexOf(']');
                if (lastBracket > firstBracket)
                {
                    return text.Substring(firstBracket, lastBracket - firstBracket + 1).Trim();
                }
            }

            return text.Trim();
        }

        /// <summary>
        /// 安全反序列化 JSON 文本，包含异常捕获与容错机制
        /// </summary>
        public static bool TryParseJson<T>(string? rawText, out T? result, JsonSerializerOptions? options = null)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(rawText)) return false;

            try
            {
                string jsonString = ExtractJson(rawText);
                if (string.IsNullOrWhiteSpace(jsonString)) return false;

                result = JsonSerializer.Deserialize<T>(jsonString, options ?? DefaultSerializerOptions);
                return result != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
