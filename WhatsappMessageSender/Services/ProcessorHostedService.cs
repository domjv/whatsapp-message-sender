using Microsoft.Extensions.Hosting;

namespace WhatsappMessageSender.Services;

public sealed class ProcessorHostedService(IMessageProcessor processor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Starting message broker consumer…");
        // Start broker consumption loops when the host starts.
        processor.StartProcessing();
        Console.WriteLine(
            "Consumer is running. You may see no further output until a message is received — press Ctrl+C to stop.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Gracefully stop and close broker clients on host shutdown.
        await processor.CloseAsync();
    }
}
