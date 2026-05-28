using System.Windows;

namespace Booking;

public partial class BookingSetupWindow : Window
{
    public BookingSetupWindow(BookingSetupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(bool result)
    {
        Close();
    }
}

