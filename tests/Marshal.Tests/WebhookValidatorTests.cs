using System.Security.Cryptography;
using System.Text;

namespace Marshal.Tests;

public sealed class WebhookValidatorTests
{
    private const string Secret = "test-webhook-secret";

    [Fact]
    public void GitHubValidSignaturePasses()
    {
        byte[] body = Encoding.UTF8.GetBytes("""{"repository": {"full_name": "mboler/SlimShady"}}""");
        string header = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body)).ToLowerInvariant();

        Assert.True(WebhookValidator.ValidateGitHubSignature(body, header, Secret));
    }

    [Fact]
    public void GitHubTamperedBodyFails()
    {
        byte[] body = Encoding.UTF8.GetBytes("""{"repository": {"full_name": "mboler/SlimShady"}}""");
        string header = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body)).ToLowerInvariant();
        byte[] tampered = Encoding.UTF8.GetBytes("""{"repository": {"full_name": "attacker/evil"}}""");

        Assert.False(WebhookValidator.ValidateGitHubSignature(tampered, header, Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=nothex!!")]
    [InlineData("sha1=abcdef")]
    public void GitHubMissingOrMalformedHeaderFails(string? header)
    {
        Assert.False(WebhookValidator.ValidateGitHubSignature([1, 2, 3], header, Secret));
    }

    [Fact]
    public void GitHubWrongSecretFails()
    {
        byte[] body = Encoding.UTF8.GetBytes("payload");
        string header = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("other-secret"), body)).ToLowerInvariant();

        Assert.False(WebhookValidator.ValidateGitHubSignature(body, header, Secret));
    }

    [Fact]
    public void BasicAuthorizationValidPasswordPasses()
    {
        string header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"marshal:{Secret}"));
        Assert.True(WebhookValidator.ValidateBasicAuthorization(header, Secret));
    }

    [Fact]
    public void BasicAuthorizationWithoutUserStillValidatesPassword()
    {
        string header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret));
        Assert.True(WebhookValidator.ValidateBasicAuthorization(header, Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer abc")]
    [InlineData("Basic !!!notbase64!!!")]
    public void BasicAuthorizationMissingOrMalformedFails(string? header)
    {
        Assert.False(WebhookValidator.ValidateBasicAuthorization(header, Secret));
    }

    [Fact]
    public void BasicAuthorizationWrongPasswordFails()
    {
        string header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("marshal:wrong"));
        Assert.False(WebhookValidator.ValidateBasicAuthorization(header, Secret));
    }
}
