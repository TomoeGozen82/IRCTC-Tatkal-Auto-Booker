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
    [ObservableProperty] private bool isEditMode;

    private string _existingProfileName = string.Empty;

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

    public string WindowTitle => IsEditMode
        ? $"Edit Booking — {Account.Username}"
        : $"Booking Setup — {Account.Username}";

    public string SaveButtonText => IsEditMode ? "Update Booking" : "Save Booking";

    public void LoadExistingProfile(DomainBookingProfile profile)
    {
        IsEditMode = true;
        _existingProfileName = profile.ProfileName;

        FromStation = profile.FromStation;
        ToStation = profile.ToStation;
        JourneyDate = profile.JourneyDate;
        SelectedQuota = string.IsNullOrWhiteSpace(profile.Quota) ? "TATKAL" : profile.Quota;
        PreferredTrainNumbers = profile.PreferredTrainNumbers;
        ClassPriority = string.IsNullOrWhiteSpace(profile.ClassPriority) ? "3A" : profile.ClassPriority;

        var sched = profile.SchedulerSettings;
        EnableScheduler = sched.StartTime > DateTime.MinValue.AddDays(1);
        if (EnableScheduler)
        {
            StartDate = sched.StartTime.Date;
            StartTimeText = sched.StartTime.ToString("HH:mm:ss");
        }

        Passengers.Clear();
        foreach (var p in profile.PassengerList)
        {
            Passengers.Add(new PassengerEditorModel
            {
                Name = p.Name,
                Age = p.Age,
                Gender = PassengerFieldOptions.CoerceGender(p.Gender),
                Country = PassengerFieldOptions.CoerceCountry(p.Country),
                Berth = PassengerFieldOptions.CoerceBerth(p.Berth)
            });
        }

        if (Passengers.Count == 0)
        {
            Passengers.Add(new PassengerEditorModel());
        }

        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(SaveButtonText));
    }

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
            ProfileName = IsEditMode && !string.IsNullOrWhiteSpace(_existingProfileName)
                ? _existingProfileName
                : $"Adhoc-{Account.Username}-{DateTime.Now:HHmmss}",
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
                Gender = PassengerFieldOptions.CoerceGender(p.Gender),
                Country = PassengerFieldOptions.CoerceCountry(p.Country),
                Berth = PassengerFieldOptions.CoerceBerth(p.Berth)
            }).ToList()
        };

        _log("Info", IsEditMode
            ? $"Booking updated: account={Account.Username}, from={profile.FromStation}, to={profile.ToStation}, date={profile.JourneyDate:dd-MMM-yyyy}, quota={profile.Quota}, trains={profile.PreferredTrainNumbers}, class={profile.ClassPriority}, pax={profile.PassengerList.Count}"
            : $"Booking saved: account={Account.Username}, from={profile.FromStation}, to={profile.ToStation}, date={profile.JourneyDate:dd-MMM-yyyy}, quota={profile.Quota}, trains={profile.PreferredTrainNumbers}, class={profile.ClassPriority}, pax={profile.PassengerList.Count}");
        StartRequested?.Invoke(profile);
        RequestClose?.Invoke(true);
    }

    private List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(FromStation) || string.IsNullOrWhiteSpace(ToStation))
            errors.Add("From/To station cannot be empty.");
        if (Passengers.Count == 0 || Passengers.Any(p =>
                string.IsNullOrWhiteSpace(p.Name) ||
                p.Age <= 0 ||
                string.IsNullOrWhiteSpace(p.Gender) ||
                string.IsNullOrWhiteSpace(p.Country) ||
                string.IsNullOrWhiteSpace(p.Berth)))
            errors.Add("Add at least one valid passenger (name, age, gender, country, berth).");
        if (string.IsNullOrWhiteSpace(PreferredTrainNumbers) && string.IsNullOrWhiteSpace(ClassPriority))
            errors.Add("Train number or Class preference must be set.");
        if (JourneyDate.Date < DateTime.Today)
            errors.Add("Journey date is invalid.");
        if (EnableScheduler && !TimeSpan.TryParse(StartTimeText, out _))
            errors.Add("Start time must be HH:mm:ss.");
        return errors;
    }
}

