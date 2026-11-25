using System.Diagnostics;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace DynamicNotch.Views;

/// <summary>
/// Music page glow, in two forms.
/// <para>
/// <b>Static</b> (Music-reactive glow off, Low power, animations off): a soft radial ellipse in the album art's most
/// vibrant colour at the notch's bottom centre; brighter while playing, dimmed while paused, breathing slowly.
/// </para>
/// <para>
/// <b>Music-reactive</b>: built on the album cover itself (blurred heavily, drifting and slowly turning) so the colours
/// always match it, with the same blurred cover as a halo around the art (its edge colours bleed into the notch). On
/// top, each part of the music has its own effect in the cover's palette: the kick punches the whole glow (it brightens
/// and expands, a feathered ring leaves the art, the art bumps), the sub-bass pools and wobbles along the bottom, the
/// snare flashes a light streak across, vocals flow as an aurora ribbon behind the title, hi-hats twinkle sparkles
/// upward, overall energy sets saturation, speed and particle density, and build-ups tighten the glow until the drop
/// bursts colour, a big shockwave and a particle explosion. A dark scrim keeps the text and controls crisp. Paused or
/// silent, it fades to a slow breathing of the cover's colours.
/// </para>
/// Rendering is all compositor work on baked, dithered textures (<see cref="GlowTextures"/>); the app's per-frame work
/// is reading the analyser's latest frame on CompositionTarget.Rendering and setting a few values, only while the scene
/// is showing.
/// </summary>
public sealed class AlbumGlow
{
    private readonly Compositor _c;
    private readonly CompositionPropertySet _g;
    private readonly CompositionPropertySet? _notch;
    private readonly CompSpring _level, _fade, _react, _energySpring, _ab, _extra, _lyricsMode;
    private readonly CompositionColorGradientStop[] _stopsA, _stopsB;
    private readonly ContainerVisual _group;
    private bool _frontIsA = true, _hasColor, _pulsing;
    private Color _color;

    // Scene geometry (host DIPs: the host is laid out at the open notch's top-left, expanded width).
    private const float SceneW = (float)Theme.ExpandedWidth, SceneH = (float)Theme.MusicExpandedHeight;

    // ---- Tuning (main knobs; per-style weights are in Configure) ----
    private const float BaseSize = 900;           // the blurred-cover base layer (soft-edged, drifting and turning)
    private const float BaseLevel = 0.42f;        // its opacity at rest
    private const float BleedScale = 1.95f;       // the halo of the cover around the art, × the art's size
    private const float KickExpand = 0.045f;      // how much the whole glow grows on a kick
    private const float ArtBump = 0.02f;          // how much the album art itself bumps on a kick
    private const float ScrimStrength = 0.74f;    // darkness behind the text and controls
    private const int Particles = 56;             // the particle cap
    private const int Rings = 3, Streaks = 2, RibbonSegments = 7;
    // ---- Tuning: vocals (how much the voice moves the glow; raise these for an even stronger vocal reaction) ----
    private const float VocalBaseLift = 0.15f;    // the cover glow brightens with the voice
    private const float VocalHaloLift = 0.2f;     // the halo around the art brightens with the voice
    private const float VocalHaloSwell = 0.07f;   // and swells
    private const float VocalBloomLevel = 0.34f;  // the vocal bloom's opacity at full voice
    private const float RibbonLevel = 0.85f;      // the aurora ribbon's opacity at full voice (was 0.65)
    private const float VocalDuck = 0.3f;         // how much the kick / bass contributions give way while someone sings
    private const float LyricsScrimStrength = 0.5f;

    /// <param name="host">Element (laid out at the expanded layer's top-left, expanded width) that hosts the glow.</param>
    /// <param name="notch">The notch property set ("H", "Full", "Page").</param>
    public AlbumGlow(UIElement host, CompositionPropertySet notch)
    {
        _c = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _notch = notch;
        foreach (var key in new[] { "AskOv", "PalOv" }) if (notch.TryGetScalar(key, out _) != CompositionGetValueStatus.Succeeded) notch.InsertScalar(key, 0);
        _g = _c.CreatePropertySet();
        _g.InsertScalar("Music", 0);
        _g.InsertScalar("FrontA", 1);
        _g.InsertScalar("Phase", 1);
        _g.InsertScalar("Pulsing", 0);
        _level = new CompSpring(_g, "Level", Motion.GlowDim, 0);
        _fade = new CompSpring(_g, "Fade", Motion.GlowCrossfade, 1);
        // Scene inputs: R = scene shown, E = live music present, AB = which cover layer is in front (crossfade),
        // T = time (runs faster with energy), Ph / Lk = beat phase and tempo lock, L1 = (sub, kick, snare, vocal),
        // L2 = (hats, energy, build, -), Drop = the drop burst (1 → 0), W* = effect weights (style × toggles).
        _react = new CompSpring(_g, "R", Motion.GlowReact, 0);
        _energySpring = new CompSpring(_g, "E", Motion.GlowReact, 0);
        _ab = new CompSpring(_g, "AB", Motion.GlowCrossfade, 0);
        // Ex = extra height below the music page (the lyrics layout), Ly = the lyrics layout's share (0..1);
        // Tv = the vocal aurora's own time (flows faster while someone sings);
        // V2 = (word / syllable pulse, pitch 0..1, held note, vocal presence).
        _extra = new CompSpring(_g, "Ex", Motion.LyricsMorph, 0, precision: 50);
        _lyricsMode = new CompSpring(_g, "Ly", Motion.LyricsMorph, 0);
        foreach (var k in new[] { "T", "Ph", "Lk", "Drop", "Tv" }) _g.InsertScalar(k, 0);
        _g.InsertVector4("L1", Vector4.Zero);
        _g.InsertVector4("L2", Vector4.Zero);
        _g.InsertVector4("V2", Vector4.Zero);
        Configure("visualiser", true, true, true, true, true, true, false);

        var root = _c.CreateContainerVisual();

        // ---- Static glow (unchanged look) ----
        _group = _c.CreateContainerVisual();
        _group.Size = new Vector2((float)Theme.GlowWidth, (float)Theme.GlowHeight);
        _group.StartAnimation("Offset", Expr($"Vector3({Anim.F((Theme.ExpandedWidth - Theme.GlowWidth) / 2)}, {Anim.F(Theme.MusicExpandedHeight - Theme.GlowHeight / 2)} + G.Ex, 0)"));
        _group.CenterPoint = new Vector3(_group.Size / 2, 0);
        root.Children.InsertAtTop(_group);
        var a = StaticEllipse(out _stopsA);
        var b = StaticEllipse(out _stopsB);
        _group.Children.InsertAtTop(a);
        _group.Children.InsertAtTop(b);
        a.StartAnimation("Opacity", Expr("G.FrontA * Clamp(G.Fade, 0, 1) + (1 - G.FrontA) * (1 - Clamp(G.Fade, 0, 1))"));
        b.StartAnimation("Opacity", Expr("(1 - G.FrontA) * Clamp(G.Fade, 0, 1) + G.FrontA * (1 - Clamp(G.Fade, 0, 1))"));
        string pulse = $"(1 - G.Pulsing * {Anim.F(Theme.GlowPulseOpacity)} * (1 - G.Phase))";
        _group.StartAnimation("Opacity", Expr($"(1 - G.R) * Clamp(G.Level, 0, 1) * {pulse}"));
        string scale = $"(1 + G.Pulsing * {Anim.F(Theme.GlowPulseScale)} * G.Phase)";
        _group.StartAnimation("Scale", Expr($"Vector3({scale}, {scale}, 1)"));

        // ---- Music-reactive scene ----
        _textures = new GlowTextures(_c);
        BuildScene(root);

        // Shared by both: visible only on the music page, once the notch is open, and not under Ask / the palette.
        string p = $"Clamp((N.H - {Anim.F(Theme.CollapsedHeight)}) / Max(1, N.Full - {Anim.F(Theme.CollapsedHeight)}), 0, 1)";
        string musicVisible = "(G.Music >= 0 ? 1 - Clamp(Abs(N.Page - G.Music), 0, 1) : 0)";
        string opened = Anim.SmoothStep(0.3, 1, p);
        string uncovered = "(1 - Clamp(Max(N.AskOv, N.PalOv), 0, 1))";
        root.StartAnimation("Opacity", Expr($"{musicVisible} * {opened} * {uncovered}"));
        ElementCompositionPreview.SetElementChildVisual(host, root);

        _sceneOff = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _sceneOff.IsRepeating = false;
        _sceneOff.Interval = TimeSpan.FromMilliseconds(1200);
        _sceneOff.Tick += (_, _) => { _sceneOff.Stop(); if (!_sceneOn) StopFrames(); };
    }

