using System.Windows;

namespace Booking;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private SettingsWindow? _settingsWindow;
    private AccountFormWindow? _accountFormWindow;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        vm.BookingJobLauncherRequested += OpenJobLauncher;
        vm.BookingSetupRequested += OpenBookingSetup;
        vm.DeleteAccountConfirmationRequested += ConfirmDeleteAccount;
        vm.SettingsRequested += OpenSettingsWindow;
        vm.AccountFormRequested += OpenAccountFormWindow;
        vm.AccountFormCloseRequested += CloseAccountFormWindow;
        DataContext = vm;
    }

    private void OpenJobLauncher(JobLauncherViewModel viewModel)
    {
        var dialog = new JobLauncherWindow(viewModel)
        {
            Owner = this
        };
        dialog.Show();
    }

    private void OpenBookingSetup(BookingSetupViewModel viewModel)
    {
        var dialog = new BookingSetupWindow(viewModel)
        {
            Owner = this
        };
        dialog.Show();
    }

    private void OpenSettingsWindow()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(vm)
        {
            Owner = this
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OpenAccountFormWindow()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (_accountFormWindow is { IsLoaded: true })
        {
            _accountFormWindow.Close();
            _accountFormWindow = null;
        }

        _accountFormWindow = new AccountFormWindow(vm)
        {
            Owner = this
        };
        _accountFormWindow.Closed += (_, _) => _accountFormWindow = null;
        _accountFormWindow.Show();
    }

    private void CloseAccountFormWindow()
    {
        _accountFormWindow?.Close();
    }

    private bool ConfirmDeleteAccount(string username) =>
        MessageBox.Show(
            this,
            $"Are you sure you want to delete account \"{username}\"?\n\nThis cannot be undone.",
            "Delete Account",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
}