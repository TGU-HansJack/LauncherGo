using PdfSharp.Fonts;

namespace LauncherGo.Services;

internal sealed class WindowsPdfFontResolver : IFontResolver
{
    internal const string FamilyName = "LauncherGo CJK";
    private const string RegularFace = "LauncherGo-CJK-Regular";
    private const string BoldFace = "LauncherGo-CJK-Bold";
    private static readonly string FontsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "Fonts");
    private static readonly string? RegularFontPath = FindFont("Deng.ttf", "simhei.ttf");
    private static readonly string? BoldFontPath = FindFont("Dengb.ttf", "simhei.ttf", "Deng.ttf");

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        if (!familyName.Equals(FamilyName, StringComparison.OrdinalIgnoreCase))
            return PlatformFontResolver.ResolveTypeface(familyName, bold, italic);

        var fontPath = bold ? BoldFontPath : RegularFontPath;
        if (fontPath is null)
            return PlatformFontResolver.ResolveTypeface("Arial", bold, italic);

        return new FontResolverInfo(bold ? BoldFace : RegularFace, false, italic);
    }

    public byte[]? GetFont(string faceName)
    {
        return faceName switch
        {
            RegularFace when RegularFontPath is not null => File.ReadAllBytes(RegularFontPath),
            BoldFace when BoldFontPath is not null => File.ReadAllBytes(BoldFontPath),
            _ => null
        };
    }

    private static string? FindFont(params string[] fileNames)
    {
        return fileNames
            .Select(fileName => Path.Combine(FontsDirectory, fileName))
            .FirstOrDefault(File.Exists);
    }
}
