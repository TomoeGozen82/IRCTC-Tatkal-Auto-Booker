using System.IO;
using System.Text.Json;
using Booking.Domain;
using Microsoft.Data.Sqlite;

namespace Booking;

public sealed class StatePersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _dbPath;
    private readonly string _legacyJsonPath;
    private bool _isInitialized;

    public StatePersistenceService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _dbPath = Path.Combine(appData, "IrctcTatkalAutoBooker", "state.db");
        _legacyJsonPath = Path.Combine(appData, "IrctcTatkalAutoBooker", "state.json");
    }

    public async Task<AppStateSnapshot?> LoadAsync()
    {
        await EnsureInitializedAsync();
        if (!await HasAnyDataAsync() && File.Exists(_legacyJsonPath))
        {
            await TryMigrateLegacyJsonAsync();
        }
        if (!await HasAnyDataAsync())
        {
            return null;
        }

        var state = new AppStateSnapshot();
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        var accountCmd = connection.CreateCommand();
        accountCmd.CommandText = """
            SELECT id, username, encrypted_password, proxy_address, captcha_provider, captcha_api_key, session_state, is_enabled
            FROM accounts;
            """;
        await using (var reader = await accountCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                state.Accounts.Add(new IrctcAccount
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Username = reader.GetString(1),
                    EncryptedPassword = reader.GetString(2),
                    ProxyConfig = new ProxyConfig { Address = reader.IsDBNull(3) ? string.Empty : reader.GetString(3) },
                    CaptchaSettings = new CaptchaSettings
                    {
                        Provider = reader.IsDBNull(4) ? "2Captcha" : reader.GetString(4),
                        ApiKey = reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
                    },
                    SessionState = Enum.TryParse<SessionState>(reader.GetString(6), out var session) ? session : SessionState.Idle,
                    IsEnabled = reader.GetInt64(7) == 1
                });
            }
        }

        var profileCmd = connection.CreateCommand();
        profileCmd.CommandText = "SELECT profile_json FROM booking_profiles;";
        await using (var reader = await profileCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var profile = JsonSerializer.Deserialize<Booking.Domain.BookingProfile>(reader.GetString(0), JsonOptions);
                if (profile is not null)
                {
                    state.BookingProfiles.Add(profile);
                }
            }
        }

        var mappingCmd = connection.CreateCommand();
        mappingCmd.CommandText = "SELECT account_id, profile_json FROM account_booking_profiles;";
        await using (var reader = await mappingCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var accountId = Guid.Parse(reader.GetString(0));
                var profile = JsonSerializer.Deserialize<Booking.Domain.BookingProfile>(reader.GetString(1), JsonOptions);
                if (profile is not null)
                {
                    state.AccountBookingProfiles[accountId] = profile;
                }
            }
        }

        var settingsCmd = connection.CreateCommand();
        settingsCmd.CommandText = "SELECT key, value FROM app_settings;";
        await using (var reader = await settingsCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                switch (key)
                {
                    case "scheduler_settings":
                        state.SchedulerSettings = JsonSerializer.Deserialize<SchedulerSettings>(value, JsonOptions) ?? new SchedulerSettings();
                        break;
                    case "enable_auto_refresh":
                        state.EnableAutoRefresh = bool.TryParse(value, out var enableAutoRefresh) && enableAutoRefresh;
                        break;
                    case "use_proxy":
                        state.UseProxy = bool.TryParse(value, out var useProxy) && useProxy;
                        break;
                }
            }
        }

        return state;
    }

    public async Task SaveAsync(AppStateSnapshot state)
    {
        await EnsureInitializedAsync();
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        foreach (var table in new[] { "accounts", "booking_profiles", "account_booking_profiles", "app_settings" })
        {
            var clearCmd = connection.CreateCommand();
            clearCmd.Transaction = transaction;
            clearCmd.CommandText = $"DELETE FROM {table};";
            await clearCmd.ExecuteNonQueryAsync();
        }

        foreach (var account in state.Accounts)
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO accounts (id, username, encrypted_password, proxy_address, captcha_provider, captcha_api_key, session_state, is_enabled)
                VALUES (@id, @username, @encrypted_password, @proxy_address, @captcha_provider, @captcha_api_key, @session_state, @is_enabled);
                """;
            cmd.Parameters.AddWithValue("@id", account.Id.ToString());
            cmd.Parameters.AddWithValue("@username", account.Username);
            cmd.Parameters.AddWithValue("@encrypted_password", account.EncryptedPassword);
            cmd.Parameters.AddWithValue("@proxy_address", account.ProxyConfig.Address);
            cmd.Parameters.AddWithValue("@captcha_provider", account.CaptchaSettings.Provider);
            cmd.Parameters.AddWithValue("@captcha_api_key", account.CaptchaSettings.ApiKey);
            cmd.Parameters.AddWithValue("@session_state", account.SessionState.ToString());
            cmd.Parameters.AddWithValue("@is_enabled", account.IsEnabled ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var profile in state.BookingProfiles)
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO booking_profiles (id, profile_json) VALUES (@id, @profile_json);";
            cmd.Parameters.AddWithValue("@id", profile.Id.ToString());
            cmd.Parameters.AddWithValue("@profile_json", JsonSerializer.Serialize(profile, JsonOptions));
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var mapping in state.AccountBookingProfiles)
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO account_booking_profiles (account_id, profile_json) VALUES (@account_id, @profile_json);";
            cmd.Parameters.AddWithValue("@account_id", mapping.Key.ToString());
            cmd.Parameters.AddWithValue("@profile_json", JsonSerializer.Serialize(mapping.Value, JsonOptions));
            await cmd.ExecuteNonQueryAsync();
        }

        await SaveSettingAsync(connection, transaction, "scheduler_settings", JsonSerializer.Serialize(state.SchedulerSettings, JsonOptions));
        await SaveSettingAsync(connection, transaction, "enable_auto_refresh", state.EnableAutoRefresh.ToString());
        await SaveSettingAsync(connection, transaction, "use_proxy", state.UseProxy.ToString());

        await transaction.CommitAsync();
    }

    private static async Task SaveSettingAsync(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO app_settings (key, value) VALUES (@key, @value);";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    private SqliteConnection CreateConnection() => new($"Data Source={_dbPath}");

    private async Task<bool> HasAnyDataAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(1) FROM accounts) + (SELECT COUNT(1) FROM booking_profiles) + (SELECT COUNT(1) FROM app_settings);";
        var total = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return total > 0;
    }

    private async Task TryMigrateLegacyJsonAsync()
    {
        try
        {
            await using var stream = File.OpenRead(_legacyJsonPath);
            var legacy = await JsonSerializer.DeserializeAsync<AppStateSnapshot>(stream, JsonOptions);
            if (legacy is not null)
            {
                await SaveAsync(legacy);
            }
        }
        catch
        {
            // Best-effort migration only.
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS accounts (
                id TEXT PRIMARY KEY,
                username TEXT NOT NULL,
                encrypted_password TEXT NOT NULL,
                proxy_address TEXT,
                captcha_provider TEXT,
                captcha_api_key TEXT,
                session_state TEXT NOT NULL,
                is_enabled INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS booking_profiles (
                id TEXT PRIMARY KEY,
                profile_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS account_booking_profiles (
                account_id TEXT PRIMARY KEY,
                profile_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
        _isInitialized = true;
    }
}
