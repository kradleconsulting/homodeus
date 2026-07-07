using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TelegramClaudeBot.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddHttpClient();
        services.AddSingleton<RateLimiterService>();
        services.AddSingleton<ClaudeService>();
        services.AddSingleton<TelegramSenderService>();
    })
    .Build();

host.Run();
