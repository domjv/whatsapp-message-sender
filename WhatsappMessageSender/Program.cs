using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Azure.Storage.Blobs;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System.Reflection;

class Program
{
    private const string ServiceBusConnectionString = "Endpoint=sb://sbnspbinstest.servicebus.windows.net/;SharedAccessKeyName=erpnext;SharedAccessKey=Gz3sikQhDthPLKx4VRDJZ7y6xSZiV4B7e+ASbIZiPTg=";
    private const string QueueName = "sbq-dom-test";
    private static IQueueClient queueClient;
    private static IWebDriver driver;

    // Azure Blob Storage Configuration
    private const string BlobConnectionString = "DefaultEndpointsProtocol=https;AccountName=stkbprodinskrewbee;AccountKey=VbxIKsr5bZzM73TSdnl3hS8S85XKEv+k6z750+i7PSa5NF8agIHNSZMzfRcxeWRyLo/tqGuZyfmx+ASt7xq4vA==;EndpointSuffix=core.windows.net";
    private const string BlobContainerName = "pleasantbiz-attachments";

    static async Task Main(string[] args)
    {
        queueClient = new QueueClient(ServiceBusConnectionString, QueueName, ReceiveMode.PeekLock);

        Console.WriteLine("Starting WhatsApp Automation...");

        // Launch Chrome and open WhatsApp Web
        InitializeWhatsAppWeb();

        var messageHandlerOptions = new MessageHandlerOptions(ExceptionReceivedHandler)
        {
            MaxConcurrentCalls = 1,
            AutoComplete = false
        };

        queueClient.RegisterMessageHandler(ProcessMessagesAsync, messageHandlerOptions);

        Console.ReadKey();
        await queueClient.CloseAsync();
        driver.Quit();
    }

    private static void InitializeWhatsAppWeb()
    {
        var options = new ChromeOptions();
        options.AddArgument("--start-maximized");
        options.AddArgument("--disable-notifications");
        options.AddArgument("--user-data-dir=C:/WhatsAppProfile"); // Persist login
        options.AddArgument("--disable-gpu"); // Fix crashes
        options.AddArgument("--disable-dev-shm-usage"); // Prevent memory errors
        options.AddArgument("--no-sandbox"); // Bypass security sandbox

        string driverPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chromedriver-win64");
        driver = new ChromeDriver(driverPath, options);  // Path to ChromeDriver
        
        // ✅ Open WhatsApp Web only once
        driver.Navigate().GoToUrl("https://web.whatsapp.com/");
        Console.WriteLine("✅ Scan QR Code to log in to WhatsApp Web...");
        Thread.Sleep(20000); // Allow time for scanning
    }
        
