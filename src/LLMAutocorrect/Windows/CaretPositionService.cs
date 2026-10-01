namespace LLMAutocorrect.Windows;

public sealed class CaretPositionService
{
    public System.Windows.Point GetCaretScreenPosition(IntPtr foregroundWindow)
    {
        NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
        var info = new NativeMethods.GuiThreadInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        if (NativeMethods.GetGUIThreadInfo(0, ref info) && info.Caret != IntPtr.Zero)
        {
            var point = new NativeMethods.Point { X = info.CaretRect.Left, Y = info.CaretRect.Bottom };
            if (NativeMethods.ClientToScreen(info.Caret, ref point)) return new System.Windows.Point(point.X, point.Y + 4);
        }

        try
        {
            var element = System.Windows.Automation.AutomationElement.FocusedElement;
            var rect = element?.Current.BoundingRectangle ?? System.Windows.Rect.Empty;
            if (!rect.IsEmpty) return new System.Windows.Point(rect.Left + 12, rect.Bottom + 4);
        }
        catch (System.Windows.Automation.ElementNotAvailableException) { }
        return new System.Windows.Point(System.Windows.SystemParameters.WorkArea.Right - 420, System.Windows.SystemParameters.WorkArea.Bottom - 80);
    }
}
