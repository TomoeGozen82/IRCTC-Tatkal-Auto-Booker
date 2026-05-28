using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Booking.Domain;
using DomainBookingProfile = Booking.Domain.BookingProfile;

namespace Booking;

public partial class AccountViewModel : ObservableObject
{
    public IRelayCommand BookCommand { get; }

    public AccountViewModel(IrctcAccount model, Action<AccountViewModel> bookRequested)
    {
        Model = model;
        BookCommand = new RelayCommand(() => bookRequested(this));
    }

    public IrctcAccount Model { get; }
    public string Username => Model.Username;
    public string Password => PasswordCrypto.Decrypt(Model.EncryptedPassword);
    public string Proxy => Model.ProxyConfig.Address;
    public string CaptchaProvider => Model.CaptchaSettings.Provider;
    public SessionState SessionState => Model.SessionState;
    public bool IsEnabled => Model.IsEnabled;
}

public partial class BookingProfileViewModel : ObservableObject
{
    public BookingProfileViewModel(DomainBookingProfile model) => Model = model;
    public DomainBookingProfile Model { get; }
    public string ProfileName => Model.ProfileName;
    public string Route => $"{Model.FromStation} -> {Model.ToStation}";
    public string PreferredTrainNumbers => Model.PreferredTrainNumbers;
    public string ClassPriority => Model.ClassPriority;
    public int PassengerCount => Model.PassengerList.Count;
}

public partial class BookingJobViewModel : ObservableObject
{
    [ObservableProperty] private JobRuntimeStatus runtimeStatus;

    public BookingJobViewModel(BookingJob model)
    {
        Model = model;
        runtimeStatus = model.RuntimeStatus;
    }

    public BookingJob Model { get; }
    public string Account => Model.Account.Username;
    public string Profile => Model.BookingProfile.ProfileName;
    public DateTime StartTime => Model.StartTime;
    public string RuntimeStatusText => RuntimeStatus.ToString();

    partial void OnRuntimeStatusChanged(JobRuntimeStatus value) => OnPropertyChanged(nameof(RuntimeStatusText));
}

public sealed class AppStateSnapshot
{
    public List<IrctcAccount> Accounts { get; set; } = [];
    public List<DomainBookingProfile> BookingProfiles { get; set; } = [];
    public SchedulerSettings SchedulerSettings { get; set; } = new();
    public Dictionary<Guid, DomainBookingProfile> AccountBookingProfiles { get; set; } = [];
    public bool EnableAutoRefresh { get; set; } = true;
    public bool UseProxy { get; set; }
}
