using Xunit;

namespace Posters.Tests;

public class PickTests
{
    [Theory]
    [InlineData(1, 2000, 1024, 1, 1024)]
    [InlineData(4096, 2048, 1024, 1024, 512)]
    [InlineData(100, 50, 1024, 100, 50)]
    public void FitSize(int w, int h, int max, int width, int height) =>
        Assert.Equal((width, height), Pick.FitSize(w, h, max));

    [Fact]
    public void OrderIsTheSameWhicheverWayTheFilesArrive()
    {
        const string root = "/packs";
        string a = "/packs/A/mortar/posters/1.png";
        string b = "/packs/B/mortar/posters/1.png";
        Assert.Equal(Pick.Order([a, b], root), Pick.Order([b, a], root));
    }
}