    private ExpressionAnimation Expr(string e)
    {
        var x = _c.CreateExpressionAnimation(e);
        x.SetReferenceParameter("G", _g);
        if (_notch != null) x.SetReferenceParameter("N", _notch);
        return x;
    }

    /// <summary>The glow's live values (the music page binds the album art's kick bump to them).</summary>
    public CompositionPropertySet Props => _g;

    /// <summary>Expression for the album art's scale on a kick (only while the reactive scene shows).</summary>
    public static string ArtBumpExpression => $"1 + {Anim.F(ArtBump)} * G.L1.Y * G.Wk * G.E * G.R";

    // =================================================================================================================
    // Static glow
    // =================================================================================================================
    private SpriteVisual StaticEllipse(out CompositionColorGradientStop[] stops)
    {
        var brush = _c.CreateRadialGradientBrush();
        stops = new[] { _c.CreateColorGradientStop(0, default), _c.CreateColorGradientStop(0.45f, default), _c.CreateColorGradientStop(1, default) };
        foreach (var s in stops) brush.ColorStops.Add(s);
        var v = _c.CreateSpriteVisual();
        v.Size = _group.Size;
        v.Brush = brush;
        return v;
    }

    private static void Paint(CompositionColorGradientStop[] stops, Color c)
    {
        stops[0].Color = Color.FromArgb(0xFF, c.R, c.G, c.B);
        stops[1].Color = Color.FromArgb(0x70, c.R, c.G, c.B);
        stops[2].Color = Color.FromArgb(0x00, c.R, c.G, c.B);
    }

    /// <summary>Index of the music page among the enabled pages (-1 = switched off).</summary>
    public void SetMusicIndex(int index) => _g.InsertScalar("Music", index);

    /// <summary>New track colour (static glow): paints the hidden ellipse and cross-fades to it (~600 ms).</summary>
    public void SetColor(Color color, bool animate)
    {
        if (_hasColor && color == _color) return;
        _color = color;
        if (!animate || !_hasColor)
        {
            _hasColor = true;
            Paint(_frontIsA ? _stopsA : _stopsB, color);
            _fade.Snap(1);
            return;
        }
        _frontIsA = !_frontIsA;
        Paint(_frontIsA ? _stopsA : _stopsB, color);
        _g.InsertScalar("FrontA", _frontIsA ? 1 : 0);
        _fade.Snap(0);
        _fade.SetTarget(1, Motion.GlowCrossfade);
    }

    /// <summary>Brightness target: playing, paused or off (both forms; the glow strength setting is folded in).</summary>
    public void SetLevel(double level) => _level.SetTarget(level, Motion.GlowDim);

    /// <summary>The static glow's slow breathing while playing with the notch open.</summary>
    public void SetPulsing(bool on)
    {
        on &= Anim.Enabled;
        if (on == _pulsing) return;
        _pulsing = on;
        _g.InsertScalar("Pulsing", on ? 1 : 0);
        if (!on) { _g.StopAnimation("Phase"); _g.InsertScalar("Phase", 1); return; }
        var a = _c.CreateScalarKeyFrameAnimation();
        var linear = _c.CreateLinearEasingFunction();
        const int keys = 120;
        for (int i = 0; i <= keys; i++)
        {
            double t = (double)i / keys;
            a.InsertKeyFrame((float)t, (float)(0.5 + 0.5 * Math.Sin(2 * Math.PI * t + Math.PI / 2)), linear);
        }
        a.Duration = TimeSpan.FromSeconds(Theme.GlowPulsePeriod);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        _g.StartAnimation("Phase", a);
    }

    // =================================================================================================================
    // Music-reactive scene: build
    // =================================================================================================================
    private readonly GlowTextures _textures;
    private ContainerVisual _punch = null!, _baseHost = null!, _ribbon = null!, _particleHost = null!;
    private SpriteVisual _baseA = null!, _baseB = null!, _vividA = null!, _vividB = null!, _bleedA = null!, _bleedB = null!, _subPool = null!, _scrim = null!, _flash = null!;
    private SpriteVisual _vocalBloom = null!, _lyricsScrim = null!;
    private CompositionColorBrush _vocalColour = null!;
    private readonly SpriteVisual[] _rings = new SpriteVisual[Rings], _particles = new SpriteVisual[Particles];
    private readonly ContainerVisual[] _streaks = new ContainerVisual[Streaks], _ribbonAnchors = new ContainerVisual[RibbonSegments];
    private CompositionColorBrush _main = null!, _subColour = null!, _ringColour = null!, _white = null!;
    private readonly CompositionColorBrush[] _accents = new CompositionColorBrush[3];
    private readonly CompositionMaskBrush[] _particleBrushes = new CompositionMaskBrush[10]; // 5 colours × (sharp, soft)
    private readonly (CompositionMaskBrush core, CompositionMaskBrush halo)[] _streakBrushes = new (CompositionMaskBrush, CompositionMaskBrush)[3];
    private CompositionBrush _fallbackBase = null!;

