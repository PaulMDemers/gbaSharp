namespace Gba.Desktop;

internal sealed record DesktopScreenshotOptions(
    string Overlay = "",
    int Scale = 4,
    int LensTiles = 9,
    string AtlasPath = "")
{
    public bool IsLens
        => Overlay.Equals("center-lens", StringComparison.OrdinalIgnoreCase)
        || Overlay.Equals("lens", StringComparison.OrdinalIgnoreCase)
        || Overlay.Equals("coordinate-lens", StringComparison.OrdinalIgnoreCase)
        || Overlay.Equals("atlas-lens", StringComparison.OrdinalIgnoreCase)
        || Overlay.Equals("atlas-coordinate-lens", StringComparison.OrdinalIgnoreCase);

    public bool HasDenseCoordinates
        => Overlay.Equals("coordinate-lens", StringComparison.OrdinalIgnoreCase)
        || Overlay.Equals("atlas-coordinate-lens", StringComparison.OrdinalIgnoreCase);

    public bool HasMovementGrid
        => Overlay.Equals("movement-grid", StringComparison.OrdinalIgnoreCase)
        || Overlay.Equals("grid", StringComparison.OrdinalIgnoreCase)
        || IsLens;

    public bool HasAtlas
        => Overlay.Equals("atlas-grid", StringComparison.OrdinalIgnoreCase)
        || Overlay.Equals("atlas-lens", StringComparison.OrdinalIgnoreCase)
        || Overlay.Equals("atlas-coordinate-lens", StringComparison.OrdinalIgnoreCase);
}
