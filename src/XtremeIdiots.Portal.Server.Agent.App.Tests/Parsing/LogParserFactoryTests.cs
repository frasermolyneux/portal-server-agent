using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Parsing;

public sealed class LogParserFactoryTests
{
    private readonly LogParserFactory _factory = new();

    [Theory]
    [InlineData("CallOfDuty2", typeof(Cod2LogParser))]
    [InlineData("CallOfDuty4", typeof(Cod4LogParser))]
    [InlineData("CallOfDuty5", typeof(Cod5LogParser))]
    public void Create_SupportedGameType_ReturnsCorrectParser(string gameType, Type expectedType)
    {
        var parser = _factory.Create(gameType);

        Assert.IsType(expectedType, parser);
    }

    [Fact]
    public void Create_UnsupportedGameType_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _factory.Create("UnknownGame"));
    }

    [Fact]
    public void Create_ReturnsNewInstanceEachTime()
    {
        var parser1 = _factory.Create("CallOfDuty4");
        var parser2 = _factory.Create("CallOfDuty4");

        Assert.NotSame(parser1, parser2);
    }
}
