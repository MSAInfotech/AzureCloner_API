using AzureDiscovery.Core.Models;
using Xunit;

namespace AzureDiscovery.Tests;

public class UnitTest1
{
    [Fact]
    public void Test1()
    {
        // Arrange
        var resource = new AzureResource
        {
            Id = "test-id",
            Name = "test-resource",
            Type = "Microsoft.Compute/virtualMachines"
        };

        // Act & Assert
        Assert.Equal("test-id", resource.Id);
        Assert.Equal("test-resource", resource.Name);
        Assert.Equal("Microsoft.Compute/virtualMachines", resource.Type);
    }
}
