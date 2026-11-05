using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace LightningTaskbar;

/// <summary>
/// Dock 主题：只放开始按钮和应用图标，可停靠在底部、左侧或右侧。底座近乎隐形，图标带倒影，
/// 鼠标靠近时按高斯曲线放大。窗口比底座大（朝屏幕内留放大空间，沿边留变长空间），
/// 多出来的区域全透明，鼠标会穿过去。系统状态图标在 <see cref="FinderBarWindow"/> 里。
/// </summary>
public partial class TaskbarWindow
{
    const double DockInset = 10, StageInset = 8;
    /// <summary>Dock 竖放时上下各留的距离，避开顶部的 Finder 栏。</summary>
    const double VerticalReserve = 52;

    /// <summary>Dock 上的一个图标和它当前的动画状态。</summary>
    sealed class DockVisual
    {
        public FrameworkElement Button, Stage;
        public ScaleTransform Scale;
        public TranslateTransform Lift, ShineShift;
        public Ellipse Glow;
        public Rectangle Shine;
        public double Current = 1, GlowLevel, Dim = 1, ShinePhase = -1, BouncePhase = -1;
    }

    List<DockVisual> _dockVisuals;
    FinderBarWindow _finder;
    readonly Dictionary<ImageSource, Brush> _glowBrushes = new();
    double _pitch, _restContentLen, _thickness, _reachDip, _iconSize;
    int _hoverIndex = -1;
    int _bodyMainStart;   // 底座沿边方向的起点（物理像素）
    bool _hoverActive;
    DateTime _finderEdgeSince = DateTime.MaxValue;

    public bool IsDock => Weather.Settings.Theme == BarTheme.Dock;
    public DockEdge ActiveEdge => IsDock ? Weather.Settings.DockEdge : DockEdge.Bottom;
    bool DockIsVertical => ActiveEdge != DockEdge.Bottom;
    DependencyProperty MainProperty => DockIsVertical ? HeightProperty : WidthProperty;

    /// <summary>屏幕底边到任务栏本体顶边的距离（DIP）。</summary>
    public double BodyTopFromBottom => IsDock ? Weather.Settings.DockLift + _thickness : BottomMargin + Weather.Settings.BarHeight;

    /// <summary>通知卡片排在这个角时，顶部要让开的高度（DIP）。</summary>
    public double FinderClearance(TopCorner corner) =>
        IsDock && _finder != null && Weather.Settings.FinderCorner == corner ? _finder.ActualHeight - FinderBarWindow.ShadowPad * 2 + FinderBarWindow.TopGap + 6 : 0;

    /// <summary>开始菜单的位置（物理像素）：摆在 Dock 朝屏幕内的一侧。</summary>
    internal (int x, int y) MenuPosition(int w, int h, double scale, Native.RECT mon)
    {
        int gap = (int)((Weather.Settings.DockLift + _thickness + 12) * scale);
        int midX = mon.Left + (mon.Width - w) / 2, midY = mon.Top + (mon.Height - h) / 2;
        if (!IsDock) return (mon.Left + (int)(14 * scale), mon.Bottom - (int)((BodyTopFromBottom + 12) * scale) - h);
        return ActiveEdge switch
        {
            DockEdge.Left => (mon.Left + gap, midY),
            DockEdge.Right => (mon.Right - gap - w, midY),
            _ => (midX, mon.Bottom - gap - h),
        };
    }

