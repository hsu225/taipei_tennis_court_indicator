using System;
using System.Linq;
using System.Reflection;

namespace CourtFinder.Core.Tests.Providers;

public class ProviderContractsTests
{
    [Fact]
    public void CoreAssembly_ShouldContain_ITennisCourtProvider_Interface()
    {
        var asm = Assembly.Load("CourtFinder.Core");
        Assert.NotNull(asm);

        var iface = asm
            .GetTypes()
            .FirstOrDefault(t => t.IsInterface && t.Name == "ITennisCourtProvider");

        Assert.NotNull(iface);
    }

    [Fact]
    public void CoreAssembly_ShouldHave_Concrete_Providers_Implementing_Interface()
    {
        var asm = Assembly.Load("CourtFinder.Core");
        var iface = asm
            .GetTypes()
            .First(t => t.IsInterface && t.Name == "ITennisCourtProvider");

        var implementations = asm
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && iface.IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(implementations);
    }
}

