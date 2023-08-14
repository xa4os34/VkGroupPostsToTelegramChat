using Microsoft.EntityFrameworkCore;
using VkGroupPostsToTelegramChat.Configuration;

namespace VkGroupPostsToTelegramChat.Database;

public static class DbContextOptionsBuilderExtensions
{
    public static DbContextOptionsBuilder UseDatabaseConfiguration(
        this DbContextOptionsBuilder optionsBuilder,
        DatabaseConfiguration configuration)
    {
        switch (configuration.DatabaseType)
        {
            case DatabaseType.SqlServer:
                optionsBuilder.UseSqlServer(configuration.ConnectionString);
                break;
            
            case DatabaseType.SqLite:
                optionsBuilder.UseSqlite(configuration.ConnectionString);
                break;

            case DatabaseType.InMemory:
                optionsBuilder.UseInMemoryDatabase(configuration.InMemoryDatabaseName);
                break;
        }
        return optionsBuilder;
    }
}