using MflManager.App.ViewModels;
using MflManager.Application;
using MflManager.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MflManager.App;

public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; } = App.Services.GetRequiredService<MainPageViewModel>();
    private OnePasswordCredentialProvider OnePassword { get; } = App.Services.GetRequiredService<OnePasswordCredentialProvider>();

    public MainPage()
    {
        InitializeComponent();
        ViewModel.AgentMessages.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.AgentMessages.Count > 0) AgentChat.ScrollIntoView(ViewModel.AgentMessages[^1]);
        });
        ViewModel.ConfirmWaiverAsync = async text => await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Confirm blind-bid waiver claim", Content = text,
            PrimaryButtonText = "Submit bid", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        }.ShowAsync() == ContentDialogResult.Primary;
        ViewModel.ConfirmCancelWaiverAsync = async text => await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Cancel waiver claim", Content = text,
            PrimaryButtonText = "Cancel claim", CloseButtonText = "Keep claim", DefaultButton = ContentDialogButton.Close
        }.ShowAsync() == ContentDialogResult.Primary;
        ViewModel.ConfirmReorderWaiverAsync = async text => await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Change waiver priority", Content = text,
            PrimaryButtonText = "Save order", CloseButtonText = "Keep current order", DefaultButton = ContentDialogButton.Close
        }.ShowAsync() == ContentDialogResult.Primary;
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility InvertBoolToVisibility(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility BothBoolToVisibility(bool first, bool second) =>
        first && second ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility InvertEitherBoolToVisibility(bool first, bool second) =>
        first || second ? Visibility.Collapsed : Visibility.Visible;
    public static bool IsNotBusy(bool value) => !value;
    public static Visibility AgentVisibility(string page) => page == "Agent" ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility NonAgentVisibility(string page) => page == "Agent" ? Visibility.Collapsed : Visibility.Visible;

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? "Settings" : (args.SelectedItemContainer?.Tag as string ?? "Dashboard");
        await ViewModel.SetPageAsync(tag, CancellationToken.None);
    }

    private async void LineupWeekSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: int week })
            await ViewModel.ChangeLineupWeekAsync(week, CancellationToken.None);
    }

    private async void LeagueSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LeagueSelector.SelectedItem is LeagueSummary league && league != ViewModel.SelectedLeague)
            await ViewModel.SelectLeagueAsync(league, CancellationToken.None);
    }

    private async void LeagueRankingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView { SelectedItem: LeagueRankingRow team } list) return;
        list.SelectedItem = null;
        await ViewModel.OpenTeamViewAsync(team, CancellationToken.None);
    }

    private async void TeamView_BackRequested(object? sender, EventArgs e) =>
        await ViewModel.ReturnFromTeamViewAsync(CancellationToken.None);

    private static IEnumerable<Button> PendingWaiverHoverButtons(object sender) =>
        sender is Grid row ? row.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Button>())
            .Where(button => button.Tag as string == "HoverAction") : [];

    private async void PendingWaiverClaims_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move) return;
        await ViewModel.ReorderPendingWaiversAsync(ViewModel.PendingWaivers.Select(row => row.Claim).ToArray());
    }

    private void PendingWaiverRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        foreach (var button in PendingWaiverHoverButtons(sender)) button.Opacity = 1;
    }

    private void PendingWaiverRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        foreach (var button in PendingWaiverHoverButtons(sender))
            if (button.FocusState == FocusState.Unfocused) button.Opacity = 0;
    }

    private void PendingWaiverCancel_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.Opacity = 1;
    }

    private void PendingWaiverCancel_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.Opacity = 0;
    }

    private void IntegerBid_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        args.Cancel = args.NewText.Any(character => character is < '0' or > '9');
    }

    private void GlobalSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) ViewModel.Filter(sender.Text);
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var username = new TextBox { Header = "MFL username", PlaceholderText = "Username" };
        var password = new PasswordBox { Header = "MFL password", PlaceholderText = "Password" };
        var remember = new CheckBox { Content = "Remember this account securely", IsChecked = true };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(username, "MflUsername");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(password, "MflPassword");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(remember, "RememberAccount");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(username); content.Children.Add(password); content.Children.Add(remember);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Sign in to MyFantasyLeague",
            PrimaryButtonText = "Sign in",
            SecondaryButtonText = "Load from 1Password",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content
        };
        while (true)
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.LoginAsync(username.Text, password.Password, remember.IsChecked == true, CancellationToken.None);
                break;
            }
            if (result != ContentDialogResult.Secondary) break;
            var credentials = await OnePassword.GetAsync("Myfantasyleague", CancellationToken.None);
            if (credentials is null)
            {
                ViewModel.StatusMessage = "1Password item 'Myfantasyleague' could not be read. Unlock 1Password and verify the CLI is installed.";
                ViewModel.IsStatusOpen = true;
                break;
            }
            username.Text = credentials.Username;
            password.Password = credentials.Password;
        }
        password.Password = string.Empty;
    }

    private void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var command = ViewModel.IsLeagueRankingsPage ? ViewModel.RefreshLeagueRankingsCommand : ViewModel.RefreshCommand;
        if (command.CanExecute(null)) command.Execute(null);
        args.Handled = true;
    }
    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { GlobalSearch.Focus(FocusState.Keyboard); args.Handled = true; }
    private void AgentAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(x => string.Equals(x.Tag as string, "Agent", StringComparison.Ordinal));
        args.Handled = true;
    }
    private void NavigationAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var index = sender.Key switch
        {
            Windows.System.VirtualKey.Number1 => 0, Windows.System.VirtualKey.Number2 => 1,
            Windows.System.VirtualKey.Number3 => 2, Windows.System.VirtualKey.Number4 => 3,
            Windows.System.VirtualKey.Number5 => 4, Windows.System.VirtualKey.Number6 => 5,
            Windows.System.VirtualKey.Number7 => 6, Windows.System.VirtualKey.Number8 => 7,
            Windows.System.VirtualKey.Number9 => 8, _ => -1
        };
        if (index >= 0 && index < Navigation.MenuItems.Count) Navigation.SelectedItem = Navigation.MenuItems[index];
        args.Handled = index >= 0;
    }
    private void NextAreaAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var current = Navigation.MenuItems.IndexOf(Navigation.SelectedItem);
        Navigation.SelectedItem = Navigation.MenuItems[(current + 1) % Navigation.MenuItems.Count];
        args.Handled = true;
    }
    private async void SendTerminalInput_Click(object sender, RoutedEventArgs e) => await SendTerminalInputAsync();
    private async void TerminalInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        e.Handled = true;
        await SendTerminalInputAsync();
    }
    private async Task SendTerminalInputAsync()
    {
        var text = TerminalInput.Text;
        if (string.IsNullOrWhiteSpace(text) || ViewModel.IsAgentWorking) return;
        TerminalInput.Text = string.Empty;
        await ViewModel.SendTerminalAsync(text, CancellationToken.None);
    }
}