    private void BuildScene(ContainerVisual root)
    {
        _main = _c.CreateColorBrush(Theme.GlowNeutral);
        _subColour = _c.CreateColorBrush(Theme.GlowNeutral);
        _ringColour = _c.CreateColorBrush(Theme.GlowNeutral);
        _white = _c.CreateColorBrush(Colors.White);
        for (int i = 0; i < 3; i++) _accents[i] = _c.CreateColorBrush(Theme.GlowNeutral);
        _fallbackBase = _textures.Tinted(_subColour, _textures.Disc);

        var scene = _c.CreateContainerVisual();
        scene.Size = new Vector2(SceneW, SceneH + 40);
        root.Children.InsertAtTop(scene);
        scene.StartAnimation("Opacity", Expr($"G.R * Clamp(G.Level / {Anim.F(Theme.GlowMaxOpacity)} * 0.9, 0, 1)"));

        // The punch layer: everything that grows on a kick (about the art) and tightens during a build-up.
        _punch = _c.CreateContainerVisual();
        _punch.Size = scene.Size;
        scene.Children.InsertAtTop(_punch);
        _punch.StartAnimation("Scale", Expr(
            $"Vector3(1, 1, 0) * (1 - 0.05 * G.L2.Z * G.E) * (1 + {Anim.F(KickExpand)} * G.L1.Y * G.Wk * G.E + 0.012 * G.Lk * Pow(1 - G.Ph, 4) * G.Wk) + Vector3(0, 0, 1)"));

        // Shared levels, computed once per frame: the cover layer's brightness (paused: a slow 6 s breath) and the halo's.
        // Vx = how strongly the voice drives the glow right now; while someone sings, the kick and bass give way a little
        // (Duck) so the voice stands out, without changing how they move.
        foreach (var k in new[] { "BaseLvl", "BleedLvl", "Vx", "Duck" }) _g.InsertScalar(k, 0);
        _g.StartAnimation("Vx", Expr("Clamp(G.E * G.Wv * (G.L1.W + 0.35 * G.V2.X), 0, 1.4)"));
        _g.StartAnimation("Duck", Expr($"1 - {Anim.F(VocalDuck)} * Clamp(G.Vx, 0, 1)"));
        string breath = "(1 - (1 - G.E) * 0.22 * (0.5 + 0.5 * Sin(G.T * 1.0472)))";
        _g.StartAnimation("BaseLvl", Expr($"Clamp({Anim.F(BaseLevel)} + G.E * ((0.08 * G.L1.X * G.Wb + 0.16 * G.L1.Y * G.Wk) * G.Duck + 0.08 * G.L2.Z + 0.1 * G.L2.Y) + {Anim.F(VocalBaseLift)} * G.Vx + 0.3 * G.Drop * G.Wd, 0, 1) * {breath}"));
        _g.StartAnimation("BleedLvl", Expr($"Clamp(0.72 + G.E * (0.25 * G.L1.Y * G.Wk * G.Duck + 0.06 * G.L2.Y) + {Anim.F(VocalHaloLift)} * G.Vx, 0, 1) * {breath}"));

        // 1. The blurred cover, oversized, drifting and turning slowly (two layers cross-fade between tracks).
        _baseHost = _c.CreateContainerVisual();
        _baseHost.Size = new Vector2(BaseSize, BaseSize);
        _baseHost.CenterPoint = new Vector3(BaseSize / 2, BaseSize / 2, 0);
        float cx = SceneW * 0.42f, cy = SceneH * 0.5f;
        _baseHost.StartAnimation("Offset", Expr($"Vector3({Anim.F(cx - BaseSize / 2)} + Sin(G.T * 0.11) * 40, {Anim.F(cy - BaseSize / 2)} + Cos(G.T * 0.083) * 26, 0)"));
        _baseHost.StartAnimation("RotationAngle", Expr("G.T * 0.035"));
        _punch.Children.InsertAtTop(_baseHost);
        _baseA = Sprite(new Vector2(BaseSize), _fallbackBase);
        _baseB = Sprite(new Vector2(BaseSize), _fallbackBase);
        _baseA.StartAnimation("Opacity", Expr("G.BaseLvl * (1 - G.AB)"));
        _baseB.StartAnimation("Opacity", Expr("G.BaseLvl * G.AB"));
        _baseHost.Children.InsertAtTop(_baseA);
        _baseHost.Children.InsertAtTop(_baseB);
        // Energy and drops saturate the colours: a vivid grade of the same cover fades in on top (baked, no effect).
        _vividA = Sprite(new Vector2(BaseSize), _fallbackBase);
        _vividB = Sprite(new Vector2(BaseSize), _fallbackBase);
        string vivid = "G.BaseLvl * Clamp(G.E * (0.1 + 0.6 * G.L2.Y) + 0.6 * G.Drop * G.Wd, 0, 1)";
        _vividA.StartAnimation("Opacity", Expr($"{vivid} * (1 - G.AB)"));
        _vividB.StartAnimation("Opacity", Expr($"{vivid} * G.AB"));
        _baseHost.Children.InsertAtTop(_vividA);
        _baseHost.Children.InsertAtTop(_vividB);

        // 2. The cover's halo around the art: its edge colours bleed into the notch.
        _bleedA = Sprite(Vector2.One, _fallbackBase);
        _bleedB = Sprite(Vector2.One, _fallbackBase);
        _bleedA.StartAnimation("Opacity", Expr("G.BleedLvl * (1 - G.AB)"));
        _bleedB.StartAnimation("Opacity", Expr("G.BleedLvl * G.AB"));
        // The halo swells with the voice (and a little with each sung word / syllable).
        string swell = $"(1 + {Anim.F(VocalHaloSwell)} * Clamp(G.Vx, 0, 1.2) + 0.03 * G.V2.X * G.Wv * G.E)";
        foreach (var bl in new[] { _bleedA, _bleedB }) bl.StartAnimation("Scale", Expr($"Vector3({swell}, {swell}, 1)"));
        _punch.Children.InsertAtTop(_bleedA);
        _punch.Children.InsertAtTop(_bleedB);

        // 3. Sub-bass: a deep glow pooled along the bottom that swells and wobbles with the bassline (and the beat).
        var subSize = new Vector2(820, 280);
        _subPool = Sprite(subSize, _textures.Tinted(_subColour, _textures.Disc));
        _subPool.CenterPoint = new Vector3(subSize / 2, 0);
        _subPool.StartAnimation("Offset", Expr($"Vector3({Anim.F(SceneW / 2 - subSize.X / 2)} + (Sin(G.T * 1.9) * 22 + Sin(G.Ph * 6.2832) * 10 * G.Lk) * G.L1.X, {Anim.F(SceneH - subSize.Y / 2 + 8)} + G.Ex, 0)"));
        _subPool.StartAnimation("Scale", Expr("Vector3(1 + 0.1 * G.L1.X, 0.7 + 0.75 * G.L1.X, 1)"));
        _subPool.StartAnimation("Opacity", Expr("G.Wb * G.E * Clamp(0.2 + 0.75 * G.L1.X, 0, 1)"));
        _punch.Children.InsertAtTop(_subPool);

        // 4. Vocals. A soft bloom of colour that swells and brightens with the voice and builds on held notes, and an
        //    aurora ribbon of overlapping segments that flows faster while someone sings, rises with their pitch and
        //    swells on each sung word (behind the title, or behind the current lyric line in the lyrics layout).
        _vocalBloom = Sprite(new Vector2(560, 210), _textures.Tinted(_vocalColour = _c.CreateColorBrush(Theme.GlowNeutral), _textures.Disc));
        _vocalBloom.CenterPoint = new Vector3(280, 105, 0);
        _vocalBloom.StartAnimation("Opacity", Expr($"Clamp(Clamp(G.Vx, 0, 1) * ({Anim.F(VocalBloomLevel)} + 0.2 * G.V2.Z) + 0.1 * G.V2.X * G.Wv * G.E, 0, 0.8)"));
        _vocalBloom.StartAnimation("Scale", Expr("Vector3(0.72 + 0.32 * G.L1.W + 0.1 * G.V2.X + 0.12 * G.V2.Z, 0.7 + 0.38 * G.L1.W + 0.12 * G.V2.X, 1)"));
        _punch.Children.InsertAtTop(_vocalBloom);
        _ribbon = _c.CreateContainerVisual();
        _ribbon.Size = scene.Size;
        _ribbon.StartAnimation("Opacity", Expr($"G.Wv * G.E * Clamp(G.L1.W * 1.6 + 0.35 * G.V2.X, 0, 1) * {Anim.F(RibbonLevel)}"));
        _punch.Children.InsertAtTop(_ribbon);
        var segSize = new Vector2(150, 46);
        for (int i = 0; i < RibbonSegments; i++)
        {
            var anchor = _ribbonAnchors[i] = _c.CreateContainerVisual();
            anchor.Size = segSize;
            var seg = Sprite(segSize, _textures.Tinted(_accents[i % 2], _textures.Disc));
            seg.CenterPoint = new Vector3(segSize / 2, 0);
            string ph = Anim.F(i * 0.85);
            seg.StartAnimation("Offset", Expr($"Vector3(Sin(G.Tv * 0.9 + {ph}) * (10 + 8 * G.L1.W), Sin(G.Tv * 1.6 + {ph}) * (3 + 16 * G.L1.W) - (G.V2.Y - 0.45) * 26 * G.L1.W - 6 * G.V2.X, 0)"));
            seg.StartAnimation("Scale", Expr("Vector3(1 + 0.4 * G.L1.W + 0.2 * G.V2.X + 0.18 * G.V2.Z, 0.55 + 1.25 * G.L1.W + 0.45 * G.V2.X, 1)"));
            seg.StartAnimation("Opacity", Expr($"Clamp(0.6 + 0.4 * Sin(G.Tv * 1.3 + {Anim.F(i * 1.1)}), 0, 1)"));
            anchor.Children.InsertAtTop(seg);
            _ribbon.Children.InsertAtTop(anchor);
        }

        // 5. Snare: light streaks that flash across (played per snare).
        for (int i = 0; i < 3; i++) _streakBrushes[i] = (_textures.Tinted(_accents[i], _textures.Disc), _textures.Tinted(_accents[i], _textures.Disc));
        for (int i = 0; i < Streaks; i++)
        {
            var s = _streaks[i] = _c.CreateContainerVisual();
            s.Size = new Vector2(560, 120);
            s.CenterPoint = new Vector3(280, 60, 0);
            s.Opacity = 0;
            var halo = Sprite(new Vector2(560, 120), _streakBrushes[0].halo);
            halo.Opacity = 0.35f;
            var core = Sprite(new Vector2(560, 26), _streakBrushes[0].core);
            core.Offset = new Vector3(0, 47, 0);
            s.Children.InsertAtTop(halo);
            s.Children.InsertAtTop(core);
            _punch.Children.InsertAtTop(s);
        }

        // 6. Kick shockwaves: feathered rings from behind the art (played per kick).
        for (int i = 0; i < Rings; i++)
        {
            var ring = _rings[i] = Sprite(new Vector2(360, 360), _textures.Tinted(_ringColour, _textures.Ring));
            ring.CenterPoint = new Vector3(180, 180, 0);
            ring.Opacity = 0;
            _punch.Children.InsertAtTop(ring);
        }

        // 7. Drop: a burst of colour (played per drop).
        _flash = Sprite(new Vector2(1300, 700), _textures.Tinted(_accents[0], _textures.Disc));
        _flash.CenterPoint = new Vector3(650, 350, 0);
        _flash.Opacity = 0;
        _punch.Children.InsertAtTop(_flash);

        // 8. Scrim behind the text and controls (not part of the punch: it stays put).
        _scrim = Sprite(Vector2.One, _textures.Tinted(_c.CreateColorBrush(Colors.Black), _textures.Scrim));
        _scrim.Opacity = ScrimStrength;
        scene.Children.InsertAtTop(_scrim);
        // In the lyrics layout, a lighter scrim behind the lyrics keeps the lines readable over the glow.
        _lyricsScrim = Sprite(Vector2.One, _textures.Tinted(_c.CreateColorBrush(Colors.Black), _textures.Scrim));
        _lyricsScrim.StartAnimation("Opacity", Expr($"{Anim.F(LyricsScrimStrength)} * Clamp(G.Ly, 0, 1)"));
        scene.Children.InsertAtTop(_lyricsScrim);

        // 9. Particles, above everything.
        _particleHost = _c.CreateContainerVisual();
        _particleHost.Size = scene.Size;
        root.Children.InsertAtTop(_particleHost);
        _particleHost.StartAnimation("Opacity", Expr($"G.R * Clamp(G.Level / {Anim.F(Theme.GlowMaxOpacity)}, 0, 1)"));
        var colours = new[] { _main, _accents[0], _accents[1], _accents[2], _white };
        for (int c = 0; c < 5; c++)
        {
            _particleBrushes[c * 2] = _textures.Tinted(colours[c], _textures.Sharp);
            _particleBrushes[c * 2 + 1] = _textures.Tinted(colours[c], _textures.Soft);
        }
        for (int i = 0; i < Particles; i++)
        {
            var v = _particles[i] = _c.CreateSpriteVisual();
            v.Opacity = 0;
            _particleHost.Children.InsertAtTop(v);
        }

        SetLayout(new Rect(24, 71, Theme.AlbumArtSize, Theme.AlbumArtSize), new Rect(204, 72, 452, 154));
    }

