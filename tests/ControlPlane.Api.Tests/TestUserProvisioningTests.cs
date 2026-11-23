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
    [InlineData("_EMAIL")]
    [InlineData("_USERNAME")]
    [InlineData("_PASSWORD")]
    public void Parse_ConfiguredSlotAfterGap_IsRejected(string suffix)
    {
        var values = new Dictionary<string, string?>
        {
            [HicTestUserProvisioner.EnabledKey] = "true"
        };
        for (var number = 1; number <= 3; number++)
        {
            values[$"HIC_TEST_USER_{number}_EMAIL"] = $"hic-test-{number}@example.test";
            values[$"HIC_TEST_USER_{number}_USERNAME"] = $"hic-test-{number}";
            values[$"HIC_TEST_USER_{number}_PASSWORD"] = new string('a', 20);
        }

        // Fourth slot intentionally empty; even one fifth-slot field must fail closed.
        values[$"HIC_TEST_USER_5{suffix}"] = "configured";
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var error = Assert.Throws<InvalidOperationException>(
            () => HicTestUserProvisioner.Parse(config));
        Assert.Contains("Non-contiguous", error.Message);
    }

    [Fact]
    public void Parse_FiveContiguousAccounts_AreAccepted()
    {
        var values = new Dictionary<string, string?>
        {
            [HicTestUserProvisioner.EnabledKey] = "true"
        };
        for (var number = 1; number <= 5; number++)
        {
            values[$"HIC_TEST_USER_{number}_EMAIL"] = $"hic-test-{number}@example.test";
            values[$"HIC_TEST_USER_{number}_USERNAME"] = $"hic-test-{number}";
            values[$"HIC_TEST_USER_{number}_PASSWORD"] = new string('a', 20);
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Equal(5, HicTestUserProvisioner.Parse(config).Count);
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
