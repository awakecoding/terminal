using Avalonia;
using Avalonia.Input.TextInput;

namespace Microsoft.Terminal.Control;

internal sealed class TerminalTextInputMethodClient(TermControl owner) : TextInputMethodClient
{
    private string _preeditText = string.Empty;
    private int? _preeditCursor;

    public override Visual TextViewVisual => owner;

    public override bool SupportsPreedit => true;

    public override bool SupportsSurroundingText => true;

    public override string SurroundingText => owner.GetImeContext().Text;

    public override Rect CursorRectangle => owner.GetImeCursorRectangle();

    public override TextSelection Selection
    {
        get
        {
            var offset = owner.GetImeContext().CursorTextOffset;
            return new TextSelection(offset, offset);
        }
        set
        {
            var offset = Math.Clamp(value.End, 0, SurroundingText.Length);
            owner.SetImeSelectionOffset(offset);
            RaiseSelectionChanged();
            RaiseCursorRectangleChanged();
        }
    }

    public override void SetPreeditText(string? preeditText, int? cursorPos)
    {
        _preeditText = preeditText ?? string.Empty;
        _preeditCursor = cursorPos;
        owner.SetImeComposition(_preeditText, _preeditCursor);
        RaiseSurroundingTextChanged();
        RaiseCursorRectangleChanged();
    }

    internal void NotifyCursorChanged()
    {
        RaiseSurroundingTextChanged();
        RaiseSelectionChanged();
        RaiseCursorRectangleChanged();
    }
}
