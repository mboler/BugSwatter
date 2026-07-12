namespace BugSwatter.Common;

/// <summary>Reads an untrusted stream without allowing it to consume unbounded memory</summary>
public static class BoundedStreamReader
{
    private const int BufferSize = 16 * 1024;

    /// <summary>Reads the stream through its end when it fits within <paramref name="maxBytes"/></summary>
    /// <returns>The complete bytes, or null when the stream exceeds the limit</returns>
    public static async Task<byte[]?> ReadAsync(Stream input, int maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        using var output = new MemoryStream(Math.Min(maxBytes, BufferSize));
        byte[] buffer = new byte[(int)Math.Min((long)maxBytes + 1, BufferSize)];

        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maxBytes)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
