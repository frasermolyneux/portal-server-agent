using System.Text.Json;

using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class ConfigHashTests
{
    [Fact]
    public void ComputeConfigHash_IsDeterministic()
    {
        // Arrange
        var configs = CreateSampleConfigs();

        // Act
        var hash1 = RepositoryServerConfigProvider.ComputeConfigHash(configs);
        var hash2 = RepositoryServerConfigProvider.ComputeConfigHash(configs);

        // Assert
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeConfigHash_ProducesValidHexString()
    {
        // Arrange
        var configs = CreateSampleConfigs();

        // Act
        var hash = RepositoryServerConfigProvider.ComputeConfigHash(configs);

        // Assert — SHA256 produces 64 hex characters
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9A-F]+$", hash);
    }

    [Fact]
    public void ComputeConfigHash_ChangesWhenValueChanges()
    {
        // Arrange
        var configs1 = CreateSampleConfigs();
        var configs2 = CreateSampleConfigs();
        configs2["ftp"]["password"] = JsonDocument.Parse("\"newpass\"").RootElement;

        // Act
        var hash1 = RepositoryServerConfigProvider.ComputeConfigHash(configs1);
        var hash2 = RepositoryServerConfigProvider.ComputeConfigHash(configs2);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeConfigHash_ChangesWhenNamespaceAdded()
    {
        // Arrange
        var configs1 = CreateSampleConfigs();
        var configs2 = CreateSampleConfigs();
        configs2["banfiles"] = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["checkIntervalSeconds"] = JsonDocument.Parse("60").RootElement
        };

        // Act
        var hash1 = RepositoryServerConfigProvider.ComputeConfigHash(configs1);
        var hash2 = RepositoryServerConfigProvider.ComputeConfigHash(configs2);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeConfigHash_IsOrderIndependent()
    {
        // Arrange — same data, different insertion order
        var configs1 = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ftp"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["hostname"] = JsonDocument.Parse("\"ftp.example.com\"").RootElement,
                ["port"] = JsonDocument.Parse("21").RootElement,
            },
            ["rcon"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["password"] = JsonDocument.Parse("\"secret\"").RootElement,
            }
        };

        var configs2 = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase)
        {
            ["rcon"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["password"] = JsonDocument.Parse("\"secret\"").RootElement,
            },
            ["ftp"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["port"] = JsonDocument.Parse("21").RootElement,
                ["hostname"] = JsonDocument.Parse("\"ftp.example.com\"").RootElement,
            }
        };

        // Act
        var hash1 = RepositoryServerConfigProvider.ComputeConfigHash(configs1);
        var hash2 = RepositoryServerConfigProvider.ComputeConfigHash(configs2);

        // Assert
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeConfigHash_EmptyConfigs_ProducesValidHash()
    {
        // Arrange
        var configs = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);

        // Act
        var hash = RepositoryServerConfigProvider.ComputeConfigHash(configs);

        // Assert
        Assert.Equal(64, hash.Length);
    }

    private static Dictionary<string, Dictionary<string, JsonElement>> CreateSampleConfigs()
    {
        return new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ftp"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["hostname"] = JsonDocument.Parse("\"ftp.example.com\"").RootElement,
                ["port"] = JsonDocument.Parse("21").RootElement,
                ["username"] = JsonDocument.Parse("\"user\"").RootElement,
                ["password"] = JsonDocument.Parse("\"pass\"").RootElement,
            },
            ["rcon"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["password"] = JsonDocument.Parse("\"secret\"").RootElement,
            },
            ["agent"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["logFilePath"] = JsonDocument.Parse("\"/logs/games_mp.log\"").RootElement,
            }
        };
    }
}
