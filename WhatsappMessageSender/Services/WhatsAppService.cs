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
            var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));

            // Navigate and send text message
            var url = $"https://web.whatsapp.com/send?phone={phoneNumber}&text={Uri.EscapeDataString(textMessage)}";
            await _driver.Navigate().GoToUrlAsync(url);
            
            // Wait for chat to load
            await Task.Delay(5000);

            // Send text message
            var chatInputBox = wait.Until(d => d.FindElement(By.XPath("//div[@contenteditable='true' and @aria-placeholder='Type a message']")));
            chatInputBox.Click();
            chatInputBox.SendKeys(Keys.Enter);
            Thread.Sleep(3000);

            var jsObject = _driver.ExecuteScript("return window.WA");
            Console.WriteLine(jsObject);

            var attachButton = wait.Until(d => d.FindElement(By.XPath("//button[@title='Attach']")));
            attachButton.Click();
            Thread.Sleep(1000);

            var documentOption = wait.Until(d => d.FindElement(By.XPath("//span[text()='Document']")));
            documentOption.Click();
            Thread.Sleep(1000);

            var fileInput = wait.Until(d => d.FindElement(By.XPath("//input[@accept='*']")));
            fileInput.SendKeys(filePath);
            Thread.Sleep(3000);

            var sendFileButton = wait.Until(d => d.FindElement(By.XPath("//div[@aria-label='Send']")));
            sendFileButton.Click();

            Console.WriteLine($"Message sent to {phoneNumber}");
            return new SendMessageResult
            {
                Success = true
            };
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
