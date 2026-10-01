using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LLMAutocorrect.Autocomplete;

public partial class SuggestionOverlay : Window, ISuggestionPresenter
{
    private const int GwlExstyle = -20;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExToolwindow = 0x00000080;

    public SuggestionOverlay()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, GwlExstyle).ToInt64();
            SetWindowLongPtr(handle, GwlExstyle, new IntPtr(style | WsExNoactivate | WsExToolwindow));
        };
    }

    public void Show(string text, int selectedIndex, int total, System.Windows.Point position, bool isTerminal)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SuggestionText.Text = text;
            TerminalTag.Visibility = isTerminal ? Visibility.Visible : Visibility.Collapsed;
            CounterText.Text = total > 1 ? $"{selectedIndex + 1}/{total}" : string.Empty;
            NextHint.Visibility = total > 1 ? Visibility.Visible : Visibility.Collapsed;
            Left = Math.Min(position.X, SystemParameters.WorkArea.Right - 640);
            Top = Math.Min(position.Y, SystemParameters.WorkArea.Bottom - 90);
            if (!IsVisible) Show();
        });
    }

    public new void Hide() => Dispatcher.BeginInvoke(base.Hide);

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Hide();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr newValue);
}
