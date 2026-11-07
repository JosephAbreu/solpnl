using System.Security.Cryptography;
using System.Text;

namespace SolPnl.Core.Setup;

/// <summary>
/// One-time setup code that gates the setup wizard while SolPnl has no storage configured. The code is printed to
/// the service log, so only someone who can read the log of this machine can complete setup.
/// </summary>
public sealed class SetupState
{
    /// <summary>After this many wrong codes, setup is refused until SolPnl restarts (which issues a new code).</summary>
    public const int MaxFailedAttempts = 10;

    // Unambiguous characters (no 0/O, 1/I/L).
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly byte[] _code;
    private int _failedAttempts;

    public SetupState(string? code = null)
    {
        Code = code ?? Generate();
        _code = Encoding.UTF8.GetBytes(Normalize(Code));
    }

    /// <summary>Formatted as XXXX-XXXX.</summary>
    public string Code { get; }

    public bool IsLockedOut => Volatile.Read(ref _failedAttempts) >= MaxFailedAttempts;

    /// <summary>Constant-time comparison; ignores case, spaces and dashes. Counts failures.</summary>
    public bool Verify(string? candidate)
    {
        if (IsLockedOut) return false;
        var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Normalize(candidate ?? "")), _code);
        if (!ok) Interlocked.Increment(ref _failedAttempts);
        return ok;
    }

    private static string Normalize(string code) =>
        new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Generate()
    {
        var chars = Enumerable.Range(0, 8).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray();
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
    }
}
