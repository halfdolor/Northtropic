using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Northtropic.Helpers;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class LlmHttpHelperTests
    {
        [Theory]
        [InlineData(null, LlmHttpHelper.DefaultGeminiBaseUrl)]
        [InlineData("", LlmHttpHelper.DefaultGeminiBaseUrl)]
        [InlineData("   ", LlmHttpHelper.DefaultGeminiBaseUrl)]
        [InlineData("https://api.iorai.com/interface/proxy2/llm/gemini/v1beta/openai", "https://api.iorai.com/interface/proxy2/llm/gemini/v1beta/openai/chat/completions")]
        [InlineData("https://api.iorai.com/interface/proxy2/llm/gemini/v1beta/openai/", "https://api.iorai.com/interface/proxy2/llm/gemini/v1beta/openai/chat/completions")]
        [InlineData("https://api.iorai.com/interface/proxy2/llm/gemini/v1beta/openai/chat/completions", "https://api.iorai.com/interface/proxy2/llm/gemini/v1beta/openai/chat/completions")]
        [InlineData("https://api.iorai.com/interface/proxy2/llm/gemini/v1beta/openai/chat/completions/", "https://api.iorai.com/interface/proxy2/llm/gemini/v1beta/openai/chat/completions")]
        public void NormalizeChatCompletionsUrl_ShouldFormatCorrectly(string? input, string expected)
        {
            var result = LlmHttpHelper.NormalizeChatCompletionsUrl(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void CreateJsonContent_ShouldHaveStrictApplicationJson_WithoutCharset()
        {
            var payload = new { model = "gemini-3.8-flash", test = 123 };
            using var content = LlmHttpHelper.CreateJsonContent(payload);

            // 关键验证：MediaType 必须为 application/json 且 CharSet 必须为空
            // 防止反向代理网关因 charset=utf-8 严格匹配失败抛 HTTP 500
            Assert.Equal("application/json", content.Headers.ContentType?.MediaType);
            Assert.Null(content.Headers.ContentType?.CharSet);
        }

        [Fact]
        public void ExtractMessageContent_ShouldExtractNormalContent()
        {
            var json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"READY\"}}]}";
            var text = LlmHttpHelper.ExtractMessageContent(json, "FALLBACK");
            Assert.Equal("READY", text);
        }

        [Fact]
        public void ExtractMessageContent_ShouldFallbackToReasoning_WhenContentMissing()
        {
            var json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"reasoning_content\":\"thinking step\"}}]}";
            var text = LlmHttpHelper.ExtractMessageContent(json, "FALLBACK");
            Assert.Equal("thinking step", text);
        }

        [Fact]
        public void ExtractMessageContent_ShouldReturnDefault_OnMalformedJson()
        {
            var json = "invalid json payload";
            var text = LlmHttpHelper.ExtractMessageContent(json, "FALLBACK");
            Assert.Equal("FALLBACK", text);
        }

        [Fact]
        public async Task TestConnectionAsync_WithMockHandler_ShouldNormalizeAndSucceed()
        {
            string? capturedUrl = null;
            string? capturedContentType = null;

            var mockHandler = new TestHttpMessageHandler((req, ct) =>
            {
                capturedUrl = req.RequestUri?.ToString();
                capturedContentType = req.Content?.Headers.ContentType?.ToString();

                var responseBody = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"READY\"}}]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody)
                });
            });

            var httpClient = new HttpClient(mockHandler);
            var factory = new SimpleTestHttpClientFactory(httpClient);
            var tutorService = new AiTutorService(new FakeGamificationService(), factory);

            var res = await tutorService.TestConnectionAsync(
                "mock-key",
                "https://api.example.com/openai/chat/completions",
                "gemini-3.8-flash"
            );

            Assert.True(res.IsSuccess);
            Assert.Equal("READY", res.SampleResponse);
            Assert.Equal("https://api.example.com/openai/chat/completions", capturedUrl);
            Assert.Equal("application/json", capturedContentType); // 确保无 ; charset=utf-8
        }
    }

    public class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    public class SimpleTestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SimpleTestHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}
