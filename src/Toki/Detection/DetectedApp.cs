namespace Toki.Detection;

/// <summary>An app currently holding the microphone open.</summary>
/// <param name="Id">Stable identity (decoded exe path, or packaged AUMID).</param>
/// <param name="DisplayName">Human-friendly name (exe file name, or AUMID).</param>
/// <param name="Packaged">True for Store/packaged apps, false for classic exes.</param>
public sealed record DetectedApp(string Id, string DisplayName, bool Packaged);
