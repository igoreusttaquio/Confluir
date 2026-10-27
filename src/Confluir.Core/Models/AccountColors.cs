namespace Confluir.Core.Models;

public sealed record AccountColor(string Key, string Hex);

/// <summary>Cores que o usuário pode atribuir a uma conta.</summary>
public static class AccountColors
{
    public static readonly AccountColor Blue = new("Blue", "#3D6BFF");
    public static readonly AccountColor Coral = new("Coral", "#FF7A66");
    public static readonly AccountColor Teal = new("Teal", "#14C7B4");
    public static readonly AccountColor Amber = new("Amber", "#FFB938");
    public static readonly AccountColor Violet = new("Violet", "#8B5CF6");
    public static readonly AccountColor Pink = new("Pink", "#EC4899");
    public static readonly AccountColor Green = new("Green", "#22C55E");
    public static readonly AccountColor Cyan = new("Cyan", "#06B6D4");

    public static IReadOnlyList<AccountColor> All { get; } =
        [Blue, Coral, Teal, Amber, Violet, Pink, Green, Cyan];
}
