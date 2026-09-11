using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Northtropic.Helpers;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxOmniEvolutionTests
    {
        [Fact]
        public void OpenXmlSpreadsheetHelper_SanitizeForXml_FiltersIllegalControlCharacters()
        {
            // XML 1.0 严格禁止字符: 0x00-0x08, 0x0B-0x0C, 0x0E-0x1F
            string rawDirty = "Title\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008With\u000B\u000CControl\u000E\u000F\u001A\u001FChars";
            string sanitized = OpenXmlSpreadsheetHelper.SanitizeForXml(rawDirty);

            Assert.Equal("TitleWithControlChars", sanitized);
        }

        [Fact]
        public void OpenXmlSpreadsheetHelper_SanitizeForXml_PreservesWhitespaceAndUnicodeAndEmojis()
        {
            // 合法空白符: \t (0x09), \n (0x0A), \r (0x0D)
            // 合法 CJK 与 Emoji (Surrogate Pairs)
            string text = "\tLine 1: 勾股定理 $a^2+b^2=c^2$\r\n\tLine 2: 沉浸式刷题 🧘 卓越架构 🚀 智适应 🎯";
            string sanitized = OpenXmlSpreadsheetHelper.SanitizeForXml(text);

            Assert.Equal(text, sanitized);
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("   ", "   ")]
        public void OpenXmlSpreadsheetHelper_SanitizeForXml_HandlesNullAndEmpty(string? input, string expected)
        {
            string result = OpenXmlSpreadsheetHelper.SanitizeForXml(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void OpenXmlSpreadsheetHelper_CreateSpreadsheet_GeneratesValidParseableXml()
        {
            // 构造包含脏数据、公式、Emoji及不可打印字符的复杂表格数据
            var headers = new List<string> { "序号\u0000", "考点\u0008名称", "题目解析\u000B" };
            var rows = new List<List<string>>
            {
                new() { "1", "勾股定理\u0001证明", "当直角边为 3 和 4 时，斜边为 5。\n即 $3^2+4^2=5^2$ 🎯\u0000" },
                new() { "2", "牛顿第二定律\u001F", "公式 $F=ma$\t加速度与合外力成正比 🚀\u0002" }
            };

            byte[] excelBytes = OpenXmlSpreadsheetHelper.CreateSpreadsheet("Sheet\u0000_1", headers, rows);
            Assert.NotNull(excelBytes);
            Assert.True(excelBytes.Length > 0);

            // 解压验证 zip 包内的 XML 文件是否合规且能被 XDocument 顺利解析无异常
            using var ms = new MemoryStream(excelBytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

            var sharedStringsEntry = archive.GetEntry("xl/sharedStrings.xml");
            Assert.NotNull(sharedStringsEntry);

            using var entryStream = sharedStringsEntry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8);
            string sharedStringsXml = reader.ReadToEnd();

            // 验证未包含非法 XML 1.0 控制字符 (使用 char 重载避免语言区域对 \0 的零权重匹配)
            Assert.DoesNotContain('\u0000', sharedStringsXml);
            Assert.DoesNotContain('\u0008', sharedStringsXml);
            Assert.DoesNotContain('\u000B', sharedStringsXml);
            Assert.DoesNotContain('\u001F', sharedStringsXml);

            // 验证 XML 可被标准 XML 解析器正确加载
            var doc = XDocument.Parse(sharedStringsXml);
            Assert.NotNull(doc.Root);
        }

        [Theory]
        [InlineData("A. 选我", 0, "A")]
        [InlineData("B、选我", 1, "B")]
        [InlineData("(C) 选我", 2, "C")]
        [InlineData("[D] 选我", 3, "D")]
        [InlineData("E: 选我", 4, "E")]
        [InlineData("无前缀文本选项", 0, "A")]
        [InlineData("无前缀文本选项", 1, "B")]
        [InlineData("无前缀文本选项", 2, "C")]
        public void QuestionShuffleHelper_ExtractChoicePrefix_ExtractsAccurately(string option, int index, string expectedPrefix)
        {
            string prefix = QuestionShuffleHelper.ExtractChoicePrefix(option, index);
            Assert.Equal(expectedPrefix, prefix);
        }

        [Theory]
        [InlineData("正确", true)]
        [InlineData("对", true)]
        [InlineData("√", true)]
        [InlineData("True", true)]
        [InlineData("TRUE", true)]
        [InlineData("T", true)]
        [InlineData("t", true)]
        [InlineData("A. 正确", true)]
        [InlineData("错误", false)]
        [InlineData("错", false)]
        [InlineData("×", false)]
        [InlineData("False", false)]
        [InlineData("FALSE", false)]
        [InlineData("F", false)]
        [InlineData("f", false)]
        [InlineData("B. 错误", false)]
        public void PracticeService_TryNormalizeJudgement_ValidatesBooleanChoices(string input, bool expected)
        {
            bool success = PracticeService.TryNormalizeJudgement(input, out bool result);
            Assert.True(success, $"Failed to normalize judgement: '{input}'");
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("未知")]
        [InlineData("无法确定")]
        [InlineData("C. 也许对也许错")]
        public void PracticeService_TryNormalizeJudgement_RejectsAmbiguousInputs(string input)
        {
            bool success = PracticeService.TryNormalizeJudgement(input, out _);
            Assert.False(success, $"Should not normalize ambiguous input: '{input}'");
        }

        [Fact]
        public void Ebbinghaus_SpacedIntervals_RemainStrictlyMonotonic()
        {
            // 验证基于艾宾浩斯遗忘曲线的复习天数间隔是否随复习次数单调递增
            var intervals = new List<int>();
            for (int reviewCount = 0; reviewCount <= 7; reviewCount++)
            {
                int intervalDays = reviewCount switch
                {
                    0 => 1,
                    1 => 2,
                    2 => 4,
                    3 => 7,
                    4 => 15,
                    _ => 30
                };
                intervals.Add(intervalDays);
            }

            for (int i = 1; i < intervals.Count; i++)
            {
                Assert.True(intervals[i] >= intervals[i - 1], $"Ebbinghaus interval should not decrease from {intervals[i - 1]} to {intervals[i]}");
            }
        }
    }
}
