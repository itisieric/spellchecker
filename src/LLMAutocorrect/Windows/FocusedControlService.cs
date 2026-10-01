using System.Windows.Automation;
using System.Runtime.InteropServices;
using LLMAutocorrect.Models;

namespace LLMAutocorrect.Windows;

public sealed class FocusedControlService
{
    public FocusedControlIdentity GetCurrent()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null) return new(null, false, false);
            var id = string.Join('.', element.GetRuntimeId());
            var isPassword = element.Current.IsPassword;
            var editable = element.TryGetCurrentPattern(ValuePattern.Pattern, out _) ||
                           element.TryGetCurrentPattern(TextPattern.Pattern, out _);
            return new(id, isPassword, editable, element.Current.Name ?? string.Empty,
                element.Current.AutomationId ?? string.Empty, element.Current.ClassName ?? string.Empty);
        }
        catch (ElementNotAvailableException) { return new(null, false, false); }
        catch (InvalidOperationException) { return new(null, false, false); }
        catch (COMException) { return new(null, false, false); }
        catch (UnauthorizedAccessException) { return new(null, false, false); }
    }
}
