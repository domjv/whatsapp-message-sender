namespace WhatsappMessageSender.Services;

using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using Microsoft.Extensions.Configuration;

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
    private readonly FileStream _profileLockStream;

    public WhatsAppService(IConfiguration configuration)
    {
        _profilePath = GetPlatformSpecificProfilePath(configuration["WhatsApp:ProfilePath"] ?? throw new InvalidOperationException());
        _profileLockStream = AcquireProfileLock(_profilePath);

        ChromeDriver? driver = null;
        try
        {
            driver = InitializeDriver(configuration["WhatsApp:ChromeDriverPath"] ?? "");
            _driver = driver;
            InitializeWhatsAppWeb(GetStartupWaitSeconds(configuration));
        }
        catch
        {
            if (driver != null)
            {
                try
                {
                    driver.Quit();
                }
                finally
                {
                    driver.Dispose();
                }
            }

            _profileLockStream.Dispose();
            throw;
        }
    }

    private static string GetPlatformSpecificProfilePath(string configPath)
    {
        string profilePath;
        if (string.IsNullOrEmpty(configPath))
        {
            profilePath = Path.Combine(
                (Environment.OSVersion.Platform == PlatformID.Unix
                    ? Environment.GetEnvironmentVariable("HOME")
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? throw new InvalidOperationException(),
                "WhatsAppProfile"
            );
        }
        else
        {
            profilePath = configPath;
        }

        return Path.GetFullPath(profilePath);
    }

    private static FileStream AcquireProfileLock(string profilePath)
    {
        Directory.CreateDirectory(profilePath);
        var lockPath = Path.Combine(profilePath, ".whatsapp-message-sender.lock");

        try
        {
            var lockStream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            lockStream.SetLength(0);
            using var writer = new StreamWriter(lockStream, leaveOpen: true);
            writer.WriteLine($"ProcessId={Environment.ProcessId}");
            writer.WriteLine($"MachineName={Environment.MachineName}");
            writer.WriteLine($"StartedUtc={DateTime.UtcNow:o}");
            writer.Flush();
            lockStream.Flush();
            lockStream.Position = 0;

            Console.WriteLine($"WhatsApp: using Chrome profile path '{profilePath}'.");
            return lockStream;
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Chrome profile path '{profilePath}' is already locked by another running sender instance. " +
                "Each service instance must use a unique WhatsApp:ProfilePath.",
                ex);
        }
    }

    private static int GetStartupWaitSeconds(IConfiguration configuration)
    {
        return int.TryParse(configuration["WhatsApp:StartupWaitSeconds"], out var seconds)
            ? Math.Max(1, seconds)
            : 120;
    }

    private ChromeDriver InitializeDriver(string driverPath)
    {
        var options = BuildChromeOptions();
        var trimmed = driverPath.Trim();

        // Selenium Manager (built into Selenium 4.6+) downloads a ChromeDriver that matches the
        // installed Chrome. Prefer this when no fixed path is set — avoids Homebrew driver
        // ahead of Chrome (e.g. driver 148 vs Chrome 147).
        if (string.IsNullOrEmpty(trimmed) ||
            trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                "WhatsApp:ChromeDriverPath empty or 'auto' — using Selenium Manager (driver matched to installed Chrome).");
            return new ChromeDriver(options);
        }

        try
        {
            var service = CreateChromeDriverService(trimmed);
            return new ChromeDriver(service, options);
        }
        catch (InvalidOperationException ex) when (IsChromeDriverVersionMismatch(ex))
        {
            Console.WriteLine(
                "Configured ChromeDriver does not match installed Google Chrome. Retrying with Selenium Manager.");
            return new ChromeDriver(options);
        }
    }

    private ChromeOptions BuildChromeOptions()
    {
        var options = new ChromeOptions();
        options.AddArgument("--start-maximized");
        options.AddArgument("--disable-notifications");
        options.AddArgument($"--user-data-dir={_profilePath}");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--no-sandbox");

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

    private static ChromeDriverService CreateChromeDriverService(string driverPath)
    {
        if (File.Exists(driverPath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(driverPath));
            var fileName = Path.GetFileName(driverPath);
            if (string.IsNullOrEmpty(dir))
            {
                dir = ".";
            }

            return ChromeDriverService.CreateDefaultService(dir, fileName);
        }

        if (Directory.Exists(driverPath))
        {
            return ChromeDriverService.CreateDefaultService(driverPath);
        }

        var bundledDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            Environment.OSVersion.Platform == PlatformID.Unix ? "chromedriver-mac" : "chromedriver-win64");
        if (Directory.Exists(bundledDir))
        {
            Console.WriteLine(
                $"WhatsApp:ChromeDriverPath '{driverPath}' not found; using bundled driver folder: {bundledDir}");
            return ChromeDriverService.CreateDefaultService(bundledDir);
        }

        Console.WriteLine(
            $"WhatsApp:ChromeDriverPath '{driverPath}' not found; using Selenium Manager / PATH (install chromedriver or run from a machine with Chrome).");
        return ChromeDriverService.CreateDefaultService();
    }

    private void InitializeWhatsAppWeb(int startupWaitSeconds)
    {
        _driver.Navigate().GoToUrl("https://web.whatsapp.com/");
        Console.WriteLine(
            $"Waiting up to {startupWaitSeconds} seconds for WhatsApp Web login on profile '{_profilePath}'. " +
            "Scan the QR code if this is the first start for this profile.");

        var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(startupWaitSeconds))
        {
            PollingInterval = TimeSpan.FromSeconds(1)
        };
        wait.IgnoreExceptionTypes(typeof(NoSuchElementException), typeof(StaleElementReferenceException));

        try
        {
            wait.Until(IsWhatsAppWebLoggedIn);
            Console.WriteLine("WhatsApp Web is logged in — the worker will now connect to your message broker.");
        }
        catch (WebDriverTimeoutException ex)
        {
            throw new InvalidOperationException(
                $"WhatsApp Web was not logged in within {startupWaitSeconds} seconds for Chrome profile '{_profilePath}'. " +
                "Scan the QR code or increase WhatsApp:StartupWaitSeconds before running this instance as a service.",
                ex);
        }
    }

    private static bool IsWhatsAppWebLoggedIn(IWebDriver driver)
    {
        return driver.FindElements(By.XPath("//div[@aria-label='Chat list']")).Count > 0
            || driver.FindElements(By.XPath("//div[@role='textbox' and @contenteditable='true']")).Count > 0
            || driver.FindElements(By.XPath("//div[@data-testid='chat-list']")).Count > 0;
    }

    public async Task<SendMessageResult> SendMessageAsync(string phoneNumber, string textMessage, string? filePath)
    {
        try
        {
            var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(30))
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
            Console.WriteLine($"Error sending WhatsApp message: {ex.Message}");
            return new SendMessageResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    public void Dispose()
    {
        try
        {
            _driver.Quit();
        }
        finally
        {
            _driver.Dispose();
            _profileLockStream.Dispose();
        }
    }
}
