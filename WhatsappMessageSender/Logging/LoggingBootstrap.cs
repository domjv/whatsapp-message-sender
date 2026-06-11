using Microsoft.Extensions.Configuration;
using WhatsappMessageSender.Models;

namespace WhatsappMessageSender.Logging;

public static class LoggingBootstrap
{
    public static string Configure(IConfiguration configuration)
    {
        var settings = configuration.GetSection("FileLogging").Get<FileLoggingSettings>() ?? new FileLoggingSettings();
        var logDirectory = ResolveLogDirectory(settings.LogDirectory);

        var fileWriter = new DailyRollingTextWriter(
            logDirectory,
            settings.FileNamePrefix,
            settings.RetainedFileCountLimit);

        var originalOut = Console.Out;
        var originalErr = Console.Error;

        TextWriter output = settings.WriteToConsole
            ? new TeeTextWriter(fileWriter, originalOut)
            : fileWriter;

        Console.SetOut(output);
        Console.SetError(settings.WriteToConsole
            ? new TeeTextWriter(fileWriter, originalErr)
            : fileWriter);

        return logDirectory;
    }

    private static string ResolveLogDirectory(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return Path.Combine(AppContext.BaseDirectory, "logs");

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
    }
}
