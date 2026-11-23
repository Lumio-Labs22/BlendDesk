using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BlenderLibrary;

public class AnimatedWindow : Window
{
    private bool closingAnimation;
    private bool finishClose;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (Motion.Enabled && Content is UIElement surface)
            AnimateSurface(surface, opening: true);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (finishClose)
            return;
        if (closingAnimation)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
        if (e.Cancel || !Motion.Enabled || !IsVisible || WindowState == WindowState.Minimized || Content is not UIElement surface)
            return;

        bool? result = DialogResult;
        e.Cancel = true;
        closingAnimation = true;
        surface.IsHitTestVisible = false;
        AnimateSurface(surface, opening: false, () =>
        {
            if (!IsVisible)
                return;

            finishClose = true;
            if (result is bool dialogResult)
                DialogResult = dialogResult;
            else
                Close();
        });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (closingAnimation)
            e.Handled = true;
        else
            base.OnPreviewKeyDown(e);
    }

    private static void AnimateSurface(UIElement surface, bool opening, Action? finished = null)
    {
        var oldTransforms = surface.RenderTransform as TransformGroup;
        double oldScale = oldTransforms?.Children.OfType<ScaleTransform>().FirstOrDefault()?.ScaleX ?? 1;
        double oldOffset = oldTransforms?.Children.OfType<TranslateTransform>().FirstOrDefault()?.Y ?? 0;
        var scale = new ScaleTransform();
        var slide = new TranslateTransform();
        surface.RenderTransformOrigin = new Point(.5, .5);
        surface.RenderTransform = new TransformGroup { Children = { scale, slide } };

        int duration = opening ? 240 : 155;
        var direction = opening ? EasingMode.EaseOut : EasingMode.EaseIn;
        double targetScale = opening ? 1 : .985;
        double targetOffset = opening ? 0 : 9;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Ease(opening ? .985 : oldScale, targetScale, duration, direction));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.Ease(opening ? .985 : oldScale, targetScale, duration, direction));
        slide.BeginAnimation(TranslateTransform.YProperty, Motion.Ease(opening ? 12 : oldOffset, targetOffset, duration, direction));
        var fade = Motion.Ease(opening ? 0 : surface.Opacity, opening ? 1 : 0, duration, direction);
        if (finished is not null)
        {
            fade.FillBehavior = FillBehavior.HoldEnd;
            fade.Completed += (_, _) => finished();
        }
        surface.BeginAnimation(OpacityProperty, fade);
    }
}
