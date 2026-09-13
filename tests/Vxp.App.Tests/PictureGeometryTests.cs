using Vxp.Config;
using Vxp.Format;
using Xunit;

namespace Vxp.Tests;

/// <summary>
/// The stored 144 x 80 grid is not the shape of the picture, and the difference is easy
/// to lose track of, so the two are pinned down here.
/// </summary>
public class PictureGeometryTests
{
    [Fact]
    public void TheDefaultPutsAFrameBackAtFourByThree()
    {
        var video = new VideoSettings();
        var width = PlayerWindow.DisplayWidth(video);

        var aspect = width / (double)FrameLayout.Height;
        Assert.InRange(aspect, 4.0 / 3.0 - 0.02, 4.0 / 3.0 + 0.02);
    }

    [Fact]
    public void OneShowsTheStoredGridUntouched()
    {
        var video = new VideoSettings { PixelAspect = 1.0 };

        Assert.Equal(FrameLayout.Width, PlayerWindow.DisplayWidth(video));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(99.0)]
    public void AnImpossibleAspectIsClampedRatherThanProducingAnUnusableWindow(double aspect)
    {
        var video = new VideoSettings { PixelAspect = aspect };
        var width = PlayerWindow.DisplayWidth(video);

        Assert.InRange(width, FrameLayout.Width / 2, FrameLayout.Width * 2);
    }
}
