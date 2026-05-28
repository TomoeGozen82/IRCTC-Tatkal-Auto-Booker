using System.Net.Http;
using System.Text.Json;
using Microsoft.Playwright;

namespace Booking;

public sealed class BookingService
{
    private static readonly HttpClient HttpClient = new();
    private const int DesktopViewportWidth = 1920;
    private const int DesktopViewportHeight = 1080;

    public event Action<ServiceLog>? LogEmitted;

    public async Task<BookingResult> StartBookingAsync(
        AccountModel account,
        BookingProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            account.UpdateStatus("Initializing");
            Log(account.Username, "Info", "Launching browser session.");

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                // Always run with a visible browser window for easier monitoring/debugging.
                Headless = false,
                Args =
                [
                    "--disable-blink-features=AutomationControlled",
                    "--start-maximized",
                    $"--window-size={DesktopViewportWidth},{DesktopViewportHeight}"
                ]
            });

            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                // Fixed desktop viewport so IRCTC shows the full header (LOGIN / REGISTER), not the mobile hamburger menu.
                ViewportSize = new ViewportSize { Width = DesktopViewportWidth, Height = DesktopViewportHeight },
                ScreenSize = new ScreenSize { Width = DesktopViewportWidth, Height = DesktopViewportHeight },
                IsMobile = false,
                HasTouch = false
            });

            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(profile.DefaultTimeoutMs);
            await page.SetViewportSizeAsync(DesktopViewportWidth, DesktopViewportHeight);

            await LoginAsync(page, account, cancellationToken);
            await SearchTrainAsync(page, account, profile, cancellationToken);
            await EnterBookingLoopAsync(page, account, profile, cancellationToken);
            await FillPassengersAsync(page, account, profile, cancellationToken);
            await SolveCaptchaAndSubmitAsync(page, account, profile, cancellationToken);

            account.UpdateStatus("PaymentReached");
            Log(account.Username, "Success", "Booking reached payment page.");
            return new BookingResult(BookingRunStatus.PaymentReached, "Reached payment page successfully.");
        }
        catch (OperationCanceledException)
        {
            account.UpdateStatus("Stopped");
            Log(account.Username, "Info", "Booking cancelled by user.");
            return new BookingResult(BookingRunStatus.Failed, "Booking cancelled.");
        }
        catch (TimeoutException ex)
        {
            account.UpdateStatus("Failed");
            Log(account.Username, "Error", $"Timeout: {ex.Message}");
            return new BookingResult(BookingRunStatus.Failed, $"Timeout occurred: {ex.Message}");
        }
        catch (Exception ex)
        {
            account.UpdateStatus("Failed");
            Log(account.Username, "Error", $"Unexpected failure: {ex.Message}");
            return new BookingResult(BookingRunStatus.Failed, ex.Message);
        }
    }

    private async Task LoginAsync(IPage page, AccountModel account, CancellationToken cancellationToken)
    {
        account.UpdateStatus("Logging In");
        account.UpdateLastAction("Navigating to IRCTC login.");
        Log(account.Username, "Info", "Navigating to IRCTC login page.");
        cancellationToken.ThrowIfCancellationRequested();

        await page.GotoAsync("https://www.irctc.co.in/nget/train-search", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60_000
        });

        account.UpdateLastAction("Opening login modal.");
        Log(account.Username, "Info", "Opening login modal.");
        await OpenLoginModalAsync(page, cancellationToken);

        var password = account.GetDecryptedPassword();
        if (string.IsNullOrWhiteSpace(account.Username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("IRCTC username or password is missing for this account.");
        }

        account.UpdateLastAction("Entering credentials.");
        Log(account.Username, "Info", "Typing username and password in login modal.");
        await FillLoginFormAndSubmitAsync(page, account.Username, password, cancellationToken);

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        account.UpdateLastAction("Login submitted.");
        Log(account.Username, "Info", "Login submitted.");
    }

    private static async Task FillLoginFormAndSubmitAsync(
        IPage page,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var loginForm = page.Locator("form[formcontrolname='loginForm']");
        await loginForm.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        var usernameField = loginForm.Locator("input[formcontrolname='userId']");
        var passwordField = loginForm.Locator("input[formcontrolname='password']");

        await usernameField.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await passwordField.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        await TypeIntoLoginFieldAsync(page, usernameField, username, cancellationToken);
        await TypeIntoLoginFieldAsync(page, passwordField, password, cancellationToken);

        var signInButton = loginForm
            .Locator("button[type='submit'].search_btn.train_Search")
            .Filter(new LocatorFilterOptions { HasText = "SIGN IN" });

        if (await signInButton.CountAsync() == 0)
        {
            signInButton = loginForm.GetByRole(AriaRole.Button, new() { Name = "SIGN IN", Exact = true });
        }

        await signInButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await signInButton.ScrollIntoViewIfNeededAsync();

        try
        {
            await signInButton.ClickAsync(new LocatorClickOptions { Timeout = 8_000 });
        }
        catch (PlaywrightException)
        {
            await signInButton.EvaluateAsync("node => node.click()");
        }
    }

    private static async Task TypeIntoLoginFieldAsync(
        IPage page,
        ILocator field,
        string text,
        CancellationToken cancellationToken,
        int delayMs = 80)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await field.ScrollIntoViewIfNeededAsync();
        await field.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });

        // Clear any existing value so sequential typing starts from an empty field.
        await field.FillAsync(string.Empty);
        await page.Keyboard.PressAsync("Control+A");
        await page.Keyboard.PressAsync("Backspace");

        await field.PressSequentiallyAsync(text, new LocatorPressSequentiallyOptions { Delay = delayMs });
        await field.DispatchEventAsync("input");
        await field.DispatchEventAsync("change");
        await field.BlurAsync();
    }

    private async Task SearchTrainAsync(
        IPage page,
        AccountModel account,
        BookingProfile profile,
        CancellationToken cancellationToken)
    {
        account.UpdateStatus("Searching");
        account.UpdateLastAction("Filling search criteria.");
        Log(account.Username, "Info", "Setting source, destination, date and Tatkal quota.");
        cancellationToken.ThrowIfCancellationRequested();

        await page.FillAsync("input[aria-controls='pr_id_1_list']", profile.FromStation);
        await page.FillAsync("input[aria-controls='pr_id_2_list']", profile.ToStation);
        await page.FillAsync("input[placeholder='Journey Date(dd-mm-yyyy)*']", profile.JourneyDate.ToString("dd-MM-yyyy"));
        await page.ClickAsync("p-dropdown[formcontrolname='journeyQuota'] .p-dropdown-trigger");
        await page.ClickAsync("li[aria-label='TATKAL']");

        await ClickWithFallbackAsync(page, "button:has-text('Search')", cancellationToken);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        account.UpdateLastAction("Train search submitted.");
        Log(account.Username, "Info", "Train search submitted.");
    }

    private async Task EnterBookingLoopAsync(
        IPage page,
        AccountModel account,
        BookingProfile profile,
        CancellationToken cancellationToken)
    {
        account.UpdateStatus("Watching Availability");
        Log(account.Username, "Info", $"Entering booking loop for {profile.TrainNumber} / {profile.TravelClass}.");

        for (var attempt = 1; attempt <= profile.MaxRefreshAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            account.UpdateLastAction($"Refresh attempt {attempt}/{profile.MaxRefreshAttempts}");

            if (attempt > 1)
            {
                await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            }

            var trainRow = page.Locator($"tr:has-text('{profile.TrainNumber}')");
            var targetClass = trainRow.Locator($"td:has-text('{profile.TravelClass}')");
            var bookNowButton = targetClass.Locator("button:has-text('Book Now')");

            if (await bookNowButton.CountAsync() > 0 && await bookNowButton.First.IsVisibleAsync())
            {
                account.UpdateStatus("Booking");
                account.UpdateLastAction("Book Now available.");
                Log(account.Username, "Success", "Book Now found, opening passenger page.");
                await bookNowButton.First.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                return;
            }

            Log(account.Username, "Info", $"Book Now not available (attempt {attempt}).");
            await Task.Delay(profile.RefreshIntervalMs, cancellationToken);
        }

        throw new TimeoutException("Train/class did not become bookable before refresh limit.");
    }

    private async Task FillPassengersAsync(
        IPage page,
        AccountModel account,
        BookingProfile profile,
        CancellationToken cancellationToken)
    {
        account.UpdateStatus("Passenger Fill");
        account.UpdateLastAction("Entering passenger details.");
        Log(account.Username, "Info", "Filling passenger details.");

        for (var i = 0; i < profile.Passengers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = profile.Passengers[i];
            var row = i + 1;
            await page.FillAsync($"input[formcontrolname='passengerName{row}']", p.Name);
            await page.FillAsync($"input[formcontrolname='passengerAge{row}']", p.Age.ToString());
            await page.SelectOptionAsync($"select[formcontrolname='passengerGender{row}']", p.Gender);
            await page.SelectOptionAsync($"select[formcontrolname='passengerBerthChoice{row}']", p.BerthPreference);
        }

        account.UpdateLastAction("Passenger details entered.");
        Log(account.Username, "Success", "Passenger details submitted.");
    }

    private async Task SolveCaptchaAndSubmitAsync(
        IPage page,
        AccountModel account,
        BookingProfile profile,
        CancellationToken cancellationToken)
    {
        account.UpdateStatus("Captcha");
        account.UpdateLastAction("Waiting for captcha solve.");
        Log(account.Username, "Info", "Solving captcha with 2Captcha.");

        var captchaImage = page.Locator("img.captcha-img");
        await captchaImage.WaitForAsync(new LocatorWaitForOptions { Timeout = profile.DefaultTimeoutMs });
        var captchaBase64 = await captchaImage.EvaluateAsync<string>("img => img.src");
        var solvedCaptcha = await SolveCaptchaAsync(captchaBase64, profile.TwoCaptchaApiKey, cancellationToken);

        await page.FillAsync("input[formcontrolname='captcha']", solvedCaptcha);
        await ClickWithFallbackAsync(page, "button:has-text('Continue')", cancellationToken);

        await page.WaitForURLAsync("**/payment/**", new PageWaitForURLOptions
        {
            Timeout = profile.DefaultTimeoutMs
        });

        account.UpdateLastAction("Payment page reached.");
    }

    private static async Task<string> SolveCaptchaAsync(
        string base64Image,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["key"] = apiKey,
            ["method"] = "base64",
            ["body"] = base64Image.Replace("data:image/png;base64,", string.Empty),
            ["json"] = "1"
        });

        using var submitResponse = await HttpClient.PostAsync("https://2captcha.com/in.php", body, cancellationToken);
        submitResponse.EnsureSuccessStatusCode();
        using var submitJson = JsonDocument.Parse(await submitResponse.Content.ReadAsStringAsync(cancellationToken));

        if (submitJson.RootElement.GetProperty("status").GetInt32() != 1)
        {
            throw new InvalidOperationException($"2Captcha submit failed: {submitJson.RootElement.GetProperty("request").GetString()}");
        }

        var captchaId = submitJson.RootElement.GetProperty("request").GetString()
            ?? throw new InvalidOperationException("2Captcha response did not contain captcha id.");

        for (var attempt = 0; attempt < 24; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            using var pollResponse = await HttpClient.GetAsync(
                $"https://2captcha.com/res.php?key={apiKey}&action=get&id={captchaId}&json=1",
                cancellationToken);
            pollResponse.EnsureSuccessStatusCode();

            using var pollJson = JsonDocument.Parse(await pollResponse.Content.ReadAsStringAsync(cancellationToken));
            var status = pollJson.RootElement.GetProperty("status").GetInt32();
            var request = pollJson.RootElement.GetProperty("request").GetString();

            if (status == 1 && !string.IsNullOrWhiteSpace(request))
            {
                return request;
            }

            if (!string.Equals(request, "CAPCHA_NOT_READY", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"2Captcha polling failed: {request}");
            }
        }

        throw new TimeoutException("Captcha solve timed out.");
    }

    private static async Task OpenLoginModalAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // IRCTC is an Angular SPA; header controls render after DOMContentLoaded.
        await page.WaitForLoadStateAsync(LoadState.Load);
        await TryDismissBlockingOverlaysAsync(page);

        var loginModalInput = page.Locator("form[formcontrolname='loginForm'] input[formcontrolname='userId']");
        if (await loginModalInput.IsVisibleAsync())
        {
            return;
        }

        var loginButtonCandidates = new ILocator[]
        {
            page.GetByLabel("Click here to Login in application"),
            page.Locator("nav.nav-bar a.search_btn.loginText"),
            page.Locator("a.search_btn.loginText"),
            page.GetByRole(AriaRole.Link, new() { Name = "LOGIN / REGISTER", Exact = true })
        };

        foreach (var candidate in loginButtonCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ClickFirstVisibleAsync(candidate, cancellationToken))
            {
                continue;
            }

            try
            {
                await loginModalInput.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 8_000
                });
                return;
            }
            catch (TimeoutException)
            {
                // Try the next selector strategy.
            }
        }

        throw new TimeoutException("Could not open the LOGIN / REGISTER modal.");
    }

    private static async Task TryDismissBlockingOverlaysAsync(IPage page)
    {
        var dismissSelectors = new[]
        {
            "button:has-text('OK')",
            "button:has-text('Close')",
            "button:has-text('Accept')",
            ".modal-dialog button.close",
            "img[alt='Close']"
        };

        foreach (var selector in dismissSelectors)
        {
            var button = page.Locator(selector).First;
            if (await button.IsVisibleAsync())
            {
                try
                {
                    await button.ClickAsync(new LocatorClickOptions { Timeout = 2_000 });
                }
                catch (PlaywrightException)
                {
                    // Ignore and continue.
                }
            }
        }
    }

    private static async Task<bool> ClickFirstVisibleAsync(ILocator locator, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ILocator target;
        var count = await locator.CountAsync();
        if (count == 0)
        {
            return false;
        }

        if (count == 1)
        {
            target = locator.First;
            if (!await target.IsVisibleAsync())
            {
                return false;
            }
        }
        else
        {
            target = locator;
            var foundVisible = false;
            for (var i = 0; i < count; i++)
            {
                var item = locator.Nth(i);
                if (!await item.IsVisibleAsync())
                {
                    continue;
                }

                target = item;
                foundVisible = true;
                break;
            }

            if (!foundVisible)
            {
                return false;
            }
        }

        await target.ScrollIntoViewIfNeededAsync();
        try
        {
            await target.ClickAsync(new LocatorClickOptions { Timeout = 8_000 });
        }
        catch (PlaywrightException)
        {
            await target.EvaluateAsync("node => node.click()");
        }

        return true;
    }

    private static async Task ClickWithFallbackAsync(IPage page, string selector, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var locator = page.Locator(selector).First;
        await locator.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });

        try
        {
            await locator.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
        }
        catch (PlaywrightException)
        {
            await locator.EvaluateAsync("node => node.click()");
        }
    }

    private void Log(string username, string level, string message)
    {
        LogEmitted?.Invoke(new ServiceLog(username, level, message));
    }
}

public sealed record BookingResult(BookingRunStatus Status, string Message);

public enum BookingRunStatus
{
    Success,
    Failed,
    PaymentReached
}

public sealed record ServiceLog(string Username, string Level, string Message);
