using MattEland.HopeLab.Themes.Tokens;

namespace MattEland.HopeLab.Themes.Tests;

public sealed class TokenColorTests
{
    [Theory]
    [InlineData("#F9A8C9", "#FFF9A8C9")]
    [InlineData("#abc", "#FFAABBCC")]
    [InlineData("#11223344", "#44112233")]
    [InlineData("rgba(249, 168, 201, 0.25)", "#40F9A8C9")]
    [InlineData("rgb(10 20 30 / 50%)", "#800A141E")]
    [InlineData("transparent", "#00000000")]
    [InlineData("white", "#FFFFFFFF")]
    public void Parses_every_form_a_token_uses(string css, string avalonia) =>
        Assert.Equal(avalonia, TokenColor.Parse(css)!.Value.ToString());

    [Theory]
    [InlineData("linear-gradient(180deg, #000 0%, #fff 100%)")]
    [InlineData("{surface.panel}")]
    [InlineData("color-mix(in srgb, red 50%, blue)")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void Is_null_for_anything_but_a_plain_colour(string css) => Assert.Null(TokenColor.Parse(css));

    [Fact]
    public void Black_on_white_is_21_to_1() => Assert.Equal(21, TokenColor.Black.Contrast(TokenColor.White), 3);

    [Fact]
    public void Identical_colours_are_0_apart_and_1_to_1()
    {
        var c = TokenColor.Parse("#93BBF5")!.Value;
        Assert.Equal(0, c.DeltaE(c), 6);
        Assert.Equal(1, c.Contrast(c), 6);
    }

    [Fact]
    public void AtLeast_lifts_a_colour_to_the_ratio_away_from_its_ground()
    {
        var ground = TokenColor.Parse("#FFFFFF")!.Value;
        var faint = TokenColor.Parse("#E3CF2A")!.Value;
        var lifted = faint.AtLeast(3, ground);
        Assert.True(lifted.Contrast(ground) >= 3);
        Assert.True(lifted.Luminance < faint.Luminance); // darker, on a light ground
    }
}
