using Microsoft.Extensions.Hosting;

namespace WhatsappMessageSender.Services;

public sealed class ProcessorHostedService(IMessageProcessor processor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        processor.StartProcessing();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await processor.CloseAsync();
    }
}
