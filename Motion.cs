using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BlenderLibrary;

public static class Motion
{
    internal static bool Enabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    public static readonly DependencyProperty HoverScaleProperty = DependencyProperty.RegisterAttached(
        "HoverScale", typeof(double), typeof(Motion), new PropertyMetadata(1d, OnHoverScaleChanged));

    public static double GetHoverScale(DependencyObject element) => (double)element.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject element, double value) => element.SetValue(HoverScaleProperty, value);

    public static readonly DependencyProperty RevealWhenVisibleProperty = DependencyProperty.RegisterAttached(
        "RevealWhenVisible", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnRevealWhenVisibleChanged));

    public static bool GetRevealWhenVisible(DependencyObject element) => (bool)element.GetValue(RevealWhenVisibleProperty);
    public static void SetRevealWhenVisible(DependencyObject element, bool value) => element.SetValue(RevealWhenVisibleProperty, value);

    private static void OnHoverScaleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element)
            return;

        element.MouseEnter -= HoverChanged;
        element.MouseLeave -= HoverChanged;
        if ((double)e.NewValue != 1)
        {
            element.MouseEnter += HoverChanged;
            element.MouseLeave += HoverChanged;
        }
    }

    private static void HoverChanged(object sender, MouseEventArgs e)
    {
        var element = (UIElement)sender;
        if (!Enabled || !element.IsEnabled)
        {
            element.RenderTransform = Transform.Identity;
            return;
        }

        double target = element.IsMouseOver ? GetHoverScale(element) : 1;
        if (element.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform();
            element.RenderTransformOrigin = new Point(.5, .5);
            element.RenderTransform = scale;
        }

        double start = scale.ScaleX;
        scale.ScaleX = scale.ScaleY = target;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Ease(start, target, 135));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Ease(start, target, 135));
    }

    private static void OnRevealWhenVisibleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element)
            return;

        if ((bool)e.NewValue)
            element.IsVisibleChanged += RevealVisibilityChanged;
        else
            element.IsVisibleChanged -= RevealVisibilityChanged;
    }

    private static void RevealVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
            FadeIn((UIElement)sender);
    }

    internal static void FadeIn(UIElement element, double from = .25)
    {
        if (Enabled)
            element.BeginAnimation(UIElement.OpacityProperty, Ease(from, 1, 180));
    }

    internal static DoubleAnimation Ease(double from, double to, int milliseconds,
        EasingMode direction = EasingMode.EaseOut) => new(from, to, TimeSpan.FromMilliseconds(milliseconds))
    {
        EasingFunction = new CubicEase { EasingMode = direction },
        FillBehavior = FillBehavior.Stop
    };
}
