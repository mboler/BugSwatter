# BugSwatter reviewing MVNC

MVNC is a cross-platform remote framebuffer library. These validated findings involved protocol parsing and asynchronous native-resource lifecycles.

## The protocol banner accepted non-digit numeric fields

**Reported finding:** The parser checked the prefix, separator, and newline, then passed the three-character version fields to a general integer parser. The wire format requires exactly three ASCII digits in each field.

```csharp
if (!text.StartsWith("RFB ") || text[7] != '.' || text[11] != '\n')
{
    throw new ProtocolException();
}

int major = int.Parse(text.AsSpan(4, 3));
```

**Supporting context:** The protocol contract lived outside the parser implementation. Looking only at the parse method made a permissive numeric parser appear reasonable; the fixed-width wire specification made the acceptance gap clear.

**Validator disposition:** Confirmed. The parser now checks every numeric byte as an ASCII digit before computing the version.

## One failed cleanup could strand later Wayland resources

**Reported finding:** Disposal ran several native and portal cleanup steps in sequence. If an early step threw, later resources were never released.

```csharp
await capture.DisposeAsync();
await portalSession.DisposeAsync();
connection.Dispose();
```

**Supporting context:** Ownership was split across the desktop session, portal session, PipeWire remote, D-Bus connection, and cancellation subscription. No single changed file showed the full cleanup chain.

**Validator disposition:** Confirmed. A shared cleanup helper now attempts every release, then rethrows one failure or aggregates several failures after cleanup completes.

## Concurrent disposal callers did not await the same cleanup

**Reported finding:** An interlocked disposed flag made the second `DisposeAsync` caller return immediately while the first caller was still releasing shared native resources.

```csharp
if (Interlocked.Exchange(ref disposed, 1) != 0)
{
    return;
}

await DisposeResourcesAsync();
```

**Supporting context:** The flag looked thread-safe in isolation. The caller contract required every disposer to observe completion, which depended on the asynchronous owners in supporting files.

**Validator disposition:** Confirmed. Cleanup is now represented by one lazily created task, and every caller awaits that same task.
