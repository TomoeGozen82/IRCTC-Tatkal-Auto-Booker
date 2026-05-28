using System.Windows;

namespace Booking;

public partial class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
