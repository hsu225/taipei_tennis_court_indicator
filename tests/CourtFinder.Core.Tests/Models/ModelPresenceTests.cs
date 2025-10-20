using System.Linq;
using System.Reflection;

namespace CourtFinder.Core.Tests.Models;

public class ModelPresenceTests
{
    [Theory]
    [InlineData("Court")]
    [InlineData("Availability")]
    [InlineData("TimeSlot")]
    public void CoreAssembly_ShouldContain_Model(string typeName)
    {
        var asm = Assembly.Load("CourtFinder.Core");
        var modelType = asm.GetTypes().FirstOrDefault(t => t.IsClass && t.Name == typeName);
        Assert.NotNull(modelType);
    }
}

