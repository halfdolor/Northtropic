using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;

namespace Northtropic.Tests
{
    public static class TestDbContextFactory
    {
        public static (AppDbContext Context, SqliteConnection Connection) CreateInMemoryContext()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            var context = new AppDbContext(options);
            context.Database.EnsureCreated();

            return (context, connection);
        }
    }
}
