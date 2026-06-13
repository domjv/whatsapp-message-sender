namespace WhatsappMessageSender.Services;

using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.WindowsServices;

public class SendMessageResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    /// <summary>Optional provider id (e.g. WhatsApp <c>wamid.*</c>) when available.</summary>
    public string? ProviderMessageId { get; set; }
}

public class WhatsAppService : IWhatsAppService, IDisposable
{
    private readonly ChromeDriver _driver;
    private readonly string _profilePath;
    private readonly bool _headless;
    private readonly int _sendTimeoutSeconds;

    public WhatsAppService(IConfiguration configuration)
    {
        var whatsAppSection = configuration.GetSection("WhatsApp");
        _profilePath = GetPlatformSpecificProfilePath(whatsAppSection["ProfilePath"] ?? throw new InvalidOperationException("WhatsApp:ProfilePath is required."));
        _headless = whatsAppSection.GetValue("Headless", IsHeadlessByDefault());
        _sendTimeoutSeconds = Math.Max(10, whatsAppSection.GetValue("SendTimeoutSeconds", 60));
        var hideDriverWindow = whatsAppSection.GetValue("HideDriverWindow", true);
        var clearProfileLocks = whatsAppSection.GetValue("ClearProfileLocksOnStartup", OperatingSystem.IsWindows());
        var driverPath = whatsAppSection["ChromeDriverPath"] ?? "";

        EnsureProfileDirectoryReady(_profilePath, clearProfileLocks);
        _driver = InitializeDriver(driverPath, hideDriverWindow);
        InitializeWhatsAppWeb();
    }

    private static bool IsHeadlessByDefault() =>
        OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();

    private static string GetPlatformSpecificProfilePath(string configPath)
    {
        if (string.IsNullOrEmpty(configPath))
        {
            return Path.Combine(
                (Environment.OSVersion.Platform == PlatformID.Unix
                    ? Environment.GetEnvironmentVariable("HOME")
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? throw new InvalidOperationException(),
                "WhatsAppProfile"
            );
        }
        return configPath;
    }

    private ChromeDriver InitializeDriver(string driverPath, bool hideDriverWindow)
    {
        var options = BuildChromeOptions();
        var trimmed = driverPath.Trim();

        try
        {
            return CreateDriverWithPath(trimmed, options, hideDriverWindow);
        }
        catch (Exception ex) when (IsChromeStartupCrash(ex))
        {
            Console.WriteLine(
                "Chrome failed to start (common when running as a Windows Service). " +
                "Clearing stale profile lock files and retrying once…");
            EnsureProfileDirectoryReady(_profilePath, clearLocks: true);
            return CreateDriverWithPath(trimmed, options, hideDriverWindow);
        }
    }

    private ChromeDriver CreateDriverWithPath(string driverPath, ChromeOptions options, bool hideDriverWindow)
    {
        var trimmed = driverPath.Trim();

        if (string.IsNullOrEmpty(trimmed) ||
            trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                "WhatsApp:ChromeDriverPath empty or 'auto' — using Selenium Manager (driver matched to installed Chrome).");
            return CreateChromeDriver(null, options, hideDriverWindow, _profilePath);
        }

        try
        {
            var service = CreateChromeDriverService(trimmed, hideDriverWindow);
            return CreateChromeDriver(service, options, hideDriverWindow, _profilePath);
        }
        catch (InvalidOperationException ex) when (IsChromeDriverVersionMismatch(ex))
        {
            Console.WriteLine(
                "Configured ChromeDriver does not match installed Google Chrome. Retrying with Selenium Manager.");
            return CreateChromeDriver(null, options, hideDriverWindow, _profilePath);
        }
    }

