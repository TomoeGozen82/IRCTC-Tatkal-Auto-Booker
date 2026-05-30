using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Booking;

public partial class BookingConfigurationViewModel : ObservableObject
{
    private readonly Action<string, string> _log;

    [ObservableProperty] private string fromStation = string.Empty;
    [ObservableProperty] private string toStation = string.Empty;
    [ObservableProperty] private DateTime journeyDate = DateTime.Today;
    [ObservableProperty] private string selectedQuota = "TATKAL";
    [ObservableProperty] private string preferredTrainNumbers = string.Empty;
    [ObservableProperty] private string preferredClassPriority = "3A";
    [ObservableProperty] private PassengerEditorModel? selectedPassenger;
    [ObservableProperty] private DateTime bookingStartDate = DateTime.Today;
    [ObservableProperty] private string bookingStartTimeText = "09:59:50";
    [ObservableProperty] private string warmupTimeText = "00:00:10";
    [ObservableProperty] private string selectedCaptchaProvider = "2Captcha";
    [ObservableProperty] private string captchaApiKey = string.Empty;

    public BookingConfigurationViewModel(BookingProfile profile, IEnumerable<AccountModel> accounts, Action<string, string> logger)
    {
        _log = logger;
        QuotaOptions = ["TATKAL", "GENERAL", "PREMIUM TATKAL"];
        ClassPriorityOptions = ["1A", "2A", "3A", "CC", "SL", "2S"];
        CaptchaProviders = ["2Captcha", "Manual", "AnyCaptcha"];

        FromStation = profile.FromStation;
        ToStation = profile.ToStation;
        JourneyDate = profile.JourneyDate;
        SelectedQuota = string.IsNullOrWhiteSpace(profile.Quota) ? "TATKAL" : profile.Quota;
        PreferredTrainNumbers = profile.TrainNumber;
        PreferredClassPriority = string.IsNullOrWhiteSpace(profile.TravelClass) ? "3A" : profile.TravelClass;
        CaptchaApiKey = profile.TwoCaptchaApiKey;

        Passengers = new ObservableCollection<PassengerEditorModel>(
            profile.Passengers.Select(p => new PassengerEditorModel
            {
                Name = p.Name,
                Age = p.Age,
                Gender = PassengerFieldOptions.CoerceGender(p.Gender),
                Country = PassengerFieldOptions.CoerceCountry(p.Country),
                Berth = PassengerFieldOptions.CoerceBerth(p.Berth)
            }));

        if (Passengers.Count == 0)
        {
            Passengers.Add(new PassengerEditorModel());
        }

        AccountSelections = new ObservableCollection<AccountSelectionItem>(
            accounts.Select(a => new AccountSelectionItem { Account = a, IsSelected = a.IsEnabled }));
    }

    public ObservableCollection<PassengerEditorModel> Passengers { get; }
    public ObservableCollection<AccountSelectionItem> AccountSelections { get; }
    public List<string> QuotaOptions { get; }
    public List<string> ClassPriorityOptions { get; }
    public List<string> CaptchaProviders { get; }

    public event Action<BookingSessionConfig>? StartRequested;
    public event Action<bool>? RequestClose;

    [RelayCommand]
    private void AddPassenger()
    {
        Passengers.Add(new PassengerEditorModel());
    }

    [RelayCommand(CanExecute = nameof(CanRemovePassenger))]
    private void RemovePassenger()
    {
        if (SelectedPassenger is null)
        {
            return;
        }

        Passengers.Remove(SelectedPassenger);
    }

    private bool CanRemovePassenger() => SelectedPassenger is not null;

    partial void OnSelectedPassengerChanged(PassengerEditorModel? value)
    {
        RemovePassengerCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }

    [RelayCommand]
    private void StartAutomation()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                _log("Error", error);
            }

            return;
        }

        var selectedAccounts = AccountSelections.Where(x => x.IsSelected).Select(x => x.Account).ToList();
        var config = new BookingSessionConfig
        {
            Profile = new BookingProfile
            {
                FromStation = FromStation.Trim(),
                ToStation = ToStation.Trim(),
                JourneyDate = JourneyDate,
                Quota = SelectedQuota,
                TrainNumber = PreferredTrainNumbers.Trim(),
                TravelClass = PreferredClassPriority,
                TwoCaptchaApiKey = CaptchaApiKey.Trim()
            },
            SelectedAccounts = selectedAccounts,
            BookingStartTime = BookingStartDate.Date + TimeSpan.Parse(BookingStartTimeText),
            WarmupTime = TimeSpan.Parse(WarmupTimeText),
            CaptchaProvider = SelectedCaptchaProvider,
            CaptchaApiKey = CaptchaApiKey.Trim()
        };

        foreach (var passenger in Passengers)
        {
            config.Profile.Passengers.Add(new PassengerModel(
                passenger.Name.Trim(),
                passenger.Age,
                passenger.Gender.Trim(),
                passenger.Country.Trim(),
                passenger.Berth.Trim()));
        }

        _log("Success", "Booking configuration accepted. Starting automation.");
        StartRequested?.Invoke(config);
        RequestClose?.Invoke(true);
    }

    private List<string> Validate()
    {
        var errors = new List<string>();
        if (AccountSelections.All(a => !a.IsSelected))
        {
            errors.Add("Select at least one account.");
        }

        if (Passengers.Count == 0)
        {
            errors.Add("Add at least one passenger.");
        }

        if (Passengers.Any(p =>
                string.IsNullOrWhiteSpace(p.Name) ||
                p.Age <= 0 ||
                string.IsNullOrWhiteSpace(p.Gender) ||
                string.IsNullOrWhiteSpace(p.Country) ||
                string.IsNullOrWhiteSpace(p.Berth)))
        {
            errors.Add("Passenger details are incomplete (name, age, gender, country, berth).");
        }

        if (string.IsNullOrWhiteSpace(PreferredTrainNumbers))
        {
            errors.Add("Train number cannot be empty.");
        }

        if (JourneyDate.Date < DateTime.Today)
        {
            errors.Add("Journey date is invalid.");
        }

        if (!TimeSpan.TryParse(BookingStartTimeText, out _))
        {
            errors.Add("Booking start time must be HH:mm:ss.");
        }

        if (!TimeSpan.TryParse(WarmupTimeText, out _))
        {
            errors.Add("Warmup time must be HH:mm:ss.");
        }

        return errors;
    }
}

public partial class PassengerEditorModel : ObservableObject
{
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private int age = 30;
    [ObservableProperty] private string gender = "Male";
    [ObservableProperty] private string country = "India";
    [ObservableProperty] private string berth = "No Preference";
}

public partial class AccountSelectionItem : ObservableObject
{
    [ObservableProperty] private bool isSelected;
    public required AccountModel Account { get; init; }
}

public sealed class BookingSessionConfig
{
    public required BookingProfile Profile { get; init; }
    public required List<AccountModel> SelectedAccounts { get; init; }
    public DateTime BookingStartTime { get; init; }
    public TimeSpan WarmupTime { get; init; }
    public string CaptchaProvider { get; init; } = "2Captcha";
    public string CaptchaApiKey { get; init; } = string.Empty;
}
