using System.ComponentModel.DataAnnotations;

namespace VkGroupPostsToTelegramChat.Configuration;

public class DatabaseConfiguration
{
    public const string SectionName = "Database";

    [EnumDataType(typeof(DatabaseType))]
    public DatabaseType DatabaseType { get; set; }

    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    public string InMemoryDatabaseName { get; set; } = "InMemoryDatbase";
}

public enum DatabaseType 
{
    SqlServer,
    SqLite,
    InMemory,
}