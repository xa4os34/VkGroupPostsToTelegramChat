using Microsoft.EntityFrameworkCore;

namespace VkGroupPostsToTelegramChat.Database;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }
}
