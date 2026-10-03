using PrintHub.Core.Http;
using Xunit;

namespace PrintHub.Tests;

public class LocalTlsTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("192.168.1.50", true)]
    [InlineData("10.4.5.6", true)]
    [InlineData("172.16.0.9", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("169.254.10.2", true)]
    [InlineData("hp-printer.local", true)]
    [InlineData("NPI123456", true)]
    [InlineData("fe80::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("203.0.113.5", false)]
    [InlineData("printer.example.com", false)]
    public void Self_signed_certificates_are_only_trusted_on_local_addresses(string host, bool expected) =>
        Assert.Equal(expected, LocalTls.IsPrivateHost(host));
}
