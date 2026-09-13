using GameLibrary.Domain.Paths;
using Xunit;

namespace GameLibrary.UnitTests.Paths;

public sealed class GamePathTests
{
    [Theory]
    [InlineData(@"D:\Official\游戏库\ウォーターエンブレム_解放の")]
    [InlineData(@"F:\[toolNeed]\[Unity]\Example C&D 100%")]
    [InlineData(@"F:\[26.7.27]\[Clockup]\ExampleB")]
    [InlineData(@"D:\Games\Example A")]
    public void Create_PreservesContentCharactersVerbatim(string input)
    {
        var path = GamePath.Create(input);

        Assert.Equal(input, path.PhysicalPath);
        Assert.Equal(input.ToUpperInvariant(), path.ComparisonKey);
    }

    [Fact]
    public void Create_NormalizesAlternateSeparator()
    {
        var path = GamePath.Create("D:/Games/Example");

        Assert.Equal(@"D:\Games\Example", path.PhysicalPath);
    }

    [Theory]
    [InlineData(@"D:\Games\Example")]
    [InlineData(@"d:\games\example")]
    [InlineData("D:/games/EXAMPLE/")]
    [InlineData(@"D:\Games\.\Example")]
    [InlineData(@"D:\Games\Sub\..\Example")]
    [InlineData(@"D:\Games\Example\\")]
    public void EquivalentPaths_ProduceEqualGamePaths(string input)
    {
        var canonical = GamePath.Create(@"D:\Games\Example");

        var actual = GamePath.Create(input);

        Assert.Equal(canonical, actual);
        Assert.Equal(canonical.GetHashCode(), actual.GetHashCode());
    }

    [Fact]
    public void Create_ResolvesDotDotWithinRoot()
    {
        var path = GamePath.Create(@"D:\A\B\..\C");

        Assert.Equal(@"D:\A\C", path.PhysicalPath);
        Assert.Equal(["A", "C"], path.Segments);
    }

    [Fact]
    public void Create_DriveRoot_IsValidAndEmptySegments()
    {
        var root = GamePath.Create(@"F:\");

        Assert.Equal(@"F:\", root.PhysicalPath);
        Assert.True(root.IsDriveRoot);
        Assert.Empty(root.Segments);
    }

    [Fact]
    public void Create_LongPath_IsAcceptedWithoutArtificialLimit()
    {
        var segment = new string('长', 80);
        var longPath = @"D:\L\" + string.Join('\\', Enumerable.Repeat(segment, 5));

        Assert.True(longPath.Length > 260);
        Assert.True(GamePath.TryCreate(longPath).IsValid);
    }

    [Theory]
    [InlineData(@"D:\Game""s", PathRejectReason.IllegalCharacters)]
    [InlineData(@"D:\Game<Name", PathRejectReason.IllegalCharacters)]
    [InlineData(@"D:\Game>Name", PathRejectReason.IllegalCharacters)]
    [InlineData(@"D:\Game|Name", PathRejectReason.IllegalCharacters)]
    [InlineData(@"D:\Game?Name", PathRejectReason.IllegalCharacters)]
    [InlineData(@"D:\Game*Name", PathRejectReason.IllegalCharacters)]
    [InlineData(@"D:\Game" + "\u001F" + "Name", PathRejectReason.IllegalCharacters)]
    [InlineData(@"D:\file.txt:stream", PathRejectReason.InvalidColonUse)]
    [InlineData(@"D:\file.txt::$DATA", PathRejectReason.InvalidColonUse)]
    [InlineData(@"D:\Trailing.", PathRejectReason.TrailingDotOrSpaceSegment)]
    [InlineData(@"D:\Trailing ", PathRejectReason.TrailingDotOrSpaceSegment)]
    [InlineData(@"D:\A.\B", PathRejectReason.TrailingDotOrSpaceSegment)]
    [InlineData(@"D:\CON", PathRejectReason.ReservedDeviceName)]
    [InlineData(@"D:\sub\nul.exe", PathRejectReason.ReservedDeviceName)]
    [InlineData(@"D:\Com1.dat", PathRejectReason.ReservedDeviceName)]
    [InlineData(@"\\server\share\game", PathRejectReason.UncPath)]
    [InlineData(@"\\?\D:\Game", PathRejectReason.DeviceNamespace)]
    [InlineData(@"\\.\D:\Game", PathRejectReason.DeviceNamespace)]
    [InlineData(@"Game\Relative", PathRejectReason.NotRootedLocalDrive)]
    [InlineData(@"D:NoSeparator", PathRejectReason.DriveRelativePath)]
    [InlineData(@"D:\A\..\..", PathRejectReason.EscapesRoot)]
    [InlineData("", PathRejectReason.NullOrEmpty)]
    public void TryCreate_RejectsInvalidInputsWithSpecificReasons(string input, PathRejectReason expected)
    {
        var result = GamePath.TryCreate(input);

        Assert.False(result.IsValid);
        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void TryCreate_NullInput_IsRejected()
    {
        Assert.False(GamePath.TryCreate(null!).IsValid);
    }

    [Fact]
    public void IsUnsupported_DistinguishesUnsupportedFromInvalid()
    {
        Assert.True(GamePath.TryCreate(@"\\server\share").IsUnsupported);
        Assert.True(GamePath.TryCreate(@"\\?\D:\X").IsUnsupported);
        Assert.False(GamePath.TryCreate(@"D:\A""B").IsUnsupported);
        Assert.False(GamePath.TryCreate(@"D:\OK").IsUnsupported);
    }

    [Fact]
    public void IsUnderOrEqualTo_RespectsSegmentBoundaries()
    {
        var games = GamePath.Create(@"F:\Games");
        var games2 = GamePath.Create(@"F:\Games2");
        var sub = GamePath.Create(@"F:\Games\Sub\Game");
        var root = GamePath.Create(@"F:\");
        var otherDrive = GamePath.Create(@"G:\Games\Sub");

        Assert.True(sub.IsUnderOrEqualTo(games));
        Assert.True(games.IsUnderOrEqualTo(games));
        Assert.False(games2.IsUnderOrEqualTo(games));
        Assert.True(sub.IsUnderOrEqualTo(root));
        Assert.False(sub.IsUnderOrEqualTo(otherDrive));
    }

    [Fact]
    public void TryGetRelativeSegments_ReturnsTailSegments()
    {
        var game = GamePath.Create(@"F:\[Unity]\ExampleC\ExampleC.exe");
        var root = GamePath.Create(@"F:\[Unity]");

        Assert.True(game.TryGetRelativeSegments(root, out var relative));
        Assert.Equal(["ExampleC", "ExampleC.exe"], relative);
    }
}
