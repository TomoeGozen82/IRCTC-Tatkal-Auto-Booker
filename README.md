# IRCTC Tatkal Auto Booker

A Windows desktop app for automating IRCTC Tatkal train ticket booking. It uses Playwright to drive a real Chromium browser, supports multiple IRCTC accounts, scheduled Tatkal-window starts, and captcha solving via 2Captcha.

Built with **WPF (.NET 8)**, **Material Design**, **Playwright**, and **SQLite**.

---

## Features

- **Multi-account management** — Add, edit, enable/disable IRCTC accounts. Passwords are encrypted with Windows DPAPI.
- **Booking profiles** — Configure route, journey date, quota (Tatkal), train number, class, and passenger details.
- **Browser automation** — Visible Chromium session for monitoring and debugging.
- **Tatkal scheduler** — Schedule bookings to start at a precise time (default: 09:59:50 for the 10:00 AM AC window).
- **Availability refresh loop** — Repeatedly checks for **Book Now** on the target train/class.
- **Captcha solving** — Integrates with [2Captcha](https://2captcha.com/) during passenger review.
- **Persistent state** — Accounts and profiles are saved locally in SQLite.
- **Live logging** — In-app log panel for each booking run.

---

## Requirements

| Requirement | Details |
|---|---|
| OS | Windows 10/11 (x64) |
| Runtime | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for development) |
| Browser | Chromium (installed via Playwright) |
| Optional | 2Captcha API key for automated captcha solving |

---

## Getting Started

### 1. Clone and restore

```powershell
git clone <repository-url>
cd Booking
dotnet restore
```

### 2. Install Playwright browsers

After the first build, install Chromium:

```powershell
dotnet build
pwsh bin\Debug\net8.0-windows\win-x64\playwright.ps1 install chromium
```

> If `pwsh` is not available, use PowerShell 7+ or run the script from the build output folder that matches your configuration.

### 3. Run the app

```powershell
dotnet run
```

Or open the published single-file executable (see [Publish](#publish) below).

---

## Usage

### 1. Add IRCTC accounts

1. Open the app dashboard.
2. Add an account with your IRCTC **User ID** and **password**.
3. Optionally set a proxy address per account.
4. Enable the account when ready to book.

Passwords are stored encrypted in the local SQLite database and can only be decrypted by the same Windows user account.

### 2. Configure a booking

For each account, set up a booking with:

- **From / To** station codes or names
- **Journey date**
- **Quota** (Tatkal)
- **Train number** and **travel class** (e.g. `3A`)
- **Passenger list** (name, age, gender, berth preference)
- **2Captcha API key** (required for captcha step)

You can start immediately or enable the scheduler with a start time such as `09:59:50`.

### 3. Start booking

- **Start All** — Launch jobs for all enabled accounts with configured profiles.
- **Per-account Start** — Run booking for a single account.
- **Stop All** — Cancel active booking runs.

The app opens a visible browser window so you can watch progress in real time.

---

## Booking Flow

The automation performs these steps in order:

1. **Navigate** to `https://www.irctc.co.in/nget/train-search`
2. **Open login modal** — Click **LOGIN / REGISTER**
3. **Enter credentials** — Type username and password one character at a time
4. **Sign in** — Click **SIGN IN** in the login form
5. **Search train** — Fill route, date, and Tatkal quota, then search
6. **Wait for availability** — Refresh until **Book Now** appears for the target train/class
7. **Fill passengers** — Enter passenger details on the booking page
8. **Solve captcha** — Submit captcha image to 2Captcha and continue
9. **Reach payment page** — Stops once the payment page is reached

---

## Tatkal Timing

IRCTC Tatkal booking windows:

| Class | Opens at |
|---|---|
| AC (1A, 2A, 3A, CC, EC, etc.) | 10:00 AM |
| Non-AC (SL, 2S, etc.) | 11:00 AM |

Use the built-in scheduler to start a few seconds before the window (e.g. `09:59:50` for AC Tatkal) so login and search are ready when booking opens.

---

## Data Storage

Application state is stored under:

```
%LocalAppData%\IrctcTatkalAutoBooker\
├── state.db      # SQLite database (accounts, booking profiles)
└── state.json    # Legacy format (auto-migrated on first load)
```

---

## Publish

The project is configured to publish as a self-contained single-file Windows executable:

```powershell
dotnet publish -c Release
```

Output:

```
bin\Release\net8.0-windows\win-x64\publish\Booking.exe
```

After publishing, install Playwright browsers from the publish folder if needed:

```powershell
pwsh bin\Release\net8.0-windows\win-x64\publish\playwright.ps1 install chromium
```

---

## Project Structure

```
Booking/
├── App.xaml / App.xaml.cs          # Application entry
├── MainWindow.xaml                 # Main dashboard UI
├── MainViewModel.cs                # Dashboard logic, account/job orchestration
├── BookingService.cs               # Playwright automation (login, search, book)
├── SchedulerService.cs             # Tatkal start-time scheduler
├── StatePersistenceService.cs      # SQLite persistence
├── BookingSetupWindow/ViewModel    # Per-account booking configuration
├── JobLauncherWindow/ViewModel     # Multi-account job launcher
├── DomainModels.cs                 # Domain entities (account, profile, job)
└── Booking.csproj                  # Project file and dependencies
```

---

## Dependencies

| Package | Purpose |
|---|---|
| [Microsoft.Playwright](https://playwright.dev/dotnet/) | Browser automation |
| [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) | MVVM helpers |
| [MaterialDesignThemes](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) | UI styling |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) | Local database |

---

## Troubleshooting

| Issue | Suggestion |
|---|---|
| Browser does not launch | Run `playwright.ps1 install chromium` from the build/publish output folder |
| Login fails | Confirm IRCTC credentials; check that the login modal opens and fields are filled |
| Captcha errors | Verify your 2Captcha API key and account balance |
| Timeout during booking | Increase default timeout in the booking profile or check IRCTC site availability |
| Schedule did not trigger | Ensure system clock is accurate and the scheduled time is in the future |

---

## Disclaimer

This tool automates interaction with the IRCTC website. Use it responsibly and in compliance with IRCTC terms of service and applicable laws. Automated booking may be restricted or blocked by IRCTC. The authors are not responsible for account suspension, failed bookings, or any misuse of this software.

---

## License

No license file is included in this repository. Add one if you plan to distribute the project.
