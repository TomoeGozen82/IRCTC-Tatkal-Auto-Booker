using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Booking;

public sealed class BookingService
{
    private static readonly HttpClient HttpClient = new();
    private const int DesktopViewportWidth = 1920;
    private const int DesktopViewportHeight = 1080;
    private const string IrctcJourneyDateFormat = "dd/MM/yyyy";

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
        await EnsureLoginModalVisibleAsync(page, cancellationToken);
        await WaitForLoginModalAsync(page, cancellationToken);

        var password = account.GetDecryptedPassword();
        if (string.IsNullOrWhiteSpace(account.Username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("IRCTC username or password is missing for this account.");
        }

        account.UpdateLastAction("Entering credentials.");
        Log(account.Username, "Info", "Typing username and password in login modal.");
        await FillLoginFormAndSubmitAsync(page, account.Username, password, cancellationToken);

        account.UpdateLastAction("Waiting for train search page.");
        Log(account.Username, "Info", "Login submitted. Waiting for BOOK TICKET form.");
        await WaitForTrainSearchFormAsync(page, cancellationToken);
        account.UpdateLastAction("Login complete.");
        Log(account.Username, "Info", "Train search form is ready.");
    }

    private static async Task FillLoginFormAndSubmitAsync(
        IPage page,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await WaitForLoginModalAsync(page, cancellationToken);

        var loginForm = page.Locator("form[formcontrolname='loginForm']");
        var usernameField = await ResolveLoginFieldAsync(
            page,
            loginForm,
            placeholder: "User Name",
            formControlName: "userId",
            cancellationToken);
        var passwordField = await ResolveLoginFieldAsync(
            page,
            loginForm,
            placeholder: "Password",
            formControlName: "password",
            cancellationToken);

        await TypeIntoLoginFieldAsync(page, usernameField, username, cancellationToken);
        await Task.Delay(150, cancellationToken);
        await TypeIntoLoginFieldAsync(page, passwordField, password, cancellationToken);
        await Task.Delay(200, cancellationToken);

        await ClickSignInButtonAsync(page, cancellationToken);
    }

    private static async Task ClickSignInButtonAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Match IRCTC login modal submit button from DOM:
        // <button type="submit" class="search_btn train_Search train_Search_custom_hover">SIGN IN</button>
        var signInCandidates = new ILocator[]
        {
            page.Locator(".ui-dialog-visible form[formcontrolname='loginForm'] button[type='submit'].search_btn.train_Search"),
            page.Locator("form[formcontrolname='loginForm'] button[type='submit'].search_btn.train_Search"),
            page.Locator(".ui-dialog-visible button.search_btn.train_Search.train_Search_custom_hover[type='submit']"),
            page.Locator("form[formcontrolname='loginForm'] button[type='submit']:has-text('SIGN IN')"),
            page.Locator("button.search_btn.train_Search[type='submit']")
        };

        foreach (var candidate in signInCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await candidate.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var button = candidate.Nth(i);
                if (!await button.IsVisibleAsync())
                {
                    continue;
                }

                var text = (await button.InnerTextAsync()).Trim();
                if (!text.Contains("SIGN IN", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await button.ScrollIntoViewIfNeededAsync();

                try
                {
                    await button.ClickAsync(new LocatorClickOptions { Timeout = 3_000, Force = true });
                    return;
                }
                catch (Exception)
                {
                    await button.EvaluateAsync("node => node.click()");
                    return;
                }
            }
        }

        throw new TimeoutException("Could not click the SIGN IN button in the login modal.");
    }

    private static async Task<ILocator> ResolveLoginFieldAsync(
        IPage page,
        ILocator loginForm,
        string placeholder,
        string formControlName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var byPlaceholder = page.GetByPlaceholder(placeholder, new PageGetByPlaceholderOptions { Exact = true });
        if (await byPlaceholder.CountAsync() > 0)
        {
            var visible = byPlaceholder.Locator("visible=true");
            if (await visible.CountAsync() > 0)
            {
                await visible.First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 10_000
                });
                return visible.First;
            }

