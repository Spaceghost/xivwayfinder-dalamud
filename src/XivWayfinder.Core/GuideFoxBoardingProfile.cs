namespace XivWayfinder.Core;

/// <summary>Explicit opt-in by selecting this exact derived CC-BY asset. Original
/// Fox and unrelated models remain unsupported; clip names alone never qualify.</summary>
public static class GuideFoxBoardingProfile
{
    public const string Sha256 = "48625dfbdd2304ccd7fc6ef090220675d0cdb8c78634564765099c222d304f67";
    public static GuideBoardingCapability Capability(GuideMesh mesh, float scale)
    {
        if (mesh.SourceSha256 != Sha256 || !float.IsFinite(scale) || scale is < .1f or > 1.5f
            || mesh.Animations.Count < 6 || mesh.Animations[3].Name != "Procedural.Board.CrouchLeapSettle"
            || mesh.Animations[4].Name != "Procedural.Board.Held" || mesh.Animations[5].Name != "Procedural.Board.ReturnLand") return default;
        return new(GuideJumpAnimationKind.ImportedClip, Sha256, 3, 4, 5, .56f * scale + .03f, .56f * scale + .03f,
            .8f, GuideBoardingHoldMode.FixedPose);
    }
}
