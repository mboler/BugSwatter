namespace BugSwatter.Email.Tests;

public sealed class EmailContractsTests
{
    [Fact]
    public void MessageSnapshotsRecipientsAndAttachments()
    {
        string[] recipients = ["first@example.test"];
        EmailFileAttachment[] attachments = [new("report.md", "text/markdown")];

        var message = new EmailMessage("sender@example.test", recipients, "subject", "body", attachments);
        recipients[0] = "changed@example.test";
        attachments[0] = new EmailFileAttachment("changed.txt", "text/plain");

        Assert.Equal(["first@example.test"], message.Recipients);
        EmailFileAttachment attachment = Assert.Single(message.Attachments);
        Assert.Equal("report.md", attachment.Path);
        Assert.Equal("text/markdown", attachment.ContentType);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void SenderAddressIsRequired(string from)
    {
        Assert.Throws<ArgumentException>(() => new EmailMessage(from, ["recipient@example.test"], "subject", "body"));
    }

    [Fact]
    public void AtLeastOneNonEmptyRecipientIsRequired()
    {
        Assert.Throws<ArgumentException>(() => new EmailMessage("sender@example.test", [], "subject", "body"));
        Assert.Throws<ArgumentException>(() => new EmailMessage("sender@example.test", [""], "subject", "body"));
    }

    [Fact]
    public void AttachmentRequiresPathAndContentType()
    {
        Assert.Throws<ArgumentException>(() => new EmailFileAttachment("", "text/plain"));
        Assert.Throws<ArgumentException>(() => new EmailFileAttachment("report.txt", ""));
    }

    [Fact]
    public void SmtpOptionsSupportAuthenticatedAndAnonymousRelays()
    {
        var authenticated = new SmtpEmailOptions("smtp.example.test", 587, true, "user", "resolved-test-value");
        var anonymous = new SmtpEmailOptions("relay.example.test", 25, false);

        Assert.True(authenticated.RequiresAuthentication);
        Assert.Equal("resolved-test-value", authenticated.Password);
        Assert.False(anonymous.RequiresAuthentication);
        Assert.False(anonymous.UseStartTls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void InvalidSmtpPortIsRejected(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SmtpEmailOptions("smtp.example.test", port, true));
    }

    [Fact]
    public void AuthenticatedSmtpRequiresPassword()
    {
        Assert.Throws<ArgumentException>(() => new SmtpEmailOptions("smtp.example.test", 587, true, "user"));
    }

    [Fact]
    public void TransportConstructorsValidateTheirConfiguration()
    {
        Assert.IsAssignableFrom<IEmailSender>(new SmtpEmailSender(new SmtpEmailOptions("smtp.example.test", 25, false)));
        Assert.IsAssignableFrom<IEmailSender>(new AcsEmailSender("test-connection-value"));
        Assert.Throws<ArgumentException>(() => new AcsEmailSender(""));
    }
}
