using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LLMAutocorrect.UI;

public partial class ManualCorrectionOverlay : Window
{
    private const double EstimatedWidth = 300;
    private const double EstimatedHeight = 80;
    private const double PointerGap = 16;
    private const int GwlExstyle = -20;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;
    private readonly DispatcherTimer _timer = new();
    private Func<Task>? _action;
    private IntPtr _handle;
    private int _offered;

    public ManualCorrectionOverlay()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Dismiss();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(_handle, GwlExstyle).ToInt64();
            SetWindowLongPtr(_handle, GwlExstyle, new IntPtr(style | WsExNoactivate | WsExToolwindow));
        };
    }

    public void Offer(string preview, System.Windows.Point position, Func<Task> action)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _action = action;
            PreviewText.Text = preview.Length > 50 ? preview[..50] + "..." : preview;
            var location = CalculatePosition(position, SystemParameters.WorkArea,
                new System.Windows.Size(EstimatedWidth, EstimatedHeight), PointerGap);
            Left = location.X;
            Top = location.Y;
            if (!IsVisible) Show();
            Volatile.Write(ref _offered, 1);
            _timer.Stop();
            _timer.Interval = TimeSpan.FromSeconds(5);
            _timer.Start();
        });
    }

    public void Dismiss() => Dispatcher.BeginInvoke(() =>
    {
        Volatile.Write(ref _offered, 0);
        _timer.Stop();
        _action = null;
        base.Hide();
    });

    // Native context menus can consume the first click made outside their bounds.
    // The global mouse hook recognizes a click over this overlay and invokes the
    // action while still allowing the click through so Windows closes its menu.
    public bool TryInvokeAt(System.Windows.Point screenPoint)
    {
        if (Volatile.Read(ref _offered) == 0 || _handle == IntPtr.Zero || !GetWindowRect(_handle, out var bounds))
            return false;
        if (screenPoint.X < bounds.Left || screenPoint.X >= bounds.Right ||
            screenPoint.Y < bounds.Top || screenPoint.Y >= bounds.Bottom) return false;
        Dispatcher.BeginInvoke(InvokeCurrentAction);
        return true;
    }

    internal static System.Windows.Point CalculatePosition(System.Windows.Point pointer, Rect workArea,
        System.Windows.Size popup, double gap)
    {
        var left = pointer.X - popup.Width - gap;
        var top = pointer.Y - popup.Height - gap;
        if (left < workArea.Left) left = pointer.X + gap;
        if (top < workArea.Top) top = pointer.Y + gap;
        left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - popup.Width));
        top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - popup.Height));
        return new(left, top);
    }

    private async void CorrectButton_Click(object sender, RoutedEventArgs e) => await InvokeCurrentAction();

    private async Task InvokeCurrentAction()
    {
        var action = _action;
        Volatile.Write(ref _offered, 0);
        _timer.Stop();
        _action = null;
        base.Hide();
        if (action is not null) await action();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr newValue);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
