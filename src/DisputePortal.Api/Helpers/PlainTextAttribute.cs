using System.ComponentModel.DataAnnotations;

namespace DisputePortal.Api.Helpers;

/// <summary>
/// Free text must be text: no control characters (NUL, escape, backspace...) and no bidi
/// overrides, which make stored text display differently from what it says. Mirrored by
/// <c>plainText</c> in web/src/lib/disputeRules.ts.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PlainTextAttribute : ValidationAttribute
{
    /// <summary>Allow <c>\n</c>, <c>\r</c> and <c>\t</c>, for multi-line fields.</summary>
    public bool AllowLineBreaks { get; init; }

    // The value is never part of the message: it may hold personal information.
    public PlainTextAttribute() : base("{0} contains characters that aren't allowed.") { }

    public override bool IsValid(object? value) =>
        value is not string text || !text.Any(c => IsDisallowed(c, AllowLineBreaks));

    public static bool IsDisallowed(char c, bool allowLineBreaks) =>
        (char.IsControl(c) && !(allowLineBreaks && c is '\n' or '\r' or '\t'))
        || c is >= '‪' and <= '‮'
        || c is >= '⁦' and <= '⁩';
}
