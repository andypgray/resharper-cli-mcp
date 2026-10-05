using System.Buffers.Binary;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Reads a PNG's pixel dimensions out of its header, so a test can hold a declared <c>sizes</c> entry
///     to the render it actually names.
/// </summary>
/// <remarks>
///     Every icon channel states a size next to a path, and a copied entry with an unchanged size is the drift
///     that no build catches and every client renders wrongly.
/// </remarks>
internal static class PngRender
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    ///     The render's size in the <c>WxH</c> form the MCP icons schema specifies, read from the IHDR
    ///     chunk that a PNG is required to open with.
    /// </summary>
    /// <exception cref="ArgumentException">The bytes are not a PNG.</exception>
    public static string SizeOf(byte[] png)
    {
        // The 8-byte signature, then IHDR's 4-byte length and 4-byte type: the width sits at byte 16 and the
        // height at byte 20, so 24 bytes is the least that holds both.
        bool isPng = png.Length >= 24 && png.AsSpan(0, Signature.Length).SequenceEqual(Signature);
        if (!isPng)
            throw new ArgumentException("Not a PNG: the 8-byte signature and IHDR header are required.", nameof(png));

        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));

        return $"{width}x{height}";
    }
}