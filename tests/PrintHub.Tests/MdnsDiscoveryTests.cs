using PrintHub.Core.Discovery;
using Xunit;

namespace PrintHub.Tests;

public sealed class MdnsFakePrinterFixture : FakePrinterFixture { public MdnsFakePrinterFixture() : base(true) { } }

/// <summary>Real multicast sockets: the discovery code asks 224.0.0.251:5353 and a separate process answers like a Bonjour printer.</summary>
public class MdnsDiscoveryTests : IClassFixture<MdnsFakePrinterFixture>
{
    readonly MdnsFakePrinterFixture _fake;
    public MdnsDiscoveryTests(MdnsFakePrinterFixture fake) => _fake = fake;

    [Fact]
    public async Task Printer_advertising_over_bonjour_is_found_and_merged_into_one_device()
    {
        // give the responder a moment to bind and join the multicast group
        await Task.Delay(1500);
        if (_fake.Events().Any(e => e.Contains("MDNS responder failed")))
        {
            Assert.Fail("responder could not start: " + string.Join(" | ", _fake.Matching("MDNS")));
        }

        List<PrinterDevice> found = new();
        for (int attempt = 0; attempt < 3 && !found.Any(d => d.Name.Contains("Fake mDNS")); attempt++)
            found = await PrinterDiscovery.DiscoverAsync();

        var dev = found.SingleOrDefault(d => d.Name.Contains("Fake mDNS"));
        Assert.True(dev is not null, "discovery did not find the advertised printer. Events: " + string.Join(" | ", _fake.Matching("MDNS")));
        Assert.Equal("Fake mDNS OfficeJet 7777", dev!.Name);
        Assert.Equal($"http://127.0.0.1:{_fake.Port}/ipp/print", dev.IppUri!.ToString());
        Assert.Equal($"http://127.0.0.1:{_fake.Port}/eSCL/", dev.EsclUri!.ToString());   // _ipp and _uscan merged into ONE printer
        Assert.Equal("HP", dev.Manufacturer);
        Assert.True(dev.IsHp);
        Assert.Equal($"http://127.0.0.1:{_fake.Port}/", dev.WebUri!.ToString());          // adminurl from the TXT record
        Assert.NotEmpty(_fake.Matching("MDNS query"));

        // and the discovered device really works
        await using var session = await PrinterSession.OpenAsync(dev);
        var st = await session.Ipp!.GetStatusAsync();
        Assert.Equal(4, st.Supplies.Count);
    }
}
