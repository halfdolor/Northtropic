using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Northtropic.Data
{
    /// <summary>
    /// 系统架构级统一的 DbContext 生命周期管理结构体。
    /// 无论通过 IDbContextFactory 还是直接注入的 AppDbContext，均能以统一、零内存分配的方式管理生命周期与事务。
    /// </summary>
    public readonly struct AsyncDbScope : IAsyncDisposable, IDisposable
    {
        private readonly AppDbContext? _ownedContext;

        public AppDbContext Context { get; }

        public AsyncDbScope(AppDbContext context, AppDbContext? ownedContext = null)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            _ownedContext = ownedContext;
        }

        /// <summary>
        /// 优先使用 IDbContextFactory 创建独立的上下文实例；若工厂为空，则安全回退到 fallbackContext。
        /// </summary>
        public static async ValueTask<AsyncDbScope> CreateAsync(
            IDbContextFactory<AppDbContext>? factory, 
            AppDbContext fallbackContext)
        {
            if (factory != null)
            {
                var ctx = await factory.CreateDbContextAsync().ConfigureAwait(false);
                return new AsyncDbScope(ctx, ctx);
            }

            if (fallbackContext == null)
            {
                throw new InvalidOperationException("DbContext factory is null and fallback context is null.");
            }

            return new AsyncDbScope(fallbackContext, null);
        }

        /// <summary>
        /// 开启当前作用域下的数据库事务。
        /// </summary>
        public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            return Context.Database.BeginTransactionAsync(cancellationToken);
        }

        /// <summary>
        /// 异步释放所持有的上下文实例 (仅释放由本 Scope 所创建的独立上下文)。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_ownedContext != null)
            {
                await _ownedContext.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 同步释放所持有的上下文实例。
        /// </summary>
        public void Dispose()
        {
            if (_ownedContext != null)
            {
                _ownedContext.Dispose();
            }
        }

        /// <summary>
        /// 针对 SQLite 并发繁忙/写锁冲突 (SQLITE_BUSY / SQLITE_LOCKED) 提供的自适应重试弹性执行策略。
        /// 采用指数退避加随机抖动算法，提升并发事务与密集写场景下的鲁棒性。
        /// </summary>
        public static async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> operation,
            int maxRetries = 3,
            int initialDelayMs = 50,
            CancellationToken cancellationToken = default)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < maxRetries && IsTransientSqliteLockException(ex))
                {
                    attempt++;
                    int delay = initialDelayMs * (int)Math.Pow(2, attempt - 1) + Random.Shared.Next(10, 30);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 针对无返回值的数据库操作提供自适应重试弹性执行策略。
        /// </summary>
        public static async Task ExecuteWithRetryAsync(
            Func<Task> operation,
            int maxRetries = 3,
            int initialDelayMs = 50,
            CancellationToken cancellationToken = default)
        {
            await ExecuteWithRetryAsync(async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            }, maxRetries, initialDelayMs, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 递归探测异常及其内部异常链中是否包含 SQLite 锁冲突 (5: SQLITE_BUSY, 6: SQLITE_LOCKED) 或 PostgreSQL 瞬时重试异常。
        /// </summary>
        public static bool IsTransientSqliteLockException(Exception? ex)
        {
            if (ex == null) return false;

            if (ex is Microsoft.Data.Sqlite.SqliteException sqliteEx)
            {
                return sqliteEx.SqliteErrorCode == 5 || sqliteEx.SqliteErrorCode == 6;
            }

            if (ex is Npgsql.NpgsqlException npgsqlEx && npgsqlEx.IsTransient)
            {
                return true;
            }

            if (ex is System.Net.Sockets.SocketException || ex is TimeoutException)
            {
                return true;
            }

            if (ex is DbUpdateException dbUpdateEx && dbUpdateEx.InnerException != null)
            {
                return IsTransientSqliteLockException(dbUpdateEx.InnerException);
            }

            if (ex.InnerException != null)
            {
                return IsTransientSqliteLockException(ex.InnerException);
            }

            return false;
        }
    }
}

