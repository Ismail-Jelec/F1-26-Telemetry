using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace F126Overlay;

/// <summary>
/// Borderless, transparent, always-on-top window hosting one overlay.
/// Drag it with the mouse while unlocked; when locked, clicks pass through to the game.
/// </summary>
public sealed class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly OverlaySettings _settings;
    private readonly OverlayView _view;
    private readonly Action _save;
    private readonly Point _defaultPos;
    private IntPtr _hwnd;
    private bool _locked;
    private int _fadeToken;

    public OverlayWindow(string title, OverlayView view, OverlaySettings settings, Point defaultPos, Action save)
    {
        _settings = settings;
        _view = view;
        _save = save;
        _defaultPos = defaultPos;

        Title = $"F1 26 overlay - {title}";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;
        Content = view;
        Opacity = 0;

        if (settings.Position is { Length: 2 } pos) { Left = pos[0]; Top = pos[1]; }
        else { Left = defaultPos.X; Top = defaultPos.Y; }

        ApplyAppearance();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyStyles();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (_locked || e.ButtonState != MouseButtonState.Pressed) return;
            DragMove();
            _settings.Position = [Left, Top];
            _save();
        };

        // Some games grab the top spot; re-assert it now and then.
        var keepOnTop = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        keepOnTop.Tick += (_, _) => { if (IsVisible) { Topmost = false; Topmost = true; } };
        keepOnTop.Start();
    }

    public OverlayView View => _view;

    /// <summary>Re-reads scale and opacity from settings and re-measures the view.</summary>
    public void ApplyAppearance()
    {
        _view.LayoutTransform = new ScaleTransform(_settings.Scale, _settings.Scale);
        _view.Opacity = _settings.Opacity;
        _view.InvalidateMeasure();
        _view.InvalidateVisual();
    }

    public void FadeIn()
    {
        int token = ++_fadeToken;
        if (!IsVisible) { Opacity = 0; Show(); }
        var a = new DoubleAnimation(1, Theme.Ms(260)) { EasingFunction = Anim.Out };
        a.Completed += (_, _) => { if (token == _fadeToken) { BeginAnimation(OpacityProperty, null); Opacity = 1; } };
        BeginAnimation(OpacityProperty, a);
    }

    public void FadeOut()
    {
        int token = ++_fadeToken;
        if (!IsVisible) return;
        var a = new DoubleAnimation(0, Theme.Ms(220)) { EasingFunction = Anim.Out };
        a.Completed += (_, _) => { if (token == _fadeToken) { Hide(); BeginAnimation(OpacityProperty, null); Opacity = 0; } };
        BeginAnimation(OpacityProperty, a);
    }

    public void ResetPosition()
    {
        _settings.Position = null;
        var anim = new DoubleAnimation(_defaultPos.X, Theme.Ms(350)) { EasingFunction = Anim.Out };
        anim.Completed += (_, _) => { BeginAnimation(LeftProperty, null); Left = _defaultPos.X; };
        var animY = new DoubleAnimation(_defaultPos.Y, Theme.Ms(350)) { EasingFunction = Anim.Out };
        animY.Completed += (_, _) => { BeginAnimation(TopProperty, null); Top = _defaultPos.Y; };
        BeginAnimation(LeftProperty, anim);
        BeginAnimation(TopProperty, animY);
        _save();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        _view.Unlocked = !locked;
        Cursor = locked ? null : Cursors.SizeAll;
        ApplyStyles();
    }

    private void ApplyStyles()
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        ex = _locked ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
    }
}
