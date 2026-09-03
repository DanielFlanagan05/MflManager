using MflManager.AgentServer;
using MflManager.Application;
using MflManager.MflApi;
using MflManager.Persistence;
using MflManager.Terminal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;

namespace MflManager.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private readonly IHost _host;
    public static Window Window { get; private set; } = null!;
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;
    public static IServiceProvider Services { get; private set; } = null!;
    public static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public App()
    {
        InitializeComponent();
        var builder = Host.CreateApplicationBuilder();
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MflManager");
        builder.Services.AddSingleton<ILeagueState, LeagueState>();
        builder.Services.AddSingleton<IConfirmationPolicy, ConfirmationPolicy>();
        builder.Services.AddSingleton<IMflCookieStore, WindowsCredentialCookieStore>();
        builder.Services.AddSingleton<ISecureSessionStore, WindowsCredentialSessionStore>();
        builder.Services.AddSingleton<WindowsCredentialLoginStore>();
        builder.Services.AddSingleton<OnePasswordCredentialProvider>();
        builder.Services.AddMflPersistence(Path.Combine(appData, "mfl-manager.db"));
        builder.Services.AddMflApi();
        builder.Services.AddSingleton<ICodexTerminalService, CodexTerminalService>();
        builder.Services.AddSingleton<MflToolRegistry>();
        builder.Services.AddSingleton(sp => new MflAgentPipeServer("mfl-manager-agent", sp.GetRequiredService<MflToolRegistry>()));
        builder.Services.AddSingleton<ViewModels.MainPageViewModel>();
        _host = builder.Build();
        Services = _host.Services;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await _host.StartAsync();
        await Services.GetRequiredService<MflAgentPipeServer>().StartAsync(CancellationToken.None);
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
        await Services.GetRequiredService<ViewModels.MainPageViewModel>().TryWindowsCredentialLoginAsync(CancellationToken.None);
    }
}
