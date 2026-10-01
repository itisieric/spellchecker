using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using LLMAutocorrect.Configuration;

namespace LLMAutocorrect.UI;

public sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly SettingsManager _settings;
    private readonly Func<SettingsWindow> _settingsWindow;
    private readonly Action _undo;

    public TrayIconManager(SettingsManager settings, Func<SettingsWindow> settingsWindow, Action undo,
        Action manualCorrection, Action exit)
    {
        _settings = settings; _settingsWindow = settingsWindow; _undo = undo;
        _icon = new Forms.NotifyIcon { Icon = Drawing.SystemIcons.Information, Text = "LLM Autocorrect", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        var enabled = new Forms.ToolStripMenuItem("Enabled") { Checked = settings.Current.Enabled, CheckOnClick = true };
        enabled.CheckedChanged += async (_, _) => { settings.Current.Enabled = enabled.Checked; await settings.SaveAsync(); };
        menu.Items.Add(enabled);
        var autocomplete = new Forms.ToolStripMenuItem("Autocomplete") { Checked = settings.Current.AutocompleteEnabled, CheckOnClick = true };
        autocomplete.CheckedChanged += async (_, _) => { settings.Current.AutocompleteEnabled = autocomplete.Checked; await settings.SaveAsync(); };
        menu.Items.Add(autocomplete);
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
