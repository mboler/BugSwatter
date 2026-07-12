namespace Marshal.Tests;

public sealed class FileWatchTriggerTests
{
    [Fact]
    public void WatcherErrorWithAnExceptionReportsItsMessage()
    {
        Assert.Equal("disk fell over", FileWatchTrigger.DescribeWatcherError(new InvalidOperationException("disk fell over")));
    }

    [Fact]
    public void WatcherErrorWithoutAnExceptionReportsAPlaceholderAndDoesNotThrow()
    {
        // FileSystemWatcher can raise Error with no exception; a null here must never become a NullReferenceException
        // inside the event handler, which would silently disable the watcher. This guards that the null path is handled
        string message = FileWatchTrigger.DescribeWatcherError(null);

        Assert.Equal("(no exception provided)", message);
    }
}
