namespace WhatsappMessageSender.Services;

using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using Microsoft.Extensions.Configuration;

public class SendMessageResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class WhatsAppService : IDisposable
{
    private readonly ChromeDriver _driver;
    private readonly string _profilePath;

    public WhatsAppService(IConfiguration configuration)
    {
        _profilePath = GetPlatformSpecificProfilePath(configuration["WhatsApp:ProfilePath"] ?? throw new InvalidOperationException());
        _driver = InitializeDriver(configuration["WhatsApp:ChromeDriverPath"] ?? throw new InvalidOperationException());
        InitializeWhatsAppWeb();
    }

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

    private ChromeDriver InitializeDriver(string driverPath)
    {
        var options = new ChromeOptions();
        options.AddArgument("--start-maximized");
        options.AddArgument("--disable-notifications");
        options.AddArgument($"--user-data-dir={_profilePath}");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--no-sandbox");
        
        // Add preferences to prevent download dialog
        var downloadPath = Path.Combine(Path.GetTempPath(), "WhatsAppDownloads");
        Directory.CreateDirectory(downloadPath);
        
        options.AddUserProfilePreference("download.default_directory", downloadPath);
        options.AddUserProfilePreference("download.prompt_for_download", false);
        options.AddUserProfilePreference("download.directory_upgrade", true);
        options.AddUserProfilePreference("safebrowsing.enabled", true);

        if (string.IsNullOrEmpty(driverPath))
        {
            driverPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                Environment.OSVersion.Platform == PlatformID.Unix ? "chromedriver-mac" : "chromedriver-win64");
        }

        return new ChromeDriver(driverPath, options);
    }

    private void InitializeWhatsAppWeb()
    {
        _driver.Navigate().GoToUrl("https://web.whatsapp.com/");
        Console.WriteLine("Scan QR Code to log in to WhatsApp Web...");
        Thread.Sleep(20000);
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

            // Wait for file to be sent successfully
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
        _driver.Quit();
        _driver.Dispose();
    }
}
