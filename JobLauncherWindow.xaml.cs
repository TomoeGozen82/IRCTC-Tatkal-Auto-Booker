using System.Windows;

namespace Booking;

public partial class JobLauncherWindow : Window
{
    public JobLauncherWindow(JobLauncherViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += () => Close();
    }
}
