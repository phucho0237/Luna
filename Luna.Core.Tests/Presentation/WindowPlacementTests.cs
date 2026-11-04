using Luna.Core.Presentation;

namespace Luna.Core.Tests.Presentation;

public class WindowPlacementTests
{
    private const int LoginWidth = 520;
    private const int LoginHeight = 800;
    private const int Margin = 16;

    private static PixelRect Login(int dpi, int x, int y, int w, int h, int margin = Margin) =>
        WindowPlacement.Compute(LoginWidth, LoginHeight, dpi, x, y, w, h, margin);

    [Fact]
    public void At_100_percent_the_size_is_unchanged_and_centered()
    {
        PixelRect r = Login(96, 0, 0, 1920, 1040);

        Assert.Equal(new PixelRect(700, 120, 520, 800), r);
    }

    [Fact]
    public void At_125_percent_the_size_is_scaled()
    {
        PixelRect r = Login(120, 0, 0, 1920, 1040);

        Assert.Equal(new PixelRect(635, 20, 650, 1000), r);
    }

    [Fact]
    public void At_150_percent_the_size_is_scaled()
    {
        PixelRect r = Login(144, 0, 0, 2560, 1400);

        Assert.Equal(new PixelRect(890, 100, 780, 1200), r);
    }

    [Fact]
    public void At_200_percent_the_size_is_scaled()
    {
        PixelRect r = Login(192, 0, 0, 3840, 2040);

        Assert.Equal(new PixelRect(1400, 220, 1040, 1600), r);
    }

    [Fact]
    public void At_175_percent_an_odd_dpi_is_scaled()
    {
        PixelRect r = Login(168, 0, 0, 2560, 1440);

        Assert.Equal(new PixelRect(825, 20, 910, 1400), r);
    }

    [Fact]
    public void Fractional_pixels_round_away_from_zero()
    {
        // 101 * 120 / 96 = 126.25 -> 126; 3 * 144 / 96 = 4.5 -> 5.
        Assert.Equal(126, WindowPlacement.Compute(101, 101, 120, 0, 0, 2000, 2000, 0).Width);
        Assert.Equal(5, WindowPlacement.Compute(3, 3, 144, 0, 0, 2000, 2000, 0).Height);
    }

    [Fact]
    public void A_work_area_shorter_than_the_window_clamps_the_height()
    {
        // 1366 x 728 at 125 percent: the 800 DIP height (1000 px) cannot fit, the width can.
        PixelRect r = Login(120, 0, 0, 1366, 728);

        Assert.Equal(new PixelRect(358, 16, 650, 696), r);
    }

    [Fact]
    public void A_work_area_narrower_than_the_window_clamps_the_width()
    {
        PixelRect r = Login(96, 0, 0, 400, 1000);

        Assert.Equal(new PixelRect(16, 100, 368, 800), r);
    }

    [Fact]
    public void A_work_area_with_a_negative_origin_keeps_the_window_on_that_display()
    {
        PixelRect left = Login(96, -1920, 0, 1920, 1040);
        PixelRect above = Login(96, 0, -1080, 1920, 1040);

        Assert.Equal(new PixelRect(-1220, 120, 520, 800), left);
        Assert.Equal(new PixelRect(700, -960, 520, 800), above);
    }

    [Fact]
    public void A_clamped_window_on_a_negative_origin_stays_inside()
    {
        PixelRect r = Login(120, -1366, -300, 1366, 728);

        Assert.Equal(new PixelRect(-1008, -284, 650, 696), r);
    }

    [Fact]
    public void A_zero_margin_allows_the_full_work_area()
    {
        PixelRect r = Login(96, 5, 7, 300, 500, margin: 0);

        Assert.Equal(new PixelRect(5, 7, 300, 500), r);
    }

    [Fact]
    public void A_negative_margin_is_treated_as_zero()
    {
        PixelRect r = Login(96, 5, 7, 300, 500, margin: -50);

        Assert.Equal(new PixelRect(5, 7, 300, 500), r);
    }

    [Fact]
    public void A_tiny_work_area_still_gets_a_window_inside_it()
    {
        PixelRect r = Login(96, 10, 20, 100, 50);

        Assert.Equal(new PixelRect(26, 36, 68, 18), r);
    }

    [Fact]
    public void A_work_area_smaller_than_twice_the_margin_falls_back_to_the_work_area()
    {
        PixelRect r = Login(96, 10, 20, 20, 20);

        Assert.Equal(new PixelRect(10, 20, 20, 20), r);
    }

    [Fact]
    public void An_empty_work_area_gives_an_empty_rectangle_at_its_origin()
    {
        PixelRect r = Login(96, -30, 40, 0, 0);

        Assert.Equal(new PixelRect(-30, 40, 0, 0), r);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-96)]
    public void A_non_positive_dpi_is_treated_as_96(int dpi)
    {
        PixelRect r = Login(dpi, 0, 0, 1920, 1040);

        Assert.Equal(new PixelRect(700, 120, 520, 800), r);
    }

    [Fact]
    public void A_huge_request_does_not_overflow()
    {
        PixelRect r = WindowPlacement.Compute(int.MaxValue, int.MaxValue, 480, 0, 0, 1920, 1040, 16);

        Assert.Equal(new PixelRect(16, 16, 1888, 1008), r);
    }

    [Fact]
    public void The_result_always_lies_inside_the_work_area()
    {
        int[] dpis = [-1, 0, 96, 120, 144, 168, 192, 240, 288];
        int[] origins = [-3840, -1920, -17, 0, 5, 1920];
        int[] sizes = [0, 1, 20, 100, 728, 1040, 1920, 3840];
        int[] margins = [-5, 0, 16, 40, 500];
        (int W, int H)[] desired = [(520, 800), (1100, 760), (1, 1), (0, 0), (100000, 100000)];

        foreach (int dpi in dpis)
        foreach (int ox in origins)
        foreach (int oy in origins)
        foreach (int w in sizes)
        foreach (int h in sizes)
        foreach (int margin in margins)
        foreach ((int dw, int dh) in desired)
        {
            PixelRect r = WindowPlacement.Compute(dw, dh, dpi, ox, oy, w, h, margin);
            string context = $"dpi={dpi} origin=({ox},{oy}) size={w}x{h} margin={margin} desired={dw}x{dh} result={r}";

            Assert.True(r.Width >= 0 && r.Height >= 0, "Negative size: " + context);
            Assert.True(r.X >= ox && r.Y >= oy, "Outside at top-left: " + context);
            Assert.True((long)r.X + r.Width <= (long)ox + w, "Outside at right: " + context);
            Assert.True((long)r.Y + r.Height <= (long)oy + h, "Outside at bottom: " + context);
        }
    }
}
