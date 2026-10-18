using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GibiOMatic.Conversion.Reading;
using GibiOMatic.Core.Settings;

namespace GibiOMatic.App.Controls;

/// <summary>
/// Draws the page (or the two pages) on screen in the reader: fit to height, width or 100%,
/// zoom with Ctrl+wheel around the pointer, drag to move, wheel to scroll and turn pages,
/// click on the sides to turn, double click back to the fitted size.
/// </summary>
public sealed class PageSpreadView : Control
{
    public static readonly StyledProperty<IImage?> LeftImageProperty = AvaloniaProperty.Register<PageSpreadView, IImage?>(nameof(LeftImage));
    public static readonly StyledProperty<IImage?> RightImageProperty = AvaloniaProperty.Register<PageSpreadView, IImage?>(nameof(RightImage));
    public static readonly StyledProperty<PageSize> LeftSizeProperty = AvaloniaProperty.Register<PageSpreadView, PageSize>(nameof(LeftSize));
    public static readonly StyledProperty<PageSize> RightSizeProperty = AvaloniaProperty.Register<PageSpreadView, PageSize>(nameof(RightSize));
    public static readonly StyledProperty<ReaderFit> FitProperty = AvaloniaProperty.Register<PageSpreadView, ReaderFit>(nameof(Fit));
    public static readonly StyledProperty<int> PageTurnProperty = AvaloniaProperty.Register<PageSpreadView, int>(nameof(PageTurn));
    public static readonly StyledProperty<int> NeededHeightProperty = AvaloniaProperty.Register<PageSpreadView, int>(nameof(NeededHeight));
    public static readonly StyledProperty<ICommand?> NextCommandProperty = AvaloniaProperty.Register<PageSpreadView, ICommand?>(nameof(NextCommand));
    public static readonly StyledProperty<ICommand?> PreviousCommandProperty = AvaloniaProperty.Register<PageSpreadView, ICommand?>(nameof(PreviousCommand));
    public static readonly StyledProperty<ICommand?> LeftCommandProperty = AvaloniaProperty.Register<PageSpreadView, ICommand?>(nameof(LeftCommand));
    public static readonly StyledProperty<ICommand?> RightCommandProperty = AvaloniaProperty.Register<PageSpreadView, ICommand?>(nameof(RightCommand));

    private const double MaxZoom = 8;
    private const double ScrollStep = 90;

    private double _zoom = 1;
    private Vector _offset;
    private Point? _pressedAt;
    private Point _lastDrag;
    private bool _dragging;
    private double _wheel;
    private double _edgeWheel;

    static PageSpreadView()
    {
        AffectsRender<PageSpreadView>(LeftImageProperty, RightImageProperty, LeftSizeProperty, RightSizeProperty, FitProperty);
        FocusableProperty.OverrideDefaultValue<PageSpreadView>(true);
    }

