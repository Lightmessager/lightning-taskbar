using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LightningTaskbar;

/// <summary>悬停预览里的一张窗口卡片。</summary>
public sealed class PeekItem
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; }
    public ImageSource Icon { get; init; }
}

/// <summary>
/// 悬停预览：鼠标停在运行中的应用图标上，弹出该应用全部窗口的实时缩略图（DWM 缩略图，多窗口时并排），
/// 弹出时带回弹动画，点卡片切换到对应窗口。
/// </summary>
public partial class TaskbarWindow
{
    readonly DispatcherTimer _peekOpen = new() { Interval = TimeSpan.FromMilliseconds(280) };
    readonly DispatcherTimer _peekClose = new() { Interval = TimeSpan.FromMilliseconds(320) };
    readonly List<(IntPtr thumb, FrameworkElement area)> _thumbs = new();
    TaskItem _peekTarget, _pendingTarget;
    FrameworkElement _peekAnchor, _pendingAnchor;
    bool _rendering;

    void InitPeek()
    {
        _peekOpen.Tick += (_, _) => { _peekOpen.Stop(); ShowPeek(_pendingTarget, _pendingAnchor, bounce: 1); };
        _peekClose.Tick += (_, _) => { _peekClose.Stop(); HidePeek(); };
        PeekPopup.CustomPopupPlacementCallback = (popup, target, _) => new[]
        {
            // 水平居中在图标正上方；PeekRoot 底部留了回弹余量，所以只需略微往下压
            new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, -popup.Height + 4), PopupPrimaryAxis.Horizontal),
        };
        PeekPopup.Closed += (_, _) => ClearThumbs();
    }

    bool PeekOpen => PeekPopup.IsOpen;

    // ---------------- 悬停 ----------------

    void Task_MouseEnter(object sender, MouseEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TaskItem t) return;
        _peekClose.Stop();
        if (!t.IsRunning || _openMenus > 0)
        {
            _peekOpen.Stop();
            if (PeekOpen) _peekClose.Start();
            return;
        }
        if (PeekOpen)
        {
            // 已经展开时在图标间移动：直接换内容，小幅回弹
            if (t != _peekTarget) ShowPeek(t, (FrameworkElement)sender, bounce: 0.25);
            return;
        }
        _pendingTarget = t;
        _pendingAnchor = (FrameworkElement)sender;
        _peekOpen.Stop();
        _peekOpen.Start();
    }

    void Task_MouseLeave(object sender, MouseEventArgs e)
    {
        _peekOpen.Stop();
        if (PeekOpen) _peekClose.Start();
    }

    void Peek_MouseEnter(object sender, MouseEventArgs e) => _peekClose.Stop();
    void Peek_MouseLeave(object sender, MouseEventArgs e) => _peekClose.Start();

    // ---------------- 显示 / 隐藏 ----------------

    /// <param name="bounce">回弹幅度：1 = 从小弹出，0 = 无动画。</param>
    void ShowPeek(TaskItem item, FrameworkElement anchor, double bounce)
    {
        if (item == null || anchor == null || item.WindowCount == 0) { HidePeek(); return; }
        ClearThumbs();
        _peekTarget = item;
        _peekAnchor = anchor;
        PeekList.ItemsSource = item.Windows
            .Select(h => new PeekItem { Handle = h, Title = Native.GetText(h), Icon = item.Icon })
            .ToList();

        PeekPopup.PlacementTarget = anchor;
        if (PeekPopup.IsOpen) PeekPopup.IsOpen = false;   // 重新定位到新图标
        PeekPopup.IsOpen = true;

        if (bounce > 0) Bounce(bounce);
        Dispatcher.BeginInvoke(RegisterThumbs, DispatcherPriority.Loaded);
    }

    void HidePeek()
    {
        _peekOpen.Stop();
        _peekClose.Stop();
        PeekPopup.IsOpen = false;
        _peekTarget = null;
    }

    /// <summary>Q 弹：从底部中心放大并越过头一点再回来，同时略微上浮。</summary>
    void Bounce(double amount)
    {
        double from = 1 - 0.45 * amount;
        var dur = TimeSpan.FromMilliseconds(380 + 160 * amount);
        var spring = new ElasticEase { Oscillations = 1, Springiness = 3.5, EasingMode = EasingMode.EaseOut };
        PeekScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, 1, dur) { EasingFunction = spring });
        PeekScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, 1, dur) { EasingFunction = spring });
        PeekShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(18 * amount, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } });
        PeekPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(amount >= 1 ? 0 : 0.6, 1, TimeSpan.FromMilliseconds(150)));
    }

    // ---------------- 卡片操作 ----------------

    void PeekCard_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PeekItem p) return;
        HidePeek();
        WindowTracker.Focus(p.Handle);
    }

    void PeekClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).DataContext is not PeekItem p) return;
        WindowTracker.Close(p.Handle);

        // 等窗口真正关掉再刷新：窗口全关了就收起预览
        var target = _peekTarget;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _tracker.Refresh();
            if (_peekTarget != target) return;
            if (target.WindowCount > 0) ShowPeek(target, _peekAnchor, bounce: 0);
            else HidePeek();
        };
        t.Start();
    }

    // ---------------- DWM 实时缩略图 ----------------

    void RegisterThumbs()
    {
        if (!PeekPopup.IsOpen || PresentationSource.FromVisual(PeekRoot) is not HwndSource src) return;
        ClearThumbs();
        for (int i = 0; i < PeekList.Items.Count; i++)
        {
            if (PeekList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            cp.ApplyTemplate();
            if (cp.ContentTemplate?.FindName("Thumb", cp) is not FrameworkElement area) continue;
            var item = (PeekItem)PeekList.Items[i];
            if (DwmRegisterThumbnail(src.Handle, item.Handle, out var thumb) == 0) _thumbs.Add((thumb, area));
        }
        if (!_rendering) { CompositionTarget.Rendering += UpdateThumbs; _rendering = true; }
        UpdateThumbs(null, EventArgs.Empty);
    }

    void ClearThumbs()
    {
        foreach (var (thumb, _) in _thumbs) DwmUnregisterThumbnail(thumb);
        _thumbs.Clear();
        if (_rendering) { CompositionTarget.Rendering -= UpdateThumbs; _rendering = false; }
    }

    /// <summary>
    /// 缩略图是 DWM 直接画在弹窗上的，不跟随 WPF 变换，所以每帧按卡片当前（含回弹缩放）的位置重新摆放。
    /// </summary>
    void UpdateThumbs(object sender, EventArgs e)
    {
        if (!PeekPopup.IsOpen) return;
        var dpi = VisualTreeHelper.GetDpi(PeekRoot);
        byte opacity = (byte)Math.Round(255 * Math.Clamp(PeekPanel.Opacity, 0, 1));
        foreach (var (thumb, area) in _thumbs)
        {
            if (!area.IsVisible) continue;
            Rect r;
            try { r = area.TransformToAncestor(PeekRoot).TransformBounds(new Rect(area.RenderSize)); }
            catch (InvalidOperationException) { continue; }

            // 保持窗口比例，居中放进卡片的缩略图区域
            DwmQueryThumbnailSourceSize(thumb, out var size);
            double inset = 4;
            r.Inflate(-inset, -inset);
            if (size.cx > 0 && size.cy > 0 && r.Width > 0 && r.Height > 0)
            {
                double k = Math.Min(r.Width / size.cx, r.Height / size.cy);
                double w = size.cx * k, h = size.cy * k;
                r = new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
            }

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = 0x1 | 0x4 | 0x8 | 0x10,   // 目标矩形 | 不透明度 | 可见 | 只取客户区
                rcDestination = new Native.RECT
                {
                    Left = (int)Math.Round(r.Left * dpi.DpiScaleX), Top = (int)Math.Round(r.Top * dpi.DpiScaleY),
                    Right = (int)Math.Round(r.Right * dpi.DpiScaleX), Bottom = (int)Math.Round(r.Bottom * dpi.DpiScaleY),
                },
                opacity = opacity,
                fVisible = 1,
                fSourceClientAreaOnly = 0,
            };
            DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public Native.RECT rcDestination, rcSource;
        public byte opacity;
        public int fVisible, fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)] struct PSIZE { public int cx, cy; }

    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);
}
