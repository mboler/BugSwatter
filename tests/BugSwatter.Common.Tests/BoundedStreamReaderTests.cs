namespace BugSwatter.Common.Tests;

public sealed class BoundedStreamReaderTests
{
    [Fact]
    public async Task StreamWithinLimitIsReadCompletely()
    {
        byte[] payload = new byte[64 * 1024];
        new Random(7).NextBytes(payload);
        using var input = new MemoryStream(payload);

        byte[]? result = await BoundedStreamReader.ReadAsync(input, 1024 * 1024);

        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task StreamBeyondLimitIsRejected()
    {
        using var input = new MemoryStream(new byte[1025]);
        Assert.Null(await BoundedStreamReader.ReadAsync(input, 1024));
    }

    [Fact]
    public async Task StreamExactlyAtLimitIsAccepted()
    {
        using var input = new MemoryStream(new byte[1024]);
        Assert.Equal(1024, (await BoundedStreamReader.ReadAsync(input, 1024))!.Length);
    }

    [Fact]
    public async Task NonPositiveLimitIsRejected()
    {
        using var input = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BoundedStreamReader.ReadAsync(input, 0));
    }
}
