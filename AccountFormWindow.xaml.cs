using System.Windows;

namespace Booking;

public partial class AccountFormWindow : Window
{
    public AccountFormWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
