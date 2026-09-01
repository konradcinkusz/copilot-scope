using System.Security.Cryptography;
using System.Text;

namespace CopilotScope.Collector.Privacy;

/// <summary>
/// Turns an identifying value into a stable, non-reversible token.
///
/// Salted HMAC-SHA256 rather than a bare hash: the identifier space here is tiny and
/// guessable (hostnames, usernames, email addresses at one company), so an unsalted digest
/// is reversible by anyone willing to hash a wordlist — which is not pseudonymization in
/// any sense GDPR Art. 4(5) would recognize. With a secret salt the mapping cannot be
/// rebuilt from the stored data alone, which is the whole requirement: re-identification
/// must need "additional information kept separately".
///
/// The token keeps equality, and only equality. Two signals from one machine still resolve
/// to one subject — that is what session correlation and the k-anonymity count both need —
/// while nothing in the token says which machine.
/// </summary>
public sealed class Pseudonymizer
{
    private readonly byte[] _salt;

    /// <summary>True when no salt was configured and one was generated for this process.
    /// Pseudonyms then change on every restart: history stops correlating across a deploy,
    /// so the operator has to be told rather than left to discover it.</summary>
    public bool SaltIsEphemeral { get; }

    public Pseudonymizer(string? salt)
    {
        if (string.IsNullOrEmpty(salt))
        {
            _salt = RandomNumberGenerator.GetBytes(32);
            SaltIsEphemeral = true;
        }
        else
        {
            _salt = Encoding.UTF8.GetBytes(salt);
        }
    }

    /// <summary>
    /// Tokenizes one value. The prefix keeps a pseudonymized field legible in the UI and in
    /// an export — a works council reviewing a screenshot can see at a glance that the
    /// column holds tokens, not names.
    /// </summary>
    public string Token(string value) => Token("anon", value);

    /// <summary>Tokenizes with a caller-chosen prefix, so a host token and a user token
    /// stay distinguishable in the data map without either being reversible.</summary>
    public string Token(string prefix, string value)
    {
        using var hmac = new HMACSHA256(_salt);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        // 10 bytes ≈ 80 bits: far past any collision concern at team scale, short enough
        // that the token reads as a label rather than as a wall of hex.
        return $"{prefix}-{Convert.ToHexString(digest.AsSpan(0, 10)).ToLowerInvariant()}";
    }
}
