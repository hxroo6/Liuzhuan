using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Liuzhuan.Utils;

/// <summary>短促、可打断的界面动效。只改变视觉，不接管点击、焦点或拖放。</summary>
public static class UiMotion
{
    internal static bool? EnabledOverride { get; set; }
    public static bool Enabled => EnabledOverride ?? SystemParameters.ClientAreaAnimation;
    public const int FeedbackMs = 110;
    public const int ContentMs = 160;
    public const int PanelMs = 240;
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, long>> Versions = new();

    public static void Animate(DependencyObject target, DependencyProperty property, double to,
        int milliseconds, double? from = null, Action? completed = null)
    {
        target.Dispatcher.VerifyAccess();
        var versions = Versions.GetOrCreateValue(target);
        var version = versions.TryGetValue(property, out var previous) ? previous + 1 : 1;
        versions[property] = version;
        bool IsCurrent() => versions[property] == version;
        var current = from ?? (double)target.GetValue(property);
        void Begin(DoubleAnimation? animation)
        {
            if (target is UIElement element) element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
            else if (target is Animatable animatable) animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
        Begin(null);
        if (!Enabled || milliseconds <= 0 || Math.Abs(current - to) < 0.001)
        {
            target.SetCurrentValue(property, to);
            completed?.Invoke();
            return;
        }

        // 保留当前画面作为基础值，再让新时钟从该位置接续；不能先写入目标值，
        // 否则快速反向操作会在首帧跳到目标位置。
        target.SetCurrentValue(property, current);
        var animation = new DoubleAnimation(current, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            FillBehavior = FillBehavior.HoldEnd,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            if (!IsCurrent()) return;
            Begin(null);
            target.SetCurrentValue(property, to);
            completed?.Invoke();
        };
        // 让当前值先完成一帧布局，再启动新时钟；这样反向操作在同一事件循环中
        // 也不会先看到目标值的跳变。
        target.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => { if (IsCurrent()) Begin(animation); }));
    }

    public static void Reveal(FrameworkElement element)
    {
        if (!element.IsLoaded) return;
        var translate = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = translate;
        Animate(element, UIElement.OpacityProperty, 1, ContentMs, 0.72);
        Animate(translate, TranslateTransform.YProperty, 0, ContentMs, 6);
    }

    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.RegisterAttached(
        "Interactive", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnInteractiveChanged));
    public static bool GetInteractive(DependencyObject element) => (bool)element.GetValue(InteractiveProperty);
    public static void SetInteractive(DependencyObject element, bool value) => element.SetValue(InteractiveProperty, value);

    private static void OnInteractiveChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Control control) return;
        if ((bool)e.NewValue)
        {
            control.MouseEnter += Enter;
            control.MouseLeave += Leave;
            control.PreviewMouseLeftButtonDown += Down;
            control.PreviewMouseLeftButtonUp += Up;
            control.LostMouseCapture += Up;
            control.Unloaded += Unloaded;
        }
        else
        {
            control.MouseEnter -= Enter;
            control.MouseLeave -= Leave;
            control.PreviewMouseLeftButtonDown -= Down;
            control.PreviewMouseLeftButtonUp -= Up;
            control.LostMouseCapture -= Up;
            control.Unloaded -= Unloaded;
        }
    }
    private static void Enter(object sender, MouseEventArgs e) => SetInteraction((Control)sender, false, true);
    private static void Leave(object sender, MouseEventArgs e) => Reset((Control)sender);
    private static void Down(object sender, MouseButtonEventArgs e) => SetInteraction((Control)sender, true, true);
    private static void Up(object sender, MouseEventArgs e) => SetInteraction((Control)sender, false, ((Control)sender).IsMouseOver);
    private static void Unloaded(object sender, RoutedEventArgs e) => Reset((Control)sender, immediately: true);

    public static void Reset(Control control, bool immediately = false) => SetInteraction(control, false, false, immediately);
    private static void SetInteraction(Control control, bool pressed, bool hover, bool immediately = false)
    {
        if (control.Template?.FindName("MotionSurface", control) is not FrameworkElement surface) return;
        if (!control.IsEnabled) { pressed = false; hover = false; }
        if (surface.RenderTransform is not TransformGroup group)
        {
            group = new TransformGroup();
            group.Children.Add(new ScaleTransform());
            group.Children.Add(new TranslateTransform());
            surface.RenderTransformOrigin = new Point(0.5, 0.5);
            surface.RenderTransform = group;
        }
        var scale = (ScaleTransform)group.Children[0];
        var translate = (TranslateTransform)group.Children[1];
        var duration = immediately ? 0 : FeedbackMs;
        var factor = pressed ? (control is ListViewItem ? 0.985 : 0.96) : 1;
        Animate(scale, ScaleTransform.ScaleXProperty, factor, duration);
        Animate(scale, ScaleTransform.ScaleYProperty, factor, duration);
        Animate(translate, TranslateTransform.YProperty, hover && !pressed && control is ListViewItem ? -1 : 0, duration);
        if (control.Template.FindName("HoverWash", control) is UIElement wash)
            Animate(wash, UIElement.OpacityProperty, pressed ? 0.13 : hover ? 0.07 : 0, duration);
    }
}
