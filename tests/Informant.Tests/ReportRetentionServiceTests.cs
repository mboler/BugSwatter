using Microsoft.Extensions.Time.Testing;

namespace Informant.Tests;

public sealed class ReportRetentionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _directory = new();
    private readonly FakeTimeProvider _timeProvider = new(Now);

    public void Dispose() => _directory.Dispose();

    public static TheoryData<string> ManagedArtifactNames => new()
    {
        "Informant-Report-2026-06-01_01-02-03.md",
        "Informant-Report-2026-06-01_01-02-03-validated.md",
        "Informant-Report-2026-06-01_01-02-03-validated.json",
        "Informant-Changes-2026-06-01_01-02-03.json",
        "Informant-Manifest-2026-06-01_01-02-03.json"
    };

    public static TheoryData<string> UnmanagedArtifactNames => new()
    {
        "informant-report-2026-06-01_01-02-03.md",
        "Informant-Report-2026-06-01_01-02-03.json",
        "Informant-Report-2026-06-01_01-02-03.md.bak",
        "Informant-Report-2026-99-99_01-02-03.md",
        "Informant-Report-latest.md",
        "Informant-Changes-2026-06-01_01-02-03.md",
        "Informant-Changes-2026-06-01_01-02-03-validated.json",
        "Informant-Manifest-2026-06-01_01-02-03.md",
        "Informant-Manifest-2026-06-01_01-02-03.json.bak",
        "informant.state.json",
        "informant-.log",
        "notes.md"
    };

    [Theory]
    [MemberData(nameof(ManagedArtifactNames))]
    public void DeletesEveryManagedArtifactOlderThanRetention(string fileName)
    {
        string path = WriteFile(fileName, Now.AddDays(-31).AddTicks(-1));

        ReportRetentionResult result = CreateService(31).DeleteExpired();

        Assert.False(File.Exists(path));
        Assert.Equal(new ReportRetentionResult(1, 0), result);
    }

    [Theory]
    [MemberData(nameof(ManagedArtifactNames))]
    public void RetainsEveryManagedArtifactInsideRetention(string fileName)
    {
        string path = WriteFile(fileName, Now.AddDays(-30));

        ReportRetentionResult result = CreateService(31).DeleteExpired();

        Assert.True(File.Exists(path));
        Assert.Equal(new ReportRetentionResult(0, 0), result);
    }

    [Fact]
    public void RetainsArtifactExactlyAtCutoff()
    {
        string path = WriteFile("Informant-Report-2026-06-01_01-02-03.md", Now.AddDays(-31));

        CreateService(31).DeleteExpired();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DeletesArtifactOneTickBeforeCutoff()
    {
        string path = WriteFile("Informant-Report-2026-06-01_01-02-03.md", Now.AddDays(-31).AddTicks(-1));

        CreateService(31).DeleteExpired();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AdvancingInjectedClockMakesArtifactExpire()
    {
        string path = WriteFile("Informant-Report-2026-06-01_01-02-03.md", Now.AddDays(-30).AddHours(-12));
        ReportRetentionService service = CreateService(31);

        Assert.Equal(new ReportRetentionResult(0, 0), service.DeleteExpired());
        Assert.True(File.Exists(path));

        _timeProvider.Advance(TimeSpan.FromDays(1));

        Assert.Equal(new ReportRetentionResult(1, 0), service.DeleteExpired());
        Assert.False(File.Exists(path));
    }

    [Theory]
    [MemberData(nameof(UnmanagedArtifactNames))]
    public void NeverDeletesUnmanagedOrLookalikeFile(string fileName)
    {
        string path = WriteFile(fileName, Now.AddYears(-10));

        ReportRetentionResult result = CreateService(31).DeleteExpired();

        Assert.True(File.Exists(path));
        Assert.Equal(new ReportRetentionResult(0, 0), result);
    }

    [Fact]
    public void KeepForeverDoesNotDeleteAncientManagedArtifact()
    {
        string path = WriteFile("Informant-Report-2020-01-01_00-00-00.md", Now.AddYears(-6));

        ReportRetentionResult result = CreateService(-1).DeleteExpired();

        Assert.True(File.Exists(path));
        Assert.Equal(new ReportRetentionResult(0, 0), result);
    }

    [Fact]
    public void ExtremelyLargeRetentionDoesNotOverflowOrDelete()
    {
        string path = WriteFile("Informant-Report-2020-01-01_00-00-00.md", Now.AddYears(-6));

        ReportRetentionResult result = CreateService(int.MaxValue).DeleteExpired();

        Assert.True(File.Exists(path));
        Assert.Equal(new ReportRetentionResult(0, 0), result);
    }

    [Fact]
    public void MissingReportDirectoryIsANoOp()
    {
        string missing = Path.Combine(_directory.Path, "missing");
        var service = new ReportRetentionService(missing, 31, _timeProvider);

        Assert.Equal(new ReportRetentionResult(0, 0), service.DeleteExpired());
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void DoesNotTraverseSubdirectories()
    {
        string nested = Path.Combine(_directory.Path, "nested");
        Directory.CreateDirectory(nested);
        string path = Path.Combine(nested, "Informant-Report-2020-01-01_00-00-00.md");
        File.WriteAllText(path, "nested");
        File.SetLastWriteTimeUtc(path, Now.AddYears(-6).UtcDateTime);

        ReportRetentionResult result = CreateService(31).DeleteExpired();

        Assert.True(File.Exists(path));
        Assert.Equal(new ReportRetentionResult(0, 0), result);
    }

    [Fact]
    public void FutureTimestampDoesNotMatterWhenFileIsOld()
    {
        string path = WriteFile("Informant-Report-2099-01-01_00-00-00.md", Now.AddYears(-1));

        CreateService(31).DeleteExpired();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void OldTimestampDoesNotMatterWhenFileIsFresh()
    {
        string path = WriteFile("Informant-Report-2020-01-01_00-00-00.md", Now);

        CreateService(31).DeleteExpired();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RejectsManagedArtifactSymbolicLink()
    {
        using var outside = new TempDirectory();
        string target = Path.Combine(outside.Path, "outside.md");
        string link = Path.Combine(_directory.Path, "Informant-Report-2020-01-01_00-00-00.md");
        File.WriteAllText(target, "outside");
        File.SetLastWriteTimeUtc(target, Now.AddYears(-6).UtcDateTime);
        bool created = TryCreateFileSymbolicLink(link, target);
        Assert.SkipUnless(created, "symbolic links are not available on this test host");
        string regular = WriteFile("Informant-Changes-2020-01-01_00-00-00.json", Now.AddYears(-6));
        try
        {
            ReportRetentionResult result = CreateService(31).DeleteExpired();

            Assert.True(File.Exists(link));
            Assert.True(File.Exists(target));
            Assert.False(File.Exists(regular));
            Assert.Equal(new ReportRetentionResult(1, 1), result);
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void RejectsReportDirectorySymbolicLink()
    {
        using var outside = new TempDirectory();
        string reportDirectory = Path.Combine(_directory.Path, "linked-reports");
        string targetFile = Path.Combine(outside.Path, "Informant-Report-2020-01-01_00-00-00.md");
        File.WriteAllText(targetFile, "outside");
        File.SetLastWriteTimeUtc(targetFile, Now.AddYears(-6).UtcDateTime);
        bool created = TryCreateDirectorySymbolicLink(reportDirectory, outside.Path);
        Assert.SkipUnless(created, "symbolic links are not available on this test host");
        try
        {
            var service = new ReportRetentionService(reportDirectory, 31, _timeProvider);

            Assert.Equal(new ReportRetentionResult(0, 1), service.DeleteExpired());
            Assert.True(File.Exists(targetFile));
        }
        finally
        {
            Directory.Delete(reportDirectory);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void ConstructorRejectsInvalidRetention(int retentionDays)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportRetentionService(_directory.Path, retentionDays, _timeProvider));
    }

    private ReportRetentionService CreateService(int retentionDays) => new(_directory.Path, retentionDays, _timeProvider);

    private string WriteFile(string fileName, DateTimeOffset lastWriteTime)
    {
        string path = Path.Combine(_directory.Path, fileName);
        File.WriteAllText(path, "test");
        File.SetLastWriteTimeUtc(path, lastWriteTime.UtcDateTime);
        return path;
    }

    private static bool TryCreateFileSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
