using System;
using System.Security.Cryptography;
using System.Text;

namespace CompareManager.Classes
{
    /// <summary>
    /// Keeps saved SQL passwords out of clear text, with Windows DPAPI under the signed-in
    /// user — the same scheme as Issue Manager's <c>SecretStore</c>, under an entropy of its
    /// own. As strong as the Windows login and no stronger: it stops a password being read
    /// out of a file, it is not a vault.
    ///
    /// It can also read Issue Manager's own saved passwords (<see cref="RevealIssueManager"/>),
    /// which is what lets Compare Manager offer that app's SQL connections without anyone
    /// typing a password twice. That works only for the same Windows user, which is the point.
    /// </summary>
    public static class Secret
    {
        private const string Marker = "enc:v1:";

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Symplexity.CompareManager.Settings.v1");

        /// <summary>Issue Manager's entropy (<c>SecretStore.Entropy</c>); read only, never written with.</summary>
        private static readonly byte[] IssueManagerEntropy = Encoding.UTF8.GetBytes("Symplexity.IssueManager.Settings.v1");

        /// <summary>Encrypts a password for storage. Empty stays empty; a failure stores nothing rather than clear text.</summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
                return Marker + Convert.ToBase64String(blob);
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Reads a stored password back; empty when it cannot be decrypted (another user, another machine).</summary>
        public static string Reveal(string stored) => Reveal(stored, Entropy);

        /// <summary>Reads a password Issue Manager saved. Values without the marker are its older clear-text saves.</summary>
        public static string RevealIssueManager(string stored) => Reveal(stored, IssueManagerEntropy);

        private static string Reveal(string stored, byte[] entropy)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith(Marker, StringComparison.Ordinal)) return stored;

            try
            {
                byte[] blob = Convert.FromBase64String(stored.Substring(Marker.Length));
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser));
            }
            catch
            {
                return "";
            }
        }
    }
}