    private static async Task ProcessMessagesAsync(Message message, CancellationToken token)
    {
        string messageBody = Encoding.UTF8.GetString(message.Body);
        Console.WriteLine($"Received message: {messageBody}");

        try
        {
            var msg = JsonConvert.DeserializeObject<WhatsAppMessage>(messageBody);

            // Download the attachment from Azure Blob Storage
            string filePath = await DownloadFileFromBlob(msg.AttachmentUrl, msg.Name);

            // Send WhatsApp message with attachment
            SendWhatsAppMessage(msg.Phone, msg.Message, filePath);

            await queueClient.CompleteAsync(message.SystemProperties.LockToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing message: {ex.Message}");
            await queueClient.AbandonAsync(message.SystemProperties.LockToken);
        }
    }

    private static async Task<string> DownloadFileFromBlob(string blobUrl, string fileName)
    {
        try
        {
            // Dowload the file to local disk at the location of the executable
            string path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            string filePath = Path.Combine(path, $"{fileName}.pdf");

            BlobServiceClient blobServiceClient = new BlobServiceClient(BlobConnectionString);
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(BlobContainerName);

            string blobName = new Uri(blobUrl).Segments[^1]; // Extract file name from URL
            BlobClient blobClient = containerClient.GetBlobClient(blobName);

            Console.WriteLine($"Downloading {blobName} from Blob Storage...");

            using FileStream downloadFileStream = File.OpenWrite(filePath);
            await blobClient.DownloadToAsync(downloadFileStream);
            downloadFileStream.Close();

            Console.WriteLine($"File downloaded: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error downloading file: {ex.Message}");
            throw;
            //return null;
        }
    }

    private static void SendWhatsAppMessage(string phoneNumber, string textMessage, string filePath)
    {
        try
        {
            //// Open chat with the given phone number
            //string url = $"https://web.whatsapp.com/send?phone={phoneNumber}&text={Uri.EscapeDataString(textMessage)}";
            //driver.Navigate().GoToUrl(url);

            // ✅ WebDriverWait to wait for the chat box to load
            WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(15));

            //// ✅ Check if we are still on WhatsApp Web
            //if (!driver.Url.Contains("web.whatsapp.com"))
            //{
            //    driver.Navigate().GoToUrl("https://web.whatsapp.com/");
            //    Thread.Sleep(5000);
            //}

            // ✅ Open a new chat for the given phone number
            string url = $"https://web.whatsapp.com/send?phone={phoneNumber}&text={Uri.EscapeDataString(textMessage)}";
            driver.Navigate().GoToUrl(url);
            Thread.Sleep(5000); // Wait for WhatsApp Web to load the new chat

            // ✅ Wait for the message input box to be visible
            var chatInputBox = wait.Until(d => d.FindElement(By.XPath("//div[@contenteditable='true']")));
            chatInputBox.Click();
            chatInputBox.SendKeys(textMessage);
            chatInputBox.SendKeys(Keys.Enter);
            Thread.Sleep(3000);

            //// ✅ Use JavaScript to open the chat instead of reloading
            //var searchBox = wait.Until(d => d.FindElement(By.XPath("//div[@title='Search input textbox']")));
            //searchBox.Clear();
            //searchBox.SendKeys(phoneNumber);
            //Thread.Sleep(3000); // Wait for results

            //var chat = wait.Until(d => d.FindElement(By.XPath("//span[contains(@title, '" + phoneNumber + "')]")));
            //chat.Click();
            //Thread.Sleep(3000);

            //// ✅ Send the message
            //var chatInputBox = wait.Until(d => d.FindElement(By.XPath("//div[@contenteditable='true']")));
            //chatInputBox.SendKeys(textMessage);
            //chatInputBox.SendKeys(Keys.Enter);
            //Thread.Sleep(3000);

            //// ✅ Ensure the chat is open before interacting
            //wait.Until(d =>
            //{
            //    var elements = d.FindElements(By.XPath("//div[@contenteditable='true']"));
            //    return elements.Count > 0;
            //});

            //var chatInputBox = driver.FindElement(By.XPath("//div[@contenteditable='true']"));
            //chatInputBox.SendKeys(Keys.Enter); // Send the message
            //Thread.Sleep(3000);

            // Convert d to jsobject and print
            var js = (IJavaScriptExecutor)driver;
            var jsObject = js.ExecuteScript("return window.WA");
            Console.WriteLine(jsObject);

            // ✅ Click the attach button (New XPath)
            var attachButton = wait.Until(d => d.FindElement(By.XPath("//button[@title='Attach']")));
            attachButton.Click();
            Thread.Sleep(1000);

            // ✅ Click the "Document" option
            var documentOption = wait.Until(d => d.FindElement(By.XPath("//span[text()='Document']")));
            documentOption.Click();
            Thread.Sleep(1000);

            // ✅ Select document upload field and attach file
            var fileInput = wait.Until(d => d.FindElement(By.XPath("//input[@accept='*']")));
            //var fileInput = wait.Until(d => d.FindElement(By.CssSelector("input[type='file']")));
            fileInput.SendKeys(filePath);
            //var documentInput = wait.Until(d => d.FindElement(By.XPath("//input[@accept='.pdf']")));
            //documentInput.SendKeys(filePath);
            Thread.Sleep(3000);

            // ✅ Click "Send" after attachment uploads
            var sendFileButton = wait.Until(d => d.FindElement(By.XPath("//div[@aria-label='Send']")));
            sendFileButton.Click();

            Console.WriteLine($"✅ Message sent to {phoneNumber}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error sending WhatsApp message: {ex.Message}");
        }
    }

    private static Task ExceptionReceivedHandler(ExceptionReceivedEventArgs exceptionReceivedEventArgs)
    {
        Console.WriteLine($"Message handler encountered an exception: {exceptionReceivedEventArgs.Exception}");
        return Task.CompletedTask;
    }
}

class WhatsAppMessage
{
    public required string Name { get; set; }
    public required string Phone { get; set; }
    public required string Message { get; set; }
    public required string AttachmentUrl { get; set; }
}
