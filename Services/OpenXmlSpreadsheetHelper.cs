using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Northtropic.Services
{
    /// <summary>
    /// 零外部 NuGet 依赖、基于 .NET 8 BCL (ZipArchive + XDocument) 的高性能 OpenXML (.xlsx) 电子表格读写器
    /// </summary>
    public static class OpenXmlSpreadsheetHelper
    {
        public static byte[] CreateSpreadsheet(string sheetName, List<string> headers, List<List<string>> rows)
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                // 1. [Content_Types].xml
                var contentTypesEntry = archive.CreateEntry("[Content_Types].xml");
                using (var writer = new StreamWriter(contentTypesEntry.Open(), Encoding.UTF8))
                {
                    writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""xml"" ContentType=""application/xml""/>
  <Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>
  <Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>
  <Override PartName=""/xl/sharedStrings.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml""/>
</Types>");
                }

                // 2. _rels/.rels
                var relsEntry = archive.CreateEntry("_rels/.rels");
                using (var writer = new StreamWriter(relsEntry.Open(), Encoding.UTF8))
                {
                    writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>
</Relationships>");
                }

                // 3. xl/workbook.xml
                var safeSheetName = SecurityElement.Escape(SanitizeForXml(sheetName)) ?? "Sheet1";
                if (string.IsNullOrWhiteSpace(safeSheetName)) safeSheetName = "Sheet1";
                var workbookEntry = archive.CreateEntry("xl/workbook.xml");
                using (var writer = new StreamWriter(workbookEntry.Open(), Encoding.UTF8))
                {
                    writer.Write($@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
  <sheets>
    <sheet name=""{safeSheetName}"" sheetId=""1"" r:id=""rId1""/>
  </sheets>
</workbook>");
                }

                // 4. xl/_rels/workbook.xml.rels
                var workbookRelsEntry = archive.CreateEntry("xl/_rels/workbook.xml.rels");
                using (var writer = new StreamWriter(workbookRelsEntry.Open(), Encoding.UTF8))
                {
                    writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/>
  <Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings"" Target=""sharedStrings.xml""/>
</Relationships>");
                }

                // 收集所有去重共享字符串并过滤 XML 1.0 非法字符
                var allRows = new List<List<string>>();
                if (headers != null && headers.Count > 0)
                {
                    allRows.Add(headers);
                }
                if (rows != null && rows.Count > 0)
                {
                    allRows.AddRange(rows);
                }

                // 构建共享字符串池
                var sharedStrings = new List<string>();
                var stringIndexMap = new Dictionary<string, int>(StringComparer.Ordinal);

                int GetStringIndex(string str)
                {
                    var cleanStr = SanitizeForXml(str);
                    if (stringIndexMap.TryGetValue(cleanStr, out var idx)) return idx;
                    idx = sharedStrings.Count;
                    sharedStrings.Add(cleanStr);
                    stringIndexMap[cleanStr] = idx;
                    return idx;
                }

                foreach (var r in allRows)
                {
                    foreach (var c in r)
                    {
                        if (!string.IsNullOrEmpty(c))
                        {
                            GetStringIndex(c);
                        }
                    }
                }

                // 5. xl/sharedStrings.xml
                var sharedStringsEntry = archive.CreateEntry("xl/sharedStrings.xml");
                using (var writer = new StreamWriter(sharedStringsEntry.Open(), Encoding.UTF8))
                {
                    writer.Write($@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<sst xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" count=""{sharedStrings.Count}"" uniqueCount=""{sharedStrings.Count}"">");
                    foreach (var s in sharedStrings)
                    {
                        string escaped = SecurityElement.Escape(s) ?? "";
                        writer.Write($"<si><t>{escaped}</t></si>");
                    }
                    writer.Write("</sst>");
                }

                // 列序号转换为字母标识 (0 -> A, 25 -> Z, 26 -> AA)
                static string GetColumnName(int columnIndex)
                {
                    int dividend = columnIndex + 1;
                    string columnName = string.Empty;
                    while (dividend > 0)
                    {
                        int modulo = (dividend - 1) % 26;
                        columnName = Convert.ToChar(65 + modulo) + columnName;
                        dividend = (dividend - modulo) / 26;
                    }
                    return columnName;
                }

                // 6. xl/worksheets/sheet1.xml
                var sheetEntry = archive.CreateEntry("xl/worksheets/sheet1.xml");
                using (var writer = new StreamWriter(sheetEntry.Open(), Encoding.UTF8))
                {
                    writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<worksheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
  <sheetData>");
                    for (int r = 0; r < allRows.Count; r++)
                    {
                        int rowNum = r + 1;
                        writer.Write($@"<row r=""{rowNum}"">");
                        var rowData = allRows[r];
                        for (int c = 0; c < rowData.Count; c++)
                        {
                            var cellVal = rowData[c];
                            if (string.IsNullOrEmpty(cellVal)) continue;
                            string colName = GetColumnName(c);
                            string cellRef = $"{colName}{rowNum}";
                            int strIdx = GetStringIndex(cellVal);
                            writer.Write($@"<c r=""{cellRef}"" t=""s""><v>{strIdx}</v></c>");
                        }
                        writer.Write("</row>");
                    }
                    writer.Write(@"</sheetData></worksheet>");
                }
            }
            return ms.ToArray();
        }

        public static List<(int RowNumber, List<string> Cells)> ReadSpreadsheet(Stream stream)
        {
            var rawLines = new List<(int RowNumber, List<string> Cells)>();

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;

            using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);

            // 1. 读取共享字符串池
            var sharedStrings = new List<string>();
            var sharedStringsEntry = archive.GetEntry("xl/sharedStrings.xml");
            if (sharedStringsEntry != null)
            {
                using var sstStream = sharedStringsEntry.Open();
                var xdoc = XDocument.Load(sstStream);
                foreach (var si in xdoc.Descendants().Where(d => d.Name.LocalName == "si"))
                {
                    var textNodes = si.Descendants().Where(d => d.Name.LocalName == "t").Select(d => d.Value);
                    sharedStrings.Add(string.Concat(textNodes));
                }
            }

            // 2. 定位工作表
            var sheetEntry = archive.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                             ?? archive.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

            if (sheetEntry == null)
            {
                throw new InvalidDataException("Excel 文件缺少有效的工作表 (Worksheet)！");
            }

            using (var sheetStream = sheetEntry.Open())
            {
                var sheetDoc = XDocument.Load(sheetStream);
                var rows = sheetDoc.Descendants().Where(d => d.Name.LocalName == "row");

                foreach (var rowElem in rows)
                {
                    int rowNumber = int.TryParse(rowElem.Attribute("r")?.Value, out var rn) ? rn : (rawLines.Count + 1);

                    var cellDict = new Dictionary<int, string>();
                    foreach (var cellElem in rowElem.Elements().Where(e => e.Name.LocalName == "c"))
                    {
                        string cellRef = cellElem.Attribute("r")?.Value ?? "";
                        int colIndex = 0;
                        if (!string.IsNullOrEmpty(cellRef))
                        {
                            string colLetters = new string(cellRef.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
                            int colAccum = 0;
                            foreach (char ch in colLetters)
                            {
                                colAccum = colAccum * 26 + (ch - 'A' + 1);
                            }
                            colIndex = colAccum - 1; // 0-based
                        }

                        string typeAttr = cellElem.Attribute("t")?.Value ?? "";
                        string val = "";

                        if (typeAttr == "s")
                        {
                            var vElem = cellElem.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
                            if (vElem != null && int.TryParse(vElem.Value, out var sIndex) && sIndex >= 0 && sIndex < sharedStrings.Count)
                            {
                                val = sharedStrings[sIndex];
                            }
                        }
                        else if (typeAttr == "inlineStr")
                        {
                            var tNodes = cellElem.Descendants().Where(d => d.Name.LocalName == "t").Select(d => d.Value);
                            val = string.Concat(tNodes);
                        }
                        else if (typeAttr == "b")
                        {
                            var vElem = cellElem.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
                            val = vElem?.Value == "1" ? "True" : (vElem?.Value == "0" ? "False" : (vElem?.Value ?? ""));
                        }
                        else if (typeAttr == "str")
                        {
                            var vElem = cellElem.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
                            val = vElem?.Value ?? "";
                        }
                        else
                        {
                            var vElem = cellElem.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
                            val = vElem?.Value ?? "";
                        }

                        cellDict[colIndex] = val.Trim();
                    }

                    if (cellDict.Count == 0) continue;

                    int maxCol = cellDict.Keys.Max();
                    var fields = new List<string>();
                    for (int i = 0; i <= maxCol; i++)
                    {
                        fields.Add(cellDict.TryGetValue(i, out var cVal) ? cVal : "");
                    }

                    rawLines.Add((rowNumber, fields));
                }
            }

            return rawLines;
        }

        /// <summary>
        /// 过滤 XML 1.0 规范不允许的非法字符（如 0x00-0x08, 0x0B-0x0C, 0x0E-0x1F 等控制字符），防止 Excel 解析崩溃
        /// </summary>
        public static string SanitizeForXml(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                // XML 1.0 合法字符: 0x9 (\t), 0xA (\n), 0xD (\r), 0x20-0xD7FF, 0xE000-0xFFFD, 代理对
                if (c == 0x9 || c == 0xA || c == 0xD || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
                {
                    sb.Append(c);
                }
                else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    // 支持合法的 UTF-16 补充字符/Emoji
                    sb.Append(c);
                    sb.Append(text[++i]);
                }
            }
            return sb.ToString();
        }
    }
}
