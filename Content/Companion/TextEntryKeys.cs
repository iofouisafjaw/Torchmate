namespace TerrarAI.Content.Companion;

public enum TextEntryAction { None, Submit, Blur }

// The IME can clear its composition during the same Enter that commits a word.
public sealed class TextEntryKeys
{
    private bool _enter, _escape, _composing;
    public void Reset(bool enter, bool escape) { _enter = enter; _escape = escape; _composing = false; }
    public TextEntryAction Consume(bool enter, bool escape, bool composing)
    {
        bool imeOwnsPress = composing || _composing;
        var result = !imeOwnsPress && escape && !_escape ? TextEntryAction.Blur
            : !imeOwnsPress && enter && !_enter ? TextEntryAction.Submit : TextEntryAction.None;
        _enter = enter; _escape = escape; _composing = composing;
        return result;
    }
}
