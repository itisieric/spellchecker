namespace LLMAutocorrect.Autocomplete;

public interface ISuggestionPresenter
{
    void Show(string text, int selectedIndex, int total, System.Windows.Point position, bool isTerminal);
    void Hide();
}