    public PageSpreadView()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        ClipToBounds = true;
    }

    public IImage? LeftImage { get => GetValue(LeftImageProperty); set => SetValue(LeftImageProperty, value); }
    public IImage? RightImage { get => GetValue(RightImageProperty); set => SetValue(RightImageProperty, value); }
    public PageSize LeftSize { get => GetValue(LeftSizeProperty); set => SetValue(LeftSizeProperty, value); }
    public PageSize RightSize { get => GetValue(RightSizeProperty); set => SetValue(RightSizeProperty, value); }
    public ReaderFit Fit { get => GetValue(FitProperty); set => SetValue(FitProperty, value); }

    /// <summary>Changes on every page turn: zoom and scrolling start over.</summary>
    public int PageTurn { get => GetValue(PageTurnProperty); set => SetValue(PageTurnProperty, value); }

    /// <summary>Height of the pages on screen in device pixels (read by the view model, which renders at that size).</summary>
    public int NeededHeight { get => GetValue(NeededHeightProperty); set => SetValue(NeededHeightProperty, value); }

    /// <summary>Wheel down at the end of the page (reading order).</summary>
    public ICommand? NextCommand { get => GetValue(NextCommandProperty); set => SetValue(NextCommandProperty, value); }
    public ICommand? PreviousCommand { get => GetValue(PreviousCommandProperty); set => SetValue(PreviousCommandProperty, value); }

    /// <summary>Click on the left or right side (the view model knows the reading direction).</summary>
    public ICommand? LeftCommand { get => GetValue(LeftCommandProperty); set => SetValue(LeftCommandProperty, value); }
    public ICommand? RightCommand { get => GetValue(RightCommandProperty); set => SetValue(RightCommandProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageTurnProperty || change.Property == FitProperty)
        {
            _zoom = 1;
            _offset = default;
            _edgeWheel = 0;
            InvalidateVisual();
        }
    }

    /// <summary>The spread in "100%" units at a common height: (total width, height, width of the left page).</summary>
    private (double Width, double Height, double LeftWidth) Spread()
    {
        static PageSize SizeOf(IImage image, PageSize declared) =>
            declared.Width > 0 && declared.Height > 0 ? declared : new PageSize((int)image.Size.Width, (int)image.Size.Height);

        if (LeftImage is not { } left)
        {
            return (0, 0, 0);
        }

        var l = SizeOf(left, LeftSize);
        var width = (double)l.Width;
        if (RightImage is { } right)
        {
            var r = SizeOf(right, RightSize);
            width += r.Width * ((double)l.Height / r.Height);
        }

        return (width, l.Height, l.Width);
    }

    /// <summary>Screen size (DIPs) of one "100%" unit at the current fit and zoom; 0 when there is nothing to show.</summary>
    private double Scale()
    {
        var (width, height, _) = Spread();
        var viewport = Bounds.Size;
        if (width <= 0 || height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return 0;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return Fit switch
        {
            ReaderFit.Width => viewport.Width / width,
            ReaderFit.Original => 1 / scaling,
            _ => Math.Min(viewport.Width / width, viewport.Height / height),
        } * _zoom;
    }

    private (Rect Content, double LeftWidth) Layout()
    {
        var (width, height, leftWidth) = Spread();
        var scale = Scale();
        if (scale <= 0)
        {
            return (default, 0);
        }

        var viewport = Bounds.Size;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var size = new Size(width * scale, height * scale);
        var x = size.Width <= viewport.Width ? (viewport.Width - size.Width) / 2 : Math.Clamp(_offset.X, viewport.Width - size.Width, 0);
        var y = size.Height <= viewport.Height ? (viewport.Height - size.Height) / 2 : Math.Clamp(_offset.Y, viewport.Height - size.Height, 0);
        _offset = new Vector(x, y);

        var needed = (int)Math.Ceiling(size.Height * scaling);
        if (needed > 0 && needed != NeededHeight)
        {
            // After this frame: the view model renders sharper pages if needed.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => NeededHeight = needed);
        }

        return (new Rect(new Point(x, y), size), leftWidth * scale);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var (content, leftWidth) = Layout();
        if (content.Width <= 0 || LeftImage is not { } left)
        {
            return;
        }

        var leftRect = RightImage is null ? content : new Rect(content.X, content.Y, leftWidth, content.Height);
        context.DrawImage(left, new Rect(left.Size), leftRect);
        if (RightImage is { } right)
        {
            context.DrawImage(right, new Rect(right.Size), new Rect(content.X + leftWidth, content.Y, content.Width - leftWidth, content.Height));
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var (content, _) = Layout();
        if (content.Width <= 0)
        {
            return;
        }

        e.Handled = true;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ZoomAt(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
            return;
        }

        var viewport = Bounds.Size;
        if (content.Height > viewport.Height + 1)
        {
            var atTop = content.Y >= -0.5;
            var atBottom = content.Bottom <= viewport.Height + 0.5;
            if ((e.Delta.Y < 0 && atBottom) || (e.Delta.Y > 0 && atTop))
            {
                // A deliberate extra turn of the wheel at the end of the page goes to the next one.
                _edgeWheel += e.Delta.Y;
                if (Math.Abs(_edgeWheel) >= 2)
                {
                    Turn(_edgeWheel < 0);
                }

                return;
            }

            _edgeWheel = 0;
            _offset = new Vector(_offset.X, _offset.Y + (e.Delta.Y * ScrollStep));
            InvalidateVisual();
            return;
        }

        // Touchpads send many small deltas: one page per whole notch.
        _wheel += e.Delta.Y;
        if (Math.Abs(_wheel) >= 1)
        {
            Turn(_wheel < 0);
        }
    }

    private void Turn(bool next)
    {
        _wheel = 0;
        _edgeWheel = 0;
        var command = next ? NextCommand : PreviousCommand;
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    private void ZoomAt(Point point, double factor)
    {
        var (before, _) = Layout();
        var zoom = Math.Clamp(_zoom * factor, 1, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 0.0001 || before.Width <= 0)
        {
            return;
        }

        // Keep the point under the pointer where it is.
        var relative = new Point((point.X - before.X) / before.Width, (point.Y - before.Y) / before.Height);
        _zoom = zoom;
        var (width, height, _) = Spread();
        var scale = Scale();
        _offset = new Vector(point.X - (relative.X * width * scale), point.Y - (relative.Y * height * scale));
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        if (e.ClickCount >= 2)
        {
            // Double click: back to the fitted size.
            _pressedAt = null;
            _zoom = 1;
            _offset = default;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _pressedAt = _lastDrag = e.GetPosition(this);
        _dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressedAt is not { } start)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (!_dragging && (Math.Abs(point.X - start.X) > 4 || Math.Abs(point.Y - start.Y) > 4))
        {
            _dragging = true;
        }

        if (_dragging)
        {
            _offset += point - _lastDrag;
            _lastDrag = point;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressedAt is not { } start)
        {
            return;
        }

        _pressedAt = null;
        e.Pointer.Capture(null);
        if (_dragging)
        {
            _dragging = false;
            return;
        }

        // A click on the sides turns the page; the middle does nothing.
        var width = Bounds.Width;
        var command = start.X < width * 0.35 ? LeftCommand : start.X > width * 0.65 ? RightCommand : null;
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }
}
