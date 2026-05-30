using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DomainBookingJob = Booking.Domain.BookingJob;
using DomainBookingProfile = Booking.Domain.BookingProfile;
using DomainIrctcAccount = Booking.Domain.IrctcAccount;
using Booking.Domain;

namespace Booking;

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _clockTimer;
    private readonly BookingService _bookingService;
    private readonly SchedulerService _schedulerService;
    private readonly StatePersistenceService _statePersistenceService;
    private readonly object _runLock = new();
    private readonly Dictionary<Guid, DomainBookingProfile> _accountBookingProfiles = [];
    private CancellationTokenSource? _bookingCts;
    private bool _isBookingInProgress;

    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string currentTimeDisplay = DateTime.Now.ToString("hh:mm:ss tt");
    [ObservableProperty] private string tatkalTimingHint = "Tatkal Window: 10:00 AM (AC) / 11:00 AM (Non-AC)";
    [ObservableProperty] private string currentStatus = "Idle";
    [ObservableProperty] private string nextTatkalWindow = "10:00 AM";
    [ObservableProperty] private bool enableAutoRefresh = true;
    [ObservableProperty] private bool useProxy;
    [ObservableProperty] private string selectedJourney = "NDLS -> MMCT | 27-May-2026 | Tatkal | 3A";
    [ObservableProperty] private string passengerSummary = "Passengers: 4";
    [ObservableProperty] private AccountModel? selectedAccount;
    [ObservableProperty] private AccountFormModel accountForm = new();
    [ObservableProperty] private bool isEditMode;
    [ObservableProperty] private DateTime scheduledStartDate = DateTime.Today;
    [ObservableProperty] private string scheduledStartTimeText = "09:59:50";
    [ObservableProperty] private string schedulerCountdownText = "Not scheduled";
    [ObservableProperty] private string schedulerStatus = "Idle";
    [ObservableProperty] private bool isScheduleActive;
    [ObservableProperty] private AccountViewModel? selectedAccountItem;
    [ObservableProperty] private string selectedAccountActionButtonText = "Start";
    [ObservableProperty] private bool isSelectedAccountActionRunning;
    
    public event Action<JobLauncherViewModel>? BookingJobLauncherRequested;
    public event Action<BookingSetupViewModel>? BookingSetupRequested;
    public event Func<string, bool>? DeleteAccountConfirmationRequested;
    public event Action? SettingsRequested;
    public event Action? AccountFormRequested;
    public event Action? AccountFormCloseRequested;

    public MainViewModel()
    {
        _bookingService = new BookingService();
        _bookingService.LogEmitted += OnServiceLog;
        _schedulerService = new SchedulerService();
        _schedulerService.CountdownUpdated += OnSchedulerCountdownUpdated;
        _schedulerService.ScheduleStatusChanged += HandleSchedulerStatusChanged;
        _schedulerService.BookingTriggered += OnSchedulerBookingTriggered;
        _statePersistenceService = new StatePersistenceService();
        IsConnected = true;

        // Start empty; everything should load from SQLite.
        CurrentBookingProfile = new BookingProfile();
        Accounts = new ObservableCollection<AccountModel>();
        IrctcAccounts = new ObservableCollection<IrctcAccount>();
        BookingProfiles = new ObservableCollection<Domain.BookingProfile>();
        AccountItems = new ObservableCollection<AccountViewModel>();
        RefreshAccountItems();
        ActiveJobs = new ObservableCollection<BookingJobViewModel>();

        Logs = new ObservableCollection<LogEntry>();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => CurrentTimeDisplay = DateTime.Now.ToString("hh:mm:ss tt");
        _clockTimer.Start();
        _ = LoadStateAsync();
    }

    public ObservableCollection<AccountModel> Accounts { get; }
    public ObservableCollection<LogEntry> Logs { get; }
    public BookingProfile CurrentBookingProfile { get; }
    public ObservableCollection<DomainIrctcAccount> IrctcAccounts { get; }
    public ObservableCollection<DomainBookingProfile> BookingProfiles { get; }
    public ObservableCollection<AccountViewModel> AccountItems { get; }
    public ObservableCollection<BookingJobViewModel> ActiveJobs { get; }

    public string ConnectionStatusText => IsConnected ? "Connected" : "Disconnected";
    public Brush ConnectionStatusBrush => IsConnected
        ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E7D32"))
        : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C62828"));

    public string ActiveAccountsText => $"{Accounts.Count(a => a.IsEnabled && a.Status == "Ready")}/5";
    public DateTime DefaultTatkalStartTime => DateTime.Today.AddHours(9).AddMinutes(59).AddSeconds(50);

    public bool IsBookingInProgress
    {
        get => _isBookingInProgress;
        private set
        {
            if (!SetProperty(ref _isBookingInProgress, value))
            {
                return;
            }

            StartBookingCommand.NotifyCanExecuteChanged();
            StopBookingCommand.NotifyCanExecuteChanged();
            StartAllCommand.NotifyCanExecuteChanged();
            StopAllCommand.NotifyCanExecuteChanged();
            ScheduleBookingCommand.NotifyCanExecuteChanged();
            StartImmediatelyCommand.NotifyCanExecuteChanged();
            CancelScheduledBookingCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(ConnectionStatusBrush));
    }

    partial void OnEnableAutoRefreshChanged(bool value) => _ = PersistStateAsync();
    partial void OnUseProxyChanged(bool value) => _ = PersistStateAsync();

    [RelayCommand(CanExecute = nameof(CanStartBooking))]
    private async Task StartAllAsync() => await StartBookingInternalAsync(Accounts.Where(a => a.IsEnabled).ToList());

    [RelayCommand(CanExecute = nameof(CanStopBooking))]
    private void StopAll() => StopBooking();

    [RelayCommand(CanExecute = nameof(CanScheduleBooking))]
    private async Task ScheduleBookingAsync()
    {
        if (!TryBuildScheduledDateTime(out var targetTime))
        {
            AddLog("Error", "Invalid schedule time. Use HH:mm:ss format.");
            return;
        }

        var enabledAccounts = Accounts.Where(a => a.IsEnabled).ToList();
        if (enabledAccounts.Count == 0)
        {
            AddLog("Error", "Enable at least one account before scheduling.");
            return;
        }

        if (string.IsNullOrWhiteSpace(CurrentBookingProfile.FromStation) || string.IsNullOrWhiteSpace(CurrentBookingProfile.ToStation))
        {
            AddLog("Error", "Booking profile is incomplete.");
            return;
        }

        if (targetTime <= DateTime.Now)
        {
            AddLog("Error", "Scheduled time must be in the future.");
            return;
        }

        IsScheduleActive = true;
        SchedulerStatus = $"Scheduled for {targetTime:hh:mm:ss tt}";
        AddLog("Info", $"Booking scheduled for {targetTime:hh:mm:ss tt}.");
        await PersistStateAsync();
        await _schedulerService.ScheduleBooking(targetTime, enabledAccounts, CurrentBookingProfile);
    }

    [RelayCommand(CanExecute = nameof(CanStartImmediately))]
    private async Task StartImmediatelyAsync()
    {
        _schedulerService.CancelSchedule();
        IsScheduleActive = false;
        SchedulerStatus = "Running";
        SchedulerCountdownText = "Starting now...";
        await StartBookingInternalAsync(Accounts.Where(a => a.IsEnabled).ToList());
    }

    [RelayCommand(CanExecute = nameof(CanCancelSchedule))]
    private void CancelScheduledBooking()
    {
        _schedulerService.CancelSchedule();
        IsScheduleActive = false;
        SchedulerStatus = "Idle";
        SchedulerCountdownText = "Schedule cancelled.";
        AddLog("Info", "Scheduled booking cancelled.");
    }

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private void AddAccount()
    {
        IsEditMode = false;
        AccountForm = new AccountFormModel();
        AccountFormRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanModifySelectedAccount))]
    private void EditAccount()
    {
        if (SelectedAccountItem is null)
        {
            return;
        }

        var domain = SelectedAccountItem.Model;
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == domain.Id);

        IsEditMode = true;
        AccountForm = new AccountFormModel
        {
            Username = domain.Username,
            PlainPassword = PasswordCrypto.Decrypt(domain.EncryptedPassword),
            Proxy = domain.ProxyConfig.Address,
            IsEnabled = domain.IsEnabled
        };
        AccountFormRequested?.Invoke();
    }

    private void StartActionForAccount(AccountViewModel accountItem)
    {
        if (!_accountBookingProfiles.TryGetValue(accountItem.Model.Id, out var profile))
        {
            AddLog("Error", $"No booking configured for {accountItem.Username}. Use Book first.");
            return;
        }

        AddLog("Info", $"Starting action for {accountItem.Username} with saved booking.");
        _ = LaunchJobsAsync(profile, [accountItem.Model]);
    }

    [RelayCommand(CanExecute = nameof(CanBookSelectedAccount))]
    private void BookSelectedAccount()
    {
        if (SelectedAccountItem is null)
        {
            return;
        }

        OnBookRequestedForAccount(SelectedAccountItem);
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelectedAccountAction))]
    private void ToggleSelectedAccountAction()
    {
        if (SelectedAccountItem is null)
        {
            return;
        }

        if (GetActiveJobForAccount(SelectedAccountItem.Model) is not null)
        {
            StopJobForAccount(SelectedAccountItem);
            return;
        }

        StartActionForAccount(SelectedAccountItem);
    }

    private bool CanBookSelectedAccount() => SelectedAccountItem is not null;
    private bool CanToggleSelectedAccountAction()
    {
        if (SelectedAccountItem is null)
        {
            return false;
        }

        // Allow Stop whenever a job is currently running.
        if (GetActiveJobForAccount(SelectedAccountItem.Model) is not null)
        {
            return true;
        }

        // Allow Start only after booking is saved for this account.
        return _accountBookingProfiles.ContainsKey(SelectedAccountItem.Model.Id);
    }

    [RelayCommand]
    private async Task SaveAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(AccountForm.Username))
        {
            AddLog("Error", "Username is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(AccountForm.PlainPassword))
        {
            AddLog("Error", "Password is required.");
            return;
        }

        if (IsEditMode && SelectedAccountItem is not null)
        {
            var domain = SelectedAccountItem.Model;
            var username = AccountForm.Username.Trim();
            var encryptedPassword = PasswordCrypto.Encrypt(AccountForm.PlainPassword);

            domain.Username = username;
            domain.EncryptedPassword = encryptedPassword;
            domain.ProxyConfig.Address = AccountForm.Proxy.Trim();
            domain.IsEnabled = AccountForm.IsEnabled;

            SyncLegacyAccountFromDomain(domain);
            RefreshAccountItems();
            AddLog("Success", $"Updated account {username}.");
        }
        else
        {
            var account = AccountModel.Create(
                AccountForm.Username.Trim(),
                AccountForm.PlainPassword,
                "Ready",
                AccountForm.Proxy.Trim());
            account.IsEnabled = AccountForm.IsEnabled;
            Accounts.Add(account);
            IrctcAccounts.Add(ToIrctcAccount(account));
            RefreshAccountItems();
            AddLog("Success", $"Added account {account.Username}.");
        }

        AccountFormCloseRequested?.Invoke();
        OnPropertyChanged(nameof(ActiveAccountsText));
        await PersistStateAsync();
    }

    [RelayCommand(CanExecute = nameof(CanModifySelectedAccount))]
    private void DeleteAccount()
    {
        if (SelectedAccountItem is null)
        {
            return;
        }

        var removedUsername = SelectedAccountItem.Username;
        var confirmed = DeleteAccountConfirmationRequested?.Invoke(removedUsername) ?? false;
        if (!confirmed)
        {
            return;
        }

        var domainAccount = SelectedAccountItem.Model;
        var legacyAccount = Accounts.FirstOrDefault(a => a.Id == domainAccount.Id);
        if (legacyAccount is not null)
        {
            Accounts.Remove(legacyAccount);
        }

        _accountBookingProfiles.Remove(domainAccount.Id);
        IrctcAccounts.Remove(domainAccount);
        SelectedAccountItem = null;
        SelectedAccount = null;
        RefreshAccountItems();
        AddLog("Info", $"Removed account {removedUsername}.");
        OnPropertyChanged(nameof(ActiveAccountsText));
        _ = PersistStateAsync();
    }

    [RelayCommand]
    private void EditBookingProfile() => AddLog("Info", "Booking profile edit opened.");

    [RelayCommand(CanExecute = nameof(CanStartBooking))]
    private void StartBooking()
    {
        AddLog("Info", "Opening job launcher dialog.");
        var launcher = new JobLauncherViewModel(IrctcAccounts.Where(a => a.IsEnabled), BookingProfiles);
        launcher.LaunchRequested += (profile, accounts) => _ = LaunchJobsAsync(profile, accounts);
        BookingJobLauncherRequested?.Invoke(launcher);
    }

    [RelayCommand(CanExecute = nameof(CanStopBooking))]
    private void StopBooking()
    {
        lock (_runLock)
        {
            _bookingCts?.Cancel();
        }

        CurrentStatus = "Stopping";
        AddLog("Info", "Stop requested. Cancelling active booking tasks.");
    }

    partial void OnSelectedAccountChanged(AccountModel? value)
    {
        StartBookingCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedAccountItemChanged(AccountViewModel? value)
    {
        SelectedAccount = value is null ? null : Accounts.FirstOrDefault(a => a.Id == value.Model.Id);
        BookSelectedAccountCommand.NotifyCanExecuteChanged();
        ToggleSelectedAccountActionCommand.NotifyCanExecuteChanged();
        EditAccountCommand.NotifyCanExecuteChanged();
        DeleteAccountCommand.NotifyCanExecuteChanged();
        UpdateSelectedAccountActionButton();
    }

    private bool CanModifySelectedAccount() => SelectedAccountItem is not null;
    private bool CanStartBooking() => !IsBookingInProgress && IrctcAccounts.Any(a => a.IsEnabled) && BookingProfiles.Count > 0;
    private bool CanStopBooking() => IsBookingInProgress;
    private bool CanScheduleBooking() => !IsBookingInProgress && !IsScheduleActive && Accounts.Any(a => a.IsEnabled);
    private bool CanStartImmediately() => !IsBookingInProgress && Accounts.Any(a => a.IsEnabled);
    private bool CanCancelSchedule() => IsScheduleActive;

    private async Task StartBookingInternalAsync(IReadOnlyCollection<AccountModel> targetAccounts)
    {
        if (targetAccounts.Count == 0)
        {
            AddLog("Info", "No accounts available to start booking.");
            return;
        }

        CancellationTokenSource cts;
        lock (_runLock)
        {
            if (IsBookingInProgress)
            {
                AddLog("Info", "Booking run already in progress.");
                return;
            }

            _bookingCts = new CancellationTokenSource();
            cts = _bookingCts;
            IsBookingInProgress = true;
        }

        CurrentStatus = "Booking";
        AddLog("Info", $"Starting booking for {targetAccounts.Count} account(s).");

        try
        {
            await Task.WhenAll(targetAccounts.Select(account => RunBookingForAccountAsync(account, CurrentBookingProfile, cts.Token)));
            CurrentStatus = cts.IsCancellationRequested ? "Stopped" : "Idle";
            AddLog("Success", "Booking run completed.");
        }
        finally
        {
            lock (_runLock)
            {
                _bookingCts?.Dispose();
                _bookingCts = null;
                IsBookingInProgress = false;
            }
        }
    }

    private async Task RunBookingForAccountAsync(AccountModel account, BookingProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            account.UpdateStatus("Queued");
            var result = await _bookingService.StartBookingAsync(account, profile, cancellationToken);
            var finalStatus = result.Status switch
            {
                BookingRunStatus.PaymentReached => "Ready",
                BookingRunStatus.Success => "Ready",
                _ => "Invalid"
            };

            account.UpdateStatus(finalStatus);
        }
        catch (OperationCanceledException)
        {
            account.UpdateStatus("Stopped");
        }
        catch (Exception ex)
        {
            account.UpdateStatus("Invalid");
            AddLog("Error", $"{account.Username}: {ex.Message}");
        }
        finally
        {
            RunOnUi(() => OnPropertyChanged(nameof(ActiveAccountsText)));
        }
    }

    private void OnServiceLog(ServiceLog log) => AddLog(log.Level, $"{log.Username}: {log.Message}");
    
    private void OnSchedulerCountdownUpdated(string countdown)
    {
        RunOnUi(() => SchedulerCountdownText = countdown);
    }

    private void HandleSchedulerStatusChanged(string status)
    {
        RunOnUi(() =>
        {
            SchedulerStatus = status;
            IsScheduleActive = status.StartsWith("Scheduled", StringComparison.OrdinalIgnoreCase);
            ScheduleBookingCommand.NotifyCanExecuteChanged();
            CancelScheduledBookingCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnSchedulerBookingTriggered(List<AccountModel> accounts, BookingProfile profile)
    {
        _ = RunScheduledBookingAsync(accounts);
    }

    private async Task RunScheduledBookingAsync(IReadOnlyCollection<AccountModel> accounts)
    {
        await StartBookingInternalAsync(accounts);
        RunOnUi(() =>
        {
            SchedulerStatus = "Idle";
            SchedulerCountdownText = "Not scheduled";
            IsScheduleActive = false;
        });
    }

    private bool TryBuildScheduledDateTime(out DateTime targetTime)
    {
        targetTime = DateTime.MinValue;
        if (!TimeSpan.TryParse(ScheduledStartTimeText, out var time))
        {
            return false;
        }

        targetTime = ScheduledStartDate.Date + time;
        return true;
    }

    private async Task LaunchJobsAsync(DomainBookingProfile profile, IReadOnlyCollection<DomainIrctcAccount> selectedAccounts)
    {
        if (selectedAccounts.Count == 0)
        {
            AddLog("Error", "Select at least one account.");
            return;
        }

        AddLog("Info", $"Automation start event: launching {selectedAccounts.Count} isolated job(s) with profile {profile.ProfileName}.");
        foreach (var account in selectedAccounts)
        {
            var job = new DomainBookingJob
            {
                Account = account,
                BookingProfile = profile,
                RuntimeStatus = JobRuntimeStatus.Queued,
                StartTime = DateTime.Now
            };
            var vm = new BookingJobViewModel(job);
            ActiveJobs.Add(vm);
            SubscribeToJobUpdates(vm);
            _ = RunJobAsync(vm);
        }
        await Task.CompletedTask;
    }

    private async Task RunJobAsync(BookingJobViewModel jobVm)
    {
        var job = jobVm.Model;
        var retry = job.BookingProfile.RetryPolicy;
        for (var attempt = 1; attempt <= Math.Max(1, retry.MaxRetries); attempt++)
        {
            try
            {
                jobVm.RuntimeStatus = JobRuntimeStatus.LoggingIn;
                var legacyAccount = ToLegacyAccount(job.Account);
                var legacyProfile = ToLegacyProfile(job.BookingProfile);
                var result = await _bookingService.StartBookingAsync(legacyAccount, legacyProfile, job.CancellationSource.Token);

                jobVm.RuntimeStatus = result.Status switch
                {
                    BookingRunStatus.PaymentReached => JobRuntimeStatus.PaymentPage,
                    BookingRunStatus.Success => JobRuntimeStatus.Success,
                    _ => JobRuntimeStatus.Failed
                };
                return;
            }
            catch (OperationCanceledException)
            {
                jobVm.RuntimeStatus = JobRuntimeStatus.Cancelled;
                return;
            }
            catch (Exception ex)
            {
                AddLog("Error", $"{job.Account.Username} job attempt {attempt} failed: {ex.Message}");
                jobVm.RuntimeStatus = JobRuntimeStatus.Failed;
                if (attempt < retry.MaxRetries)
                {
                    await Task.Delay(Math.Max(100, retry.RetryDelayMs), job.CancellationSource.Token);
                }
            }
        }
    }

    private void StopJobForAccount(AccountViewModel accountItem)
    {
        var activeJob = GetActiveJobForAccount(accountItem.Model);
        if (activeJob is null)
        {
            return;
        }

        AddLog("Info", $"Stop requested for account {accountItem.Username}.");
        activeJob.Model.CancellationSource.Cancel();
    }

    private BookingJobViewModel? GetActiveJobForAccount(DomainIrctcAccount? account)
    {
        if (account is null)
        {
            return null;
        }

        return ActiveJobs.LastOrDefault(job =>
            job.Model.Account.Id == account.Id && IsJobRunning(job.RuntimeStatus));
    }

    private static bool IsJobRunning(JobRuntimeStatus status) =>
        status is JobRuntimeStatus.Queued
            or JobRuntimeStatus.LoggingIn
            or JobRuntimeStatus.Searching
            or JobRuntimeStatus.WaitingTatkal
            or JobRuntimeStatus.Booking
            or JobRuntimeStatus.PaymentPage;

    private void SubscribeToJobUpdates(BookingJobViewModel jobVm)
    {
        jobVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(BookingJobViewModel.RuntimeStatus) or nameof(BookingJobViewModel.RuntimeStatusText)))
            {
                return;
            }

            RunOnUi(() =>
            {
                UpdateSelectedAccountActionButton();
                ToggleSelectedAccountActionCommand.NotifyCanExecuteChanged();
            });
        };
    }

    private void UpdateSelectedAccountActionButton()
    {
        var isRunning = SelectedAccountItem is not null && GetActiveJobForAccount(SelectedAccountItem.Model) is not null;
        IsSelectedAccountActionRunning = isRunning;
        SelectedAccountActionButtonText = isRunning ? "Stop" : "Start";
    }

    private static DomainBookingProfile ToDomainProfile(BookingProfile profile, string name)
    {
        var p = new DomainBookingProfile
        {
            ProfileName = name,
            FromStation = profile.FromStation,
            ToStation = profile.ToStation,
            JourneyDate = profile.JourneyDate,
            Quota = profile.Quota,
            PreferredTrainNumbers = profile.TrainNumber,
            ClassPriority = profile.TravelClass,
            SchedulerSettings = new SchedulerSettings(),
            RetryPolicy = new RetryPolicy(),
            PassengerList = profile.Passengers.Select(x => new PassengerRecord
            {
                Name = x.Name,
                Age = x.Age,
                Gender = x.Gender,
                Country = x.Country,
                Berth = x.Berth
            }).ToList()
        };
        return p;
    }

    private static DomainIrctcAccount ToIrctcAccount(AccountModel account)
    {
        return new DomainIrctcAccount
        {
            Id = account.Id,
            Username = account.Username,
            EncryptedPassword = account.Password,
            ProxyConfig = new ProxyConfig { Address = account.Proxy },
            CaptchaSettings = new CaptchaSettings(),
            SessionState = account.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) ? SessionState.Idle : SessionState.Failed,
            IsEnabled = account.IsEnabled
        };
    }

    private static AccountModel ToLegacyAccount(DomainIrctcAccount account)
    {
        return new AccountModel
        {
            Id = account.Id,
            Username = account.Username,
            Password = account.EncryptedPassword,
            Proxy = account.ProxyConfig.Address,
            Status = "Ready",
            IsEnabled = account.IsEnabled
        };
    }

    private void SyncLegacyAccountFromDomain(IrctcAccount domain)
    {
        var legacy = Accounts.FirstOrDefault(a => a.Id == domain.Id);
        if (legacy is null)
        {
            Accounts.Add(ToLegacyAccount(domain));
            return;
        }

        legacy.Username = domain.Username;
        legacy.Password = domain.EncryptedPassword;
        legacy.Proxy = domain.ProxyConfig.Address;
        legacy.IsEnabled = domain.IsEnabled;
        legacy.NotifyPasswordDisplayChanged();
    }

    private static BookingProfile ToLegacyProfile(DomainBookingProfile profile)
    {
        var legacy = new BookingProfile
        {
            FromStation = profile.FromStation,
            ToStation = profile.ToStation,
            JourneyDate = profile.JourneyDate,
            Quota = profile.Quota,
            TrainNumber = profile.PreferredTrainNumbers,
            TravelClass = profile.ClassPriority
        };

        foreach (var p in profile.PassengerList)
        {
            legacy.Passengers.Add(new PassengerModel(
                p.Name,
                p.Age,
                PassengerValueNormalizer.NormalizeGender(p.Gender),
                PassengerValueNormalizer.NormalizeCountry(p.Country),
                PassengerValueNormalizer.NormalizeBerth(p.Berth)));
        }
        return legacy;
    }

    private async Task LoadStateAsync()
    {
        var state = await _statePersistenceService.LoadAsync();
        if (state is null)
        {
            return;
        }

        RunOnUi(() =>
        {
            // Dashboard binds to legacy `Accounts`, so keep it in sync with domain `IrctcAccounts`.
            Accounts.Clear();
            IrctcAccounts.Clear();
            foreach (var a in state.Accounts)
            {
                IrctcAccounts.Add(a);
                Accounts.Add(ToLegacyAccount(a));
            }

            BookingProfiles.Clear();
            foreach (var p in state.BookingProfiles)
            {
                BookingProfiles.Add(p);
            }
            _accountBookingProfiles.Clear();
            foreach (var kv in state.AccountBookingProfiles)
            {
                _accountBookingProfiles[kv.Key] = kv.Value;
            }
            RefreshAccountItems();

            ScheduledStartDate = state.SchedulerSettings.StartTime.Date;
            ScheduledStartTimeText = state.SchedulerSettings.StartTime.ToString("HH:mm:ss");
            EnableAutoRefresh = state.EnableAutoRefresh;
            UseProxy = state.UseProxy;
            OnPropertyChanged(nameof(ActiveAccountsText));
        });
    }

    private async Task PersistStateAsync()
    {
        var state = new AppStateSnapshot
        {
            Accounts = IrctcAccounts.ToList(),
            BookingProfiles = BookingProfiles.ToList(),
            AccountBookingProfiles = _accountBookingProfiles.ToDictionary(k => k.Key, v => v.Value),
            EnableAutoRefresh = EnableAutoRefresh,
            UseProxy = UseProxy,
            SchedulerSettings = new SchedulerSettings
            {
                StartTime = ScheduledStartDate.Date + (TimeSpan.TryParse(ScheduledStartTimeText, out var time) ? time : TimeSpan.Zero),
                WarmupTime = TimeSpan.FromSeconds(10)
            }
        };
        await _statePersistenceService.SaveAsync(state);
    }

    private void RefreshAccountItems()
    {
        AccountItems.Clear();
        foreach (var account in IrctcAccounts)
        {
            AccountItems.Add(new AccountViewModel(account, OnBookRequestedForAccount));
        }
    }

    private void OnBookRequestedForAccount(AccountViewModel accountItem)
    {
        AddLog("Info", $"Booking setup window opened for account {accountItem.Username}.");
        var setup = new BookingSetupViewModel(
            accountItem,
            quotaOptions: ["TATKAL", "GENERAL", "PREMIUM TATKAL"],
            classOptions: ["SL", "3A", "2A", "1A", "CC", "2S"],
            logger: AddLog);

        setup.StartRequested += profile =>
        {
            _accountBookingProfiles[accountItem.Model.Id] = profile;
            AddLog("Success", $"Booking saved for {accountItem.Username}. Use Start to run.");
            _ = PersistStateAsync();
            UpdateSelectedAccountActionButton();
            ToggleSelectedAccountActionCommand.NotifyCanExecuteChanged();
        };

        BookingSetupRequested?.Invoke(setup);
    }

    private void AddLog(string level, string message) => RunOnUi(() => Logs.Add(new LogEntry(level, message)));

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
}

