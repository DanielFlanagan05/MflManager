using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MflManager.App.Views;

public sealed partial class PlayerNameBadge : UserControl
{
    public static readonly DependencyProperty PlayerNameProperty = DependencyProperty.Register(
        nameof(PlayerName), typeof(string), typeof(PlayerNameBadge), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(PlayerNameBadge), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty IsStarterProperty = DependencyProperty.Register(
        nameof(IsStarter), typeof(bool), typeof(PlayerNameBadge), new PropertyMetadata(false,
            (sender, _) => ((PlayerNameBadge)sender).UpdateRosterState()));

    public string PlayerName
    {
        get => (string)GetValue(PlayerNameProperty);
        set => SetValue(PlayerNameProperty, value);
    }
    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
    public bool IsStarter
    {
        get => (bool)GetValue(IsStarterProperty);
        set => SetValue(IsStarterProperty, value);
    }

    public PlayerNameBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateRosterState();
    }

    private void UpdateRosterState()
    {
        VisualStateManager.GoToState(this, IsStarter ? "Starter" : "Bench", false);
        ToolTipService.SetToolTip(this, IsStarter ? "Starting lineup" : "Bench");
    }
}