    /// <summary>主题或外观设置变化时调用，改动立即生效。</summary>
    public void ApplyTheme()
    {
        bool dock = IsDock;
        var s = Weather.Settings;
        var edge = ActiveEdge;

        ResetDockVisuals();
        Resources["IconSize"] = dock ? s.DockIconSize : 32.0;
        Resources["DockReflection"] = dock ? s.DockReflection : 0.0;
        Resources["TaskItemMargin"] = dock ? new Thickness(0) : new Thickness(3, 0, 3, 0);
        Resources["TaskButtonStyle"] = Resources[dock ? "TaskDockStyle" : "TaskGlassStyle"];
        ApplyEdgeResources(edge, dock);

        var tile = dock ? Visibility.Visible : Visibility.Collapsed;
        StartTile.Visibility = StartTileReflection.Visibility = tile;
        StartTile.CornerRadius = StartTileReflection.CornerRadius = new CornerRadius(s.DockIconSize * 0.24);
        StartButton.Style = (Style)Resources[dock ? "TaskDockStyle" : "GlassButton"];
        StartButton.Margin = new Thickness(0);

        // 图标排布：竖放时开始按钮在上，应用在下
        bool vertical = DockIsVertical;
        var panel = (ItemsPanelTemplate)Resources[vertical ? "TasksColumnPanel" : "TasksRow"];
        if (Tasks.ItemsPanel != panel) Tasks.ItemsPanel = panel;
        ContentGrid.RowDefinitions.Clear();
        if (vertical)
        {
            ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        Grid.SetRow(Tasks, vertical ? 1 : 0);
        Grid.SetColumn(Tasks, dock && !vertical ? 1 : 0);
        Grid.SetColumnSpan(Tasks, dock ? 1 : 3);
        TasksColumn.Width = dock ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        Tasks.Margin = new Thickness(0);
        Tasks.MaxWidth = double.PositiveInfinity;
        Tasks.VerticalAlignment = vertical ? VerticalAlignment.Top : VerticalAlignment.Center;

        // 底座：Dock 的折射、投影和高光都压低，让它几乎融进桌面
        GlassBg.Bevel = dock ? 12 : 16;
        GlassBg.Refraction = dock ? 9 : 14;
        GlassBg.FrostRadius = dock ? 9 : 7;
        GlassBg.FrostMix = dock ? 0.72 : 0.9;
        GlassBg.RimStrength = dock ? 0.45 : 1;
        GlassBg.ShadowOpacity = dock ? 0.14 : 0.32;
        GlassBg.Veil = dock ? 0.01 : 0.025;

        if (dock) ApplyDockElements(edge);
        else ApplyClassicElements();
        MoveStatusPanel(dock);

        _themeApplied = false;
        ApplyWallpaperTint();
        ApplyGlassMode();
        Layout();
        if (!dock) CenterTasks();
    }

    /// <summary>图标位置、缩放轴心、倒影方向和运行指示条都跟着停靠边走：倒影永远朝向屏幕边。</summary>
    void ApplyEdgeResources(DockEdge edge, bool dock)
    {
        double inset = dock ? StageInset : 11;
        Resources["StageMargin"] = edge switch
        {
            DockEdge.Left => new Thickness(0, 0, inset, 0),
            DockEdge.Right => new Thickness(inset, 0, 0, 0),
            _ => new Thickness(0, inset, 0, 0),
        };
        Resources["StageH"] = edge switch { DockEdge.Left => HorizontalAlignment.Right, DockEdge.Right => HorizontalAlignment.Left, _ => HorizontalAlignment.Center };
        Resources["StageV"] = edge == DockEdge.Bottom ? VerticalAlignment.Top : VerticalAlignment.Center;

        Point origin = edge switch { DockEdge.Left => new Point(0, 0.5), DockEdge.Right => new Point(1, 0.5), _ => new Point(0.5, 1) };
        Resources["StageOrigin"] = origin;
        Resources["ReflectOrigin"] = origin;

        var flip = new TransformGroup();
        flip.Children.Add(edge == DockEdge.Bottom ? new ScaleTransform(1, -1) : new ScaleTransform(-1, 1));
        flip.Children.Add(edge switch { DockEdge.Left => new TranslateTransform(-2, 0), DockEdge.Right => new TranslateTransform(2, 0), _ => new TranslateTransform(0, 2) });
        flip.Freeze();
        Resources["ReflectTransform"] = flip;

        // 元素被翻转，遮罩从紧贴图标的那条边开始往外淡出
        var (from, to) = edge switch
        {
            DockEdge.Left => (new Point(0, 0), new Point(1, 0)),
            DockEdge.Right => (new Point(1, 0), new Point(0, 0)),
            _ => (new Point(0, 1), new Point(0, 0)),
        };
        var mask = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0xFF, 0, 0, 0), 0),
            new(Color.FromArgb(0, 0, 0, 0), 0.48),
        }, from, to);
        mask.Freeze();
        Resources["ReflectMask"] = mask;

        Resources["IndicatorH"] = edge switch { DockEdge.Left => HorizontalAlignment.Left, DockEdge.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        Resources["IndicatorV"] = edge == DockEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        Resources["IndicatorMargin"] = edge switch
        {
            DockEdge.Left => new Thickness(3, 0, 0, 0),
            DockEdge.Right => new Thickness(0, 0, 3, 0),
            _ => new Thickness(0, 0, 0, 3),
        };
        Resources["IndicatorRotate"] = edge == DockEdge.Bottom ? Transform.Identity : new RotateTransform(90);
    }

    static readonly DependencyProperty[] SizeProperties = { WidthProperty, HeightProperty };

    void ApplyClassicElements()
    {
        foreach (FrameworkElement e in new FrameworkElement[] { GlassBg, FallbackBg, BodyHit, ContentGrid })
        {
            foreach (var p in SizeProperties) e.ClearValue(p);
            e.HorizontalAlignment = HorizontalAlignment.Stretch;
            e.VerticalAlignment = VerticalAlignment.Stretch;
        }
        GlassBg.Margin = new Thickness(0);
        GlassBg.CornerRadius = CornerRadiusDip;
        FallbackBg.CornerRadius = new CornerRadius(CornerRadiusDip);
        FallbackBg.Margin = BodyHit.Margin = new Thickness(ShadowPad);
        ContentGrid.Margin = new Thickness(22, 12, 22, 12);
        Resources["TaskItemHeight"] = 54.0;
        StartButton.Width = 52;
        StartButton.Height = 54;
    }

    void ApplyDockElements(DockEdge edge)
    {
        var h = edge switch { DockEdge.Left => HorizontalAlignment.Left, DockEdge.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        var v = edge == DockEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        var margin = edge switch
        {
            DockEdge.Left => new Thickness(ShadowPad, 0, 0, 0),
            DockEdge.Right => new Thickness(0, 0, ShadowPad, 0),
            _ => new Thickness(0, 0, 0, ShadowPad),
        };
        foreach (FrameworkElement e in new FrameworkElement[] { GlassBg, FallbackBg, BodyHit, ContentGrid })
        {
            foreach (var p in SizeProperties) e.ClearValue(p);
            e.HorizontalAlignment = h;
            e.VerticalAlignment = v;
        }
        GlassBg.Margin = new Thickness(0);
        FallbackBg.Margin = BodyHit.Margin = ContentGrid.Margin = margin;
    }

    // ---------------- Finder 栏 ----------------

    /// <summary>状态图标在 Dock 主题下搬到 Finder 栏，经典主题下搬回通栏；弹窗方向和控件尺寸随之切换。</summary>
    void MoveStatusPanel(bool toFinder)
    {
        if (toFinder)
        {
            _finder ??= new FinderBarWindow(this);
            if (RightPanel.Parent == ContentGrid) ContentGrid.Children.Remove(RightPanel);
            if (RightPanel.Parent == null) _finder.Host.Child = RightPanel;
            _finder.Show();
            _finder.Reposition();
        }
        else if (_finder != null)
        {
            _finder.Host.Child = null;
            if (RightPanel.Parent == null) ContentGrid.Children.Add(RightPanel);
            _finder.Hide();
        }

        var place = toFinder ? System.Windows.Controls.Primitives.PlacementMode.Bottom : System.Windows.Controls.Primitives.PlacementMode.Top;
        TrayPopup.Placement = ImePopup.Placement = place;
        TrayPopup.VerticalOffset = ImePopup.VerticalOffset = toFinder ? 12 : -12;

        // Finder 栏是一条细栏：控件全部收紧，时钟排成一行
        double h = toFinder ? 32 : 46;
        TrayOverflowButton.Height = ImeButton.Height = WeatherButton.Height = SoundButton.Height = QuickButton.Height = h;
        TrayOverflowButton.Width = toFinder ? 26 : 30;
        ImeButton.Width = toFinder ? 32 : 40;
        WeatherButton.Width = SoundButton.Width = QuickButton.Width = toFinder ? 34 : 46;
        ClockButton.Height = toFinder ? 32 : 52;
        DesktopButton.Height = toFinder ? 26 : 40;
        ImeText.FontSize = toFinder ? 13 : 15;
        WeatherGlyph.FontSize = toFinder ? 15 : 20;
        SoundGlyph.FontSize = QuickGlyph.FontSize = toFinder ? 14 : 17;

        ClockPanel.Orientation = toFinder ? Orientation.Horizontal : Orientation.Vertical;
        ClockPanel.Margin = new Thickness(toFinder ? 8 : 12, 0, toFinder ? 8 : 12, 0);
        if (toFinder)
        {
            // 日期在前、时间在后
            ClockPanel.Children.Clear();
            ClockPanel.Children.Add(DateText);
            ClockPanel.Children.Add(TimeText);
            TimeText.Margin = new Thickness(8, 0, 0, 0);
        }
        else
        {
            ClockPanel.Children.Clear();
            ClockPanel.Children.Add(TimeText);
            ClockPanel.Children.Add(DateText);
            TimeText.Margin = new Thickness(0);
        }
        TimeText.FontSize = toFinder ? 13 : 18;
        DateText.FontSize = toFinder ? 13 : 11.5;
        TimeText.VerticalAlignment = DateText.VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>Finder 栏的自动隐藏：窗口挡到它时收起；鼠标在它正上方的屏幕顶边停留一下呼出（那里常有最大化窗口的关闭按钮）。</summary>
    void UpdateFinder(Native.POINT pt, IntPtr fg, DateTime now)
    {
        if (_finder == null) return;
        var rect = _finder.ShownRect;
        var state = ClassifyForeground(fg, rect);
        bool inRange = pt.X >= rect.Left && pt.X < rect.Right;
        bool atTop = inRange && pt.Y <= _mon.Top + 1;
        if (atTop) { if (_finderEdgeSince == DateTime.MaxValue) _finderEdgeSince = now; }
        else _finderEdgeSince = DateTime.MaxValue;
        bool edge = atTop && (state == FgState.Clear || (now - _finderEdgeSince).TotalMilliseconds > 400);
        bool over = inRange && pt.Y >= _mon.Top && pt.Y < rect.Bottom && _finder.IsMostlyShown;
        bool want = state == FgState.Clear || edge || over || now < _revealUntil || TrayPopup.IsOpen || ImePopup.IsOpen || _openMenus > 0 || AppInstance.AppearanceOpen;
        _finder.Step(want);
    }

    /// <summary>Finder 栏空白处右键：和任务栏共用一个菜单。</summary>
    internal void OpenBarMenu(UIElement target)
    {
        BarMenu.PlacementTarget = target;
        BarMenu.IsOpen = true;
    }

    // ---------------- 几何 ----------------

    /// <summary>底座矩形（物理像素），slide 是滑动轴上的位置。</summary>
    Native.RECT BodyRect(int slide) => DockIsVertical
        ? new Native.RECT { Left = slide, Top = _bodyMainStart, Right = slide + _barW, Bottom = _bodyMainStart + _barH }
        : new Native.RECT { Left = _x, Top = slide, Right = _x + _barW, Bottom = slide + _barH };

    /// <summary>鼠标是否在底座上（Dock 放大时图标伸出底座的那一段也算）。</summary>
    bool OverBody(Native.POINT pt)
    {
        var r = BodyRect(CurrentY());
        if (IsDock)
        {
            int reach = (int)Math.Round(_reachDip * _scale);
            switch (ActiveEdge)
            {
                case DockEdge.Left: r.Right += reach; break;
                case DockEdge.Right: r.Left -= reach; break;
                default: r.Top -= reach; break;
            }
        }
        return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
    }

    bool AtScreenEdge(Native.POINT pt) => ActiveEdge switch
    {
        DockEdge.Left => pt.X <= _mon.Left + 1 && pt.Y >= _mon.Top && pt.Y < _mon.Bottom,
        DockEdge.Right => pt.X >= _mon.Right - 2 && pt.Y >= _mon.Top && pt.Y < _mon.Bottom,
        _ => pt.Y >= _mon.Bottom - 2 && pt.X >= _mon.Left && pt.X < _mon.Right,
    };

    /// <summary>
    /// 计算 Dock 的几何并摆放窗口。图标太多放不下时压缩间距和图标（放大余量也算在内），不会超出屏幕。
    /// </summary>
    void LayoutDock(bool force = false)
    {
        if (_hwnd == IntPtr.Zero || !IsDock) return;
        var s = Weather.Settings;
        var edge = ActiveEdge;
        bool vertical = DockIsVertical;
        double icon = s.DockIconSize, mag = s.DockMagnify;

        int slots = _tracker.Items.Count + 1;   // 开始按钮也占一格
        double monMain = (vertical ? _mon.Height : _mon.Width) / _scale;
        // 放大时多出来的长度 = 高斯曲线下的面积 σ√π，σ 正比于间距，所以可以和图标数量一起解出间距
        double growthSlots = 1.6 * Math.Sqrt(Math.PI) * (mag - 1);
        double chrome = 2 * (ShadowPad + DockInset);
        double reserve = vertical ? VerticalReserve : SideMargin;
        double pitch = Math.Clamp(Math.Floor((monMain - 2 * reserve - chrome) / (slots + growthSlots)), 18, icon + 8);
        double contentLen = pitch * slots;
        double winMain = Math.Min(monMain, contentLen + chrome + pitch * growthSlots);

        // 放不下时图标真的变小：图标比格子大的话，WPF 会按格子把它裁掉
        double iconSize = Math.Min(icon, pitch - 2);
        double thickness = Math.Round(StageInset + iconSize + Math.Max(14, iconSize * 0.4));
        // 朝屏幕内要留够放大的空间，再加上特效伸出去的部分
        double effectReach = s.DockHoverEffect switch { HoverEffect.Bounce => iconSize * 0.3, HoverEffect.None or HoverEffect.Spotlight => 0, _ => 16 };
        _reachDip = Math.Ceiling(iconSize * (mag - 1) + effectReach) + 6;
        double inner = Math.Max(_reachDip, ShadowPad);
        double winCross = inner + thickness + ShadowPad;

        if (!force && _pitch == pitch && _restContentLen == contentLen && _thickness == thickness && _iconSize == iconSize) return;

        _pitch = pitch;
        _restContentLen = contentLen;
        _thickness = thickness;
        _iconSize = iconSize;
        ResetDockVisuals();

        Resources["IconSize"] = iconSize;
        StartTile.CornerRadius = StartTileReflection.CornerRadius = new CornerRadius(iconSize * 0.24);
        StartShine.RadiusX = StartShine.RadiusY = iconSize * 0.24;
        Resources["TaskItemWidth"] = vertical ? thickness : pitch;
        Resources["TaskItemHeight"] = vertical ? pitch : thickness;
        StartButton.Width = vertical ? thickness : pitch;
        StartButton.Height = vertical ? pitch : thickness;

        double radius = Math.Min(30, thickness * 0.38);
        GlassBg.CornerRadius = radius;
        FallbackBg.CornerRadius = new CornerRadius(radius);
        if (vertical)
        {
            GlassBg.Width = thickness + ShadowPad * 2;
            FallbackBg.Width = BodyHit.Width = ContentGrid.Width = thickness;
        }
        else
        {
            GlassBg.Height = thickness + ShadowPad * 2;
            FallbackBg.Height = BodyHit.Height = ContentGrid.Height = thickness;
        }

        int mainPx = (int)Math.Round(winMain * _scale), crossPx = (int)Math.Round(winCross * _scale);
        int thickPx = (int)Math.Round(thickness * _scale), liftPx = (int)Math.Round(s.DockLift * _scale);
        int padPx = (int)Math.Round(ShadowPad * _scale), innerPx = (int)Math.Round(inner * _scale);

        Width = vertical ? winCross : winMain;
        Height = vertical ? winMain : winCross;

        if (vertical) _barW = thickPx; else _barH = thickPx;
        switch (edge)
        {
            case DockEdge.Left:
                _shownY = _mon.Left + liftPx;
                _hiddenY = _mon.Left - thickPx - 2;
                PlaceWindow(0, _mon.Top + (_mon.Height - mainPx) / 2, crossPx, mainPx, padPx);
                break;
            case DockEdge.Right:
                _shownY = _mon.Right - liftPx - thickPx;
                _hiddenY = _mon.Right + 2;
                PlaceWindow(0, _mon.Top + (_mon.Height - mainPx) / 2, crossPx, mainPx, innerPx);
                break;
            default:
                _shownY = _mon.Bottom - thickPx - liftPx;
                _hiddenY = _mon.Bottom + 2;
                PlaceWindow(_mon.Left + (_mon.Width - mainPx) / 2, 0, mainPx, crossPx, innerPx);
                break;
        }
        SetBodyLength(contentLen);
        ApplyRegion();
    }

    /// <summary>内容长度变化时同步底座；悬停放大期间每帧调用。</summary>
    void SetBodyLength(double contentLen)
    {
        double body = contentLen + 2 * DockInset;
        int bodyPx = (int)Math.Round(body * _scale);
        if (DockIsVertical)
        {
            GlassBg.Height = body + ShadowPad * 2;
            FallbackBg.Height = BodyHit.Height = body;
            _barH = bodyPx;
            _bodyMainStart = _winY + (_winH - _barH) / 2;
        }
        else
        {
            GlassBg.Width = body + ShadowPad * 2;
            FallbackBg.Width = BodyHit.Width = body;
            _barW = bodyPx;
            _x = _winX + (_winW - _barW) / 2;
        }
    }

    // ---------------- 悬停：放大 + 特效 ----------------

    /// <summary>把所有图标恢复原状并丢弃缓存（主题、方向或图标数量变了）。</summary>
    void ResetDockVisuals()
    {
        if (_dockVisuals != null)
            foreach (var v in _dockVisuals)
            {
                v.Stage.RenderTransform = null;
                v.Stage.Opacity = 1;
                v.Glow.Opacity = v.Shine.Opacity = 0;
                if (v.Button != StartButton) { v.Button.ClearValue(WidthProperty); v.Button.ClearValue(HeightProperty); }
            }
        _dockVisuals = null;
        _hoverActive = false;
        _hoverIndex = -1;
    }

    /// <summary>
    /// 找到每个图标的元素。容器还没生成时返回 false，下一帧再试。
    /// 换方向时容器会全部重建，所以每次都要确认缓存的还在界面上，否则放大就作用到了已经移除的旧元素上。
    /// </summary>
    bool EnsureDockVisuals()
    {
        if (_dockVisuals != null && _dockVisuals.Count == _tracker.Items.Count + 1 && _dockVisuals.All(v => v.Button.IsVisible)) return true;
        var list = new List<DockVisual> { BuildVisual(StartButton, StartStage, StartGlow, StartShine, null, "start") };
        for (int i = 0; i < _tracker.Items.Count; i++)
        {
            if (Tasks.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp || !cp.IsVisible || cp.ContentTemplate == null) return false;
            cp.ApplyTemplate();
            var t = cp.ContentTemplate;
            if (t.FindName("TaskButton", cp) is not Button b || t.FindName("IconStage", cp) is not Grid stage
                || t.FindName("Glow", cp) is not Ellipse glow || t.FindName("Shine", cp) is not Rectangle shine) return false;
            list.Add(BuildVisual(b, stage, glow, shine, _tracker.Items[i].Icon, null));
            b.ClearValue(WidthProperty);
            b.ClearValue(HeightProperty);
        }
        _dockVisuals = list;
        SetBodyLength(_restContentLen);
        _hoverActive = false;
        _hoverIndex = -1;
        return true;
    }

    DockVisual BuildVisual(FrameworkElement button, Grid stage, Ellipse glow, Rectangle shine, ImageSource icon, string kind)
    {
        // 模板里的 Freezable 是冻结的共享实例，每个图标要换可写的
        var scale = new ScaleTransform();
        var lift = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(lift);
        stage.RenderTransform = group;

        var shift = new TranslateTransform(-1, 0);
        var band = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0, 255, 255, 255), 0.32),
            new(Color.FromArgb(0xD8, 255, 255, 255), 0.5),
            new(Color.FromArgb(0, 255, 255, 255), 0.68),
        }, new Point(0, 0), new Point(1, 1)) { RelativeTransform = shift };
        shine.Fill = band;
        // 光遮罩：扫光只落在图标的不透明像素上，所以看起来是图标本身在反光
        if (icon != null) shine.OpacityMask = new ImageBrush(icon);
        else if (kind == null) shine.Visibility = Visibility.Collapsed;   // 没有图标的占位项

        glow.Fill = GlowBrush(icon);
        return new DockVisual { Button = button, Stage = stage, Scale = scale, Lift = lift, ShineShift = shift, Glow = glow, Shine = shine };
    }

    /// <summary>光晕取图标自己的主色：每个应用发出的光都不一样。</summary>
    Brush GlowBrush(ImageSource icon)
    {
        if (icon != null && _glowBrushes.TryGetValue(icon, out var cached)) return cached;
        var c = IconAccent(icon);
        var brush = new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0xE0, c.R, c.G, c.B), 0.35),
            new(Color.FromArgb(0x60, c.R, c.G, c.B), 0.68),
            new(Color.FromArgb(0, c.R, c.G, c.B), 1),
        });
        brush.Freeze();
        if (icon != null) _glowBrushes[icon] = brush;
        return brush;
    }

    /// <summary>按饱和度加权的平均色，再提到最亮，避免灰扑扑的光。</summary>
    static Color IconAccent(ImageSource icon)
    {
        var fallback = Color.FromRgb(0x9C, 0xC0, 0xFF);
        if (icon is not BitmapSource src) return fallback;
        try
        {
            var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bmp.CopyPixels(px, stride, 0);
            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 12)
            {
                int bb = px[i], gg = px[i + 1], rr = px[i + 2], a = px[i + 3];
                if (a < 80) continue;
                int max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
                double weight = (max - min) + 8;
                r += rr * weight; g += gg * weight; b += bb * weight; total += weight;
            }
            if (total <= 0) return fallback;
            r /= total; g /= total; b /= total;
            double peak = Math.Max(1, Math.Max(r, Math.Max(g, b)));
            return Color.FromRgb((byte)(r * 255 / peak), (byte)(g * 255 / peak), (byte)(b * 255 / peak));
        }
        catch { return fallback; }
    }

    void UpdateHover(Native.POINT pt)
    {
        if (!EnsureDockVisuals()) return;
        bool vertical = DockIsVertical;
        var s = Weather.Settings;
        double mag = s.DockMagnify;
        var effect = s.DockHoverEffect;
        var (wx, wy) = WindowAt(_lastY);
        double mx = (pt.X - wx) / _scale, my = (pt.Y - wy) / _scale;
        double winW = _winW / _scale, winH = _winH / _scale;
        double along = vertical ? my : mx;
        double start = ((vertical ? winH : winW) - _restContentLen) / 2;

        bool hover = _shown > 0.95 && _openMenus == 0 && !AppInstance.AppearanceOpen
                     && mx >= 0 && mx < winW && my >= 0 && my < winH
                     && along >= start - _pitch / 2 && along <= start + _restContentLen + _pitch / 2;
        if (!hover && !_hoverActive) return;

        // 指针换到新图标时，扫光和弹跳各放一次
        int nearest = hover ? Math.Clamp((int)Math.Floor((along - start) / _pitch), 0, _dockVisuals.Count - 1) : -1;
        if (nearest != _hoverIndex)
        {
            _hoverIndex = nearest;
            if (nearest >= 0) _dockVisuals[nearest].ShinePhase = _dockVisuals[nearest].BouncePhase = 0;
        }

        double sigma = 1.6 * _pitch;
        double content = 0;
        bool active = false;
        var main = MainProperty;
        for (int i = 0; i < _dockVisuals.Count; i++)
        {
            var v = _dockVisuals[i];
            // 都用未放大时的位置算距离，放大本身不会反过来影响结果，不会抖
            double offset = along - (start + (i + 0.5) * _pitch);

            double target = hover && mag > 1.001 ? 1 + (mag - 1) * Math.Exp(-(offset / sigma) * (offset / sigma)) : 1;
            v.Current += (target - v.Current) * 0.3;
            if (Math.Abs(v.Current - target) < 0.002) v.Current = target;
            if (v.Current != 1) active = true;
            v.Scale.ScaleX = v.Scale.ScaleY = v.Current;
            if (v.Current != 1 || v.Button == StartButton) v.Button.SetValue(main, _pitch * v.Current);
            else v.Button.ClearValue(main);   // 回到样式里的动态资源
            content += _pitch * v.Current;

            double near = hover ? Math.Exp(-(offset / _pitch) * (offset / _pitch)) : 0;
            active |= StepGlow(v, effect is HoverEffect.Glow or HoverEffect.LightMask ? near : 0);
            active |= StepSpotlight(v, hover && effect == HoverEffect.Spotlight ? 0.38 + 0.62 * near : 1);
            active |= StepShine(v, effect == HoverEffect.LightMask);
            active |= StepBounce(v, effect == HoverEffect.Bounce);
        }
        SetBodyLength(content);
        _hoverActive = active;
    }

    static bool StepGlow(DockVisual v, double target)
    {
        v.GlowLevel += (target - v.GlowLevel) * 0.2;
        if (Math.Abs(v.GlowLevel - target) < 0.004) v.GlowLevel = target;
        v.Glow.Opacity = v.GlowLevel * 0.9;
        return v.GlowLevel != 0;
    }

    static bool StepSpotlight(DockVisual v, double target)
    {
        v.Dim += (target - v.Dim) * 0.2;
        if (Math.Abs(v.Dim - target) < 0.004) v.Dim = target;
        v.Stage.Opacity = v.Dim;
        return v.Dim != 1;
    }

    static bool StepShine(DockVisual v, bool enabled)
    {
        if (v.ShinePhase < 0 || !enabled) { v.ShinePhase = -1; v.Shine.Opacity = 0; return false; }
        v.ShinePhase += 0.016 / 0.65;
        if (v.ShinePhase >= 1) { v.ShinePhase = -1; v.Shine.Opacity = 0; return false; }
        v.Shine.Opacity = 1;
        double eased = 1 - Math.Pow(1 - v.ShinePhase, 2);
        v.ShineShift.X = -1 + 2 * eased;
        return true;
    }

    /// <summary>朝屏幕内跳两下，第二下更低。</summary>
    bool StepBounce(DockVisual v, bool enabled)
    {
        double height = 0;
        if (v.BouncePhase >= 0 && enabled)
        {
            v.BouncePhase += 0.016 / 0.6;
            if (v.BouncePhase >= 1) v.BouncePhase = -1;
            else height = Math.Abs(Math.Sin(v.BouncePhase * Math.PI * 2)) * (1 - v.BouncePhase) * _iconSize * 0.3;
        }
        else v.BouncePhase = -1;
        switch (ActiveEdge)
        {
            case DockEdge.Left: v.Lift.X = height; break;
            case DockEdge.Right: v.Lift.X = -height; break;
            default: v.Lift.Y = -height; break;
        }
        return v.BouncePhase >= 0;
    }
}
