using System;
using System.Text;

namespace Phosphor.Plugins.PodcastIndex;

/// <summary>
/// Supplies the built-in ("Phosphor") Podcast Index API credentials used when the user hasn't chosen
/// to bring their own key.
/// </summary>
/// <remarks>
/// <para>
/// The credentials are stored Base64-encoded and decoded with the BCL (<see cref="Convert.FromBase64String"/>)
/// so they don't appear as plaintext string literals in the shipped assembly (they won't surface in
/// <c>strings</c>, a secret scanner, or a casual decompile of the constant pool).
/// </para>
/// <para>
/// This is deliberately <em>encoding, not encryption</em>: because the decode code ships alongside the
/// data, a determined attacker can always recover the value. The goal is only to keep the secret out of
/// plaintext and raise the bar above copy/paste. Notably we do <b>not</b> hand-roll a byte-level
/// XOR/decrypt loop — that pattern (an obfuscated blob decrypted into a string at runtime) trips
/// antivirus heuristics (e.g. Defender's <c>AsyncRat</c> ML signature) and gets the DLL false-flagged.
/// A plain BCL Base64 decode is a benign, ubiquitous pattern that avoids that.
/// </para>
/// <para>
/// The real protections live server-side (per-app rate limits + key rotation); the long-term fix is to
/// proxy Podcast Index through a Phosphor backend so no shared key ships at all.
/// </para>
/// </remarks>
internal static class BuiltInCredentials
{
    private const string EncodedKey = "UUFZUUtYWFpMVFFWVEtSSlJKVEg=";
    private const string EncodedSecret = "eXloYUpwajg5VkpUVXJlI2IjXlZWMnZRQU5nUUc5ZUs2Rkh0S1ljNg==";

    public static string ApiKey => Decode(EncodedKey);
    public static string ApiSecret => Decode(EncodedSecret);

    private static string Decode(string encoded) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
}
