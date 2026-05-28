using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Booking.Domain;
using DomainBookingProfile = Booking.Domain.BookingProfile;

namespace Booking;

public partial class BookingSetupViewModel : ObservableObject
{
    private readonly Action<string, string> _log;

    [ObservableProperty] private string fromStation = string.Empty;
    [ObservableProperty] private string toStation = string.Empty;
    [ObservableProperty] private DateTime journeyDate = DateTime.Today;
    [ObservableProperty] private string selectedQuota = "TATKAL";
    [ObservableProperty] private string preferredTrainNumbers = string.Empty;
    [ObservableProperty] private string classPriority = "3A";
    [ObservableProperty] private bool enableScheduler;
    [ObservableProperty] private DateTime startDate = DateTime.Today;
    [ObservableProperty] private string startTimeText = "09:59:50";
    [ObservableProperty] private PassengerEditorModel? selectedPassenger;

    public BookingSetupViewModel(AccountViewModel account, IEnumerable<string> quotaOptions, IEnumerable<string> classOptions, Action<string, string> logger)
    {
        Account = account;
        _log = logger;
        QuotaOptions = new ObservableCollection<string>(quotaOptions);
        ClassOptions = new ObservableCollection<string>(classOptions);
        Passengers = new ObservableCollection<PassengerEditorModel> { new PassengerEditorModel() };
    }

    public AccountViewModel Account { get; }
    public ObservableCollection<string> QuotaOptions { get; }
    public ObservableCollection<string> ClassOptions { get; }
    public ObservableCollection<PassengerEditorModel> Passengers { get; }

    public event Action<DomainBookingProfile>? StartRequested;
    public event Action<bool>? RequestClose;

    [RelayCommand]
    private void AddPassenger() => Passengers.Add(new PassengerEditorModel());

    [RelayCommand(CanExecute = nameof(CanRemovePassenger))]
    private void RemovePassenger()
    {
        if (SelectedPassenger is null) return;
        Passengers.Remove(SelectedPassenger);
    }

    private bool CanRemovePassenger() => SelectedPassenger is not null;
    partial void OnSelectedPassengerChanged(PassengerEditorModel? value) => RemovePassengerCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Cancel()
    {
        _log("Info", $"Booking setup closed for {Account.Username} (Cancel).");
        RequestClose?.Invoke(false);
    }

    [RelayCommand]
    private void StartBooking()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            foreach (var e in errors) _log("Error", e);
            return;
        }

        var profile = new DomainBookingProfile
        {
            ProfileName = $"Adhoc-{Account.Username}-{DateTime.Now:HHmmss}",
            FromStation = FromStation.Trim(),
            ToStation = ToStation.Trim(),
            JourneyDate = JourneyDate.Date,
            Quota = SelectedQuota,
            PreferredTrainNumbers = PreferredTrainNumbers.Trim(),
            ClassPriority = ClassPriority,
            SchedulerSettings = new SchedulerSettings
            {
                StartTime = EnableScheduler
                    ? StartDate.Date + TimeSpan.Parse(StartTimeText)
                    : DateTime.Now,
                WarmupTime = TimeSpan.FromSeconds(10)
            },
            RetryPolicy = new RetryPolicy(),
            PassengerList = Passengers.Select(p => new PassengerRecord
            {
                Name = p.Name.Trim(),
                Age = p.Age,
                Gender = p.Gender.Trim(),
                BerthPreference = p.BerthPreference.Trim()
            }).ToList()
        };

        _log("Info", $"Booking saved: account={Account.Username}, from={profile.FromStation}, to={profile.ToStation}, date={profile.JourneyDate:dd-MMM-yyyy}, quota={profile.Quota}, trains={profile.PreferredTrainNumbers}, class={profile.ClassPriority}, pax={profile.PassengerList.Count}");
        StartRequested?.Invoke(profile);
        RequestClose?.Invoke(true);
    }

    private List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(FromStation) || string.IsNullOrWhiteSpace(ToStation))
            errors.Add("From/To station cannot be empty.");
        if (Passengers.Count == 0 || Passengers.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Age <= 0))
            errors.Add("Add at least one valid passenger (Name + Age).");
        if (string.IsNullOrWhiteSpace(PreferredTrainNumbers) && string.IsNullOrWhiteSpace(ClassPriority))
            errors.Add("Train number or Class preference must be set.");
        if (JourneyDate.Date < DateTime.Today)
            errors.Add("Journey date is invalid.");
        if (EnableScheduler && !TimeSpan.TryParse(StartTimeText, out _))
            errors.Add("Start time must be HH:mm:ss.");
        return errors;
    }
}

