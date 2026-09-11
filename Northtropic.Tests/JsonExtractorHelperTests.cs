using System.Text.Json;
using Northtropic.Helpers;
using Xunit;

namespace Northtropic.Tests
{
    public class JsonExtractorHelperTests
    {
        [Fact]
        public void ExtractJson_WithThinkTagsAndMarkdown_ShouldExtractPureJson()
        {
            // Arrange
            string llmResponse = @"<think>
思考一下这道中考物理题的考点，应该是浮力与阿基米德原理。
选项设置应该包含重力与浮力的关系。
</think>
```json
{
  ""stem"": ""浸在液体中的物体受到的浮力大小等于什么？"",
  ""type"": 0,
  ""difficulty"": 2
}
```";

            // Act
            string extracted = JsonExtractorHelper.ExtractJson(llmResponse);

            // Assert
            Assert.DoesNotContain("<think>", extracted);
            Assert.DoesNotContain("```", extracted);
            using var doc = JsonDocument.Parse(extracted);
            Assert.Equal("浸在液体中的物体受到的浮力大小等于什么？", doc.RootElement.GetProperty("stem").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("difficulty").GetInt32());
        }

        [Fact]
        public void ExtractJson_WithJsonArray_ShouldExtractFullArray()
        {
            // Arrange
            string raw = @"以下是为您生成的变式题目：
```json
[
  { ""stem"": ""变式题1"", ""difficulty"": 1 },
  { ""stem"": ""变式题2"", ""difficulty"": 2 }
]
```
希望对您有帮助！";

            // Act
            string extracted = JsonExtractorHelper.ExtractJson(raw);

            // Assert
            using var doc = JsonDocument.Parse(extracted);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Equal(2, doc.RootElement.GetArrayLength());
            Assert.Equal("变式题1", doc.RootElement[0].GetProperty("stem").GetString());
        }

        [Fact]
        public void ExtractJson_WithTrailingCommas_ShouldBeCleanedOrParsed()
        {
            // Arrange
            string rawWithTrailing = @"
{
  ""title"": ""测试题目"",
  ""options"": [
    ""A. 选项一"",
    ""B. 选项二"",
  ],
}";

            // Act
            string extracted = JsonExtractorHelper.ExtractJson(rawWithTrailing);

            // Assert - AllowTrailingCommas option will parse it smoothly
            var options = new JsonDocumentOptions { AllowTrailingCommas = true };
            using var doc = JsonDocument.Parse(extracted, options);
            Assert.Equal("测试题目", doc.RootElement.GetProperty("title").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("options").GetArrayLength());
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ExtractJson_EmptyOrNull_ReturnsEmptyString(string? input)
        {
            string result = JsonExtractorHelper.ExtractJson(input!);
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void ExtractJson_TextWithoutJsonFences_FindsBrackets()
        {
            string raw = "这是一些前导文字 {\"name\": \"Northtropic\", \"ver\": 2} 这是后续说明";
            string extracted = JsonExtractorHelper.ExtractJson(raw);

            using var doc = JsonDocument.Parse(extracted);
            Assert.Equal("Northtropic", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("ver").GetInt32());
        }
    }
}