public partial class AccountModel : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty] private string username = string.Empty;
    [ObservableProperty] private string password = string.Empty;
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private bool isEnabled = true;
    [ObservableProperty] private DateTime lastSuccessfulLogin = DateTime.MinValue;
    [ObservableProperty] private string proxy = string.Empty;
    [ObservableProperty] private string lastAction = "Idle";

    public string LastLoginDisplay => LastSuccessfulLogin == DateTime.MinValue ? "Never" : LastSuccessfulLogin.ToString("dd-MMM-yyyy HH:mm");

    public string PasswordDisplay => PasswordCrypto.Decrypt(Password);

    partial void OnLastSuccessfulLoginChanged(DateTime value) => OnPropertyChanged(nameof(LastLoginDisplay));

    partial void OnPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(PasswordDisplay));
    }

    public void NotifyPasswordDisplayChanged() => OnPropertyChanged(nameof(PasswordDisplay));

    public static AccountModel Create(string username, string plainPassword, string status, string proxy)
    {
        var model = new AccountModel
        {
            Id = Guid.NewGuid(),
            Username = username,
            Status = status,
            Proxy = proxy,
            LastSuccessfulLogin = DateTime.Now
        };
        model.SetPassword(plainPassword);
        return model;
    }

    public void SetPassword(string plainPassword)
    {
        Password = PasswordCrypto.Encrypt(plainPassword);
    }

    public string GetDecryptedPassword() => PasswordCrypto.Decrypt(Password);

    public void UpdateStatus(string newStatus) => UpdateOnUi(() => Status = newStatus);
    public void UpdateLastAction(string newAction) => UpdateOnUi(() => LastAction = newAction);

    private static void UpdateOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
}

