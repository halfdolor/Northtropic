using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Northtropic.Models;

namespace Northtropic.Services
{
    public interface IErrorBookService
    {
        Task<List<ErrorItem>> GetUnmasteredErrorsAsync(Guid? targetUserId = null, Guid? requestorUserId = null);
        Task<List<ErrorItem>> GetMasteredErrorsAsync(Guid? targetUserId = null, Guid? requestorUserId = null);
        Task UpdateErrorReasonAsync(Guid errorItemId, string category, Guid? userId = null);
        Task<RewardResult> MarkErrorAsMasteredAsync(Guid errorItemId, Guid? userId = null);
        Task<BatchMarkMasteredResult> BatchMarkErrorsAsMasteredAsync(IEnumerable<Guid> errorItemIds, Guid? userId = null);
        Task<bool> DeleteErrorItemAsync(Guid errorItemId, Guid? userId = null);
        Task<int> BatchDeleteErrorItemsAsync(IEnumerable<Guid> errorItemIds, Guid? userId = null);
        Task<int> ClearMasteredErrorsAsync(Guid? userId = null);
        Task<int> GetUnmasteredCountAsync(Guid? userId = null);

        // 艾宾浩斯抗遗忘记忆曲线领域接口
        TimeSpan GetRecommendedReviewInterval(int revisionCount);
        bool IsReviewDue(ErrorItem item, DateTime? asOf = null);
        double CalculateRetentionHealthScore(ErrorItem item, DateTime? asOf = null);
        Task<List<ErrorItem>> GetEbbinghausReviewQueueAsync(Guid userId, string? subject = null, int count = 10);
        Task<RewardResult> ReviseErrorAsync(Guid errorItemId, bool isCorrect, Guid? userId = null, bool debounce = false);
        Task<ErrorItem?> GetErrorItemByQuestionAsync(Guid userId, Guid questionId);
    }

    public class BatchMarkMasteredResult
    {
        public int MarkedCount { get; set; }
        public int EarnedExp { get; set; }
        public int EarnedCoins { get; set; }
    }
}
