# BugSwatter reviewing BugSwatter

These cases came from BugSwatter runs against its own source. Each confirmed finding was inspected against the implementation and covered by regression tests during remediation.

## An operational Git failure looked like a missing baseline

**Reported finding:** The reachability probe treated every nonzero `git cat-file` result as an unreachable commit. A transient repository or process failure could therefore send the run down the rewritten-history path instead of reporting the real failure.

```csharp
GitResult result = await git.RunAsync("cat-file", "-e", revision);
return result.ExitCode == 0;
```

**Supporting context:** The baseline controller interpreted `false` as permission to perform a full review. The changed detector alone did not show that consequence, and the controller alone did not show that ordinary Git failures collapsed into `false`.

**Validator disposition:** Confirmed. The fix distinguishes Git's invalid-object response from other failures and preserves the baseline when reachability cannot be established.

## One file-read exception could abort an unattended review

**Reported finding:** The model tool loop called the bounded file reader without isolating unexpected read failures. A file changing after manifest creation could throw through the conversation and end the whole run.

```csharp
string result = tool.Execute(arguments);
messages.Add(new ChatMessage("tool", result));
```

**Supporting context:** The unchanged repository reader intentionally throws when live content no longer matches the manifest. The tool loop needed that contract in view to distinguish cancellation from a recoverable, model-visible read failure.

**Validator disposition:** Confirmed. Non-cancellation failures now return a bounded error to the model, while cancellation still stops the run.

## Validator failures were indistinguishable from empty answers

**Reported finding:** The second-opinion reviewer caught transport failures and returned `null`, the same value used for an empty model response. Downstream reporting could not tell a failed request from a model that answered with nothing.

```csharp
catch (ModelCallException exception)
{
    Log.Warning("Validation failed: {Reason}", exception.Message);
    return null;
}
```

**Supporting context:** The report writer and email gate relied on the reviewer's return value to determine whether severity was known. The failure became visible only when the reviewer, report accumulator, and notification decision were considered together.

**Validator disposition:** Confirmed. Request failure, empty response, and parse failure now have separate recorded states, and undetermined severity cannot be presented as a clean validation.

## A clock rollback could repeat a scheduled occurrence

**Reported finding:** The scheduler recalculated its next occurrence from the current wall clock after every wake. Moving the clock backward could make an already-fired time look future again and enqueue a second run.

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    DateTime next = GetNextOccurrence(DateTime.Now);
    await Task.Delay(next - DateTime.Now, stoppingToken);
    queue.Enqueue(job);
}
```

**Supporting context:** Queue serialization prevented simultaneous execution but did not prevent a duplicate occurrence from waiting behind the first. The scheduler needed persistent per-entry occurrence state, not only queue state.

**Validator disposition:** Confirmed. Each schedule entry now remembers its next calendar occurrence, and fake-clock tests cover backward and forward adjustments.
