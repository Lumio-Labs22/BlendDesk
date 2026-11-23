using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BlenderLibrary;

public sealed class GlassReflection : FrameworkElement
{
    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(
        nameof(Strength), typeof(double), typeof(GlassReflection),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private UIElement? host;
    private Point pointer;

    public double Strength
    {
        get => (double)GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }

    public GlassReflection()
    {
        IsHitTestVisible = false;
        Opacity = 0;
        Loaded += Connect;
        Unloaded += Disconnect;
    }

    private void Connect(object sender, RoutedEventArgs e)
    {
        if (host is not null || TemplatedParent is not UIElement parent)
            return;

        host = parent;
        host.MouseEnter += Enter;
        host.MouseMove += Move;
        host.MouseLeave += Leave;
    }

    private void Disconnect(object sender, RoutedEventArgs e)
    {
        if (host is null)
            return;

        host.MouseEnter -= Enter;
        host.MouseMove -= Move;
        host.MouseLeave -= Leave;
        host = null;
        BeginAnimation(OpacityProperty, null);
    }

    private void Enter(object sender, MouseEventArgs e)
    {
        if (Strength == 0)
            return;

        Move(sender, e);
        Fade(1, 135);
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (Strength == 0)
            return;

        pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    private void Leave(object sender, MouseEventArgs e) => Fade(0, 200);

    private void Fade(double target, int milliseconds)
    {
        if (Strength > 0)
            BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds)));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Strength == 0 || ActualWidth < 3 || ActualHeight < 3)
            return;

        var glass = new Rect(1, 1, ActualWidth - 2, ActualHeight - 2);
        var light = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(Math.Clamp(pointer.X, 0, ActualWidth), Math.Clamp(pointer.Y * 0.28, 0, ActualHeight * 0.26)),
            RadiusX = Math.Clamp(ActualWidth * 0.55, 18, 100),
            RadiusY = Math.Max(10, ActualHeight * 0.52),
            Opacity = Strength
        };
        light.GradientOrigin = light.Center;
        light.GradientStops.Add(new GradientStop(Color.FromArgb(215, 255, 249, 236), 0));
        light.GradientStops.Add(new GradientStop(Color.FromArgb(92, 255, 207, 170), 0.48));
        light.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 207, 170), 1));
        light.Freeze();
        drawingContext.DrawRoundedRectangle(light, null, glass, glass.Height / 2, glass.Height / 2);
    }
}
