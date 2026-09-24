using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace CardForge.Controls;

/// <summary>Click-through, full-window overlay (Win2D). Overheated: fire bursts in from every edge of the window.
/// Cooling: ice crystals grow in from the edges while snow drifts down. Idle: hidden and not rendering.
/// Uses CanvasControl (an image source XAML alpha-blends) rather than a swap chain, which would hide the UI below.</summary>
public sealed class ScreenFx : UserControl
{
    sealed class Particle
    {
        public Vector2 P, V;
        public float Life, Max, Size, Seed, Spin;
    }

    sealed class Branch
    {
        public Vector2 A, Dir;
        public float Len, Delay;       // Delay: growth fraction before this branch starts
        public List<Branch> Kids = new();
    }

    const float Strength = 0.5f;       // overall size/intensity of the effect

    readonly CanvasControl _canvas;
    readonly System.Diagnostics.Stopwatch _clock = new();
    bool _running;
    readonly object _lock = new();
    readonly Random _rng = new();
    readonly List<Particle> _flames = new(), _embers = new(), _snow = new();
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    List<Branch> _ice = new();
    Size _iceFor;

    // inputs (UI thread)
    string _mode = "";
    float _fireTarget, _frostTarget;

    // render thread
    float _fire, _frost;
    double _time;
    CanvasRenderTarget? _layer;

