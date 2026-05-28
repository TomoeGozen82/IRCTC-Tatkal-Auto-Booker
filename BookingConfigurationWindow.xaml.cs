using System.Windows;

namespace Booking;

public partial class BookingConfigurationWindow : Window
{
    public BookingConfigurationWindow(BookingConfigurationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(bool result)
    {
        Close();
    }
}
