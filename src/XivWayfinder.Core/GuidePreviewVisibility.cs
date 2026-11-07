using System.Numerics;

namespace XivWayfinder.Core;

public readonly record struct GuidePreviewScreenRect(Vector2 Minimum,Vector2 Maximum)
{
    public bool Valid => float.IsFinite(Minimum.X) && float.IsFinite(Minimum.Y)
        && float.IsFinite(Maximum.X) && float.IsFinite(Maximum.Y) && Maximum.X>Minimum.X && Maximum.Y>Minimum.Y;
}

public enum GuidePreviewVisibilityResult { Clear, InvalidBounds, Offscreen, Selector }

/// <summary>Projects the box enclosing the measured preview body. Testing the
/// complete screen envelope also catches a window between projected corners.</summary>
public static class GuidePreviewVisibility
{
    public static bool Clear(Vector3 bottom,float radius,float height,GuidePreviewScreenRect selector,
        Func<Vector3,Vector2?> project,float margin=8)
        =>Evaluate(bottom,radius,height,selector,project,margin)==GuidePreviewVisibilityResult.Clear;

    public static GuidePreviewVisibilityResult Evaluate(Vector3 bottom,float radius,float height,GuidePreviewScreenRect selector,
        Func<Vector3,Vector2?> project,float margin=8)
    {
        if (!TrailPlacement.IsFinite(bottom) || !float.IsFinite(radius) || radius<=0
            || !float.IsFinite(height) || height<=0 || !selector.Valid || !float.IsFinite(margin) || margin<0) return GuidePreviewVisibilityResult.InvalidBounds;
        var minimum=new Vector2(float.PositiveInfinity);var maximum=new Vector2(float.NegativeInfinity);
        for(var corner=0;corner<8;corner++)
        {
            var point=bottom+new Vector3((corner&1)==0?-radius:radius,(corner&2)==0?0:height,(corner&4)==0?-radius:radius);
            if(project(point) is not { } at || !float.IsFinite(at.X) || !float.IsFinite(at.Y))return GuidePreviewVisibilityResult.Offscreen;
            minimum=Vector2.Min(minimum,at);maximum=Vector2.Max(maximum,at);
        }
        return maximum.X+margin<selector.Minimum.X || minimum.X-margin>selector.Maximum.X
            || maximum.Y+margin<selector.Minimum.Y || minimum.Y-margin>selector.Maximum.Y
            ? GuidePreviewVisibilityResult.Clear : GuidePreviewVisibilityResult.Selector;
    }
}
