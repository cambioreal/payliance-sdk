using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CambioReal.Payliance.Tests;

public sealed class StartupValidationTests
{
    [Fact]
    public void InvalidOptionsFailThroughTheStandardStartupValidator()
    {
        var services = new ServiceCollection();
        services.AddPaylianceClient(_ => { });

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<IStartupValidator>();

        Should.Throw<OptionsValidationException>(validator.Validate);
    }

    [Fact]
    public void ValidOptionsPassThroughTheStandardStartupValidator()
    {
        var services = new ServiceCollection();
        services.AddPaylianceClient(options => { options.MerchantId = "merchant"; options.Password = "password"; options.LocationId = "location"; });

        using var provider = services.BuildServiceProvider();

        Should.NotThrow(provider.GetRequiredService<IStartupValidator>().Validate);
    }
}
