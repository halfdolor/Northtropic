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
    }
}
