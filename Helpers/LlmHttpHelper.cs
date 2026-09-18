using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Northtropic.Helpers
{
    /// <summary>
    /// LLM HTTP 通信辅助类
    /// 解决反向代理（如 ARR/IIS/NGINX 等）与推理模型（如 Gemini 2.5/3.8 Flash）通信中的常见兼容性问题：
    /// 1. Content-Type 严格匹配：标准 StringContent 会附带 ; charset=utf-8，导致部分反代网关路由失败报 500
    /// 2. URL 规整：防止用户输入包含 /chat/completions 时发生路径双重拼接
    /// 3. 推理模型响应容错：thinking/reasoning token 耗尽或缺失 content 字段时的容错处理
    /// </summary>
    public static class LlmHttpHelper
    {
        public const string DefaultGeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";

        /// <summary>
        /// 规范化 OpenAI 兼容的 /chat/completions 端点 URL
        /// </summary>
        public static string NormalizeChatCompletionsUrl(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return DefaultGeminiBaseUrl;
            }

            var trimmed = baseUrl.Trim().TrimEnd('/');
            if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            return $"{trimmed}/chat/completions";
        }

        /// <summary>
        /// 创建反向代理友好的 JSON HttpContent（严格 application/json，无 charset=utf-8 后缀）
        /// </summary>
        public static ByteArrayContent CreateJsonContent(object body)
        {
            var json = JsonSerializer.Serialize(body);
            var bytes = Encoding.UTF8.GetBytes(json);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return content;
        }

        /// <summary>
        /// 从 OpenAI 兼容的 chat completion 响应中安全提取回答文本
        /// </summary>
        public static string ExtractMessageContent(string responseJson, string defaultFallback = "OK")
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return defaultFallback;
            }

            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("message", out var msg))
                    {
                        if (msg.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                        {
                            var contentStr = contentEl.GetString();
                            if (!string.IsNullOrWhiteSpace(contentStr))
                            {
                                return contentStr.Trim();
                            }
                        }

                        // 容错：推理模型可能将内容置于 reasoning_content
                        if (msg.TryGetProperty("reasoning_content", out var reasoningEl) && reasoningEl.ValueKind == JsonValueKind.String)
                        {
                            var reasoningStr = reasoningEl.GetString();
                            if (!string.IsNullOrWhiteSpace(reasoningStr))
                            {
                                return reasoningStr.Trim();
                            }
                        }
                    }
                }
            }
            catch
            {
                // 忽略解析异常，返回兜底文本
            }

            return defaultFallback;
        }
    }
}
