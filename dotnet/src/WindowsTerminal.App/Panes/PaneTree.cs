namespace WindowsTerminal.Panes;

public enum PaneSplitOrientation
{
    Horizontal,
    Vertical,
}

public enum PaneDirection
{
    Left,
    Right,
    Up,
    Down,
}

public readonly record struct PaneBounds(double X, double Y, double Width, double Height)
{
    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);
}

public abstract record PaneNode<T> where T : class;

public sealed record PaneLeaf<T>(T Content) : PaneNode<T> where T : class;

public sealed record PaneSplit<T>(
    PaneSplitOrientation Orientation,
    double Ratio,
    PaneNode<T> First,
    PaneNode<T> Second) : PaneNode<T>
    where T : class;

public sealed class PaneTree<T> where T : class
{
    private const double MinimumRatio = 0.1;
    private readonly IEqualityComparer<T> _comparer;

    public PaneTree(T initialContent, IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(initialContent);
        _comparer = comparer ?? EqualityComparer<T>.Default;
        Root = new PaneLeaf<T>(initialContent);
        ActiveContent = initialContent;
    }

    private PaneTree(
        PaneNode<T> root,
        T activeContent,
        T? zoomedContent,
        IEqualityComparer<T>? comparer)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(activeContent);
        _comparer = comparer ?? EqualityComparer<T>.Default;
        Root = NormalizeNode(root);
        if (FindLeaf(activeContent) is null)
        {
            throw new ArgumentException("The active pane must exist in the restored tree.", nameof(activeContent));
        }

        if (zoomedContent is not null && FindLeaf(zoomedContent) is null)
        {
            throw new ArgumentException("The zoomed pane must exist in the restored tree.", nameof(zoomedContent));
        }

