using System.Numerics;
using CardForge.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace CardForge.Controls;

/// <summary>GPU-rendered (Win2D) thermal scene: a glowing GPU die inside a segmented LED ring, embers and flames
/// that grow with the temperature, a heat-haze distortion, and frost + snow while the overheat protection cools
/// the card down. No text beyond the temperature itself: the state is shown, not written.</summary>
public sealed class ThermalScene : UserControl
{
    sealed class Particle
    {
        public Vector2 P, V;
        public float Life, Max, Size, Seed, Spin;
    }

    readonly CanvasAnimatedControl _canvas;
    readonly object _lock = new();
    readonly Random _rng = new();
    readonly List<Particle> _embers = new(), _flames = new(), _snow = new();

    // inputs, written on the UI thread
    double? _target;
    double _trigger = 90, _resume = 70, _peak;
    bool _cooling;
    float[] _history = [];

    // render state, render thread only
    float _shown = 45, _flame, _frost, _haze, _heat;
    double _time;
    bool _coolingNow;
    double _peakNow, _triggerNow = 90, _resumeNow = 70;
    float[] _historyNow = [];
    CanvasRenderTarget? _scene;
    CanvasTextFormat? _big, _unit;

    public ThermalScene()
    {
        _canvas = new CanvasAnimatedControl { ClearColor = Color.FromArgb(255, 10, 12, 18) };
        _canvas.TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60);
        _canvas.Update += OnUpdate;
        _canvas.Draw += OnDraw;

        var root = new Grid { CornerRadius = new CornerRadius(8) };
        root.Children.Add(_canvas);
        Content = root;