    public ScreenFx()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        _canvas = new CanvasControl { ClearColor = Colors.Transparent, IsHitTestVisible = false };
        _canvas.Draw += OnDraw;
        Content = _canvas;
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            lock (_lock)
                if (_mode != "") return;
            SetRunning(false);
            Visibility = Visibility.Collapsed;
        };
    }

    /// <summary>mode: "" | "fire" | "frost"; strength 0..1 (fire: how far past the trigger, frost: cooling progress).</summary>
    public void SetMode(string mode, float strength)
    {
        lock (_lock)
        {
            _mode = mode;
            _fireTarget = mode == "fire" ? (0.35f + 0.65f * strength) * Strength : 0;
            _frostTarget = mode == "frost" ? (0.3f + 0.7f * strength) * Strength : 0;
        }
        if (mode != "") Show();
        else if (Visibility == Visibility.Visible && !_hideTimer.IsEnabled) _hideTimer.Start();   // let the effect fade out
    }

    void Show()
    {
        _hideTimer.Stop();
        Visibility = Visibility.Visible;
        SetRunning(true);
    }

    void SetRunning(bool on)
    {
        if (on == _running) return;
        _running = on;
        if (on)
        {
            _clock.Restart();
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
        }
        else Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? sender, object e)
    {
        float dt = (float)Math.Min(0.05, _clock.Elapsed.TotalSeconds);
        _clock.Restart();
        Step(dt, (float)_canvas.ActualWidth, (float)_canvas.ActualHeight);
        _canvas.Invalidate();
    }

    float R(float a, float b) => a + (float)_rng.NextDouble() * (b - a);
    static Color A(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);
    static Color Mix(Color a, Color b, double f) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * f), (byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));

    // ---- simulation ---------------------------------------------------------------------------------

    void Step(float dt, float w, float h)
    {
        if (w < 20 || h < 20) return;
        _time += dt;
        float fireT, frostT;
        lock (_lock)
        {
            (fireT, frostT) = (_fireTarget, _frostTarget);
        }
        _fire += (fireT - _fire) * Math.Min(1, dt * 1.8f);
        _frost += (frostT - _frost) * Math.Min(1, dt * 0.9f);

        // flames enter from every edge; the bottom burns hardest because fire rises
        int n = (int)(dt * _fire * 320) + (_rng.NextDouble() < (dt * _fire * 320) % 1 ? 1 : 0);
        for (int i = 0; i < n && _flames.Count < 900; i++)
        {
            double edge = _rng.NextDouble();
            var p = edge switch
            {
                < 0.4 => new Particle { P = new(R(0, w), h + 12), V = new(R(-25, 25), -R(140, 300) * (0.6f + _fire)), Max = R(0.8f, 1.7f), Size = R(20, 48) },
                < 0.6 => new Particle { P = new(-12, R(0, h)), V = new(R(70, 190) * (0.6f + _fire), -R(40, 130)), Max = R(0.5f, 1.1f), Size = R(15, 36) },
                < 0.8 => new Particle { P = new(w + 12, R(0, h)), V = new(-R(70, 190) * (0.6f + _fire), -R(40, 130)), Max = R(0.5f, 1.1f), Size = R(15, 36) },
                _ => new Particle { P = new(R(0, w), -12), V = new(R(-25, 25), R(50, 130) * (0.6f + _fire)), Max = R(0.35f, 0.8f), Size = R(12, 28) },
            };
            p.Seed = R(0, 100);
            p.Size *= 0.6f;
            p.Life = p.Max;
            _flames.Add(p);
        }
        int m = (int)(dt * _fire * 90);
        for (int i = 0; i < m && _embers.Count < 400; i++)
        {
            var p = new Particle { P = new(R(0, w), h + 5), V = new(R(-60, 60), -R(120, 380)), Max = R(1.2f, 3f), Size = R(1.2f, 3.2f), Seed = R(0, 100) };
            p.Life = p.Max;
            _embers.Add(p);
        }
        int k = (int)(dt * _frost * 45) + (_rng.NextDouble() < (dt * _frost * 45) % 1 ? 1 : 0);
        for (int i = 0; i < k && _snow.Count < 300; i++)
        {
            var p = new Particle { P = new(R(-20, w + 20), -12), V = new(R(-15, 15), R(22, 60)), Max = R(9, 16), Size = R(2.5f, 7), Seed = R(0, 100), Spin = R(-1.2f, 1.2f) };
            p.Life = p.Max;
            _snow.Add(p);
        }

        foreach (var p in _flames)
        {
            p.V.X += MathF.Sin((float)_time * 8 + p.Seed) * 60 * dt;
            p.V.Y -= 60 * dt;                              // buoyancy
            p.P += p.V * dt;
            p.Life -= dt;
        }
        foreach (var p in _embers)
        {
            p.V.X += MathF.Sin((float)_time * 3 + p.Seed) * 40 * dt;
            p.P += p.V * dt;
            p.Life -= dt;
        }
        foreach (var p in _snow)
        {
            p.V.X += MathF.Sin((float)_time * 1.2f + p.Seed) * 10 * dt;
            p.P += p.V * dt;
            p.Life -= dt;
        }
        _flames.RemoveAll(p => p.Life <= 0);
        _embers.RemoveAll(p => p.Life <= 0);
        _snow.RemoveAll(p => p.Life <= 0 || p.P.Y > h + 20);
    }

    // ---- rendering ----------------------------------------------------------------------------------

    void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!_running) return;
        var size = sender.Size;
        float w = (float)size.Width, h = (float)size.Height;
        if (w < 20 || h < 20) return;
        if (_layer == null || Math.Abs(_layer.Size.Width - w) > 0.5 || Math.Abs(_layer.Size.Height - h) > 0.5)
        {
            _layer?.Dispose();
            _layer = new CanvasRenderTarget(sender, w, h);
        }
        if (_iceFor != size) BuildIce(w, h);

        var o = args.DrawingSession;
        if (_fire > 0.01f)
        {
            // particles -> soft blur -> upward-scrolling turbulence displacement = continuous licking flame tongues
            using (var ds = _layer.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                DrawFire(ds, w, h);
            }
            using var soft = new GaussianBlurEffect { Source = _layer, BlurAmount = 5, BorderMode = EffectBorderMode.Soft };
            using var noise = new TurbulenceEffect
            {
                Frequency = new Vector2(0.008f, 0.02f), Octaves = 3, Size = new Vector2(w, h),
                Offset = new Vector2((float)(_time * 12), (float)(_time * 140)),
            };
            using var tongues = new DisplacementMapEffect
            {
                Source = soft, Displacement = noise, Amount = 34 + 30 * _fire,
                XChannelSelect = EffectChannelSelect.Red, YChannelSelect = EffectChannelSelect.Green,
            };
            using var bloom = new GaussianBlurEffect { Source = tongues, BlurAmount = 16, BorderMode = EffectBorderMode.Soft };
            o.DrawImage(tongues);
            o.Blend = CanvasBlend.Add;
            o.DrawImage(bloom, 0, 0, new Rect(0, 0, w, h), 0.55f * _fire);
            o.Blend = CanvasBlend.SourceOver;
        }
        if (_frost > 0.01f) DrawFrost(o, w, h);
    }

    void EdgeGlow(CanvasDrawingSession ds, float w, float h, Color edge, float depth)
    {
        void Band(Vector2 from, Vector2 to, Rect rect)
        {
            using var b = new CanvasLinearGradientBrush(ds, edge, Colors.Transparent) { StartPoint = from, EndPoint = to };
            ds.FillRectangle(rect, b);
        }
        Band(new(0, h), new(0, h - depth * 1.4f), new Rect(0, h - depth * 1.4f, w, depth * 1.4f));
        Band(new(0, 0), new(0, depth), new Rect(0, 0, w, depth));
        Band(new(0, 0), new(depth, 0), new Rect(0, 0, depth, h));
        Band(new(w, 0), new(w - depth, 0), new Rect(w - depth, 0, depth, h));
    }

    void DrawFire(CanvasDrawingSession ds, float w, float h)
    {
        float flicker = 1 + 0.09f * MathF.Sin((float)_time * 13) + 0.06f * MathF.Sin((float)_time * 29 + 1);
        EdgeGlow(ds, w, h, Color.FromArgb((byte)(150 * _fire), 255, 70, 20), (50 + 110 * _fire) * flicker);

        ds.Blend = CanvasBlend.Add;
        var white = Color.FromArgb(255, 255, 246, 200);
        var orange = Color.FromArgb(255, 255, 140, 40);
        var red = Color.FromArgb(255, 215, 35, 45);
        foreach (var p in _flames)
        {
            float a = p.Life / p.Max;
            var col = a > 0.65f ? Mix(orange, white, (a - 0.65f) / 0.35f) : Mix(red, orange, a / 0.65f);
            float s = p.Size * (0.35f + 0.65f * a);
            ds.FillCircle(p.P, s * 1.9f, A(col, 0.06 * a));
            ds.FillCircle(p.P, s, A(col, 0.2 * a));
        }
        foreach (var p in _embers)
        {
            float a = p.Life / p.Max;
            ds.FillCircle(p.P, p.Size * 3, A(orange, 0.12 * a));
            ds.FillCircle(p.P, p.Size, A(white, a));
        }
        ds.Blend = CanvasBlend.SourceOver;
    }

    void DrawFrost(CanvasDrawingSession ds, float w, float h)
    {
        EdgeGlow(ds, w, h, Color.FromArgb((byte)(120 * _frost), 190, 232, 255), 40 + 90 * _frost);
        var ice = Color.FromArgb((byte)(215 * Math.Min(1, _frost * 1.4f)), 215, 242, 255);
        foreach (var b in _ice) DrawBranch(ds, b, _frost, ice, 1.6f);
        foreach (var p in _snow)
        {
            float life = p.Life / p.Max;
            float alpha = Math.Min(1, Math.Min(life * 3, (1 - life) * 6)) * Math.Min(1, _frost * 2);
            var col = Color.FromArgb((byte)(alpha * 230), 215, 242, 255);
            float rot = p.Seed + (p.Max - p.Life) * p.Spin;
            for (int k = 0; k < 3; k++)
            {
                float ang = rot + k * MathF.PI / 3;
                var d = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * p.Size;
                ds.DrawLine(p.P - d, p.P + d, col, 1.2f);
                var nrm = new Vector2(-d.Y, d.X) * 0.35f;
                ds.DrawLine(p.P + d * 0.6f, p.P + d * 0.6f + d * 0.25f + nrm, col, 0.9f);
                ds.DrawLine(p.P + d * 0.6f, p.P + d * 0.6f + d * 0.25f - nrm, col, 0.9f);
            }
        }
    }

    static void DrawBranch(CanvasDrawingSession ds, Branch b, float growth, Color col, float width)
    {
        float g = Math.Clamp((growth - b.Delay) / (1 - b.Delay), 0, 1);
        if (g <= 0) return;
        ds.DrawLine(b.A, b.A + b.Dir * b.Len * g, col, width);
        foreach (var k in b.Kids) DrawBranch(ds, k, growth, col, Math.Max(0.7f, width * 0.7f));
    }

    /// <summary>Fractal ice crystals rooted along the window edges, grown in proportion to the frost level.</summary>
    void BuildIce(float w, float h)
    {
        _iceFor = new Size(w, h);
        var rnd = new Random(42);
        float F(double a, double b) => (float)(a + rnd.NextDouble() * (b - a));
        Branch Grow(Vector2 a, Vector2 dir, float len, float delay, int depth)
        {
            var br = new Branch { A = a, Dir = Vector2.Normalize(dir), Len = len, Delay = delay };
            if (depth > 0)
                for (int i = 0, n = rnd.Next(2, 4); i < n; i++)
                {
                    float t = F(0.25, 0.85);
                    float ang = F(0.6, 1.1) * (rnd.Next(2) == 0 ? 1 : -1);
                    var d = Vector2.Transform(br.Dir, Matrix3x2.CreateRotation(ang));
                    br.Kids.Add(Grow(a + br.Dir * len * t, d, len * F(0.3, 0.55), delay + (1 - delay) * t * 0.6f, depth - 1));
                }
            return br;
        }
        var list = new List<Branch>();
        void Edge(Vector2 from, Vector2 to, Vector2 inward)
        {
            float length = Vector2.Distance(from, to);
            for (float s = F(0, 40); s < length; s += F(45, 95))
            {
                var p = from + (to - from) * (s / length);
                var dir = Vector2.Transform(inward, Matrix3x2.CreateRotation(F(-0.45, 0.45)));
                list.Add(Grow(p, dir, F(40, 150), F(0, 0.35), 2));
            }
        }
        Edge(new(0, 0), new(w, 0), new(0, 1));
        Edge(new(0, h), new(w, h), new(0, -1));
        Edge(new(0, 0), new(0, h), new(1, 0));
        Edge(new(w, 0), new(w, h), new(-1, 0));
        _ice = list;
    }
}
