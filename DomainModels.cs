using System.Text.Json.Serialization;

namespace Booking.Domain;

public sealed class IrctcAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string EncryptedPassword { get; set; } = string.Empty;
    public ProxyConfig ProxyConfig { get; set; } = new();
    public CaptchaSettings CaptchaSettings { get; set; } = new();
    public SessionState SessionState { get; set; } = SessionState.Idle;
    public bool IsEnabled { get; set; } = true;
}

public sealed class BookingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProfileName { get; set; } = "Default Tatkal";
    public string FromStation { get; set; } = string.Empty;
    public string ToStation { get; set; } = string.Empty;
    public DateTime JourneyDate { get; set; } = DateTime.Today;
    public string Quota { get; set; } = "TATKAL";
    public string PreferredTrainNumbers { get; set; } = string.Empty;
    public string ClassPriority { get; set; } = "3A";
    public List<PassengerRecord> PassengerList { get; set; } = [];
    public SchedulerSettings SchedulerSettings { get; set; } = new();
    public RetryPolicy RetryPolicy { get; set; } = new();
}

public sealed class BookingJob
{
    public required IrctcAccount Account { get; init; }
    public required BookingProfile BookingProfile { get; init; }
    public JobRuntimeStatus RuntimeStatus { get; set; } = JobRuntimeStatus.Queued;
    public DateTime StartTime { get; set; }

    [JsonIgnore]
    public CancellationTokenSource CancellationSource { get; } = new();
}

public sealed class ProxyConfig
{
    public string Address { get; set; } = string.Empty;
}

public sealed class CaptchaSettings
{
    public string Provider { get; set; } = "2Captcha";
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class SchedulerSettings
{
    public DateTime StartTime { get; set; } = DateTime.Today.AddHours(9).AddMinutes(59).AddSeconds(50);
    public TimeSpan WarmupTime { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class RetryPolicy
{
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 800;
}

public sealed class PassengerRecord
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; } = 30;
    public string Gender { get; set; } = "Male";
    public string Country { get; set; } = "India";
    public string Berth { get; set; } = "No Preference";

    /// <summary>Maps legacy saved profiles (berthPreference / short codes).</summary>
    [JsonPropertyName("berthPreference")]
    public string? BerthPreferenceLegacy
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Berth = global::Booking.PassengerValueNormalizer.NormalizeBerth(value);
            }
        }
    }
}

public enum SessionState
{
    Idle,
    Active,
    Expired,
    Failed
}

public enum JobRuntimeStatus
{
    Queued,
    LoggingIn,
    Searching,
    WaitingTatkal,
    Booking,
    PaymentPage,
    Failed,
    Success,
    Cancelled
}