        Loaded += (_, _) => _canvas.Paused = false;
        Unloaded += (_, _) => _canvas.Paused = true;
    }

    public void Update(ThermalInfo info)
    {
        lock (_lock)
        {
            _target = info.Gpu?.Temp;
            _trigger = info.Trigger is > 0 ? info.Trigger.Value : 90;
            _resume = info.Resume ?? 70;
            _cooling = info.Worker.Cooling && info.Worker.CoolReason == "heat";
            _peak = info.Worker.CoolPeak ?? _target ?? 0;
            _history = info.History.Select(t => (float)t).ToArray();
        }
    }

    // ---- colour ----------------------------------------------------------------------------------

    /// <summary>Temperature -> colour: ice blue, teal, green, amber, orange, red, magenta.</summary>
    public static Color TempColor(double t, byte alpha = 255)
    {
        (double T, uint C)[] stops = [(35, 0x3FA9F5), (55, 0x22D1C9), (70, 0x9BE15D), (80, 0xF7C948), (88, 0xFF8A3D), (95, 0xFF3B3B), (100, 0xE0247A)];
        uint a = stops[0].C, b = stops[0].C;
        double f = 0;
        if (t >= stops[^1].T) a = b = stops[^1].C;
        else
            for (int i = 1; i < stops.Length; i++)
                if (t <= stops[i].T)
                {
                    (a, b) = (stops[i - 1].C, stops[i].C);
                    f = Math.Clamp((t - stops[i - 1].T) / (stops[i].T - stops[i - 1].T), 0, 1);
                    break;
                }
        byte L(int sh) => (byte)(((a >> sh) & 0xFF) + ((((b >> sh) & 0xFF) - (double)((a >> sh) & 0xFF)) * f));
        return Color.FromArgb(alpha, L(16), L(8), L(0));
    }

    static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);
    static Color Mix(Color a, Color b, double f) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * f), (byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));

    float R(float min, float max) => min + (float)_rng.NextDouble() * (max - min);

    // ---- simulation --------------------------------------------------------------------------------

    void OnUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        float dt = (float)args.Timing.ElapsedTime.TotalSeconds;
        _time += dt;
        double? target;
        lock (_lock)
        {
            target = _target;
            (_triggerNow, _resumeNow, _coolingNow, _peakNow, _historyNow) = (_trigger, _resume, _cooling, _peak, _history);
        }
        if (target is double t) _shown += (float)((t - _shown) * Math.Min(1, dt * 2.5));

        var size = sender.Size;
        float w = (float)size.Width, h = (float)size.Height;
        var c = Center(w, h);
        float ring = Ring(w, h), chip = ring * 0.78f;

        _heat = Math.Clamp((_shown - 58) / 40f, 0, 1);
        double span = Math.Max(1, _peakNow - _resumeNow);
        float coolProgress = _coolingNow ? (float)Math.Clamp((_peakNow - _shown) / span, 0, 1) : 0;
        float flameTarget = _coolingNow ? (1 - coolProgress) * 0.75f
                          : _shown >= _triggerNow ? 1f
                          : Math.Clamp((_shown - ((float)_triggerNow - 10)) / 10f, 0, 1) * 0.45f;
        float frostTarget = _coolingNow ? 0.35f + 0.65f * coolProgress : 0;
        _flame += (flameTarget - _flame) * Math.Min(1, dt * 2);
        _frost += (frostTarget - _frost) * Math.Min(1, dt * 1.2f);
        _haze = Math.Clamp((_shown - 72) / 22f, 0, 1) * (1 - _frost * 0.8f);

        float top = c.Y - chip / 2;
        Spawn(_embers, dt * (_heat > 0.05f ? 8 + _heat * 70 : 0), 420, () => new Particle
        {
            P = new Vector2(c.X + R(-chip * 0.5f, chip * 0.5f), top + R(-4, 8)),
            V = new Vector2(R(-18, 18), -R(40, 120) * (0.6f + _heat)),
            Max = R(1.2f, 2.8f), Size = R(1.1f, 3.2f), Seed = R(0, 100),
        });
        Spawn(_flames, dt * _flame * 110, 360, () => new Particle
        {
            P = new Vector2(c.X + R(-chip * 0.42f, chip * 0.42f), top + R(0, 10)),
            V = new Vector2(R(-12, 12), -R(55, 125)),
            Max = R(0.55f, 1.15f), Size = R(9, 20) * (0.6f + chip / 260), Seed = R(0, 100),
        });
        Spawn(_snow, dt * _frost * 30, 220, () => new Particle
        {
            P = new Vector2(R(-10, w + 10), -10),
            V = new Vector2(R(-12, 12), R(18, 46)),
            Max = R(6, 11), Size = R(2.2f, 6), Seed = R(0, 100), Spin = R(-1.5f, 1.5f),
        });

        Step(_embers, dt, p => p.V.X += MathF.Sin((float)_time * 3 + p.Seed) * 26 * dt);
        Step(_flames, dt, p => p.V.X += MathF.Sin((float)_time * 7 + p.Seed) * 40 * dt);
        Step(_snow, dt, p => p.V.X += MathF.Sin((float)_time * 1.3f + p.Seed) * 8 * dt);
        _snow.RemoveAll(p => p.P.Y > h + 12);
    }


    void Spawn(List<Particle> list, float amount, int cap, Func<Particle> make)
    {
        int n = (int)amount + (_rng.NextDouble() < amount % 1 ? 1 : 0);
        for (int i = 0; i < n && list.Count < cap; i++)
        {
            var p = make();
            p.Life = p.Max;
            list.Add(p);
        }
    }

    static void Step(List<Particle> list, float dt, Action<Particle> force)
    {
        foreach (var p in list)
        {
            force(p);
            p.P += p.V * dt;
            p.Life -= dt;
        }
        list.RemoveAll(p => p.Life <= 0);
    }

    static Vector2 Center(float w, float h) => new(w / 2, h * 0.44f);
    static float Ring(float w, float h) => Math.Min(w * 0.36f, h * 0.3f);

    // ---- rendering ---------------------------------------------------------------------------------

    void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var size = sender.Size;
        float w = (float)size.Width, h = (float)size.Height;
        if (w < 20 || h < 20) return;
        if (_scene == null || Math.Abs(_scene.Size.Width - w) > 0.5 || Math.Abs(_scene.Size.Height - h) > 0.5)
        {
            _scene?.Dispose();
            _scene = new CanvasRenderTarget(sender, w, h);
        }
        _big ??= new CanvasTextFormat { FontWeight = FontWeights.Bold, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, FontFamily = "Segoe UI Variable Display" };
        _unit ??= new CanvasTextFormat { FontSize = 13, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };

        using (var ds = _scene.CreateDrawingSession())
            DrawScene(ds, w, h);

        var ds2 = args.DrawingSession;
        using var turbulence = new TurbulenceEffect
        {
            Frequency = new Vector2(0.012f, 0.028f), Octaves = 2, Size = new Vector2(w, h),
            Offset = new Vector2(0, (float)(_time * 70)),
        };
        using var hazed = new DisplacementMapEffect
        {
            Source = _scene, Displacement = turbulence, Amount = _haze * 26,
            XChannelSelect = EffectChannelSelect.Red, YChannelSelect = EffectChannelSelect.Green,
        };
        ICanvasImage img = _haze > 0.02f ? hazed : _scene;
        ds2.DrawImage(img);

        // bloom: a blurred copy added on top makes every emissive thing glow
        using var bloom = new GaussianBlurEffect { Source = img, BlurAmount = 9, BorderMode = EffectBorderMode.Soft };
        ds2.Blend = CanvasBlend.Add;
        ds2.DrawImage(bloom, 0, 0, new Rect(0, 0, w, h), 0.35f + _heat * 0.35f);
        ds2.Blend = CanvasBlend.SourceOver;

        if (_frost > 0.01f) DrawFrostVignette(ds2, w, h);
    }

    void DrawScene(CanvasDrawingSession ds, float w, float h)
    {
        ds.Clear(Color.FromArgb(255, 10, 12, 18));
        var c = Center(w, h);
        if (!_coolingNow && _shown >= 95)      // the whole rig trembles when it is really too hot
            ds.Transform = Matrix3x2.CreateTranslation(R(-1.4f, 1.4f), R(-1.4f, 1.4f));

        var hot = TempColor(_shown);
        float ring = Ring(w, h), chip = ring * 0.78f;

        // ambient glow and a faint engineering grid
        using (var glow = new CanvasRadialGradientBrush(ds, WithAlpha(hot, 0.18 + _heat * 0.4), Colors.Transparent)
        { Center = c, RadiusX = ring * (1.3f + _heat * 0.9f), RadiusY = ring * (1.3f + _heat * 0.9f) })
            ds.FillRectangle(0, 0, w, h, glow);
        var gridColor = Color.FromArgb(14, 255, 255, 255);
        for (float x = 0; x < w; x += 22) ds.DrawLine(x, 0, x, h, gridColor, 1);
        for (float y = 0; y < h; y += 22) ds.DrawLine(0, y, w, y, gridColor, 1);

        DrawHistory(ds, w, h);
        DrawRing(ds, c, ring);

        ds.Blend = CanvasBlend.Add;
        DrawFlames(ds);
        ds.Blend = CanvasBlend.SourceOver;

        DrawChip(ds, c, chip, hot);

        ds.Blend = CanvasBlend.Add;
        foreach (var p in _embers)
        {
            float a = p.Life / p.Max;
            var col = TempColor(_shown + p.Seed % 8);
            ds.FillCircle(p.P, p.Size * 3.2f, WithAlpha(col, 0.10 * a));
            ds.FillCircle(p.P, p.Size, WithAlpha(Mix(col, Colors.White, 0.35), a));
        }
        ds.Blend = CanvasBlend.SourceOver;

        foreach (var p in _snow) DrawFlake(ds, p);
        ds.Transform = Matrix3x2.Identity;
    }

    void DrawRing(CanvasDrawingSession ds, Vector2 c, float r)
    {
        const int n = 64;
        for (int i = 0; i < n; i++)
        {
            double f = (i + 0.5) / n, t = 30 + f * 70, a = (135 + f * 270) * Math.PI / 180;
            var dir = new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
            bool lit = _shown >= t;
            var col = TempColor(t);
            if (lit)
            {
                ds.DrawLine(c + dir * (r - 13), c + dir * (r + 13), WithAlpha(col, 0.22), 9);
                ds.DrawLine(c + dir * (r - 10), c + dir * (r + 10), col, 4.2f);
            }
            else
                ds.DrawLine(c + dir * (r - 10), c + dir * (r + 10), Color.FromArgb(255, 30, 35, 47), 4.2f);
        }
        // notches for the resume (70) and trigger (90) thresholds
        DrawNotch(ds, c, r, _resumeNow, Color.FromArgb(255, 34, 209, 201));
        DrawNotch(ds, c, r, _triggerNow, Color.FromArgb(255, 255, 59, 59));
        ds.DrawCircle(c, r + 22, Color.FromArgb(24, 255, 255, 255), 1);
        ds.DrawCircle(c, r - 22, Color.FromArgb(18, 255, 255, 255), 1);
    }

    static void DrawNotch(CanvasDrawingSession ds, Vector2 c, float r, double temp, Color col)
    {
        double a = (135 + (Math.Clamp(temp, 30, 100) - 30) / 70 * 270) * Math.PI / 180;
        var dir = new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
        var side = new Vector2(-dir.Y, dir.X);
        var tip = c + dir * (r + 17);
        using var tri = CanvasGeometry.CreatePolygon(ds, [tip, tip + dir * 11 + side * 6, tip + dir * 11 - side * 6]);
        ds.FillGeometry(tri, col);
    }

    void DrawChip(CanvasDrawingSession ds, Vector2 c, float s, Color hot)
    {
        var body = new Rect(c.X - s / 2, c.Y - s / 2, s, s);
        // pins
        var pin = Mix(Color.FromArgb(255, 110, 118, 135), hot, _heat * 0.6);
        for (int i = 0; i < 9; i++)
        {
            float o = -s / 2 + s * (i + 1) / 10f;
            ds.FillRectangle(c.X + o - 2, c.Y - s / 2 - 9, 4, 9, pin);
            ds.FillRectangle(c.X + o - 2, c.Y + s / 2, 4, 9, pin);
            ds.FillRectangle(c.X - s / 2 - 9, c.Y + o - 2, 9, 4, pin);
            ds.FillRectangle(c.X + s / 2, c.Y + o - 2, 9, 4, pin);
        }
        ds.FillRoundedRectangle(body, 12, 12, Color.FromArgb(255, 24, 29, 41));
        ds.DrawRoundedRectangle(body, 12, 12, WithAlpha(hot, 0.55 + _heat * 0.45), 2);

        // circuit traces from the die to the package edge
        float d = s * 0.58f;
        var trace = WithAlpha(hot, 0.25 + _heat * 0.5);
        for (int i = 0; i < 4; i++)
        {
            float o = -d / 2 + d * (i + 0.5f) / 4;
            ds.DrawLine(c.X + o, c.Y - d / 2, c.X + o * 1.35f, c.Y - s / 2 + 6, trace, 1.3f);
            ds.DrawLine(c.X + o, c.Y + d / 2, c.X + o * 1.35f, c.Y + s / 2 - 6, trace, 1.3f);
        }

        // the die glows with the temperature and breathes faster when hot
        float pulse = 1 + MathF.Sin((float)_time * (2 + _heat * 8)) * 0.08f * _heat;
        var die = new Rect(c.X - d / 2, c.Y - d / 2, d, d);
        using (var dieBrush = new CanvasRadialGradientBrush(ds, WithAlpha(Mix(hot, Colors.White, 0.25), 0.55 + _heat * 0.45), Color.FromArgb(255, 18, 22, 32))
        { Center = c, RadiusX = d * 0.8f * pulse, RadiusY = d * 0.8f * pulse })
            ds.FillRoundedRectangle(die, 6, 6, dieBrush);
        ds.DrawRoundedRectangle(die, 6, 6, WithAlpha(Mix(hot, Colors.White, 0.4), 0.8), 1.5f);

        // frost creeping over the package while it cools
        if (_frost > 0.01f)
        {
            var ice = Color.FromArgb((byte)(_frost * 210), 190, 235, 255);
            ds.DrawRoundedRectangle(new Rect(body.X - 4, body.Y - 4, body.Width + 8, body.Height + 8), 15, 15, ice, 2.5f);
            var rnd = new Random(7);
            for (int i = 0; i < 26; i++)
            {
                double edge = rnd.NextDouble() * 4, along = rnd.NextDouble();
                if (rnd.NextDouble() > _frost) continue;
                Vector2 p0 = (int)edge switch
                {
                    0 => new((float)(body.X + along * s), (float)body.Y),
                    1 => new((float)(body.X + s), (float)(body.Y + along * s)),
                    2 => new((float)(body.X + along * s), (float)(body.Y + s)),
                    _ => new((float)body.X, (float)(body.Y + along * s)),
                };
                var inward = Vector2.Normalize(c - p0);
                float len = 6 + (float)rnd.NextDouble() * 14 * _frost;
                var tip = p0 + inward * len;
                ds.DrawLine(p0, tip, ice, 1.4f);
                var side = new Vector2(-inward.Y, inward.X);
                ds.DrawLine(p0 + inward * len * 0.5f, p0 + inward * len * 0.5f + (inward + side) * len * 0.3f, ice, 1);
                ds.DrawLine(p0 + inward * len * 0.5f, p0 + inward * len * 0.5f + (inward - side) * len * 0.3f, ice, 1);
            }
        }

        _big!.FontSize = d * 0.52f;
        var text = ((int)Math.Round(_shown)).ToString();
        ds.DrawText(text, new Rect(die.X, die.Y - d * 0.06f, d, d), Mix(Colors.White, hot, 0.15), _big);
        ds.DrawText("°C", new Rect(die.X, die.Y + d * 0.3f, d, d * 0.4f), Color.FromArgb(150, 255, 255, 255), _unit!);
    }

    void DrawFlames(CanvasDrawingSession ds)
    {
        foreach (var p in _flames)
        {
            float a = p.Life / p.Max;                   // 1 = just born
            var core = Mix(Color.FromArgb(255, 255, 59, 59), Color.FromArgb(255, 255, 240, 170), a);
            var col = a > 0.5f ? Mix(Color.FromArgb(255, 255, 138, 61), core, (a - 0.5f) * 2) : Mix(Color.FromArgb(255, 200, 30, 60), Color.FromArgb(255, 255, 138, 61), a * 2);
            float size = p.Size * (0.35f + 0.65f * a);
            ds.FillCircle(p.P, size * 1.8f, WithAlpha(col, 0.07 * a));
            ds.FillCircle(p.P, size, WithAlpha(col, 0.22 * a));
        }
    }

    static void DrawFlake(CanvasDrawingSession ds, Particle p)
    {
        float life = p.Life / p.Max;
        float a = Math.Min(1, Math.Min(life * 3, (1 - life) * 6));
        var col = Color.FromArgb((byte)(a * 220), 205, 238, 255);
        float rot = p.Seed + (p.Max - p.Life) * p.Spin;
        for (int k = 0; k < 3; k++)
        {
            float ang = rot + k * MathF.PI / 3;
            var d = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * p.Size;
            ds.DrawLine(p.P - d, p.P + d, col, 1.1f);
            var n = new Vector2(-d.Y, d.X) * 0.35f;
            ds.DrawLine(p.P + d * 0.6f, p.P + d * 0.6f + (d * 0.25f + n), col, 0.8f);
            ds.DrawLine(p.P + d * 0.6f, p.P + d * 0.6f + (d * 0.25f - n), col, 0.8f);
        }
    }

    void DrawHistory(CanvasDrawingSession ds, float w, float h)
    {
        float top = h * 0.82f, bottom = h - 10, left = 14, right = w - 14;
        float Y(double t) => bottom - (float)((Math.Clamp(t, 30, 100) - 30) / 70) * (bottom - top);
        using var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        ds.DrawLine(left, Y(_triggerNow), right, Y(_triggerNow), Color.FromArgb(110, 255, 59, 59), 1, dash);
        ds.DrawLine(left, Y(_resumeNow), right, Y(_resumeNow), Color.FromArgb(90, 34, 209, 201), 1, dash);

        var hist = _historyNow;
        if (hist.Length < 2) return;
        const int capacity = 200;
        float step = (right - left) / (capacity - 1), x0 = right - step * (hist.Length - 1);
        for (int i = 1; i < hist.Length; i++)
        {
            float xa = x0 + (i - 1) * step, xb = x0 + i * step;
            var col = TempColor(hist[i]);
            ds.DrawLine(xb, Y(hist[i]), xb, bottom, WithAlpha(col, 0.16), step + 0.5f);
            ds.DrawLine(xa, Y(hist[i - 1]), xb, Y(hist[i]), WithAlpha(col, 0.25), 5);
            ds.DrawLine(xa, Y(hist[i - 1]), xb, Y(hist[i]), col, 1.8f);
        }
        ds.FillCircle(right, Y(hist[^1]), 3.5f, TempColor(hist[^1]));
    }

    void DrawFrostVignette(CanvasDrawingSession ds, float w, float h)
    {
        var c = new Vector2(w / 2, h / 2);
        using var brush = new CanvasRadialGradientBrush(ds,
        [
            new CanvasGradientStop { Position = 0.55f, Color = Colors.Transparent },
            new CanvasGradientStop { Position = 0.85f, Color = Color.FromArgb((byte)(_frost * 70), 120, 210, 255) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb((byte)(_frost * 150), 200, 240, 255) },
        ])
        { Center = c, RadiusX = w * 0.72f, RadiusY = h * 0.72f };
        ds.FillRectangle(0, 0, w, h, brush);
    }
}
