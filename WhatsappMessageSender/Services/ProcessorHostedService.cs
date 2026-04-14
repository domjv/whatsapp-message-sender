using Microsoft.Extensions.Hosting;

namespace WhatsappMessageSender.Services;

public sealed class ProcessorHostedService(IMessageProcessor processor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Start broker consumption loops when the host starts.
        processor.StartProcessing();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Gracefully stop and close broker clients on host shutdown.
        await processor.CloseAsync();
    }
}
