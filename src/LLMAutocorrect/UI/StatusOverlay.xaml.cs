using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LLMAutocorrect.UI;

public partial class StatusOverlay : Window
{
    private const int GwlExstyle = -20;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private readonly DispatcherTimer _timer = new();

    public StatusOverlay()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => { _timer.Stop(); Hide(); };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, GwlExstyle).ToInt64();
            SetWindowLongPtr(handle, GwlExstyle, new IntPtr(style | WsExNoactivate | WsExToolwindow | WsExTransparent));
        };
    }

    public void ShowMessage(string message, System.Windows.Point position, TimeSpan? duration = null)
    {
        Dispatcher.BeginInvoke(() =>
        {
            MessageText.Text = message;
            Left = Math.Max(SystemParameters.WorkArea.Left, Math.Min(position.X, SystemParameters.WorkArea.Right - 440));
            Top = Math.Max(SystemParameters.WorkArea.Top, Math.Min(position.Y, SystemParameters.WorkArea.Bottom - 90));
            if (!IsVisible) Show();
            _timer.Stop();
            _timer.Interval = duration ?? TimeSpan.FromSeconds(2.5);
            _timer.Start();
        });
    }

    public new void Hide() => Dispatcher.BeginInvoke(() => { _timer.Stop(); base.Hide(); });

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr newValue);
}
