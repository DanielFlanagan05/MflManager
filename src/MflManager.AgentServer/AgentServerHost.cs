using MflManager.Application;
using MflManager.MflApi;
using MflManager.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MflManager.AgentServer;

public static class AgentServerHost
{
    public static async Task RunStandaloneAsync(string[] args, CancellationToken cancellationToken)
    {
        var pipe = args.FirstOrDefault(x => x.StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1] ?? "mfl-manager-agent";
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MflManager", "mfl-manager.db");
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddSingleton<ILeagueState, LeagueState>();
        builder.Services.AddSingleton<IConfirmationPolicy, ConfirmationPolicy>();
        builder.Services.AddSingleton<IMflCookieStore, InMemoryMflCookieStore>();
        builder.Services.AddMflPersistence(data);
        builder.Services.AddMflApi();
        builder.Services.AddSingleton<MflToolRegistry>();
        builder.Services.AddSingleton(sp => new MflAgentPipeServer(pipe, sp.GetRequiredService<MflToolRegistry>()));
        using var host = builder.Build();
        await host.Services.GetRequiredService<MflAgentPipeServer>().RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
