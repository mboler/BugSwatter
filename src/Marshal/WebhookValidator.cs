using System.Security.Cryptography;
using System.Text;

namespace Marshal;

/// <summary>Validates incoming webhook authenticity. Validation is mandatory; a request that fails is rejected regardless of how it reached Marshal</summary>
public static class WebhookValidator
{
    /// <summary>Validates GitHub's X-Hub-Signature-256 header: sha256=&lt;hex HMAC-SHA256 of the raw body keyed with the shared secret&gt;</summary>
    /// <param name="body">Raw request bytes exactly as received; the HMAC is computed over these, so any transformation would break validation</param>
    /// <param name="signatureHeader">Raw header value exactly as received; null when the request carried none, which fails validation</param>
    /// <param name="secret">The resolved shared secret, keyed into the HMAC</param>
    public static bool ValidateGitHubSignature(byte[] body, string? signatureHeader, string secret)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(secret);

        const string Prefix = "sha256=";

        if (signatureHeader is null || !signatureHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(signatureHeader[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    /// <summary>Validates Azure DevOps service-hook basic authentication: the password portion of the Authorization header must match the shared secret</summary>
    /// <param name="authorizationHeader">Raw Authorization header exactly as received; null when the request carried none, which fails validation</param>
    public static bool ValidateBasicAuthorization(string? authorizationHeader, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        const string Prefix = "Basic ";
        
        if (authorizationHeader is null || !authorizationHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorizationHeader[Prefix.Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        // Credentials arrive as user:password; the user part is free-form and only the password is the shared secret
        int separator = decoded.IndexOf(':');
        string password = separator < 0 ? decoded : decoded[(separator + 1)..];
        
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(secret));
    }
}
