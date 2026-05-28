using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Booking.Domain;
using DomainBookingProfile = Booking.Domain.BookingProfile;
using DomainIrctcAccount = Booking.Domain.IrctcAccount;

namespace Booking;

public partial class JobLauncherViewModel : ObservableObject
{
    [ObservableProperty] private DomainBookingProfile? selectedProfile;

    public JobLauncherViewModel(IEnumerable<DomainIrctcAccount> accounts, IEnumerable<DomainBookingProfile> profiles)
    {
        AccountSelections = new ObservableCollection<LauncherAccountItem>(
            accounts.Select(a => new LauncherAccountItem { Account = a, IsSelected = a.IsEnabled }));
        Profiles = new ObservableCollection<DomainBookingProfile>(profiles);
        SelectedProfile = Profiles.FirstOrDefault();
    }

    public ObservableCollection<LauncherAccountItem> AccountSelections { get; }
    public ObservableCollection<DomainBookingProfile> Profiles { get; }

    public event Action<DomainBookingProfile, List<DomainIrctcAccount>>? LaunchRequested;
    public event Action? CloseRequested;

    [RelayCommand]
    private void Launch()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var selected = AccountSelections.Where(a => a.IsSelected).Select(a => a.Account).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        LaunchRequested?.Invoke(SelectedProfile, selected);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}

public partial class LauncherAccountItem : ObservableObject
{
    [ObservableProperty] private bool isSelected;
    public required DomainIrctcAccount Account { get; init; }
}
