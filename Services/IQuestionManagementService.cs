using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Northtropic.Models;

namespace Northtropic.Services
{
    public interface IQuestionManagementService
    {
        Task<List<Question>> GetQuestionsForManagementAsync(Guid currentUserId, string? subject = null, string? category = null, bool? isPublic = null, PublishStatusEnum? status = null, IEnumerable<Guid>? specificQuestionIds = null, QuestionType? questionType = null, int? difficulty = null);
        Task<Question?> GetQuestionByIdAsync(Guid id);
        Task<(bool Success, string Message, Question? Question)> AddQuestionAsync(Question question, Guid? userId = null);
        Task<bool> UpdateQuestionAsync(Question question, Guid? userId = null);
        Task<bool> DeleteQuestionAsync(Guid id, Guid? userId = null);
        Task<bool> ApplyForPublicPublishAsync(Guid questionId, Guid currentUserId);
        Task<bool> ApprovePublishRequestAsync(Guid questionId, Guid adminUserId);
        Task<bool> RejectPublishRequestAsync(Guid questionId);
        Task<bool> DirectPublishAsync(Guid questionId, Guid userId);
        Task<bool> RetractToPrivateAsync(Guid questionId, Guid userId);
        Task<string> ExportQuestionsJsonAsync(Guid currentUserId, string? subject = null, string? category = null, bool? isPublic = null, PublishStatusEnum? status = null, IEnumerable<Guid>? specificQuestionIds = null);
        Task<byte[]> ExportQuestionsCsvAsync(Guid currentUserId, string? subject = null, string? category = null, bool? isPublic = null, PublishStatusEnum? status = null, IEnumerable<Guid>? specificQuestionIds = null);
        Task<byte[]> ExportQuestionsXlsxAsync(Guid currentUserId, string? subject = null, string? category = null, bool? isPublic = null, PublishStatusEnum? status = null, IEnumerable<Guid>? specificQuestionIds = null);
        Task<int> BatchDeleteQuestionsAsync(IEnumerable<Guid> ids, Guid userId);
        Task<int> BatchDirectPublishAsync(IEnumerable<Guid> ids, Guid userId);
        Task<(int TotalQuestions, int PublicQuestions, int MyQuestions)> GetQuestionStatisticsAsync(Guid? userId = null);
    }
}