public partial class AccountFormModel : ObservableObject
{
    [ObservableProperty] private string username = string.Empty;
    [ObservableProperty] private string plainPassword = string.Empty;
    [ObservableProperty] private string proxy = string.Empty;
    [ObservableProperty] private bool isEnabled = true;
    [ObservableProperty] private string status = "Ready";
}

public sealed class BookingProfile
{
    public string FromStation { get; set; } = string.Empty;
    public string ToStation { get; set; } = string.Empty;
    public DateTime JourneyDate { get; set; } = DateTime.Today;
    public string Quota { get; set; } = "TATKAL";
    public string TrainNumber { get; set; } = string.Empty;
    public string TravelClass { get; set; } = "3A";
    public bool HeadlessMode { get; set; } = true;
    public int MaxRefreshAttempts { get; set; } = 120;
    public int RefreshIntervalMs { get; set; } = 700;
    public int DefaultTimeoutMs { get; set; } = 20000;
    public string TwoCaptchaApiKey { get; set; } = string.Empty;
    public ObservableCollection<PassengerModel> Passengers { get; } = [];
}

public sealed class PassengerModel
{
    public PassengerModel(string name, int age, string gender, string country, string berth)
    {
        Name = name;
        Age = age;
        Gender = PassengerValueNormalizer.NormalizeGender(gender);
        Country = PassengerValueNormalizer.NormalizeCountry(country);
        Berth = PassengerValueNormalizer.NormalizeBerth(berth);
    }

    public string Name { get; }
    public int Age { get; }
    public string Gender { get; }
    public string Country { get; }
    public string Berth { get; }
}

public class LogEntry
{
    public LogEntry(string level, string message)
    {
        Timestamp = DateTime.Now;
        Level = level;
        Message = message;
    }

    public DateTime Timestamp { get; }

    public string Level { get; }

    public string Message { get; }

    public string FormattedMessage => $"[{Timestamp:HH:mm:ss}] {Message}";
}
