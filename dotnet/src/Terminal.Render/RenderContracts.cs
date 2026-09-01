namespace Microsoft.Terminal.Render;

public readonly record struct CellSize(double Width, double Height);

public readonly record struct RenderViewport(int Columns, int Rows, double Scale);

public interface ITerminalRenderer
{
    void Resize(RenderViewport viewport);

    void Invalidate();
}
