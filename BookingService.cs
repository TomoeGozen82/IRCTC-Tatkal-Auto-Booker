using System.Globalization;
using System.Net.Http;
using System.Text;
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
        IPage? page = null;
        BrowserSessionDiagnostics? browserSession = null;

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

            page = await context.NewPageAsync();
            browserSession = new BrowserSessionDiagnostics();
            browserSession.Attach(browser, page);
            browserSession.Begin();

            page.SetDefaultTimeout(profile.DefaultTimeoutMs);
            await page.SetViewportSizeAsync(DesktopViewportWidth, DesktopViewportHeight);

            await LoginAsync(page, account, cancellationToken);
            await SearchTrainAsync(page, account, profile, cancellationToken);
            await EnterBookingLoopAsync(page, browserSession, account, profile, cancellationToken);
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
        catch (BookingAutomationException ex)
        {
            account.UpdateStatus("Failed");
            Log(account.Username, "Error", ex.Message);
            return new BookingResult(BookingRunStatus.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            account.UpdateStatus("Failed");
            var detail = browserSession?.DescribeFailure(page, ex)
                         ?? FormatExceptionChain(ex);
            Log(account.Username, "Error", detail);
            return new BookingResult(BookingRunStatus.Failed, detail);
        }
        finally
        {
            browserSession?.End();
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

        await RunSearchStepAsync(
            account,
            $"Search step 1/7: typing From station {profile.FromStation}",
            "FillFromStation",
            () => FillStationAutocompleteAsync(
            page,
            formControlName: "origin",
            ariaLabel: "Enter From station. Input is Mandatory.",
            station: profile.FromStation,
            cancellationToken),
            cancellationToken);

        await RunSearchStepAsync(
            account,
            $"Search step 2/7: typing To station {profile.ToStation}",
            "FillToStation",
            () => FillStationAutocompleteAsync(
            page,
            formControlName: "destination",
            ariaLabel: "Enter To station. Input is Mandatory.",
            station: profile.ToStation,
            cancellationToken),
            cancellationToken);

        await RunSearchStepAsync(
            account,
            $"Search step 3/7: setting journey date {FormatIrctcJourneyDate(profile.JourneyDate)}",
            "FillJourneyDate",
            () => FillJourneyDateAsync(page, profile.JourneyDate, cancellationToken),
            cancellationToken);

        await RunSearchStepAsync(
            account,
            "Search step 4/7: closing date picker overlay",
            "CloseDatePicker",
            async () =>
            {
                await page.Keyboard.PressAsync("Escape");
                await Task.Delay(400, cancellationToken);
            },
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(profile.TravelClass) &&
            !profile.TravelClass.Equals("All Classes", StringComparison.OrdinalIgnoreCase))
        {
            var travelClass = profile.TravelClass.Trim();
            await RunSearchStepAsync(
                account,
                $"Search step 5/7: selecting travel class {travelClass}",
                "SelectJourneyClass",
                () => SelectPrimeNgDropdownAsync(page, "journeyClass", travelClass, cancellationToken),
                cancellationToken);
        }
        else
        {
            Log(account.Username, "Info", "Search step 5/7: skipping class (All Classes).");
        }

        if (!string.IsNullOrWhiteSpace(profile.Quota))
        {
            var quota = profile.Quota.Trim();
            await RunSearchStepAsync(
                account,
                $"Search step 5b/7: selecting quota {quota}",
                "SelectJourneyQuota",
                () => SelectPrimeNgDropdownAsync(page, "journeyQuota", quota, cancellationToken),
                cancellationToken);
        }

        await RunSearchStepAsync(
            account,
            "Search step 7/7: clicking Search Trains",
            "ClickSearchTrains",
            () => ClickSearchTrainsButtonAsync(page, cancellationToken),
            cancellationToken);

        Log(account.Username, "Info", "Waiting for train results list (div.trains-div)...");
        await WaitForTrainListAsync(page, cancellationToken);

        account.UpdateLastAction("Train search submitted.");
        Log(account.Username, "Info", "Train results loaded. Entering availability loop.");
    }

    private async Task RunSearchStepAsync(
        AccountModel account,
        string stepLog,
        string stepId,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Log(account.Username, "Info", stepLog);

        try
        {
            await action();
        }
        catch (BookingAutomationException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw new BookingAutomationException(
                stepId,
                $"{stepLog} — timed out after 20s. The control may be hidden behind a popup or the value may be invalid.");
        }
        catch (InvalidOperationException ex)
        {
            throw new BookingAutomationException(stepId, ex.Message);
        }
        catch (PlaywrightException ex) when (IsPlaywrightTimeout(ex))
        {
            throw new BookingAutomationException(stepId, $"{stepLog} — {ex.Message}");
        }
    }

    private static bool IsPlaywrightTimeout(PlaywrightException ex) =>
        ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase);

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

        var dropdown = page.Locator(
            $"p-dropdown[formcontrolname='{formControlName}'], " +
            $"p-dropdown[formcontrolname='{formControlName.ToLowerInvariant()}']");
        await dropdown.First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 15_000
        });

        await SelectPrimeNgDropdownLocatorAsync(page, dropdown.First, formControlName, shortCode, cancellationToken);
    }

    private static async Task SelectPrimeNgDropdownLocatorAsync(
        IPage page,
        ILocator dropdown,
        string fieldLabel,
        string shortCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await dropdown.ScrollIntoViewIfNeededAsync();

        var trigger = dropdown.Locator(".ui-dropdown-trigger, .p-dropdown-trigger, [role='button']").First;
        await trigger.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });

        ILocator? panel = null;
        var panelSelectors = new[]
        {
            ".ui-dropdown-panel:visible",
            ".p-dropdown-panel:visible",
            "div[role='listbox']:visible"
        };

        foreach (var selector in panelSelectors)
        {
            var candidate = page.Locator(selector).Last;
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 5_000
                });
                panel = candidate;
                break;
            }
            catch (TimeoutException)
            {
                // Try next panel selector.
            }
        }

        if (panel is null)
        {
            throw new InvalidOperationException(
                $"Dropdown '{fieldLabel}' opened but no options panel appeared. " +
                "A calendar or overlay may be blocking the form.");
        }

        ILocator option;
        try
        {
            option = await FindDropdownOptionAsync(panel, shortCode);
        }
        catch (InvalidOperationException)
        {
            var labels = await CollectDropdownOptionLabelsAsync(panel);
            throw new InvalidOperationException(
                $"Could not find '{shortCode}' in {fieldLabel} dropdown. " +
                $"Options visible: {(labels.Count > 0 ? string.Join(", ", labels) : "none")}.");
        }

        await option.ScrollIntoViewIfNeededAsync();
        await option.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });
        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(200, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> CollectDropdownOptionLabelsAsync(ILocator panel)
    {
        var items = panel.Locator("li[role='option'], li.ui-dropdown-item, li.p-dropdown-item");
        var count = await items.CountAsync();
        var labels = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            labels.Add((await items.Nth(i).InnerTextAsync()).Trim());
        }

        return labels;
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
        BrowserSessionDiagnostics browserSession,
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
                browserSession.EnsurePageOpen(page, $"availability attempt {attempt}");

                Log(account.Username, "Info",
                    $"Step 1: Finding train card for ({trainNumber}) [STATE 1 — initial card].");
                var trainCard = await FindTrainCardAsync(page, trainNumber, cancellationToken);
                var trainTitle = await GetTrainCardTitleAsync(trainCard);
                Log(account.Username, "Info", $"Step 2: Scoped to train card — {trainTitle}.");

                Log(account.Username, "Info", $"Step 3: Clicking class {travelClass} (div.pre-avl, .First).");
                await SelectClassAsync(trainCard, travelClass, cancellationToken);
                Log(account.Username, "Info", "Step 3b: Expanded availability loaded [STATE 2 — p-tabmenu / dates].");

                Log(account.Username, "Info",
                    $"Step 4: Selecting date {profile.JourneyDate:ddd, d MMM}.");
                await SelectDateAsync(trainCard, profile.JourneyDate, cancellationToken);

                var availability = await GetAvailabilityAsync(trainCard, cancellationToken);
                Log(account.Username, "Info", $"Step 5: Availability on selected date — {availability}.");

                Log(account.Username, "Info", "Step 6: Clicking Book Now.");
                if (await ClickBookNowAsync(trainCard, cancellationToken))
                {
                    account.UpdateStatus("Booking");
                    account.UpdateLastAction("Book Now clicked.");
                    Log(account.Username, "Success", "Book Now clicked.");

                    Log(account.Username, "Info", "Step 6b: Checking for station mismatch confirmation dialog.");
                    await AcceptIrctcConfirmationIfPresentAsync(page, account, cancellationToken);

                    Log(account.Username, "Info", "Opening passenger page.");
                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                    return;
                }

                Log(account.Username, "Info",
                    $"Book Now disabled or not ready (attempt {attempt}). Status: {availability}. Refreshing.");
                if (attempt < profile.MaxRefreshAttempts)
                {
                    await RefreshTrainAvailabilityAsync(trainCard, cancellationToken);
                }
            }
            catch (BookingAutomationException ex)
            {
                Log(account.Username, "Error", ex.Message);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (browserSession.IsUserClosedBrowserDuringSession(page, ex))
                {
                    var browserDetail = browserSession.DescribeFailure(page, ex);
                    Log(account.Username, "Error", browserDetail);
                    throw new PlaywrightException(browserDetail, ex);
                }

                var stepDetail = browserSession.DescribeFailure(
                    page,
                    ex,
                    $"Train booking step failed on attempt {attempt}/{profile.MaxRefreshAttempts}");
                Log(account.Username, "Error", stepDetail);

                if (ex is TimeoutException && attempt > 1 && !page.IsClosed)
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
    private const string TrainCardSelector = "div.form-group.no-pad.col-xs-12.bull-back.border-all";

    private static async Task WaitForTrainListAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await page.Locator(TrainListContainerSelector).WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30_000
            });
            await page.Locator($"{TrainListContainerSelector} {TrainCardSelector}").First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 30_000
            });
        }
        catch (TimeoutException)
        {
            throw new BookingAutomationException(
                "WaitForTrainList",
                "Train search results did not load (no div.trains-div or train cards appeared within 30 seconds). " +
                "Check that Search Trains completed and IRCTC returned results.");
        }
    }

    /// <summary>STATE 1 — locate the single train card wrapper; all later steps stay scoped here.</summary>
    private static async Task<ILocator> FindTrainCardAsync(
        IPage page,
        string trainNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var trainCards = page.Locator(TrainCardSelector);
        var count = await trainCards.CountAsync();
        if (count == 0)
        {
            throw new BookingAutomationException(
                "FindTrainCard",
                "No train cards found on the results page. The route/date search may have returned zero trains.");
        }

        var numbersOnPage = await CollectTrainNumbersOnPageAsync(page);

        for (var i = 0; i < count; i++)
        {
            var card = trainCards.Nth(i);
            var title = await TryGetTrainCardTitleAsync(card);
            if (title is null)
            {
                continue;
            }

            if (TrainTitleMatchesNumber(title, trainNumber))
            {
                return card;
            }
        }

        throw new BookingAutomationException(
            "FindTrainCard",
            $"Train ({trainNumber}) was not found in the results list. " +
            $"Scanned {count} train card(s). Numbers on page: {string.Join(", ", numbersOnPage)}.");
    }

    private static bool TrainTitleMatchesNumber(string title, string trainNumber) =>
        title.Contains($"({trainNumber})", StringComparison.OrdinalIgnoreCase) ||
        title.Contains(trainNumber, StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<string>> CollectTrainNumbersOnPageAsync(IPage page)
    {
        var headings = page.Locator($"{TrainCardSelector} div.train-heading strong");
        var count = await headings.CountAsync();
        var numbers = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            var title = (await headings.Nth(i).InnerTextAsync()).Trim();
            var match = Regex.Match(title, @"\((\d+)\)");
            numbers.Add(match.Success ? match.Groups[1].Value : title);
        }

        return numbers;
    }

    private static async Task<string?> TryGetTrainCardTitleAsync(ILocator trainCard)
    {
        var heading = trainCard.Locator("div.train-heading strong");
        if (await heading.CountAsync() == 0)
        {
            return null;
        }

        return (await heading.First.InnerTextAsync()).Trim();
    }

    private static async Task<string> GetTrainCardTitleAsync(ILocator trainCard)
    {
        return await TryGetTrainCardTitleAsync(trainCard)
               ?? throw new BookingAutomationException("FindTrainCard", "Train card has no heading text.");
    }

    /// <summary>STATE 1 → 2: click class box; use .First to avoid strict-mode violation after tabs appear.</summary>
    private static async Task SelectClassAsync(
        ILocator trainCard,
        string travelClass,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var classCode = travelClass.Trim();
        var trainTitle = await GetTrainCardTitleAsync(trainCard);

        var classCell = trainCard.Locator("div.pre-avl").Filter(new LocatorFilterOptions
        {
            HasText = classCode
        });

        if (await classCell.CountAsync() == 0)
        {
            var available = await CollectClassLabelsOnCardAsync(trainCard);
            throw new BookingAutomationException(
                "SelectClass",
                $"Class '{classCode}' not found on train {trainTitle}. " +
                $"Classes on card: {(available.Count > 0 ? string.Join(", ", available) : "none visible")}.");
        }

        try
        {
            await classCell.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10_000
            });
        }
        catch (TimeoutException)
        {
            throw new BookingAutomationException(
                "SelectClass",
                $"Class '{classCode}' exists on train {trainTitle} but did not become visible within 10 seconds.");
        }

        await classCell.First.ScrollIntoViewIfNeededAsync();
        await classCell.First.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });

        await WaitForExpandedAvailabilityAsync(trainCard, trainTitle, classCode, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> CollectClassLabelsOnCardAsync(ILocator trainCard)
    {
        var labels = new List<string>();
        var classBoxes = trainCard.Locator("div.white-back div.pre-avl strong");
        var boxCount = await classBoxes.CountAsync();
        for (var i = 0; i < boxCount; i++)
        {
            labels.Add((await classBoxes.Nth(i).InnerTextAsync()).Trim());
        }

        var tabs = trainCard.Locator("p-tabmenu li.ui-tabmenuitem");
        var tabCount = await tabs.CountAsync();
        for (var i = 0; i < tabCount; i++)
        {
            labels.Add((await tabs.Nth(i).InnerTextAsync()).Trim());
        }

        return labels;
    }

    /// <summary>Waits for Angular to render STATE 2 (tab menu and/or date row).</summary>
    private static async Task WaitForExpandedAvailabilityAsync(
        ILocator trainCard,
        string trainTitle,
        string classCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tabMenu = trainCard.Locator("p-tabmenu");
        try
        {
            await tabMenu.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 15_000
            });
            return;
        }
        catch (TimeoutException)
        {
            var refreshLink = trainCard.Locator("div.white-back div.pre-avl").Locator("text=/Refresh/i");
            if (await refreshLink.CountAsync() > 0 && await refreshLink.First.IsVisibleAsync())
            {
                await refreshLink.First.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });
            }

            try
            {
                await tabMenu.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 15_000
                });
                return;
            }
            catch (TimeoutException)
            {
                throw new BookingAutomationException(
                    "WaitForAvailability",
                    $"Clicked class '{classCode}' on train {trainTitle}, but the availability panel " +
                    "(p-tabmenu / date row) did not expand within 30 seconds. Try Refresh on IRCTC.");
            }
        }
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

    /// <summary>STATE 2: pick journey date card (e.g. Sat, 30 May) inside the same train card.</summary>
    private static async Task SelectDateAsync(
        ILocator trainCard,
        DateTime journeyDate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trainTitle = await GetTrainCardTitleAsync(trainCard);
        var dateLabelsOnCard = await CollectDateLabelsOnCardAsync(trainCard);

        foreach (var label in BuildDateLabelCandidates(journeyDate))
        {
            var dateCell = trainCard.Locator("div.pre-avl").Filter(new LocatorFilterOptions
            {
                HasText = label
            });

            if (await dateCell.CountAsync() == 0)
            {
                continue;
            }

            await dateCell.First.ScrollIntoViewIfNeededAsync();
            await dateCell.First.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });
            await Task.Delay(400, cancellationToken);
            return;
        }

        throw new BookingAutomationException(
            "SelectDate",
            $"Date {journeyDate:ddd, d MMM} not found on train {trainTitle}. " +
            $"Dates on card: {(dateLabelsOnCard.Count > 0 ? string.Join(", ", dateLabelsOnCard) : "none visible — expand class first")}.");
    }

    private static async Task<IReadOnlyList<string>> CollectDateLabelsOnCardAsync(ILocator trainCard)
    {
        var labels = new List<string>();
        var dateHeaders = trainCard.Locator("td.link div.pre-avl strong, div.pre-avl.selected-class strong");
        var count = await dateHeaders.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var text = (await dateHeaders.Nth(i).InnerTextAsync()).Trim();
            if (text.Contains(',') || text.Contains("May", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Jun", StringComparison.OrdinalIgnoreCase))
            {
                labels.Add(text);
            }
        }

        return labels;
    }

    /// <summary>Reads availability text from the selected date block (AVAILABLE, WL67, etc.).</summary>
    private static async Task<string> GetAvailabilityAsync(
        ILocator trainCard,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var selected = trainCard.Locator("div.pre-avl.selected-class");
        if (await selected.CountAsync() == 0)
        {
            return "Unknown";
        }

        return (await selected.First.InnerTextAsync()).Trim();
    }

    private static async Task<bool> ClickBookNowAsync(ILocator trainCard, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bookNow = trainCard.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions
        {
            NameRegex = new Regex("Book\\s*Now", RegexOptions.IgnoreCase)
        });

        if (await bookNow.CountAsync() == 0)
        {
            return false;
        }

        var button = bookNow.First;
        if (!await button.IsVisibleAsync())
        {
            return false;
        }

        var isDisabled = await button.EvaluateAsync<bool>(
            "el => el.classList.contains('disable-book') || el.disabled");
        if (isDisabled)
        {
            return false;
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

    /// <summary>
    /// After Book Now, IRCTC may show a station-mismatch confirm dialog (NDLS→CSTM search vs NZM→BDTS train). Click Yes.
    /// </summary>
    private async Task AcceptIrctcConfirmationIfPresentAsync(
        IPage page,
        AccountModel account,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dialog = page.Locator("div.ui-confirmdialog").Filter(new LocatorFilterOptions
        {
            HasTextRegex = new Regex("Confirmation|continue with", RegexOptions.IgnoreCase)
        });

        try
        {
            await dialog.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 8_000
            });
        }
        catch (TimeoutException)
        {
            Log(account.Username, "Info", "No confirmation dialog (proceeding).");
            return;
        }

        var messageLocator = dialog.First.Locator("span.ui-confirmdialog-message");
        var message = await messageLocator.CountAsync() > 0
            ? (await messageLocator.InnerTextAsync()).Trim()
            : (await dialog.First.InnerTextAsync()).Trim();
        Log(account.Username, "Info", $"Confirmation dialog: {message}");

        var yesButton = dialog.First.Locator("button.ui-confirmdialog-acceptbutton");
        if (await yesButton.CountAsync() == 0)
        {
            yesButton = dialog.First.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Yes" });
        }

        await yesButton.First.ScrollIntoViewIfNeededAsync();
        try
        {
            await yesButton.First.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });
        }
        catch (Exception)
        {
            await yesButton.First.EvaluateAsync("node => node.click()");
        }

        Log(account.Username, "Success", "Clicked Yes on confirmation dialog.");

        await dialog.First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = 15_000
        });
    }

    private static async Task WaitForExpandedAvailabilityAsync(
        ILocator trainCard,
        CancellationToken cancellationToken)
    {
        var trainTitle = await GetTrainCardTitleAsync(trainCard);
        await WaitForExpandedAvailabilityAsync(trainCard, trainTitle, "?", cancellationToken);
    }

    private static async Task RefreshTrainAvailabilityAsync(ILocator trainCard, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var refreshIcon = trainCard.Locator("span.fa-repeat");
        if (await refreshIcon.CountAsync() > 0 && await refreshIcon.First.IsVisibleAsync())
        {
            await refreshIcon.First.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 3_000 });
            await Task.Delay(1_000, cancellationToken);
            await WaitForExpandedAvailabilityAsync(trainCard, cancellationToken);
            return;
        }

        var refreshLink = trainCard.Locator("text=/Refresh/i");
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
        Log(account.Username, "Info", "Passenger page: waiting for app-passenger form.");

        await AcceptIrctcConfirmationIfPresentAsync(page, account, cancellationToken);

        var passengerPanel = page.Locator("p-panel .p-heading, span.ui-panel-title")
            .Filter(new LocatorFilterOptions { HasTextRegex = new Regex("Passenger\\s*Details", RegexOptions.IgnoreCase) });
        if (await passengerPanel.CountAsync() > 0)
        {
            Log(account.Username, "Info", "Passenger Details panel found.");
        }

        await page.Locator("app-passenger").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000
        });

        for (var i = 0; i < profile.Passengers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = profile.Passengers[i];

            if (i > 0)
            {
                Log(account.Username, "Info", $"Passenger {i + 1}: clicking + Add Passenger.");
                await ClickAddPassengerAsync(page, cancellationToken);
                await page.Locator("app-passenger").Nth(i).WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 15_000
                });
            }

            var genderCode = PassengerValueNormalizer.ToIrctcGenderCode(p.Gender);
            var nationalityCode = PassengerValueNormalizer.ToIrctcNationalityCode(p.Country);
            var berthCode = PassengerValueNormalizer.ToIrctcBerthCode(p.Berth);

            Log(account.Username, "Info",
                $"Passenger {i + 1}/{profile.Passengers.Count}: name={p.Name}, age={p.Age}, " +
                $"gender={genderCode}, nationality={nationalityCode}, berth={(string.IsNullOrEmpty(berthCode) ? "No Preference" : berthCode)}.");

            await FillPassengerRowAsync(
                page, i, p.Name, p.Age, genderCode, nationalityCode, berthCode, p.Country, cancellationToken);
        }

        account.UpdateLastAction("Passenger details entered.");
        Log(account.Username, "Success", $"Passenger details filled ({profile.Passengers.Count} row(s)).");
    }

    private static async Task ClickAddPassengerAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var addPassenger = page.Locator("a").Filter(new LocatorFilterOptions
        {
            Has = page.Locator("span.prenext").Filter(new LocatorFilterOptions
            {
                HasTextRegex = new Regex(@"^\+\s*Add\s+Passenger\s*$", RegexOptions.IgnoreCase)
            })
        });

        if (await addPassenger.CountAsync() == 0)
        {
            addPassenger = page.GetByText("+ Add Passenger", new PageGetByTextOptions { Exact = true });
        }

        await addPassenger.First.ScrollIntoViewIfNeededAsync();
        await addPassenger.First.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5_000 });
        await Task.Delay(800, cancellationToken);
    }

    private static async Task FillPassengerRowAsync(
        IPage page,
        int passengerIndex,
        string name,
        int age,
        string genderCode,
        string nationalityCode,
        string berthCode,
        string countryLabel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var trimmedName = name.Trim();
        if (trimmedName.Length < 3 || trimmedName.Length > 16)
        {
            throw new BookingAutomationException(
                "FillPassengerName",
                $"Passenger {passengerIndex + 1} name must be 3–16 characters (IRCTC rule). Got length {trimmedName.Length}.");
        }

        var passengerForm = page.Locator("app-passenger").Nth(passengerIndex);
        await passengerForm.ScrollIntoViewIfNeededAsync();

        var nameInput = passengerForm.Locator(
            "p-autocomplete[formcontrolname='passengerName'] input.ui-autocomplete-input, input[placeholder='Name']");
        await nameInput.First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000
        });

        var nameField = nameInput.First;
        await nameField.ClickAsync(new LocatorClickOptions { Force = true });
        await nameField.FillAsync(string.Empty);
        await nameField.PressSequentiallyAsync(trimmedName, new LocatorPressSequentiallyOptions { Delay = 60 });
        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(200, cancellationToken);

        var ageInput = passengerForm.Locator("input[formcontrolname='passengerAge']");
        await ageInput.FillAsync(age.ToString(CultureInfo.InvariantCulture));

        await passengerForm.Locator("select[formcontrolname='passengerGender']")
            .SelectOptionAsync(new SelectOptionValue { Value = genderCode });

        var nationalitySelect = passengerForm.Locator("select[formcontrolname='passengerNationality']");
        try
        {
            await nationalitySelect.SelectOptionAsync(new SelectOptionValue { Value = nationalityCode });
        }
        catch (PlaywrightException)
        {
            await nationalitySelect.SelectOptionAsync(new SelectOptionValue
            {
                Label = PassengerValueNormalizer.NormalizeCountry(countryLabel)
            });
        }

        var berthSelect = passengerForm.Locator("select[formcontrolname='passengerBerthChoice']");
        if (string.IsNullOrEmpty(berthCode))
        {
            await berthSelect.SelectOptionAsync(new SelectOptionValue { Index = 0 });
        }
        else
        {
            await berthSelect.SelectOptionAsync(new SelectOptionValue { Value = berthCode });
        }
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

    private static string FormatExceptionChain(Exception ex)
    {
        var parts = new List<string>();
        for (var current = ex; current != null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" → ", parts);
    }

    private static bool IsBrowserClosedMessage(string message) =>
        message.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Target page, context or browser", StringComparison.OrdinalIgnoreCase);

    private sealed class BrowserSessionDiagnostics
    {
        private string? _chromiumCloseReason;
        private bool _sessionActive;

        public void Attach(IBrowser browser, IPage page)
        {
            browser.Disconnected += (_, _) =>
            {
                if (!_sessionActive)
                {
                    return;
                }

                _chromiumCloseReason ??=
                    "Chromium disconnected (window closed manually, browser crash, or process killed).";
            };

            page.Close += (_, _) =>
            {
                if (!_sessionActive)
                {
                    return;
                }

                _chromiumCloseReason ??= "Browser tab/page was closed during automation.";
            };
        }

        public void Begin() => _sessionActive = true;

        public void End() => _sessionActive = false;

        public void EnsurePageOpen(IPage page, string step)
        {
            if (!page.IsClosed)
            {
                return;
            }

            throw new PlaywrightException(
                $"Chromium page is already closed before {step}. " +
                (_chromiumCloseReason ?? "Close reason was not captured."));
        }

        public bool IsUserClosedBrowserDuringSession(IPage? page, Exception ex)
        {
            if (!_sessionActive)
            {
                return false;
            }

            if (_chromiumCloseReason != null)
            {
                return true;
            }

            if (page?.IsClosed == true && ex is PlaywrightException)
            {
                return IsBrowserClosedMessage(ex.Message);
            }

            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is PlaywrightException && IsBrowserClosedMessage(current.Message))
                {
                    return true;
                }
            }

            return false;
        }

        public string DescribeFailure(IPage? page, Exception ex, string? context = null)
        {
            if (ex is BookingAutomationException automationEx)
            {
                return automationEx.Message;
            }

            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(context))
            {
                sb.AppendLine(context);
            }

            if (_sessionActive && _chromiumCloseReason != null)
            {
                sb.AppendLine($"Chromium closed during automation: {_chromiumCloseReason}");
            }
            else if (_sessionActive && page?.IsClosed == true && IsBrowserClosedMessage(ex.Message))
            {
                sb.AppendLine("Chromium closed during automation: Playwright reported the target/browser was closed.");
            }

            sb.AppendLine($"Technical detail: {FormatExceptionChain(ex)}");

            if (_sessionActive && !IsUserClosedBrowserDuringSession(page, ex) && ex is TimeoutException)
            {
                sb.AppendLine(
                    "Likely cause: a page element did not appear in time (not a browser close). " +
                    "See the step message above for what was missing.");
            }

            return sb.ToString().Trim();
        }
    }
}

public sealed class BookingAutomationException : Exception
{
    public string Step { get; }

    public BookingAutomationException(string step, string reason)
        : base($"[{step}] {reason}")
    {
        Step = step;
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
