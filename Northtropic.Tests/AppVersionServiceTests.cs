using System;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests;

public class AppVersionServiceTests
{
    [Fact]
    public void AppVersionService_InitializesProperly_AndExtractsVersion()
    {
        // Arrange & Act
        var service = new AppVersionService();

        // Assert
        Assert.NotNull(service.Version);
        Assert.NotEmpty(service.Version);
        Assert.StartsWith("v", service.DisplayVersion);
        Assert.Contains(service.Version, service.DisplayVersion);
        Assert.False(string.IsNullOrWhiteSpace(service.BuildTimestamp));
        Assert.Contains("Northtropic", service.FullVersionInfo);
        Assert.Contains(service.DisplayVersion, service.FullVersionInfo);
    }

    [Fact]
    public void AppVersionService_DisplayVersion_FollowsStandardPrefix()
    {
        // Arrange
        var service = new AppVersionService();

        // Assert
        Assert.Equal($"v{service.Version}", service.DisplayVersion);
    }

    [Fact]
    public void AppVersionService_WithCustomConfiguration_FallsBackGracefully()
    {
        // Arrange
        var configValues = new System.Collections.Generic.Dictionary<string, string?>
        {
            { "AppVersion", "2.1.0" }
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        // Act
        var service = new AppVersionService(configuration);

        // Assert
        Assert.NotNull(service.Version);
        Assert.NotEmpty(service.Version);
        Assert.NotNull(service.DisplayVersion);
        Assert.NotNull(service.FullVersionInfo);
    }

    [Fact]
    public void FakeAppVersionService_ImplementsInterfaceAccurately()
    {
        // Arrange
        IAppVersionService fakeService = new FakeAppVersionService
        {
            Version = "1.0.99"
        };

        // Assert
        Assert.Equal("1.0.99", fakeService.Version);
        Assert.Equal("v1.0.99", fakeService.DisplayVersion);
        Assert.Equal("e8dcc36", fakeService.ShortCommitHash);
        Assert.Contains("v1.0.99", fakeService.FullVersionInfo);
    }
}
