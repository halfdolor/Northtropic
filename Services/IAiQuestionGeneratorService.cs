using System.Threading.Tasks;
using Northtropic.Models;

namespace Northtropic.Services
{
    public interface IAiQuestionGeneratorService
    {
        Task<Question> GenerateQuestionByGradeAsync(string grade, string subject, string? category = null);
        Task<List<Question>> GenerateBatchQuestionsAsync(string grade, string subject, string? category = null, int count = 5);
    }
}
