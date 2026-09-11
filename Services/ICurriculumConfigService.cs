using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Northtropic.Models;

namespace Northtropic.Services
{
    public interface ICurriculumConfigService
    {
        Task EnsureInitializedAsync();
        List<string> GetAvailableGrades();
        Task<List<string>> GetSubjectsByGradeAsync(string grade);
        Task<List<string>> GetCategoriesBySubjectAsync(string subject);
        Task<List<CurriculumSubjectConfig>> GetAllConfigsAsync();
        Task<List<CurriculumSubjectConfig>> GetConfigsByGradeAsync(string grade);
        Task<(bool Success, string Message)> SaveSubjectTopicsAsync(Guid configId, List<string> topics, Guid? userId = null);
        Task<(bool Success, string Message)> AddSubjectConfigAsync(string grade, string subject, List<string> topics, Guid? userId = null);
        Task<(bool Success, string Message)> DeleteSubjectConfigAsync(Guid configId, Guid? userId = null);
        Task<(bool Success, string Message)> ResetToDefaultCurriculumAsync(Guid? userId = null);
        event Action? OnCurriculumChanged;
    }
}
