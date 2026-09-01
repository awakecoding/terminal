using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Microsoft.Terminal.Core;

namespace Microsoft.Terminal.Control;

public sealed class TermControlAutomationPeer : ControlAutomationPeer, IValueProvider
{
    private string _documentText;
    private string _name;
    private bool _documentTextDirty;

    public TermControlAutomationPeer(TermControl owner)
        : base(owner)
    {
        _documentText = CreateDocumentText();
        _name = ResolveName();
        owner.AccessibilityChanged += OnAccessibilityChanged;
        owner.AccessibilityTextChanged += OnAccessibilityTextChanged;
    }

    public new TermControl Owner => (TermControl)base.Owner;

    public bool IsReadOnly => true;

    public string Value => DocumentText;

    public string DocumentText
    {
        get
        {
            if (_documentTextDirty)
            {
                _documentText = CreateDocumentText();
                _documentTextDirty = false;
            }

            return _documentText;
        }
    }

    public TerminalTextRange DocumentRange => CreateState().DocumentRange;

    public TerminalTextRange SelectionRange => CreateState().SelectionRange;

    public TerminalTextRange CaretRange => CreateState().CaretRange;

    public TerminalAccessibleState CreateState()
    {
        var snapshot = Owner.Engine.CreateSnapshot(includeHistory: true).Buffer;
        var document = TerminalTextDocument.Create(snapshot);
        var caret = Owner.Selection?.Active ??
            new TerminalSelectionPoint(
                snapshot.CursorX,
                snapshot.HistoryCount + snapshot.CursorY);
        return new TerminalAccessibleState(
            GetName(),
            Owner.IsRunning,
            IsReadOnly,
            document.LineCount,
            document.DocumentRange,
            document.Range(Owner.Selection),
            document.CaretRange(caret));
    }

    public string GetText(TerminalTextRange range, int maxLength = -1)
    {
        var text = DocumentText;
        var normalized = range.Normalize(text.Length);
        var length = normalized.Length;
        if (maxLength >= 0)
        {
            length = Math.Min(length, maxLength);
        }

        return text.Substring(normalized.Start, length);
    }

    public void SetValue(string? value) =>
        throw new InvalidOperationException("Terminal output is read-only.");

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Document;

    protected override string? GetNameCore()
    {
        return ResolveName();
    }

    private string ResolveName()
    {
        var explicitName = base.GetNameCore();
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        return Owner.AccessibleName;
    }

    protected override string? GetHelpTextCore()
    {
        var selection = Owner.HasSelection ? " Selection active." : string.Empty;
        var mode = Owner.IsMarkMode ? " Mark mode active." : string.Empty;
        return $"Terminal document.{selection}{mode}";
    }

    private void OnAccessibilityChanged(object? sender, EventArgs e)
    {
        var name = ResolveName();
        if (!string.Equals(_name, name, StringComparison.Ordinal))
        {
            var oldName = _name;
            _name = name;
            RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, oldName, name);
        }
    }

    private void OnAccessibilityTextChanged(object? sender, EventArgs e)
    {
        _documentTextDirty = true;
        RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, null, null);
    }

    private string CreateDocumentText() =>
        TerminalTextDocument.CreateText(Owner.Engine.CreateSnapshot(includeHistory: true).Buffer);
}
