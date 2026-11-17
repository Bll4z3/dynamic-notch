using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>A new UI font (Settings ▸ General) applied live: the text dips and cross-fades back in the new face.</summary>
public sealed partial class NotchWindow
{
    private CompSpring? _fontFade;

    public void ApplyFont(ISet<string> previousSources)
    {
        if (_fontFade == null)
        {
            var v = Anim.VisualOf(ContentRoot);
            var (spring, set) = Motions.Value(v.Compositor, Motion.FadeIn, 1);
            _fontFade = spring;
            v.StartAnimation("Opacity", Anim.Expr(v.Compositor, "Clamp(p.v, 0, 1)", ("p", set)));
        }
        FontService.Refresh(Root, previousSources);
        if (!Anim.Enabled) return;
        _fontFade.Snap(Motion.FontSwapDip);
        _fontFade.SetTarget(1, Motion.FadeIn);
    }
}
