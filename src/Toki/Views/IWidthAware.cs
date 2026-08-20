namespace Toki.Views;

/// <summary>
/// A view whose card WIDTH depends on its content, overriding the fixed per-state width in
/// <c>PillWindow.Sizes</c>. After changing it, call <c>PillWindow.GrowToContent()</c> to morph.
/// </summary>
public interface IWidthAware
{
    double CardWidth { get; }
}
