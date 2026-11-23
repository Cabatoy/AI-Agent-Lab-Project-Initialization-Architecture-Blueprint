using Microsoft.Extensions.Configuration;

namespace ControlPlane.Api.Tests;

public class TestUserProvisioningTests
{
    [Fact]
    public void Parse_DisabledProvisioning_FailsClosed()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Empty(HicTestUserProvisioner.Parse(configuration));
    }

    [Theory]
    [InlineData("")]
    [InlineData("weak")]
    [InlineData("not-an-email")]
    public void Parse_InvalidConfiguration_IsRejected(string invalid)
    {
        var values = new Dictionary<string, string?>
        {
            [HicTestUserProvisioner.EnabledKey] = "true"
        };
        for (var number = 1; number <= 3; number++)
        {
            values[$"HIC_TEST_USER_{number}_EMAIL"] =
                number == 3 ? invalid : $"hic-test-{number}@example.test";
            values[$"HIC_TEST_USER_{number}_USERNAME"] = $"hic-test-{number}";
            values[$"HIC_TEST_USER_{number}_PASSWORD"] =
                number == 3 ? invalid : Convert.ToHexString(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Throws<InvalidOperationException>(() => HicTestUserProvisioner.Parse(configuration));
    }
}
