using System.Threading.Channels;
using LLMAutocorrect.Autocomplete;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Correction;
using LLMAutocorrect.Logging;
using LLMAutocorrect.Windows;

namespace LLMAutocorrect.Input;

public sealed class InputRuntime : IAsyncDisposable
{
    private readonly GlobalKeyboardHook _keyboard;
    private readonly GlobalMouseHook _mouse;
    private readonly ForegroundWindowService _foreground;
    private readonly FocusedControlService _focused;
    private readonly TypingBuffer _buffer;
    private readonly InputVersionClock _clock;
    private readonly CorrectionCoordinator _correction;
    private readonly AutocompleteCoordinator _autocomplete;
    private readonly ManualCorrectionService _manualCorrection;
    private readonly SettingsManager _settings;
    private readonly DiagnosticsLogger _logger;
    private readonly Channel<InputMessage> _channel = Channel.CreateUnbounded<InputMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;

    public InputRuntime(GlobalKeyboardHook keyboard, GlobalMouseHook mouse, ForegroundWindowService foreground,
        FocusedControlService focused, TypingBuffer buffer, InputVersionClock clock,
        CorrectionCoordinator correction, AutocompleteCoordinator autocomplete, ManualCorrectionService manualCorrection,
        SettingsManager settings, DiagnosticsLogger logger)
    {
        _keyboard = keyboard; _mouse = mouse; _foreground = foreground; _focused = focused; _buffer = buffer;
        _clock = clock; _correction = correction; _autocomplete = autocomplete; _manualCorrection = manualCorrection;
        _settings = settings; _logger = logger;
    }

    public void Start()
    {
        _keyboard.InputReceived = OnKeyboardInput;
        _mouse.Clicked = () => _channel.Writer.TryWrite(new MouseMessage(_clock.Current));
        _mouse.LeftClicked = _manualCorrection.ActivateOfferAt;
        _mouse.RightClicked = point => _channel.Writer.TryWrite(new RightClickMessage(point));
        _worker = Task.Run(ProcessAsync);
        _keyboard.Start();
        _mouse.Start();
    }

    private bool OnKeyboardInput(KeyboardInputEvent input)
    {
        if (_autocomplete.InterceptOrDismiss(input)) return true;
        if (input.Control && input.Alt && input.VirtualKey == NativeMethods.VkC)
        {
            _channel.Writer.TryWrite(new ManualCorrectionMessage());
            return true;
        }
        _manualCorrection.DismissOffer();
        if (input.Control && input.Alt && input.VirtualKey == NativeMethods.VkZ)
        {
            _channel.Writer.TryWrite(new UndoMessage(_clock.Current));
            return true;
        }
        if (input.Control && input.Alt && input.VirtualKey == 0x50) // P
        {
            _channel.Writer.TryWrite(new ToggleMessage());
            return true;
        }
        _channel.Writer.TryWrite(new KeyboardMessage(input));
        return false;
    }

    private async Task ProcessAsync()
    {
        try
        {
            await ProcessResilientlyAsync(_channel.Reader.ReadAllAsync(), ProcessMessageAsync,
                HandleMessageFailureAsync, _shutdown.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await TryLogAsync("InputWorkerTerminated", new Dictionary<string, object?>
            {
                ["Type"] = ex.GetType().Name, ["HResult"] = ex.HResult, ["Message"] = ex.Message
            });
        }
    }

    private async Task ProcessMessageAsync(InputMessage message)
    {
        switch (message)
        {
            case KeyboardMessage keyboard: ProcessKeyboard(keyboard.Input); break;
            case MouseMessage mouse: ProcessInvalidation(mouse.Version); break;
            case RightClickMessage rightClick: _manualCorrection.OfferAt(rightClick.Point); break;
            case ManualCorrectionMessage: _manualCorrection.OfferCurrent(); break;
            case UndoMessage undo: await _correction.UndoLatestAsync(undo.ExpectedVersion); break;
            case ToggleMessage:
                _settings.Current.Enabled = !_settings.Current.Enabled;
                await _settings.SaveAsync(_shutdown.Token);
                _correction.Cancel(); _autocomplete.Dismiss();
                break;
        }
    }

    private async Task HandleMessageFailureAsync(InputMessage message, Exception ex)
    {
        _correction.Cancel();
        _autocomplete.Dismiss();
        _manualCorrection.DismissOffer();
        try { _buffer.Invalidate(_foreground.GetCurrent(), _clock.Current); } catch { }
        await TryLogAsync("InputEventFailed", new Dictionary<string, object?>
        {
            ["EventType"] = message.GetType().Name,
            ["Type"] = ex.GetType().Name,
            ["HResult"] = ex.HResult,
            ["Message"] = ex.Message,
            ["Recovered"] = true
        });
    }

    private async Task TryLogAsync(string eventName, IReadOnlyDictionary<string, object?> metadata)
    {
        try { await _logger.WriteAsync(eventName, metadata); } catch { }
    }

    internal static async Task ProcessResilientlyAsync<T>(IAsyncEnumerable<T> messages,
        Func<T, Task> process, Func<T, Exception, Task> onError, CancellationToken cancellationToken)
    {
        await foreach (var message in messages.WithCancellation(cancellationToken))
        {
            try { await process(message); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Diagnostics and cleanup must never become a second reason for the
                // input loop to die. The next keyboard or mouse event must still run.
                try { await onError(message, ex); } catch { }
            }
        }
    }

    private void ProcessKeyboard(KeyboardInputEvent input)
    {
        var window = _foreground.GetCurrent();
        var control = _focused.GetCurrent();
        if (control.IsPassword)
        {
            _buffer.Invalidate(window, input.InputVersion);
            _correction.Cancel(); _autocomplete.Dismiss();
            return;
        }

        var change = _buffer.Handle(input, window);
        _correction.Schedule(change.Snapshot, change.ShouldTriggerImmediately);
        _autocomplete.Schedule(change.Snapshot);
    }

    private void ProcessInvalidation(long version)
    {
        var window = _foreground.GetCurrent();
        _buffer.Invalidate(window, version);
        _correction.Cancel();
        _autocomplete.Dismiss();
    }

    public async ValueTask DisposeAsync()
    {
        _keyboard.Dispose(); _mouse.Dispose();
        _shutdown.Cancel(); _channel.Writer.TryComplete();
        _correction.Dispose(); _autocomplete.Dispose();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _shutdown.Dispose();
    }

    private abstract record InputMessage;
    private sealed record KeyboardMessage(KeyboardInputEvent Input) : InputMessage;
    private sealed record MouseMessage(long Version) : InputMessage;
    private sealed record RightClickMessage(System.Windows.Point Point) : InputMessage;
    private sealed record ManualCorrectionMessage : InputMessage;
    private sealed record UndoMessage(long ExpectedVersion) : InputMessage;
    private sealed record ToggleMessage : InputMessage;
}
