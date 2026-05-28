using System.Diagnostics;

namespace Booking;

public sealed class SchedulerService
{
    private readonly object _lock = new();
    private CancellationTokenSource? _scheduleCts;

    public event Action<string>? CountdownUpdated;
    public event Action<string>? ScheduleStatusChanged;
    public event Action<List<AccountModel>, BookingProfile>? BookingTriggered;

    public async Task ScheduleBooking(DateTime startTime, List<AccountModel> accounts, BookingProfile profile)
    {
        CancellationToken token;
        lock (_lock)
        {
            _scheduleCts?.Cancel();
            _scheduleCts?.Dispose();
            _scheduleCts = new CancellationTokenSource();
            token = _scheduleCts.Token;
        }

        var localStart = startTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(startTime, DateTimeKind.Local)
            : startTime.ToLocalTime();

        ScheduleStatusChanged?.Invoke($"Scheduled for {localStart:hh:mm:ss tt}");

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = localStart - DateTime.Now;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                CountdownUpdated?.Invoke($"Booking will start in {remaining.Minutes:D2}m {remaining.Seconds:D2}s");
                var delay = remaining > TimeSpan.FromSeconds(1)
                    ? TimeSpan.FromMilliseconds(250)
                    : TimeSpan.FromMilliseconds(20);
                await Task.Delay(delay, token);
            }

            // Final high precision wait near boundary.
            var sw = Stopwatch.StartNew();
            while (DateTime.Now < localStart && sw.Elapsed < TimeSpan.FromSeconds(2))
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(1, token);
            }

            CountdownUpdated?.Invoke("Booking trigger reached.");
            ScheduleStatusChanged?.Invoke("Running");
            BookingTriggered?.Invoke(accounts, profile);
        }
        catch (OperationCanceledException)
        {
            CountdownUpdated?.Invoke("Schedule cancelled.");
            ScheduleStatusChanged?.Invoke("Idle");
        }
    }

    public void CancelSchedule()
    {
        lock (_lock)
        {
            _scheduleCts?.Cancel();
        }
    }
}