            await byPlaceholder.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10_000
            });
            return byPlaceholder.First;
        }

        var byFormControl = loginForm.Locator($"input[formcontrolname='{formControlName}']");
        await byFormControl.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000
        });
        return byFormControl.First;
    }

    private static async Task<bool> IsLoginModalVisibleAsync(IPage page)
    {
        if (await page.GetByPlaceholder("User Name", new PageGetByPlaceholderOptions { Exact = true }).IsVisibleAsync())
        {
            return true;
        }

        return await page.Locator("form[formcontrolname='loginForm'] input[formcontrolname='userId']").IsVisibleAsync();
    }

    private static async Task WaitForLoginModalAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var usernameField = page.GetByPlaceholder("User Name", new PageGetByPlaceholderOptions { Exact = true });
        try
        {
            await usernameField.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 15_000
            });
            return;
        }
        catch (TimeoutException)
        {
            await page.Locator("form[formcontrolname='loginForm'] input[formcontrolname='userId']").WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        }
    }

    private static async Task TypeIntoLoginFieldAsync(
        IPage page,
        ILocator field,
        string text,
        CancellationToken cancellationToken,
        int delayMs = 100)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await field.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await field.ScrollIntoViewIfNeededAsync();
        await field.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });
        await field.FocusAsync();

        await page.Keyboard.PressAsync("Control+A");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.TypeAsync(text, new KeyboardTypeOptions { Delay = delayMs });

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
        Log(account.Username, "Info", $"Searching trains: {profile.FromStation} -> {profile.ToStation}, {FormatIrctcJourneyDate(profile.JourneyDate)}, {profile.TravelClass}, {profile.Quota}.");
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(profile.FromStation) || string.IsNullOrWhiteSpace(profile.ToStation))
        {
            throw new InvalidOperationException("From and To station must be set in the booking profile.");
        }

        await DismissAdOverlaysAsync(page, cancellationToken);
        await WaitForTrainSearchFormAsync(page, cancellationToken);

        Log(account.Username, "Info", $"Typing From station: {profile.FromStation}");
        await FillStationAutocompleteAsync(
            page,
            formControlName: "origin",
            ariaLabel: "Enter From station. Input is Mandatory.",
            station: profile.FromStation,
            cancellationToken);

        Log(account.Username, "Info", $"Typing To station: {profile.ToStation}");
        await FillStationAutocompleteAsync(
            page,
            formControlName: "destination",
            ariaLabel: "Enter To station. Input is Mandatory.",
            station: profile.ToStation,
            cancellationToken);

        Log(account.Username, "Info", $"Setting journey date: {FormatIrctcJourneyDate(profile.JourneyDate)}");
        await FillJourneyDateAsync(page, profile.JourneyDate, cancellationToken);

        if (!string.IsNullOrWhiteSpace(profile.TravelClass) &&
            !profile.TravelClass.Equals("All Classes", StringComparison.OrdinalIgnoreCase))
        {
            await SelectPrimeNgDropdownAsync(
                page,
                formControlName: "journeyClass",
                shortCode: profile.TravelClass.Trim(),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(profile.Quota))
        {
            await SelectPrimeNgDropdownAsync(
                page,
                formControlName: "journeyQuota",
                shortCode: profile.Quota.Trim(),
                cancellationToken);
        }

        await ClickSearchTrainsButtonAsync(page, cancellationToken);

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        account.UpdateLastAction("Train search submitted.");
        Log(account.Username, "Info", "Train search submitted.");
    }

    private static async Task WaitForTrainSearchFormAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var originInput = await ResolveStationInputAsync(page, "origin", "Enter From station. Input is Mandatory.");
        await originInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 60_000
        });

        await originInput.ScrollIntoViewIfNeededAsync();
    }

    private static async Task<ILocator> ResolveStationInputAsync(IPage page, string formControlName, string ariaLabel)
    {
        var candidates = new ILocator[]
        {
            page.Locator($"p-autocomplete#{formControlName} input.ui-autocomplete-input"),
            page.Locator($"p-autocomplete[formcontrolname='{formControlName}'] input[role='searchbox']"),
            page.Locator($"p-autocomplete[formcontrolname='{formControlName}'] input[type='text']"),
            page.Locator($"input[aria-label='{ariaLabel}']"),
            page.GetByLabel(ariaLabel, new PageGetByLabelOptions { Exact = true })
        };

        foreach (var candidate in candidates)
        {
            if (await candidate.CountAsync() == 0)
            {
                continue;
            }

            var first = candidate.First;
            if (await first.IsVisibleAsync())
            {
                return first;
            }
        }

        throw new TimeoutException($"Could not find station input for '{formControlName}'.");
    }

    private static async Task FillStationAutocompleteAsync(
        IPage page,
        string formControlName,
        string ariaLabel,
        string station,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(station))
        {
            throw new InvalidOperationException($"Station value is required for '{formControlName}'.");
        }

        var input = await ResolveStationInputAsync(page, formControlName, ariaLabel);
        await input.ScrollIntoViewIfNeededAsync();
        await input.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });

        // Type directly on the autocomplete input (more reliable than page.Keyboard).
        await input.FillAsync(string.Empty);
        await input.PressSequentiallyAsync(station.Trim(), new LocatorPressSequentiallyOptions { Delay = 100 });
        await input.DispatchEventAsync("input");

        await Task.Delay(600, cancellationToken);

        var listId = await input.GetAttributeAsync("aria-controls");
        var listSelectors = new List<string>();
        if (!string.IsNullOrWhiteSpace(listId))
        {
            listSelectors.Add($"#{listId} li");
            listSelectors.Add($"#{listId} .ui-autocomplete-list-item");
        }

        listSelectors.Add(".ui-autocomplete-panel:visible li");
        listSelectors.Add("ul.ui-autocomplete-items li");

        ILocator? firstOption = null;
        foreach (var selector in listSelectors)
        {
            var option = page.Locator(selector).First;
            try
            {
                await option.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 3_000
                });
                firstOption = option;
                break;
            }
            catch (TimeoutException)
            {
                // Try next selector.
            }
        }

        if (firstOption is null)
        {
            // Fallback: pick first highlighted/visible suggestion via keyboard.
            await input.PressAsync("ArrowDown");
            await input.PressAsync("Enter");
        }
        else
        {
            await firstOption.ClickAsync(new LocatorClickOptions { Force = true });
        }

        await Task.Delay(300, cancellationToken);
    }

    private static string FormatIrctcJourneyDate(DateTime journeyDate) =>
        journeyDate.ToString(IrctcJourneyDateFormat, CultureInfo.InvariantCulture);

    private static async Task FillJourneyDateAsync(IPage page, DateTime journeyDate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dateInput = page.Locator("p-calendar[formcontrolname='journeyDate'] input.ui-inputtext, #jDate input.ui-inputtext").First;
        await dateInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await dateInput.ScrollIntoViewIfNeededAsync();
        await dateInput.ClickAsync(new LocatorClickOptions { Force = true });

        // IRCTC expects slashes: 06/12/2026 (not 06-12-2026).
        var formatted = FormatIrctcJourneyDate(journeyDate);

        await dateInput.FillAsync(string.Empty);
        await dateInput.PressSequentiallyAsync(formatted, new LocatorPressSequentiallyOptions { Delay = 80 });

        var currentValue = await dateInput.InputValueAsync();
        if (currentValue.Contains('-', StringComparison.Ordinal) || !currentValue.Contains('/', StringComparison.Ordinal))
        {
            await dateInput.EvaluateAsync(
                """
                (el, value) => {
                    el.value = value;
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                }
                """,
                formatted);
        }

        await dateInput.DispatchEventAsync("input");
        await dateInput.DispatchEventAsync("change");
        await dateInput.PressAsync("Tab");
        await Task.Delay(200, cancellationToken);
    }

    private static async Task SelectPrimeNgDropdownAsync(
        IPage page,
        string formControlName,
        string shortCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dropdown = page.Locator($"p-dropdown[formcontrolname='{formControlName}']");
        await dropdown.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await dropdown.Locator(".ui-dropdown-trigger").ClickAsync(new LocatorClickOptions { Force = true });

        var panel = page.Locator(".ui-dropdown-panel:visible").Last;
        await panel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 8_000 });

        var option = await FindDropdownOptionAsync(panel, shortCode);
        await option.ScrollIntoViewIfNeededAsync();
        await option.ClickAsync(new LocatorClickOptions { Force = true });
        await Task.Delay(200, cancellationToken);
    }

    private static async Task<ILocator> FindDropdownOptionAsync(ILocator panel, string shortCode)
    {
        var normalized = shortCode.Trim();

        // Exact aria-label (works for quota like TATKAL, GENERAL).
        var byAriaLabel = panel.Locator($"li[role='option'][aria-label='{normalized}'], li[aria-label='{normalized}']");
        if (await byAriaLabel.CountAsync() > 0)
        {
            return byAriaLabel.First;
        }

        var byAriaLabelIgnoreCase = panel.Locator("li[role='option'], li.ui-dropdown-item")
            .Filter(new LocatorFilterOptions { HasTextString = normalized });
        if (await byAriaLabelIgnoreCase.CountAsync() > 0)
        {
            return byAriaLabelIgnoreCase.First;
        }

        // Class codes appear as "(3A)", "(2S)" inside full names.
        var bracketPattern = new Regex($@"\(\s*{Regex.Escape(normalized)}\s*\)", RegexOptions.IgnoreCase);
        var allOptions = panel.Locator("li[role='option'], li.ui-dropdown-item");
        var count = await allOptions.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var item = allOptions.Nth(i);
            var text = (await item.InnerTextAsync()).Trim();
            if (bracketPattern.IsMatch(text) ||
                text.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        // Fallback: contains short code anywhere in label text.
        var contains = panel.Locator("li[role='option'], li.ui-dropdown-item")
            .Filter(new LocatorFilterOptions { HasTextRegex = new Regex(Regex.Escape(normalized), RegexOptions.IgnoreCase) });
        if (await contains.CountAsync() > 0)
        {
            return contains.First;
        }

        throw new InvalidOperationException($"Could not find dropdown option matching '{shortCode}'.");
    }

    private static async Task ClickSearchTrainsButtonAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var searchButton = page.Locator("button.search_btn.train_Search")
            .Filter(new LocatorFilterOptions { HasTextRegex = new Regex("Search\\s*Trains", RegexOptions.IgnoreCase) });

        await searchButton.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await searchButton.First.ScrollIntoViewIfNeededAsync();

        try
        {
            await searchButton.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000, Force = true });
        }
        catch (Exception)
        {
            await searchButton.First.EvaluateAsync("node => node.click()");
        }
    }

    private async Task EnterBookingLoopAsync(
        IPage page,
        AccountModel account,
        BookingProfile profile,
        CancellationToken cancellationToken)
    {
        account.UpdateStatus("Watching Availability");
        var trainNumber = NormalizeTrainNumber(profile.TrainNumber);
        var travelClass = profile.TravelClass.Trim();
        Log(account.Username, "Info", $"Entering booking loop for train {trainNumber}, class {travelClass}, date {FormatIrctcJourneyDate(profile.JourneyDate)}.");

        if (string.IsNullOrWhiteSpace(trainNumber) || string.IsNullOrWhiteSpace(travelClass))
        {
            throw new InvalidOperationException("Train number and travel class must be set in the booking profile.");
        }

        await WaitForTrainListAsync(page, cancellationToken);

        for (var attempt = 1; attempt <= profile.MaxRefreshAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            account.UpdateLastAction($"Availability check {attempt}/{profile.MaxRefreshAttempts}");

            try
            {
                Log(account.Username, "Info",
                    $"Step 1: Scanning train cards (div.bull-back.border-all) for number ({trainNumber}).");
                var (trainBlock, trainTitle, cardIndex, cardsScanned) =
                    await GetTrainBlockByNumberAsync(page, trainNumber, cancellationToken);
                Log(account.Username, "Info",
                    $"Step 2: Found train card #{cardIndex + 1} of {cardsScanned} — {trainTitle}.");

                Log(account.Username, "Info", $"Step 3: Selecting class {travelClass}.");
                await SelectClassOnTrainAsync(trainBlock, travelClass, cancellationToken);
                await Task.Delay(800, cancellationToken);

                Log(account.Username, "Info", $"Step 4: Selecting journey date {FormatIrctcJourneyDate(profile.JourneyDate)}.");
                var dateCell = await FindDateCellAsync(trainBlock, profile.JourneyDate, cancellationToken);
                await dateCell.ScrollIntoViewIfNeededAsync();
                await dateCell.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });
                await Task.Delay(800, cancellationToken);

                Log(account.Username, "Info", "Step 5: Clicking Book Now.");
                if (await TryClickBookNowAsync(trainBlock, cancellationToken))
                {
                    account.UpdateStatus("Booking");
                    account.UpdateLastAction("Book Now clicked.");
                    Log(account.Username, "Success", "Book Now clicked — opening passenger page.");
                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                    return;
                }

                Log(account.Username, "Info", $"Book Now is disabled or unavailable (attempt {attempt}). Refreshing availability.");
                if (attempt < profile.MaxRefreshAttempts)
                {
                    await RefreshTrainAvailabilityAsync(trainBlock, cancellationToken);
                }
            }
            catch (TimeoutException ex)
            {
                Log(account.Username, "Info", $"Attempt {attempt}: {ex.Message}");
                if (attempt > 1)
                {
                    await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                    await WaitForTrainListAsync(page, cancellationToken);
                }
            }

            await Task.Delay(profile.RefreshIntervalMs, cancellationToken);
        }

        throw new TimeoutException($"Train {trainNumber} / class {travelClass} did not become bookable before refresh limit.");
    }

    private static string NormalizeTrainNumber(string trainNumber)
    {
        var trimmed = trainNumber.Trim();
        var inBrackets = Regex.Match(trimmed, @"\((\d+)\)");
        if (inBrackets.Success)
        {
            return inBrackets.Groups[1].Value;
        }

        var digits = Regex.Match(trimmed, @"\d{4,6}");
        return digits.Success ? digits.Value : trimmed;
    }

    private const string TrainListContainerSelector = "div.trains-div";
    private const string TrainCardSelector = "div.trains-div div.bull-back.border-all";

    private static async Task WaitForTrainListAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await page.Locator(TrainListContainerSelector).WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000
        });
        await page.Locator(TrainCardSelector).First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000
        });
    }

    private static async Task<(ILocator Block, string Title, int CardIndex, int CardsScanned)> GetTrainBlockByNumberAsync(
        IPage page,
        string trainNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var numberPattern = new Regex($@"\(\s*{Regex.Escape(trainNumber)}\s*\)", RegexOptions.IgnoreCase);
        var trainCards = page.Locator(TrainCardSelector);

        await trainCards.First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        var count = await trainCards.CountAsync();
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var card = trainCards.Nth(i);

            var heading = card.Locator("div.dull-back div.train-heading strong, div.train-heading strong");
            if (await heading.CountAsync() == 0)
            {
                continue;
            }

            var title = (await heading.First.InnerTextAsync()).Trim();
            if (!numberPattern.IsMatch(title))
            {
                continue;
            }

            // All actions live inside app-train-avl-enq within this card.
            var trainEnq = card.Locator("app-train-avl-enq");
            var block = await trainEnq.CountAsync() > 0 ? trainEnq.First : card;
            return (block, title, i, count);
        }

        throw new TimeoutException(
            $"Train ({trainNumber}) not found. Scanned {count} train card(s) matching '{TrainCardSelector}'.");
    }

    private static Regex ClassCodePattern(string travelClass) =>
        new($@"\(\s*{Regex.Escape(travelClass.Trim())}\s*\)", RegexOptions.IgnoreCase);

    private static async Task SelectClassOnTrainAsync(
        ILocator trainBlock,
        string travelClass,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var classPattern = ClassCodePattern(travelClass);

        // Path A: p-tabmenu class tabs (e.g. "AC 3 Tier (3A)") after availability is loaded.
        var classTab = trainBlock.Locator("p-tabmenu li.ui-tabmenuitem").Filter(new LocatorFilterOptions
        {
            HasTextRegex = classPattern
        });

        if (await classTab.CountAsync() > 0)
        {
            var tabLink = classTab.First.Locator("a.ui-menuitem-link, a[role='presentation']");
            await tabLink.ScrollIntoViewIfNeededAsync();
            await tabLink.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });
            return;
        }

        // Path B: class box in white-back table only (not date cells in td.link).
        var classBox = trainBlock.Locator("div.white-back table td div.pre-avl").Filter(new LocatorFilterOptions
        {
            HasTextRegex = classPattern
        });

        if (await classBox.CountAsync() > 0)
        {
            var box = classBox.First;
            await box.ScrollIntoViewIfNeededAsync();
            await box.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });
            await Task.Delay(400, cancellationToken);

            var refreshInBox = box.Locator("text=/^\\s*Refresh\\s*$/i");
            if (await refreshInBox.CountAsync() > 0 && await refreshInBox.First.IsVisibleAsync())
            {
                await refreshInBox.First.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 3_000 });
                await Task.Delay(800, cancellationToken);
            }

            return;
        }

        throw new TimeoutException($"Could not find class '{travelClass}' tab or box on this train.");
    }

    private static IEnumerable<string> BuildDateLabelCandidates(DateTime journeyDate)
    {
        yield return journeyDate.ToString("ddd, d MMM", CultureInfo.InvariantCulture);
        yield return journeyDate.ToString("ddd, dd MMM", CultureInfo.InvariantCulture);
        yield return $"{journeyDate.Day} {journeyDate:MMM}";
        yield return $"{journeyDate.Day:00} {journeyDate:MMM}";
        yield return FormatIrctcJourneyDate(journeyDate);
        yield return journeyDate.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);
    }

    private static async Task<ILocator> FindDateCellAsync(
        ILocator trainBlock,
        DateTime journeyDate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Date row appears only after class is loaded (dull-back section, td.link cells).
        var dateCells = trainBlock.Locator("div.dull-back table td.link div.pre-avl");
        try
        {
            await dateCells.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 12_000
            });
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                "Date availability row not visible yet — class may need a Refresh click first.");
        }

        var labels = BuildDateLabelCandidates(journeyDate).ToList();
        var count = await dateCells.CountAsync();

        for (var i = 0; i < count; i++)
        {
            var cell = dateCells.Nth(i);
            if (!await cell.IsVisibleAsync())
            {
                continue;
            }

            var text = (await cell.InnerTextAsync()).Trim();
            foreach (var label in labels)
            {
                if (text.Contains(label, StringComparison.OrdinalIgnoreCase))
                {
                    return cell;
                }
            }
        }

        var dayMonthPattern = new Regex(
            $@"\b{journeyDate.Day}\b.*\b{journeyDate:MMM}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        for (var i = 0; i < count; i++)
        {
            var cell = dateCells.Nth(i);
            if (!await cell.IsVisibleAsync())
            {
                continue;
            }

            var text = (await cell.InnerTextAsync()).Trim();
            if (dayMonthPattern.IsMatch(text))
            {
                return cell;
            }
        }

        throw new TimeoutException(
            $"Could not find date cell for {journeyDate:ddd, d MMM} (e.g. Sat, 30 May) on this train.");
    }

    private static async Task<bool> TryClickBookNowAsync(ILocator trainBlock, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bookNowButtons = trainBlock.Locator("button.train_Search").Filter(new LocatorFilterOptions
        {
            HasTextRegex = new Regex("Book\\s*Now", RegexOptions.IgnoreCase)
        });

        var count = await bookNowButtons.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var button = bookNowButtons.Nth(i);
            if (!await button.IsVisibleAsync())
            {
                continue;
            }

            var isDisabled = await button.EvaluateAsync<bool>(
                "el => el.classList.contains('disable-book') || el.disabled");
            if (isDisabled)
            {
                continue;
            }

            await button.ScrollIntoViewIfNeededAsync();
            try
            {
                await button.ClickAsync(new LocatorClickOptions { Timeout = 5_000, Force = true });
            }
            catch (Exception)
            {
                await button.EvaluateAsync("node => node.click()");
            }

            return true;
        }

        return false;
    }

    private static async Task RefreshTrainAvailabilityAsync(ILocator trainBlock, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var refreshIcon = trainBlock.Locator("span.fa-repeat, span.fa-refresh");
        if (await refreshIcon.CountAsync() > 0 && await refreshIcon.First.IsVisibleAsync())
        {
            await refreshIcon.First.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 3_000 });
            await Task.Delay(1_000, cancellationToken);
            return;
        }

        var refreshLink = trainBlock.Locator("text=/Refresh/i");
        if (await refreshLink.CountAsync() > 0 && await refreshLink.First.IsVisibleAsync())
        {
            await refreshLink.First.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 3_000 });
            await Task.Delay(1_000, cancellationToken);
        }
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

    private static async Task EnsureLoginModalVisibleAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await page.WaitForLoadStateAsync(LoadState.Load);

        if (await IsLoginModalVisibleAsync(page))
        {
            return;
        }

        await DismissAdOverlaysAsync(page, cancellationToken);

        if (await IsLoginModalVisibleAsync(page))
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

            if (await IsLoginModalVisibleAsync(page))
            {
                return;
            }

            await TryOpenLoginWithJsClickAsync(candidate, cancellationToken);
            await Task.Delay(400, cancellationToken);

            if (await IsLoginModalVisibleAsync(page))
            {
                return;
            }
        }

        if (!await IsLoginModalVisibleAsync(page))
        {
            throw new TimeoutException("Could not open the LOGIN / REGISTER modal.");
        }
    }

    private static async Task TryOpenLoginWithJsClickAsync(ILocator locator, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var count = await locator.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var target = locator.Nth(i);
            if (!await target.IsVisibleAsync())
            {
                continue;
            }

            await target.EvaluateAsync("node => node.click()");
            return;
        }
    }

    private static async Task DismissAdOverlaysAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (await IsLoginModalVisibleAsync(page))
        {
            return;
        }

        // Only close dialogs that are NOT the login modal (Escape would close login too).
        var nonLoginDialogs = page.Locator(".ui-dialog-visible").Filter(new LocatorFilterOptions
        {
            HasNot = page.Locator("form[formcontrolname='loginForm']")
        });

        var closeButtons = nonLoginDialogs.Locator(
            ".ui-dialog-titlebar-close, button:has-text('OK'), button:has-text('Close'), button:has-text('Accept')");
        var count = await closeButtons.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var button = closeButtons.Nth(i);
            if (!await button.IsVisibleAsync())
            {
                continue;
            }

            try
            {
                await button.ClickAsync(new LocatorClickOptions { Timeout = 1_500, Force = true });
            }
            catch (Exception)
            {
                // Ignore and continue.
            }
        }

        if (await IsLoginModalVisibleAsync(page))
        {
            return;
        }

        // Remove only backdrop masks that block the header, not the login dialog content.
        await page.EvaluateAsync("""
            () => {
                const hasLoginForm = document.querySelector("form[formcontrolname='loginForm']");
                if (hasLoginForm) {
                    return;
                }

                document.querySelectorAll(
                    '.ui-dialog-mask, .ui-widget-overlay.ui-dialog-visible'
                ).forEach(el => el.remove());
                document.body.classList.remove('ui-dialog-mask-scrollblocker');
                document.body.style.overflow = '';
            }
            """);
    }

    private static async Task<bool> ClickFirstVisibleAsync(
        IPage page,
        ILocator locator,
        CancellationToken cancellationToken)
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
            await target.ClickAsync(new LocatorClickOptions { Timeout = 2_000 });
            return true;
        }
        catch (Exception)
        {
            // Overlay (ui-dialog-mask) often blocks normal clicks on IRCTC.
        }

        try
        {
            await target.ClickAsync(new LocatorClickOptions { Timeout = 2_000, Force = true });
            return true;
        }
        catch (Exception)
        {
            // Fall through to DOM click.
        }

        await target.EvaluateAsync("node => node.click()");
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
