using MflManager.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MflManager.App.Views;

public sealed partial class TeamView : UserControl
{
    public TeamViewViewModel ViewModel { get; } = App.Services.GetRequiredService<TeamViewViewModel>();

    public event EventHandler? BackRequested;

    public TeamView() => InitializeComponent();

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
