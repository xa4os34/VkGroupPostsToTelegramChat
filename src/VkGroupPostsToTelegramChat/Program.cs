using Serilog;
using VkGroupPostsToTelegramChat.Configuration;
using VkGroupPostsToTelegramChat.Database;
using VkNet;
using VkNet.Abstractions;
using VkNet.Utils;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("Starting web application");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    IServiceCollection services = builder.Services;
    IConfiguration configuration = builder.Configuration;

    services.AddOptions<DatabaseConfiguration>()
        .Bind(configuration.GetSection(DatabaseConfiguration.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services.AddDbContext<ApplicationDbContext>((provider, options) => 
        options.UseDatabaseConfiguration(
            provider.GetRequiredService<DatabaseConfiguration>()));

    services.AddMediatR(options => 
        options.RegisterServicesFromAssembly(typeof(Program).Assembly));

    services.AddControllers();

    RateLimiter limiter = new RateLimiter(
        new CountByIntervalAwaitableConstraint(
            number: 20, timeSpan: TimeSpan.FromSeconds(1)));

    var vkApi = new VkApi(builder.Services);

    services.AddSingleton<IVkApi, VkApi>(_ => vkApi);


    var app = builder.Build();

    app.UseAuthorization();

    app.MapControllers();

    app.Run();

}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}