    private static bool IsChromeStartupCrash(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("DevToolsActivePort", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Chrome failed to start", StringComparison.OrdinalIgnoreCase)
            || message.Contains("SessionNotCreated", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureProfileDirectoryReady(string profilePath, bool clearLocks)
    {
        Directory.CreateDirectory(profilePath);

        if (!clearLocks || !OperatingSystem.IsWindows())
            return;

        foreach (var name in new[] { "SingletonLock", "SingletonCookie", "SingletonSocket", "lockfile" })
        {
            TryDeleteFile(Path.Combine(profilePath, name));
        }

        var defaultProfile = Path.Combine(profilePath, "Default");
        if (Directory.Exists(defaultProfile))
        {
            TryDeleteFile(Path.Combine(defaultProfile, "lockfile"));
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            File.Delete(path);
            Console.WriteLine($"Removed stale Chrome lock file: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not remove Chrome lock file '{path}': {ex.Message}");
        }
    }

    private static ChromeDriver CreateChromeDriver(
        ChromeDriverService? service,
        ChromeOptions options,
        bool hideDriverWindow,
        string profilePath)
    {
        try
        {
            if (service != null)
            {
                if (OperatingSystem.IsWindows())
                    service.HideCommandPromptWindow = hideDriverWindow;
                return new ChromeDriver(service, options);
            }

            service = ChromeDriverService.CreateDefaultService();
            if (OperatingSystem.IsWindows())
                service.HideCommandPromptWindow = hideDriverWindow;
            return new ChromeDriver(service, options);
        }
        catch (Exception ex) when (IsChromeStartupCrash(ex))
        {
            throw BuildChromeStartupException(profilePath, ex);
        }
    }

    private static InvalidOperationException BuildChromeStartupException(string profilePath, Exception ex) =>
        new(
            "Chrome failed to start. When running as a Windows Service: " +
            "(1) run the service under a real user account (not LocalSystem), " +
            "(2) ensure the service account has Full Control on WhatsApp:ProfilePath, " +
            "(3) log in to WhatsApp Web once interactively with Headless: false, " +
            "(4) confirm no other Chrome instance is using the same profile. " +
            $"ProfilePath: {profilePath}. Inner error: {ex.Message}",
            ex);

    private ChromeOptions BuildChromeOptions()
    {
        var options = new ChromeOptions();
        options.AddArgument("--disable-notifications");
        options.AddArgument($"--user-data-dir={_profilePath}");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-software-rasterizer");
        options.AddArgument("--no-first-run");
        options.AddArgument("--no-default-browser-check");
        options.AddArgument("--disable-extensions");
        options.AddArgument("--disable-breakpad");
        options.AddArgument("--remote-allow-origins=*");

        if (_headless)
        {
            options.AddArgument("--headless=new");
            options.AddArgument("--window-size=1920,1080");
            // Pipe mode avoids DevToolsActivePort crashes in headless Windows Service scenarios.
            options.AddArgument("--remote-debugging-pipe");
        }
        else
        {
            options.AddArgument("--start-maximized");
        }

        if (OperatingSystem.IsWindows() && _headless)
        {
            options.AddArgument("--disable-features=RendererCodeIntegrity,TranslateUI");
        }

        var downloadPath = Path.Combine(Path.GetTempPath(), "WhatsAppDownloads");
        Directory.CreateDirectory(downloadPath);

        options.AddUserProfilePreference("download.default_directory", downloadPath);
        options.AddUserProfilePreference("download.prompt_for_download", false);
        options.AddUserProfilePreference("download.directory_upgrade", true);
        options.AddUserProfilePreference("safebrowsing.enabled", true);
        return options;
    }

    private static bool IsChromeDriverVersionMismatch(InvalidOperationException ex)
    {
        var m = ex.Message;
        return m.Contains("session not created", StringComparison.OrdinalIgnoreCase)
            && (m.Contains("only supports Chrome version", StringComparison.OrdinalIgnoreCase)
                || m.Contains("This version of ChromeDriver only supports", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Current browser version is", StringComparison.OrdinalIgnoreCase));
    }

    private static ChromeDriverService CreateChromeDriverService(string driverPath, bool hideDriverWindow)
    {
        ChromeDriverService service;

        if (File.Exists(driverPath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(driverPath));
            var fileName = Path.GetFileName(driverPath);
            if (string.IsNullOrEmpty(dir))
                dir = ".";

            service = ChromeDriverService.CreateDefaultService(dir, fileName);
        }
        else if (Directory.Exists(driverPath))
        {
            service = ChromeDriverService.CreateDefaultService(driverPath);
        }
        else
        {
            var bundledDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                Environment.OSVersion.Platform == PlatformID.Unix ? "chromedriver-mac" : "chromedriver-win64");
            if (Directory.Exists(bundledDir))
            {
                Console.WriteLine(
                    $"WhatsApp:ChromeDriverPath '{driverPath}' not found; using bundled driver folder: {bundledDir}");
                service = ChromeDriverService.CreateDefaultService(bundledDir);
            }
            else
            {
                Console.WriteLine(
                    $"WhatsApp:ChromeDriverPath '{driverPath}' not found; using Selenium Manager / PATH.");
                service = ChromeDriverService.CreateDefaultService();
            }
        }

        if (OperatingSystem.IsWindows())
            service.HideCommandPromptWindow = hideDriverWindow;

        return service;
    }

    private void InitializeWhatsAppWeb()
    {
        _driver.Navigate().GoToUrl("https://web.whatsapp.com/");

        if (_headless)
        {
            Console.WriteLine("Starting WhatsApp Web in headless mode — waiting for an existing session…");
            try
            {
                var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(90));
                wait.Until(d =>
                    d.FindElements(By.XPath("//div[@id='pane-side']")).Count > 0
                    || d.FindElements(By.XPath("//div[@contenteditable='true' and @aria-placeholder='Type a message']")).Count > 0);
                Console.WriteLine("WhatsApp Web session is ready.");
            }
            catch (WebDriverTimeoutException)
            {
                throw new InvalidOperationException(
                    "WhatsApp Web is not logged in. Run the app once interactively (Headless: false) " +
                    "with the same WhatsApp:ProfilePath, scan the QR code, then restart with Headless: true.");
            }
            return;
        }

        Console.WriteLine("Scan QR Code to log in to WhatsApp Web (you have ~20 seconds before the app continues)…");
        Thread.Sleep(20000);
        Console.WriteLine("WhatsApp Web startup pause finished — the worker will now connect to your message broker.");
    }

    public async Task<SendMessageResult> SendMessageAsync(string phoneNumber, string textMessage, string? filePath)
    {
        try
        {
            var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(_sendTimeoutSeconds))
            {
                PollingInterval = TimeSpan.FromMilliseconds(500)
            };
            wait.IgnoreExceptionTypes(typeof(StaleElementReferenceException), typeof(NoSuchElementException));

            var url = $"https://web.whatsapp.com/send?phone={phoneNumber}&text={Uri.EscapeDataString(textMessage)}";
            await _driver.Navigate().GoToUrlAsync(url);

            var chatInputBox = wait.Until(d => {
                var element = d.FindElement(By.XPath("//div[@contenteditable='true' and @aria-placeholder='Type a message']"));
                return element is { Displayed: true, Enabled: true } ? element : null;
            });

            chatInputBox.Click();
            chatInputBox.SendKeys(Keys.Enter);

            wait.Until(d => {
                try {
                    return d.FindElements(By.XPath("//span[@data-icon='msg-time']")).Count == 0;
                }
                catch {
                    return false;
                }
            });

            if (!string.IsNullOrEmpty(filePath))
            {
                var attachButton = wait.Until(d => {
                    var element = d.FindElement(By.XPath("//button[@title='Attach']"));
                    return element is { Displayed: true, Enabled: true } ? element : null;
                });
                attachButton.Click();

                var documentOption = wait.Until(d => {
                    var element = d.FindElement(By.XPath("//span[text()='Document']"));
                    return element is { Displayed: true, Enabled: true } ? element : null;
                });
                documentOption.Click();

                var fileInput = wait.Until(d => d.FindElement(By.XPath("//input[@accept='*']")));
                fileInput.SendKeys(filePath);

                var sendFileButton = wait.Until(d => {
                    var element = d.FindElement(By.XPath("//div[@aria-label='Send']"));
                    if (!element.Displayed || !element.Enabled) return null;

                    var loadingElements = d.FindElements(By.XPath("//div[contains(@class, 'progress')]"));
                    return loadingElements.Count == 0 ? element : null;
                });

                sendFileButton.Click();

                wait.Until(d => {
                    try {
                        var sentStatus = d.FindElements(By.XPath("//span[@data-icon='msg-check']"));
                        return sentStatus.Count != 0;
                    }
                    catch {
                        return false;
                    }
                });
            }

            Console.WriteLine($"Message sent to {phoneNumber}");
            return new SendMessageResult { Success = true };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending WhatsApp message to {phoneNumber}: {ex.Message}");
            return new SendMessageResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    public void Dispose()
    {
        _driver.Quit();
        _driver.Dispose();
    }
}