    private SpriteVisual Sprite(Vector2 size, CompositionBrush brush)
    {
        var v = _c.CreateSpriteVisual();
        v.Size = size;
        v.Brush = brush;
        return v;
    }

    // =================================================================================================================
    // Inputs: layout, artwork + palette, configuration, analyser
    // =================================================================================================================
    private Rect _art, _details;
    private Vector2 _artCentre;

    /// <summary>
    /// Where the album art and the text / controls column are (host DIPs, at the page's resting spot); in the lyrics
    /// layout also the lyrics panel and its current line (the vocal ribbon and bloom flow behind that line).
    /// <paramref name="animate"/>: the layout is morphing, so everything glides to its new place instead of jumping.
    /// </summary>
    public void SetLayout(Rect art, Rect details, Rect? lyrics = null, Rect? focus = null, bool animate = false)
    {
        static bool Finite(Rect r) => double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height);
        if (!Finite(art) || !Finite(details) || art.Width <= 0 || details.Width <= 0) return;
        if (lyrics is { } lr && (!Finite(lr) || lr.Width <= 0)) lyrics = null;
        if (focus is { } fr && (!Finite(fr) || fr.Width <= 0)) focus = null;
        SetGlide(animate && Anim.Enabled && _laidOut);
        _laidOut = true;
        _art = art;
        _details = details;
        var ac = _artCentre = new Vector2((float)(art.X + art.Width / 2), (float)(art.Y + art.Height / 2));
        _punch.CenterPoint = new Vector3(ac, 0);
        var bleed = new Vector2((float)art.Width * BleedScale, (float)art.Height * BleedScale);
        foreach (var v in new[] { _bleedA, _bleedB }) { v.Size = bleed; v.CenterPoint = new Vector3(bleed / 2, 0); v.Offset = new Vector3(ac - bleed / 2, 0); }
        foreach (var ring in _rings) ring.Offset = new Vector3(ac - ring.Size / 2, 0);
        _flash.Offset = new Vector3((ac + new Vector2(SceneW / 2, SceneH / 2)) / 2 - _flash.Size / 2, 0);
        // Ribbon and bloom: along the title line (a little past both ends of the column), or behind the current lyric line.
        var band = focus ?? new Rect(details.X - 40, details.Y + 2, details.Width + 60, 0);
        float x0 = (float)band.X, x1 = (float)(band.X + band.Width), y = (float)(band.Y + band.Height / 2);
        for (int i = 0; i < RibbonSegments; i++)
        {
            float x = x0 + (x1 - x0) * i / (RibbonSegments - 1);
            _ribbonAnchors[i].Offset = new Vector3(x - 75, y - 23, 0);
        }
        _vocalBloom.Offset = new Vector3((x0 + x1) / 2 - _vocalBloom.Size.X / 2, y - _vocalBloom.Size.Y / 2 + 6, 0);
        var scrim = new Vector2((float)details.Width + 170, (float)details.Height + 130);
        _scrim.Size = scrim;
        _scrim.Offset = new Vector3((float)(details.X + details.Width / 2) - scrim.X / 2, (float)(details.Y + details.Height / 2) - scrim.Y / 2, 0);
        if (lyrics is { } l)
        {
            var ls = new Vector2((float)l.Width + 140, (float)l.Height + 90);
            _lyricsScrim.Size = ls;
            _lyricsScrim.Offset = new Vector3((float)(l.X + l.Width / 2) - ls.X / 2, (float)(l.Y + l.Height / 2) - ls.Y / 2, 0);
        }
    }

    private bool _laidOut, _gliding;
    private ImplicitAnimationCollection? _glide;

    /// <summary>While the layout morphs, every placed visual glides to its new offset / size (about as long as the morph).</summary>
    private void SetGlide(bool on)
    {
        if (on == _gliding) return;
        _gliding = on;
        if (on && _glide == null)
        {
            var ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.85f), new Vector2(0.25f, 1f));
            _glide = _c.CreateImplicitAnimationCollection();
            var offset = _c.CreateVector3KeyFrameAnimation();
            offset.Target = "Offset";
            offset.InsertExpressionKeyFrame(1, "this.FinalValue", ease);
            offset.Duration = TimeSpan.FromMilliseconds(560);
            var size = _c.CreateVector2KeyFrameAnimation();
            size.Target = "Size";
            size.InsertExpressionKeyFrame(1, "this.FinalValue", ease);
            size.Duration = TimeSpan.FromMilliseconds(560);
            _glide["Offset"] = offset;
            _glide["Size"] = size;
        }
        var visuals = new List<Visual> { _bleedA, _bleedB, _flash, _vocalBloom, _scrim, _lyricsScrim };
        visuals.AddRange(_rings);
        visuals.AddRange(_ribbonAnchors);
        foreach (var v in visuals) v.ImplicitAnimations = on ? _glide : null;
    }

    /// <summary>The lyrics layout: the notch is <paramref name="extra"/> DIPs taller (the bass pool and static glow move down) and the lyrics scrim shows.</summary>
    public void SetLyricsLayout(bool on, double extra, bool animate)
    {
        if (animate && Anim.Enabled) { _extra.SetTarget(on ? extra : 0, Motion.LyricsMorph); _lyricsMode.SetTarget(on ? 1 : 0, Motion.LyricsMorph); }
        else { _extra.Snap(on ? extra : 0); _lyricsMode.Snap(on ? 1 : 0); }
    }

    /// <summary>
    /// The lyric timing's vocal signal (<see cref="ViewModels.LyricsViewModel.VocalSignal"/>): when synced lyrics are
    /// playing, words being sung drive the vocal reaction too, exactly on time (level -1 = no lyrics: audio only).
    /// </summary>
    public Func<(float Level, int Word, bool Held)>? LyricVocal { get; set; }

    private SoftwareBitmap? _artwork;
    private bool _artInitialised, _layerB;
    private GlowPalette? _palette;

    /// <summary>
    /// The cover (null: none, or the glow follows the accent) and the palette. A new cover is blurred once into the
    /// hidden layer, which then cross-fades in (~0.9 s) together with the palette colours.
    /// </summary>
    public void SetArtwork(SoftwareBitmap? art, GlowPalette palette, bool animate)
    {
        animate &= Anim.Enabled && _artInitialised;
        if (!ReferenceEquals(art, _artwork) || !_artInitialised)
        {
            _artwork = art;
            var baked = art != null ? _textures.BlurredArt(art) : null;
            CompositionBrush brush = baked?.Normal ?? _fallbackBase;
            bool toB = _artInitialised ? !_layerB : false;
            (toB ? _baseB : _baseA).Brush = brush;
            (toB ? _vividB : _vividA).Brush = baked?.Vivid; // none without a cover
            (toB ? _bleedB : _bleedA).Brush = brush;
            _layerB = toB;
            if (animate) _ab.SetTarget(toB ? 1 : 0, Motion.GlowCrossfade); else _ab.Snap(toB ? 1 : 0);
            _artInitialised = true;
        }
        if (_palette != null && _palette.Main == palette.Main && _palette.Accents.SequenceEqual(palette.Accents)) return;
        _palette = palette;
        Recolor(_main, palette.Main, animate);
        Recolor(_subColour, Deepen(palette.Main), animate);
        Recolor(_ringColour, Lift(palette.Main, 0.3f), animate);
        Recolor(_white, palette.Monochrome ? Colors.White : Lift(palette.Main, 0.8f), animate);
        for (int i = 0; i < 3; i++) Recolor(_accents[i], palette.Accents[i % palette.Accents.Length], animate);
        Recolor(_vocalColour, Lift(palette.Accents.Length > 0 ? palette.Accents[0] : palette.Main, palette.Monochrome ? 0.5f : 0.22f), animate);
    }

    private void Recolor(CompositionColorBrush brush, Color target, bool animate)
    {
        if (!animate) { brush.Color = target; return; }
        var a = _c.CreateColorKeyFrameAnimation();
        a.InterpolationColorSpace = CompositionColorSpace.Rgb;
        a.InsertKeyFrame(1, target, _c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.2f, 1)));
        a.Duration = TimeSpan.FromMilliseconds(900);
        brush.StartAnimation("Color", a);
    }

    private static Color Lift(Color c, float t) =>
        Color.FromArgb(255, (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    private static Color Deepen(Color c) => Color.FromArgb(255, (byte)(c.R * 0.78), (byte)(c.G * 0.78), (byte)(c.B * 0.78));

    // ---- Configuration (Settings ▸ Customisation) ------------------------------------------------------------------
    private float _wk = 1, _wb = 1, _ws = 1, _wv = 1, _wh = 1, _wd = 1;
    private bool _debug;

    /// <summary>
    /// Glow style (visualiser: everything; club: bigger punches, shockwaves and drops; chill: the cover glow, the vocal
    /// aurora and a gentle bass only) times the per-effect switches, plus the debug overlay.
    /// </summary>
    public void Configure(string style, bool kick, bool bass, bool snare, bool vocal, bool sparkles, bool drops, bool debug)
    {
        var (k, b, s, v, h, d) = style switch
        {
            "club" => (1.7f, 1.25f, 1.25f, 0.8f, 1f, 1.5f),
            "chill" => (0f, 0.6f, 0f, 1f, 0f, 0f),
            _ => (1f, 1f, 1f, 1f, 1f, 1f),
        };
        _wk = kick ? k : 0; _wb = bass ? b : 0; _ws = snare ? s : 0; _wv = vocal ? v : 0; _wh = sparkles ? h : 0; _wd = drops ? d : 0;
        _g.InsertScalar("Wk", _wk);
        _g.InsertScalar("Wb", _wb);
        _g.InsertScalar("Ws", _ws);
        _g.InsertScalar("Wv", _wv);
        _g.InsertScalar("Wh", _wh);
        _g.InsertScalar("Wd", _wd);
        _debug = debug;
        UpdateDebugVisibility();
    }

    /// <summary>The analyser to read each frame (set by the window while music plays).</summary>
    public MusicReactiveService? Analyser { get; set; }

    /// <summary>The debug overlay (live levels and onset flashes), shown when the setting is on and the scene shows.</summary>
    public GlowDebugOverlay? DebugOverlay { get; set; }

    private void UpdateDebugVisibility()
    {
        if (DebugOverlay != null) DebugOverlay.Visibility = _debug && _sceneOn ? Visibility.Visible : Visibility.Collapsed;
    }

    // =================================================================================================================
    // On / off and the per-frame loop
    // =================================================================================================================
    private readonly DispatcherQueueTimer _sceneOff;
    private bool _sceneOn, _live, _signal, _frames;

    /// <summary>
    /// <paramref name="scene"/>: the reactive scene replaces the static glow (setting on, music page open; it breathes
    /// while paused). <paramref name="live"/>: music is playing, so the analyser runs. Turning the scene off fades it,
    /// then the per-frame loop stops (nothing runs while it's hidden).
    /// </summary>
    public void SetReactive(bool scene, bool live)
    {
        live &= scene;
        if (scene != _sceneOn)
        {
            _sceneOn = scene;
            _react.SetTarget(scene ? 1 : 0, Motion.GlowReact);
            if (scene) { _sceneOff.Stop(); StartFrames(); }
            else _sceneOff.Start();
            UpdateDebugVisibility();
        }
        if (live != _live)
        {
            _live = live;
            if (!live) { _signal = false; _energySpring.SetTarget(0, Motion.GlowReact); }
            else if (_sceneOn) StartFrames(); // music again: back to the per-frame loop
        }
    }

    private const double IdleTimeRate = 0.35; // how fast time runs while paused (the slow breathing)
    private bool _idleClock;
    private long _idleSince;
    private float _settledFor;

    /// <summary>Starts the per-frame loop (taking time over from the idle compositor clock, if it was running).</summary>
    private void StartFrames()
    {
        if (_idleClock)
        {
            _time += IdleTimeRate * (Stopwatch.GetTimestamp() - _idleSince) / (double)Stopwatch.Frequency;
            _g.StopAnimation("T");
            _g.InsertScalar("T", (float)_time);
            _idleClock = false;
        }
        _settledFor = 0;
        if (_frames) return;
        _frames = true;
        _lastFrame = Stopwatch.GetTimestamp();
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>
    /// Stops the per-frame loop. <paramref name="idle"/>: the scene still shows (paused), so the compositor keeps the
    /// slow breathing going on its own clock and the app does nothing per frame; otherwise everything stops.
    /// </summary>
    private void StopFrames(bool idle = false)
    {
        if (_frames)
        {
            _frames = false;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
        }
        if (idle && !_idleClock)
        {
            _g.InsertVector4("L1", Vector4.Zero);
            _g.InsertVector4("L2", Vector4.Zero);
            _g.InsertVector4("V2", Vector4.Zero);
            _g.InsertScalar("Lk", 0);
            _lock = 0;
            var a = _c.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0, (float)_time);
            a.InsertKeyFrame(1, (float)(_time + IdleTimeRate * 3600), _c.CreateLinearEasingFunction());
            a.Duration = TimeSpan.FromSeconds(3600);
            _g.StartAnimation("T", a);
            _idleSince = Stopwatch.GetTimestamp();
            _idleClock = true;
        }
        else if (!idle && _idleClock)
        {
            _time += IdleTimeRate * (Stopwatch.GetTimestamp() - _idleSince) / (double)Stopwatch.Frequency;
            _g.StopAnimation("T");
            _g.InsertScalar("T", (float)_time);
            _idleClock = false;
        }
    }

    private long _lastFrame;
    private double _time, _spawnDebt;
    private float _sub, _kick, _snare, _vocal, _hats, _energy, _build, _lock;
    private float _pulse, _vhold, _pitch = 0.4f, _presence;
    private double _tv;
    private int _kicks = -1, _snares = -1, _hatCount = -1, _drops = -1, _vocalOnsets = -1, _lyricWord;
    private int _nextRing, _nextStreak, _nextParticle, _nextAccent;
    private readonly Random _rng = new();

    /// <summary>
    /// Once per displayed frame (UI thread): the analyser's latest values become the compositor's inputs, new onsets fire
    /// their effects, and the time base advances (faster with energy). Paused, everything eases to rest and breathes.
    /// </summary>
    private void OnFrame(object? sender, object e)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        float dt = (float)Math.Clamp((nowTicks - _lastFrame) / (double)Stopwatch.Frequency, 0, 0.05);
        _lastFrame = nowTicks;
        var f = _live ? Analyser?.Latest : null;

        if (f != null)
        {
            _sub = f.Sub; _kick = f.Kick; _snare = f.Snare; _hats = f.Hats; _energy = f.Energy; _build = f.Build;
            if (f.Signal != _signal) { _signal = f.Signal; _energySpring.SetTarget(_signal ? 1 : 0, Motion.GlowReact); }
        }
        else
        {
            float decay = MathF.Exp(-dt / 0.35f); // ease to rest, no snapping
            _sub *= decay; _kick *= decay; _snare *= decay; _hats *= decay; _energy *= decay; _build *= decay;
        }

        // ---- Vocals: the voice the analysis hears and, when synced lyrics play, the words being sung (exactly on time:
        //      a word lights the voice up even under a loud mix, and the gaps between lines let it settle) ----
        var (lyric, word, held) = f != null && LyricVocal != null ? LyricVocal() : (-1f, 0, false);
        float audioVocal = f?.Vocal ?? 0, presence = f?.VocalPresence ?? 0;
        float vocalTarget = f == null ? 0
            : lyric >= 0 ? Math.Max(audioVocal * (0.65f + 0.35f * lyric), lyric * (0.4f + 0.6f * presence) * 0.9f)
            : audioVocal;
        _vocal += (vocalTarget - _vocal) * (1 - MathF.Exp(-dt / (vocalTarget > _vocal ? 0.05f : 0.3f)));
        if (f != null)
        {
            if (lyric > 0.3f && word != _lyricWord) _pulse = Math.Max(_pulse, held ? 1f : 0.8f);          // a new sung word
            if (lyric < 0 && _vocalOnsets >= 0 && f.VocalOnsetCount != _vocalOnsets) _pulse = Math.Max(_pulse, 0.6f); // or syllable
            _vocalOnsets = f.VocalOnsetCount;
            _lyricWord = word;
        }
        else _vocalOnsets = -1;
        _pulse *= MathF.Exp(-dt / 0.28f);
        float holdTarget = f != null ? Math.Max(f.VocalHold, held ? 0.75f : 0) : 0;
        _vhold += (holdTarget - _vhold) * (1 - MathF.Exp(-dt / 0.15f));
        if (f != null) _pitch += (f.VocalPitch - _pitch) * (1 - MathF.Exp(-dt / 0.1f));
        float presTarget = f != null ? Math.Max(presence, lyric > 0.3f ? 0.6f : 0) : 0;
        _presence += (presTarget - _presence) * (1 - MathF.Exp(-dt / 0.2f));
        // The aurora flows faster while someone sings, with a nudge on every word.
        _tv += dt * (f != null && _signal ? 0.5 + 1.6 * _vocal + 0.6 * _pulse : 0.35);
        _g.InsertScalar("Tv", (float)_tv);
        _g.InsertVector4("V2", new Vector4(_pulse, _pitch, _vhold, _presence));

        // Time runs faster with energy and during build-ups (motion speed), slowly while paused.
        _time += dt * (f != null && _signal ? 0.55 + 0.9 * _energy + 0.6 * _build : 0.35);
        _g.InsertScalar("T", (float)_time);
        _g.InsertVector4("L1", new Vector4(_sub, _kick, _snare, Math.Clamp(_vocal, 0, 1)));
        _g.InsertVector4("L2", new Vector4(_hats, _energy, _build, 0));

        // Beat phase from the tempo (once it's locked): a subtle on-beat pulse and the bass wobble ride it.
        float lockTarget = f != null && f.Bpm > 0 ? f.BpmConfidence : 0;
        _lock += (lockTarget - _lock) * (1 - MathF.Exp(-dt / 0.6f));
        _g.InsertScalar("Lk", _lock);
        if (f != null && f.Bpm > 0 && f.BeatTicks != 0)
        {
            double period = 60.0 / f.Bpm, since = (nowTicks - f.BeatTicks) / (double)Stopwatch.Frequency;
            _g.InsertScalar("Ph", (float)((since / period) % 1));
        }

        if (f != null)
        {
            if (_kicks >= 0 && f.KickCount != _kicks && _wk > 0) Shockwave(Math.Max(0.55f, f.Kick), big: false);
            if (_snares >= 0 && f.SnareCount != _snares && _ws > 0) Streak(Math.Max(0.55f, f.Snare));
            if (_hatCount >= 0 && f.HatCount != _hatCount && _wh > 0)
                for (int i = 0, n = _energy > 0.65f ? 2 : 1; i < n; i++) Sparkle(Math.Max(0.5f, f.Hats));
            if (_drops >= 0 && f.DropCount != _drops && _wd > 0) Drop();
            _kicks = f.KickCount; _snares = f.SnareCount; _hatCount = f.HatCount; _drops = f.DropCount;

            // A trickle of sparkles: denser with energy, much denser and faster in a build-up.
            if (_wh > 0 && _signal)
            {
                _spawnDebt += dt * (0.4 + 2.2 * _energy + 4 * _build) * _wh;
                while (_spawnDebt >= 1) { _spawnDebt -= 1; Sparkle(0.45f); }
            }
        }
        else
        {
            _kicks = _snares = _hatCount = _drops = -1; // re-sync without firing when music resumes
            // Paused and everything has eased to rest: hand the breathing to the compositor and stop working per frame.
            bool rested = _sub + _kick + _snare + _vocal + _hats + _energy + _build + _pulse + _vhold < 0.01f && _lock < 0.01f;
            _settledFor = rested ? _settledFor + dt : 0;
            if (_settledFor > 0.5f && _sceneOn) { DebugOverlay?.Update(null, _debug); StopFrames(idle: true); return; }
        }

        DebugOverlay?.Update(f, _debug && _sceneOn);
    }

    // ---- Effects ---------------------------------------------------------------------------------------------------
    /// <summary>A feathered ring grows out from behind the art and fades (snappy for kicks, large for drops).</summary>
    private void Shockwave(float strength, bool big)
    {
        var ring = _rings[_nextRing];
        _nextRing = (_nextRing + 1) % Rings;
        float reach = big ? 4.2f : 2.3f * (0.85f + 0.15f * Math.Min(_wk, 1.7f));
        var ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.05f, 0.7f), new Vector2(0.2f, 1f));
        var duration = TimeSpan.FromMilliseconds(big ? 1300 : 680);
        var scale = _c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(0.45f, 0.45f, 1));
        scale.InsertKeyFrame(1, new Vector3(reach, reach * 0.9f, 1), ease);
        scale.Duration = duration;
        var fade = _c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(0.08f, Math.Min(1, (big ? 0.95f : 0.6f) * strength * Math.Min(1.3f, _wk == 0 ? 1 : _wk)));
        fade.InsertKeyFrame(1, 0, ease);
        fade.Duration = duration;
        ring.StartAnimation("Scale", scale);
        ring.StartAnimation("Opacity", fade);
    }

    /// <summary>Snare / clap: a bright streak sweeps across the glow in an accent colour (~0.3 s).</summary>
    private void Streak(float strength)
    {
        var s = _streaks[_nextStreak];
        _nextStreak = (_nextStreak + 1) % Streaks;
        var (core, halo) = _streakBrushes[_nextAccent];
        _nextAccent = (_nextAccent + 1) % 3;
        ((SpriteVisual)s.Children.First()).Brush = halo;
        ((SpriteVisual)s.Children.Last()).Brush = core;
        float y = SceneH * (0.25f + 0.4f * (float)_rng.NextDouble()) - 60;
        bool leftToRight = _rng.Next(2) == 0;
        float from = leftToRight ? -520 : SceneW - 40, to = leftToRight ? SceneW - 40 : -520;
        var ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.15f, 0.6f), new Vector2(0.3f, 1f));
        var duration = TimeSpan.FromMilliseconds(320);
        var move = _c.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, new Vector3(from, y, 0));
        move.InsertKeyFrame(1, new Vector3(to, y, 0), ease);
        move.Duration = duration;
        var fade = _c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(0.18f, Math.Min(1, 0.85f * strength * _ws));
        fade.InsertKeyFrame(1, 0);
        fade.Duration = duration;
        var stretch = _c.CreateVector3KeyFrameAnimation();
        stretch.InsertKeyFrame(0, new Vector3(0.6f, 1, 1));
        stretch.InsertKeyFrame(1, new Vector3(1.3f, 0.8f, 1), ease);
        stretch.Duration = duration;
        s.StartAnimation("Offset", move);
        s.StartAnimation("Opacity", fade);
        s.StartAnimation("Scale", stretch);
    }

    /// <summary>The drop: a burst of colour, a big shockwave, a saturation spike and a particle explosion.</summary>
    private void Drop()
    {
        var burst = _c.CreateScalarKeyFrameAnimation();
        burst.InsertKeyFrame(0, 1);
        burst.InsertKeyFrame(1, 0, _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.5f), new Vector2(0.3f, 1f)));
        burst.Duration = TimeSpan.FromMilliseconds(1600);
        _g.StartAnimation("Drop", burst);
        var ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.7f), new Vector2(0.25f, 1f));
        var fade = _c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(0.1f, Math.Min(0.85f, 0.6f * _wd));
        fade.InsertKeyFrame(1, 0, ease);
        fade.Duration = TimeSpan.FromMilliseconds(1400);
        var scale = _c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(0.5f, 0.5f, 1));
        scale.InsertKeyFrame(1, new Vector3(1.4f, 1.4f, 1), ease);
        scale.Duration = fade.Duration;
        _flash.StartAnimation("Opacity", fade);
        _flash.StartAnimation("Scale", scale);
        Shockwave(1, big: true);
        if (_wh > 0 || _wd > 0) for (int i = 0; i < 30; i++) Explode();
    }

    /// <summary>A sparkle: a small glowing particle that rises from the lower part of the page, twinkling as it fades.</summary>
    private void Sparkle(float strength)
    {
        float speed = 1 + 1.2f * _build;
        var from = new Vector2(SceneW * (0.05f + 0.9f * (float)_rng.NextDouble()), SceneH * (0.45f + 0.55f * (float)_rng.NextDouble()));
        var to = from + new Vector2(((float)_rng.NextDouble() - 0.5f) * 60, -(40 + (float)_rng.NextDouble() * 90) * speed);
        Particle(from, to, (2.4 + _rng.NextDouble() * 2.2) / speed, strength, twinkle: true);
    }

    /// <summary>A drop particle: thrown out from behind the art in every direction.</summary>
    private void Explode()
    {
        double angle = _rng.NextDouble() * Math.PI * 2;
        float dist = 90 + (float)_rng.NextDouble() * 260;
        var from = _artCentre + new Vector2(((float)_rng.NextDouble() - 0.5f) * 40, ((float)_rng.NextDouble() - 0.5f) * 40);
        var to = from + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle) * 0.7f) * dist;
        Particle(from, to, 1.1 + _rng.NextDouble() * 0.9, 1, twinkle: false);
    }

    /// <summary>Plays one particle (the pool is the cap: the oldest is reused). Sharp and soft ones mix for depth.</summary>
    private void Particle(Vector2 from, Vector2 to, double seconds, float strength, bool twinkle)
    {
        var v = _particles[_nextParticle];
        _nextParticle = (_nextParticle + 1) % Particles;
        bool sharp = _rng.NextDouble() < 0.45;
        v.Brush = _particleBrushes[_rng.Next(5) * 2 + (sharp ? 0 : 1)];
        float size = sharp ? 2.2f + (float)_rng.NextDouble() * 2.8f : 5 + (float)_rng.NextDouble() * 8;
        float peak = Math.Min(1, (sharp ? 0.8f + 0.2f * (float)_rng.NextDouble() : 0.35f + 0.3f * (float)_rng.NextDouble()) * (0.6f + 0.5f * strength));
        v.Size = new Vector2(size);
        v.CenterPoint = new Vector3(size / 2, size / 2, 0);
        var half = new Vector2(size / 2);
        var duration = TimeSpan.FromSeconds(seconds);
        var ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.5f), new Vector2(0.4f, 1f));
        var move = _c.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, new Vector3(from - half, 0));
        move.InsertKeyFrame(1, new Vector3(to - half, 0), ease);
        move.Duration = duration;
        var fade = _c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(0.12f, peak);
        if (twinkle) { fade.InsertKeyFrame(0.3f, peak * 0.35f); fade.InsertKeyFrame(0.45f, peak); fade.InsertKeyFrame(0.62f, peak * 0.4f); fade.InsertKeyFrame(0.75f, peak * 0.8f); }
        fade.InsertKeyFrame(1, 0);
        fade.Duration = duration;
        var grow = _c.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0, new Vector3(0.5f, 0.5f, 1));
        grow.InsertKeyFrame(0.2f, Vector3.One);
        grow.InsertKeyFrame(1, new Vector3(0.4f, 0.4f, 1));
        grow.Duration = duration;
        v.StartAnimation("Offset", move);
        v.StartAnimation("Opacity", fade);
        v.StartAnimation("Scale", grow);
    }
}
