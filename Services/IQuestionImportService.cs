using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Northtropic.Models;

namespace Northtropic.Services
{
    public class QuestionImportResult
    {
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public int DuplicateCount { get; set; }
        public bool IsDryRun { get; set; } = false;
        public bool IsSuccess => FailureCount == 0 && SuccessCount > 0;
        public List<string> ErrorMessages { get; set; } = new List<string>();
        public List<Question> ImportedQuestions { get; set; } = new List<Question>();
    }

    public class PhotoOcrRecognizeResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public Question? ParsedQuestion { get; set; }
        public string RawOcrText { get; set; } = string.Empty;
        public string OcrProviderUsed { get; set; } = "BaiduOcr";
        public bool IsTokenSaved { get; set; } = true;
    }

    public interface IQuestionImportService
    {
        byte[] GenerateCsvTemplateBytes();
        byte[] GenerateJsonTemplateBytes();
        byte[] GenerateTxtTemplateBytes();
        byte[] GenerateXlsxTemplateBytes();

        Task<QuestionImportResult> ParseAndImportQuestionsAsync(Stream stream, Guid? userId = null);
        Task<QuestionImportResult> ParseAndImportQuestionsByFileFormatAsync(Stream stream, string fileName, Guid? userId = null, bool dryRun = false);
        Task<QuestionImportResult> ValidateQuestionsFileAsync(Stream stream, string fileName, Guid? userId = null);
        Task<QuestionImportResult> ParseXlsxImportAsync(Stream stream, Guid? userId = null, bool dryRun = false);
        
        Task<PhotoOcrRecognizeResult> RecognizeQuestionFromPhotoAsync(byte[] imageBytes, string fileName, string? defaultSubject = null, string? defaultGrade = null, string? explicitProvider = null);
        Task<string> RecognizeHandwritingTextAsync(byte[] imageBytes, string? subject = null);
        Task<TestConnectionResult> TestBaiduOcrConnectionAsync(string apiKey, string secretKey, string? endpoint = null);
        Task<TestConnectionResult> TestLocalPaddleOcrStatusAsync();
    }
}
