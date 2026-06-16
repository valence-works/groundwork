using Groundwork.Core.Capabilities;
using Groundwork.Modules.Inbox;
using Xunit;

namespace Groundwork.SupportTickets.Tests;

public sealed class SupportTicketExternalModuleTests
{
    [Fact]
    public async Task InboxModuleIsWiredAsAnExternalCapabilityExtension()
    {
        await using var host = await SupportTicketSampleHost.CreateAsync();

        Assert.Equal("community.inbox", host.ExternalModuleFit.ModuleName);
        Assert.Equal(InboxCapabilities.IdempotentConsumer, host.ExternalModuleFit.Capability);
        Assert.IsType<ProviderFit.Supported>(host.ExternalModuleFit.ModuleProvider);

        var unsupported = Assert.IsType<ProviderFit.Unsupported>(host.ExternalModuleFit.DocumentOnlyProvider);
        Assert.Contains(InboxCapabilities.IdempotentConsumer, unsupported.MissingRequirements);
        Assert.Contains(host.ExternalModuleFit.CoreOnlyValidationErrors, error => error.StartsWith("GW-CAP-014:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InboxAdmissionDeduplicatesRedeliveredMessages()
    {
        await using var host = await SupportTicketSampleHost.CreateAsync();

        var first = await host.Inbox.TryAdmitAsync("ticket-webhook", "evt-1001");
        var redelivery = await host.Inbox.TryAdmitAsync("ticket-webhook", "evt-1001");

        Assert.Equal(InboxAdmission.Admitted, first);
        Assert.Equal(InboxAdmission.Duplicate, redelivery);
    }
}
