using Annotator.Core;
using Annotator.Workspace.Toolbar;

namespace Annotator.Workspace.Tests;

/// <summary>
/// Glass toolbar's raised lens (WIN-UI-09): the Mac's numbers for the body, rim, shadow and each role's tint. Whether a lens
/// shows at all is <see cref="ToolbarGlass.Shows"/>: the solid toolbar keeps its flat tints (<see cref="ToolbarGlassTests"/>).
/// </summary>
public sealed class ToolbarLensTests
{
    private static readonly RGBA Accent = new(0, 0.48, 1);
    private static readonly RGBA Green = new(0.2, 0.78, 0.35);
    private static readonly RGBA Orange = new(1, 0.55, 0.16);

    [Theory]
    [InlineData(false, 0.82, 0.5, 0.95, 0.25)]
    [InlineData(true, 0.26, 0.1, 0.55, 0.06)]
    public void EveryLensHasTheSameWhiteBodyAndRim(bool dark, double bodyTop, double bodyBottom, double rimTop, double rimBottom)
    {
        foreach (ToolbarLensRole role in Enum.GetValues<ToolbarLensRole>())
        {
            ToolbarLensLook look = Look(role, dark);
            Assert.Equal(new RGBA(1, 1, 1, bodyTop), look.BodyTop);
            Assert.Equal(new RGBA(1, 1, 1, bodyBottom), look.BodyBottom);
            Assert.Equal(new RGBA(1, 1, 1, rimTop), look.RimTop);
            Assert.Equal(new RGBA(1, 1, 1, rimBottom), look.RimBottom);
        }
    }

    [Theory]
    [InlineData(ToolbarLensRole.Chosen, false, 0.1)]
    [InlineData(ToolbarLensRole.Chosen, true, 0.18)]
    [InlineData(ToolbarLensRole.On, false, 0.1)]
    [InlineData(ToolbarLensRole.On, true, 0.18)]
    [InlineData(ToolbarLensRole.Draw, false, 0.12)]
    [InlineData(ToolbarLensRole.Draw, true, 0.16)]
    [InlineData(ToolbarLensRole.Waiting, false, 0.12)]
    [InlineData(ToolbarLensRole.Waiting, true, 0.16)]
    public void EachRoleAddsAFaintTint(ToolbarLensRole role, bool dark, double alpha)
    {
        RGBA color = role switch { ToolbarLensRole.Chosen or ToolbarLensRole.On => Accent, ToolbarLensRole.Draw => Green, _ => Orange };
        Assert.Equal(color with { Alpha = alpha }, Look(role, dark).Tint);
        Assert.Null(Look(ToolbarLensRole.Neutral, dark).Tint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnythingThatsOnSitsOnTheChosenToolsLens(bool dark) =>
        Assert.Equal(Look(ToolbarLensRole.Chosen, dark), Look(ToolbarLensRole.On, dark));

    [Theory]
    [InlineData(false, 0.16)]
    [InlineData(true, 0.35)]
    public void TheShadowIsAsStrongAsTheLensIsOpaque(bool dark, double black)
    {
        double neutral = Look(ToolbarLensRole.Neutral, dark).ShadowOpacity;
        Assert.Equal(black * (dark ? 0.18 : 0.66), neutral, 6);
        // A tint makes the lens a little more opaque, and its shadow a little darker.
        foreach (ToolbarLensRole role in new[] { ToolbarLensRole.Chosen, ToolbarLensRole.On, ToolbarLensRole.Draw, ToolbarLensRole.Waiting })
        {
            Assert.InRange(Look(role, dark).ShadowOpacity, neutral, black);
        }
    }

    [Fact]
    public void TheShadowStepsFollowAGaussianEdge()
    {
        IReadOnlyList<(double Outset, double Share)> steps = ToolbarLens.ShadowSteps;
        // From the outside in, 1.5 points apart, each adding some shadow.
        for (int index = 1; index < steps.Count; index++)
        {
            Assert.Equal(steps[index - 1].Outset - 1.5, steps[index].Outset, 6);
        }
        Assert.All(steps, step => Assert.InRange(step.Share, 0.05, 0.25));
        // Half the shadow at its own edge, as a blur leaves it; nearly all of it under the lens.
        double atEdge = steps.Where(step => step.Outset >= 0).Sum(step => step.Share);
        Assert.InRange(atEdge, 0.5, 0.56);
        Assert.InRange(steps.Sum(step => step.Share), 0.85, 0.9);
    }

    private static ToolbarLensLook Look(ToolbarLensRole role, bool dark) => ToolbarLens.For(role, dark, Accent, Green, Orange);
}
