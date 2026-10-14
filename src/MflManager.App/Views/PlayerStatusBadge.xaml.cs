using MflManager.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MflManager.App.Views;

public sealed partial class PlayerStatusBadge : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(PlayerStatusBadge), new PropertyMetadata(string.Empty,
            (sender, _) => ((PlayerStatusBadge)sender).UpdateStatus()));

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public PlayerStatusBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (BadgeBorder is null) return;
        BadgeBorder.Visibility = string.IsNullOrWhiteSpace(Status) ? Visibility.Collapsed : Visibility.Visible;
        VisualStateManager.GoToState(this, PlayerAvailability.Tone(Status).ToString(), false);
    }
}
