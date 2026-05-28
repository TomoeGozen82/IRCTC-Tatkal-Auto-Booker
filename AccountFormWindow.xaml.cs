using System.Windows;

namespace Booking;

public partial class AccountFormWindow : Window
{
    public AccountFormWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void AccountPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is System.Windows.Controls.PasswordBox pb)
        {
            vm.AccountForm.PlainPassword = pb.Password;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
