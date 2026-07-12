using System.Globalization;
using System.Text;
using BugSwatter.Common;

namespace Informant;

/// <summary>One bounded line-range read, retaining only the requested lines while counting the whole file</summary>
public sealed record RepositoryLineRange(IReadOnlyList<(int Number, string Text)> Lines, int TotalLines, int EffectiveEndLine, bool Capped);

/// <summary>Reads text files through a repository path resolver while enforcing byte and binary-data limits</summary>
public sealed class RepositoryFileReader
{
    /// <summary>Default source-file limit of 10 MiB</summary>
    public const int DefaultMaxFileBytes = 10 * 1024 * 1024;

    private const int BinaryProbeBytes = 8192;

    private readonly RepositoryPathResolver _resolver;
    private readonly int _maxFileBytes;

    /// <summary>Creates a bounded reader over the supplied repository root</summary>
    public RepositoryFileReader(string root, int maxFileBytes = DefaultMaxFileBytes) : this(new RepositoryPathResolver(root), maxFileBytes)
    {
    }

    /// <summary>Creates a bounded reader over an existing resolver</summary>
    public RepositoryFileReader(RepositoryPathResolver resolver, int maxFileBytes = DefaultMaxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);

        _resolver = resolver;
        _maxFileBytes = maxFileBytes;
    }

    /// <summary>Absolute repository root</summary>
    public string Root => _resolver.Root;

    /// <summary>Reads every line from a bounded text file</summary>
    public async Task<string[]> ReadAllLinesAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        await using FileStream file = OpenFile(relativePath);
        await EnsureTextAsync(file, relativePath, cancellationToken);
        using var reader = CreateReader(file, relativePath);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            RejectBinaryLine(line, relativePath);
            lines.Add(line);
        }

        return [.. lines];
    }

    /// <summary>Reads every line from a bounded text file</summary>
    public string[] ReadAllLines(string relativePath)
    {
        var lines = new List<string>();
        using FileStream file = OpenFile(relativePath);
        EnsureText(file, relativePath);
        using var reader = CreateReader(file, relativePath);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            RejectBinaryLine(line, relativePath);
            lines.Add(line);
        }

        return [.. lines];
    }

    /// <summary>Streams a line range, retaining at most <paramref name="maxLines"/> while counting total lines</summary>
    public RepositoryLineRange ReadLines(string relativePath, int startLine, int endLine, int maxLines)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startLine);
        ArgumentOutOfRangeException.ThrowIfLessThan(endLine, startLine);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);

        using FileStream file = OpenFile(relativePath);
        EnsureText(file, relativePath);
        using var reader = CreateReader(file, relativePath);
        var selected = new List<(int Number, string Text)>();
        int requestedEnd = (int)Math.Min(endLine, (long)startLine + maxLines - 1);
        int lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            RejectBinaryLine(line, relativePath);
            if (lineNumber >= startLine && lineNumber <= requestedEnd)
            {
                selected.Add((lineNumber, line));
            }
        }

        return new RepositoryLineRange(selected, lineNumber, Math.Min(requestedEnd, lineNumber), endLine > requestedEnd);
    }

    /// <summary>Reads a bounded file from an immutable Git tree object, used when the file no longer exists in the working tree</summary>
    public static async Task<string[]> ReadGitBlobLinesAsync(GitRunner git, string treePath, string revision, string path, int maxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(treePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);

        string objectName = $"{revision}:{path}";
        GitResult sizeResult = await git.RunAsync("-C", treePath, "cat-file", "-s", objectName);
        if (sizeResult.ExitCode != 0 || !long.TryParse(sizeResult.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long size))
        {
            throw new RepositoryFileException(RepositoryFileError.ReadFailed, $"could not read deleted file '{path}' from baseline {revision}");
        }

        if (size > maxFileBytes)
        {
            throw new RepositoryFileException(RepositoryFileError.TooLarge, $"'{path}' exceeds maxFileBytes limit of {maxFileBytes} bytes in baseline {revision}");
        }

        GitResult contentResult = await git.RunAsync("-C", treePath, "cat-file", "blob", objectName);
        if (contentResult.ExitCode != 0)
        {
            throw new RepositoryFileException(RepositoryFileError.ReadFailed, $"could not read deleted file '{path}' from baseline {revision}");
        }

        if (Encoding.UTF8.GetByteCount(contentResult.StandardOutput) > maxFileBytes)
        {
            throw new RepositoryFileException(RepositoryFileError.TooLarge, $"'{path}' grew beyond maxFileBytes limit of {maxFileBytes} bytes while reading baseline {revision}");
        }

        if (contentResult.StandardOutput.Contains('\0'))
        {
            throw new RepositoryFileException(RepositoryFileError.Binary, $"'{path}' is a binary file in baseline {revision}");
        }

        var lines = new List<string>();
        using var reader = new StringReader(contentResult.StandardOutput);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    private FileStream OpenFile(string relativePath)
    {
        try
        {
            // Resolution is deliberately the final operation before opening so a replaced component gets rechecked as late as practical.
            string fullPath = _resolver.ResolveFile(relativePath);
            var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            if (file.Length > _maxFileBytes)
            {
                file.Dispose();
                throw TooLarge(relativePath);
            }

            return file;
        }
        catch (RepositoryFileException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RepositoryFileException(RepositoryFileError.ReadFailed, $"could not read '{relativePath}': {ex.Message}", ex);
        }
    }

    private StreamReader CreateReader(FileStream file, string relativePath) => new(new ReadLimitStream(file, _maxFileBytes, () => TooLarge(relativePath)), Encoding.UTF8, true, 4096);

    private static void EnsureText(FileStream file, string relativePath)
    {
        Span<byte> probe = stackalloc byte[BinaryProbeBytes];
        int read = file.Read(probe);
        file.Position = 0;
        if (probe[..read].Contains((byte)0))
        {
            throw new RepositoryFileException(RepositoryFileError.Binary, $"'{relativePath}' appears to be a binary file");
        }
    }

    private static async Task EnsureTextAsync(FileStream file, string relativePath, CancellationToken cancellationToken)
    {
        byte[] probe = new byte[BinaryProbeBytes];
        int read = await file.ReadAsync(probe, cancellationToken);
        file.Position = 0;
        if (probe.AsSpan(0, read).Contains((byte)0))
        {
            throw new RepositoryFileException(RepositoryFileError.Binary, $"'{relativePath}' appears to be a binary file");
        }
    }

    private static void RejectBinaryLine(string line, string relativePath)
    {
        if (line.Contains('\0'))
        {
            throw new RepositoryFileException(RepositoryFileError.Binary, $"'{relativePath}' appears to be a binary file");
        }
    }

    private RepositoryFileException TooLarge(string relativePath) =>
        new(RepositoryFileError.TooLarge, $"'{relativePath}' exceeds maxFileBytes limit of {_maxFileBytes} bytes");

    private sealed class ReadLimitStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private readonly Func<RepositoryFileException> _exceptionFactory;
        private long _bytesRead;

        public ReadLimitStream(Stream inner, long limit, Func<RepositoryFileException> exceptionFactory)
        {
            _inner = inner;
            _limit = limit;
            _exceptionFactory = exceptionFactory;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read = _inner.Read(Limit(buffer));
            Count(read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(Limit(buffer), cancellationToken);
            Count(read);
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private Span<byte> Limit(Span<byte> buffer)
        {
            ThrowIfExceeded();
            int allowed = (int)Math.Min(buffer.Length, _limit - _bytesRead + 1);
            return buffer[..allowed];
        }

        private Memory<byte> Limit(Memory<byte> buffer)
        {
            ThrowIfExceeded();
            int allowed = (int)Math.Min(buffer.Length, _limit - _bytesRead + 1);
            return buffer[..allowed];
        }

        private void Count(int read)
        {
            _bytesRead += read;
            ThrowIfExceeded();
        }

        private void ThrowIfExceeded()
        {
            if (_bytesRead > _limit)
            {
                throw _exceptionFactory();
            }
        }
    }
}