        ActiveContent = activeContent;
        ZoomedContent = zoomedContent;
    }

    public PaneNode<T>? Root { get; private set; }

    public T? ActiveContent { get; private set; }

    public T? ZoomedContent { get; private set; }

    public int Count => Leaves().Count;

    public static PaneTree<T> Restore(
        PaneNode<T> root,
        T activeContent,
        T? zoomedContent = null,
        IEqualityComparer<T>? comparer = null) =>
        new(root, activeContent, zoomedContent, comparer);

    public IReadOnlyList<T> Leaves() =>
        Root is null ? [] : EnumerateLeaves(Root).Select(static leaf => leaf.Content).ToArray();

    public bool Activate(T content)
    {
        if (FindLeaf(content) is null)
        {
            return false;
        }

        ActiveContent = content;
        if (ZoomedContent is not null && !_comparer.Equals(ZoomedContent, content))
        {
            ZoomedContent = null;
        }

        return true;
    }

    public bool SplitActive(
        T newContent,
        PaneSplitOrientation orientation,
        double ratio = 0.5,
        bool newContentFirst = false)
    {
        ArgumentNullException.ThrowIfNull(newContent);
        if (Root is null || ActiveContent is null || FindLeaf(newContent) is not null)
        {
            return false;
        }

        var normalizedRatio = NormalizeRatio(ratio);
        var replacement = new PaneSplit<T>(
            orientation,
            normalizedRatio,
            new PaneLeaf<T>(newContentFirst ? newContent : ActiveContent),
            new PaneLeaf<T>(newContentFirst ? ActiveContent : newContent));
        Root = ReplaceLeaf(Root, ActiveContent, replacement);
        ActiveContent = newContent;
        ZoomedContent = null;
        return true;
    }

    public bool Close(T content)
    {
        return Detach(content, out _);
    }

    public bool Detach(T content, out T? detached)
    {
        detached = default;
        if (Root is null || FindLeaf(content) is null)
        {
            return false;
        }

        detached = content;
        var replacementFocus = FindSiblingLeaf(Root, content);
        Root = RemoveLeaf(Root, content);
        if (Root is null)
        {
            ActiveContent = default;
            ZoomedContent = default;
            return true;
        }

        if (ActiveContent is not null && _comparer.Equals(ActiveContent, content))
        {
            ActiveContent = replacementFocus ?? Leaves()[0];
        }

        if (ZoomedContent is not null && _comparer.Equals(ZoomedContent, content))
        {
            ZoomedContent = default;
        }

        return true;
    }

    public bool InsertAdjacent(
        T target,
        T content,
        PaneSplitOrientation orientation,
        double ratio = 0.5,
        bool contentFirst = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(content);
        if (Root is null || FindLeaf(target) is null || FindLeaf(content) is not null)
        {
            return false;
        }

        var replacement = new PaneSplit<T>(
            orientation,
            NormalizeRatio(ratio),
            new PaneLeaf<T>(contentFirst ? content : target),
            new PaneLeaf<T>(contentFirst ? target : content));
        Root = ReplaceLeaf(Root, target, replacement);
        ActiveContent = content;
        ZoomedContent = null;
        return true;
    }

    public bool MoveFocus(PaneDirection direction)
    {
        if (Root is null || ActiveContent is null)
        {
            return false;
        }

        var bounds = CalculateBounds();
        if (!bounds.TryGetValue(ActiveContent, out var activeBounds))
        {
            return false;
        }

        var candidate = bounds
            .Where(pair => !_comparer.Equals(pair.Key, ActiveContent) && IsInDirection(activeBounds, pair.Value, direction))
            .Select(pair => new
            {
                pair.Key,
                Aligned = OrthogonallyOverlaps(activeBounds, pair.Value, direction),
                Primary = PrimaryDistance(activeBounds, pair.Value, direction),
                Secondary = SecondaryDistance(activeBounds, pair.Value, direction),
            })
            .OrderByDescending(static item => item.Aligned)
            .ThenBy(static item => item.Primary)
            .ThenBy(static item => item.Secondary)
            .FirstOrDefault();

        if (candidate is null)
        {
            return false;
        }

        ActiveContent = candidate.Key;
        ZoomedContent = null;
        return true;
    }

    public bool SwapActive(PaneDirection direction)
    {
        if (Root is null || ActiveContent is null)
        {
            return false;
        }

        var active = ActiveContent;
        if (!MoveFocus(direction) || ActiveContent is null)
        {
            return false;
        }

        var target = ActiveContent;
        Root = SwapLeafContent(Root, active, target);
        ActiveContent = active;
        ZoomedContent = null;
        return true;
    }

    public bool MoveFocusInOrder(int delta)
    {
        var leaves = Leaves();
        if (ActiveContent is null || leaves.Count <= 1)
        {
            return false;
        }

        var current = IndexOf(leaves, ActiveContent);
        if (current < 0)
        {
            return false;
        }

        var next = (current + delta) % leaves.Count;
        if (next < 0)
        {
            next += leaves.Count;
        }

        ActiveContent = leaves[next];
        ZoomedContent = null;
        return true;
    }

    public bool FocusFirst()
    {
        var first = Leaves().FirstOrDefault();
        return first is not null && Activate(first);
    }

    public bool ResizeActive(PaneDirection direction, double amount)
    {
        if (Root is null || ActiveContent is null || amount == 0)
        {
            return false;
        }

        var desiredOrientation = direction is PaneDirection.Left or PaneDirection.Right
            ? PaneSplitOrientation.Vertical
            : PaneSplitOrientation.Horizontal;
        var result = ResizeNearest(Root, ActiveContent, desiredOrientation, direction, amount);
        Root = result.Node;
        return result.Resized;
    }

    public bool SetSplitRatio(PaneSplit<T> target, double ratio)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Root is null)
        {
            return false;
        }

        var result = ReplaceSplit(Root, target, NormalizeRatio(ratio));
        Root = result.Node;
        return result.Replaced;
    }

    public bool ToggleZoom()
    {
        if (ActiveContent is null || Count <= 1)
        {
            return false;
        }

        ZoomedContent = ZoomedContent is not null && _comparer.Equals(ZoomedContent, ActiveContent)
            ? default
            : ActiveContent;
        return true;
    }

    public bool ToggleActiveSplitOrientation()
    {
        if (Root is null || ActiveContent is null)
        {
            return false;
        }

        var result = ToggleNearestSplit(Root, ActiveContent);
        Root = result.Node;
        return result.Toggled;
    }

    public IReadOnlyList<T> CloseOthers()
    {
        if (ActiveContent is null || Count <= 1)
        {
            return [];
        }

        var removed = Leaves()
            .Where(content => !_comparer.Equals(content, ActiveContent))
            .ToArray();
        Root = new PaneLeaf<T>(ActiveContent);
        ZoomedContent = null;
        return removed;
    }

    public IReadOnlyDictionary<T, PaneBounds> CalculateBounds()
    {
        var result = new Dictionary<T, PaneBounds>(_comparer);
        if (Root is not null)
        {
            AddBounds(Root, new PaneBounds(0, 0, 1, 1), result);
        }

        return result;
    }

    private PaneLeaf<T>? FindLeaf(T content) =>
        Root is null
            ? null
            : EnumerateLeaves(Root).FirstOrDefault(leaf => _comparer.Equals(leaf.Content, content));

    private PaneNode<T> ReplaceLeaf(PaneNode<T> node, T target, PaneNode<T> replacement) =>
        node switch
        {
            PaneLeaf<T> leaf when _comparer.Equals(leaf.Content, target) => replacement,
            PaneSplit<T> split => split with
            {
                First = ReplaceLeaf(split.First, target, replacement),
                Second = ReplaceLeaf(split.Second, target, replacement),
            },
            _ => node,
        };

    private PaneNode<T> SwapLeafContent(PaneNode<T> node, T first, T second) =>
        node switch
        {
            PaneLeaf<T> leaf when _comparer.Equals(leaf.Content, first) => new PaneLeaf<T>(second),
            PaneLeaf<T> leaf when _comparer.Equals(leaf.Content, second) => new PaneLeaf<T>(first),
            PaneSplit<T> split => split with
            {
                First = SwapLeafContent(split.First, first, second),
                Second = SwapLeafContent(split.Second, first, second),
            },
            _ => node,
        };

    private PaneNode<T>? RemoveLeaf(PaneNode<T> node, T target) =>
        node switch
        {
            PaneLeaf<T> leaf => _comparer.Equals(leaf.Content, target) ? null : leaf,
            PaneSplit<T> split => CollapseSplit(
                RemoveLeaf(split.First, target),
                RemoveLeaf(split.Second, target),
                split),
            _ => node,
        };

    private static PaneNode<T>? CollapseSplit(
        PaneNode<T>? first,
        PaneNode<T>? second,
        PaneSplit<T> original) =>
        (first, second) switch
        {
            (null, null) => null,
            (null, not null) => second,
            (not null, null) => first,
            _ => original with { First = first!, Second = second! },
        };

    private (PaneNode<T> Node, bool Resized) ResizeNearest(
        PaneNode<T> node,
        T target,
        PaneSplitOrientation orientation,
        PaneDirection direction,
        double amount)
    {
        if (node is not PaneSplit<T> split)
        {
            return (node, false);
        }

        var targetInFirst = Contains(split.First, target);
        var targetInSecond = Contains(split.Second, target);
        if (!targetInFirst && !targetInSecond)
        {
            return (node, false);
        }

        var child = targetInFirst ? split.First : split.Second;
        var childResult = ResizeNearest(child, target, orientation, direction, amount);
        if (childResult.Resized)
        {
            return targetInFirst
                ? (split with { First = childResult.Node }, true)
                : (split with { Second = childResult.Node }, true);
        }

        if (split.Orientation != orientation)
        {
            return (node, false);
        }

        var sign = direction is PaneDirection.Right or PaneDirection.Down ? 1 : -1;
        return (split with { Ratio = NormalizeRatio(split.Ratio + (amount * sign)) }, true);
    }

    private (PaneNode<T> Node, bool Replaced) ReplaceSplit(
        PaneNode<T> node,
        PaneSplit<T> target,
        double ratio)
    {
        if (ReferenceEquals(node, target))
        {
            return (target with { Ratio = ratio }, true);
        }

        if (node is not PaneSplit<T> split)
        {
            return (node, false);
        }

        var first = ReplaceSplit(split.First, target, ratio);
        if (first.Replaced)
        {
            return (split with { First = first.Node }, true);
        }

        var second = ReplaceSplit(split.Second, target, ratio);
        return second.Replaced
            ? (split with { Second = second.Node }, true)
            : (node, false);
    }

    private (PaneNode<T> Node, bool Toggled) ToggleNearestSplit(PaneNode<T> node, T target)
    {
        if (node is not PaneSplit<T> split)
        {
            return (node, false);
        }

        var targetInFirst = Contains(split.First, target);
        var child = targetInFirst ? split.First : split.Second;
        var childResult = ToggleNearestSplit(child, target);
        if (childResult.Toggled)
        {
            return targetInFirst
                ? (split with { First = childResult.Node }, true)
                : (split with { Second = childResult.Node }, true);
        }

        var orientation = split.Orientation == PaneSplitOrientation.Vertical
            ? PaneSplitOrientation.Horizontal
            : PaneSplitOrientation.Vertical;
        return (split with { Orientation = orientation }, true);
    }

    private T? FindSiblingLeaf(PaneNode<T> node, T target)
    {
        if (node is not PaneSplit<T> split)
        {
            return null;
        }

        if (Contains(split.First, target))
        {
            return FindSiblingLeaf(split.First, target) ??
                   EnumerateLeaves(split.Second).First().Content;
        }

        if (Contains(split.Second, target))
        {
            return FindSiblingLeaf(split.Second, target) ??
                   EnumerateLeaves(split.First).Last().Content;
        }

        return null;
    }

    private bool Contains(PaneNode<T> node, T target) =>
        EnumerateLeaves(node).Any(leaf => _comparer.Equals(leaf.Content, target));

    private int IndexOf(IReadOnlyList<T> items, T target)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (_comparer.Equals(items[index], target))
            {
                return index;
            }
        }

        return -1;
    }

    private static IEnumerable<PaneLeaf<T>> EnumerateLeaves(PaneNode<T> node)
    {
        if (node is PaneLeaf<T> leaf)
        {
            yield return leaf;
            yield break;
        }

        var split = (PaneSplit<T>)node;
        foreach (var item in EnumerateLeaves(split.First))
        {
            yield return item;
        }

        foreach (var item in EnumerateLeaves(split.Second))
        {
            yield return item;
        }
    }

    private static void AddBounds(PaneNode<T> node, PaneBounds bounds, IDictionary<T, PaneBounds> result)
    {
        if (node is PaneLeaf<T> leaf)
        {
            result[leaf.Content] = bounds;
            return;
        }

        var split = (PaneSplit<T>)node;
        if (split.Orientation == PaneSplitOrientation.Vertical)
        {
            var firstWidth = bounds.Width * split.Ratio;
            AddBounds(split.First, bounds with { Width = firstWidth }, result);
            AddBounds(
                split.Second,
                bounds with
                {
                    X = bounds.X + firstWidth,
                    Width = bounds.Width - firstWidth,
                },
                result);
        }
        else
        {
            var firstHeight = bounds.Height * split.Ratio;
            AddBounds(split.First, bounds with { Height = firstHeight }, result);
            AddBounds(
                split.Second,
                bounds with
                {
                    Y = bounds.Y + firstHeight,
                    Height = bounds.Height - firstHeight,
                },
                result);
        }
    }

    private static bool IsInDirection(PaneBounds source, PaneBounds target, PaneDirection direction) =>
        direction switch
        {
            PaneDirection.Left => target.CenterX < source.CenterX,
            PaneDirection.Right => target.CenterX > source.CenterX,
            PaneDirection.Up => target.CenterY < source.CenterY,
            PaneDirection.Down => target.CenterY > source.CenterY,
            _ => false,
        };

    private static double PrimaryDistance(PaneBounds source, PaneBounds target, PaneDirection direction) =>
        direction is PaneDirection.Left or PaneDirection.Right
            ? Math.Abs(source.CenterX - target.CenterX)
            : Math.Abs(source.CenterY - target.CenterY);

    private static double SecondaryDistance(PaneBounds source, PaneBounds target, PaneDirection direction) =>
        direction is PaneDirection.Left or PaneDirection.Right
            ? Math.Abs(source.CenterY - target.CenterY)
            : Math.Abs(source.CenterX - target.CenterX);

    private static bool OrthogonallyOverlaps(
        PaneBounds source,
        PaneBounds target,
        PaneDirection direction) =>
        direction is PaneDirection.Left or PaneDirection.Right
            ? Math.Min(source.Y + source.Height, target.Y + target.Height) > Math.Max(source.Y, target.Y)
            : Math.Min(source.X + source.Width, target.X + target.Width) > Math.Max(source.X, target.X);

    private static double NormalizeRatio(double ratio) =>
        Math.Clamp(double.IsFinite(ratio) ? ratio : 0.5, MinimumRatio, 1 - MinimumRatio);

    private static PaneNode<T> NormalizeNode(PaneNode<T> node) =>
        node switch
        {
            PaneLeaf<T> leaf => leaf,
            PaneSplit<T> split => split with
            {
                Ratio = NormalizeRatio(split.Ratio),
                First = NormalizeNode(split.First),
                Second = NormalizeNode(split.Second),
            },
            _ => throw new ArgumentException("Unknown pane node type.", nameof(node)),
        };

}
