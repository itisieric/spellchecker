using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Providers;

namespace LLMAutocorrect.UI;

public sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly SettingsManager _settings;
    private readonly Func<SettingsWindow> _settingsWindow;
    private readonly Action _undo;
    private readonly Func<Task<LocalModelResourceReleaseResult>> _releaseLocalResources;
    private readonly Func<bool> _canReleaseLocalResources;

    public TrayIconManager(SettingsManager settings, Func<SettingsWindow> settingsWindow, Action undo,
        Action manualCorrection, Func<Task<LocalModelResourceReleaseResult>> releaseLocalResources,
        Func<bool> canReleaseLocalResources, Action exit)
    {
        _settings = settings; _settingsWindow = settingsWindow; _undo = undo;
        _releaseLocalResources = releaseLocalResources;
        _canReleaseLocalResources = canReleaseLocalResources;
        _icon = new Forms.NotifyIcon { Icon = Drawing.SystemIcons.Information, Text = "LLM Autocorrect", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        var enabled = new Forms.ToolStripMenuItem("Enabled") { Checked = settings.Current.Enabled, CheckOnClick = true };
        enabled.CheckedChanged += async (_, _) => { settings.Current.Enabled = enabled.Checked; await settings.SaveAsync(); };
        menu.Items.Add(enabled);
        var autocomplete = new Forms.ToolStripMenuItem("Autocomplete") { Checked = settings.Current.AutocompleteEnabled, CheckOnClick = true };
        autocomplete.CheckedChanged += async (_, _) => { settings.Current.AutocompleteEnabled = autocomplete.Checked; await settings.SaveAsync(); };
        menu.Items.Add(autocomplete);
        var releaseResources = new Forms.ToolStripMenuItem("Free local Ollama model resources")
        {
            Checked = settings.Current.ReleaseLocalModelResources,
            CheckOnClick = true,
            ToolTipText = "Unload the local Ollama model and release GPU memory and RAM."
        };
        var changingReleaseMode = false;
        releaseResources.CheckedChanged += async (_, _) =>
        {
            if (changingReleaseMode) return;
            changingReleaseMode = true;
            try
            {
                settings.Current.ReleaseLocalModelResources = releaseResources.Checked;
                await settings.SaveAsync();
                if (!releaseResources.Checked)
                {
                    _icon.ShowBalloonTip(2500, "LLM Autocorrect",
                        "Local Ollama will stay loaded after the next request.", Forms.ToolTipIcon.Info);
                    return;
                }

                var result = await _releaseLocalResources();
                if (result.Success)
                {
                    _icon.ShowBalloonTip(3000, "LLM Autocorrect", result.Message, Forms.ToolTipIcon.Info);
                    return;
                }

                releaseResources.Checked = false;
                settings.Current.ReleaseLocalModelResources = false;
                await settings.SaveAsync();
                _icon.ShowBalloonTip(4000, "LLM Autocorrect", result.Message, Forms.ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                releaseResources.Checked = false;
                settings.Current.ReleaseLocalModelResources = false;
                try { await settings.SaveAsync(); } catch { }
                _icon.ShowBalloonTip(4000, "LLM Autocorrect",
                    $"Could not change local model resource mode: {ex.Message}", Forms.ToolTipIcon.Warning);
            }
            finally { changingReleaseMode = false; }
        };
        menu.Items.Add(releaseResources);
        menu.Opening += (_, _) =>
        {
            var available = _canReleaseLocalResources();
            releaseResources.Enabled = available;
            releaseResources.ToolTipText = available
                ? "When checked, unloads the local Ollama model now and after each request."
                : "Available only when Ollama is the selected provider at a local address.";
        };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Correct selected text / word (Ctrl+Alt+C)", null, (_, _) => manualCorrection());
        menu.Items.Add("Undo last correction", null, (_, _) => _undo());
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowSettings();
    }

    private void ShowSettings()
    {
        var window = _settingsWindow();
        window.Show(); window.Activate();
    }

    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
