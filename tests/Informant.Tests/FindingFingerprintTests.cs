namespace Informant.Tests;

/// <summary>Structural finding identity and inline suppression regression coverage</summary>
public sealed class FindingFingerprintTests
{
    /// <summary>Verifies unrelated blank lines above an anchor do not change its structural identity</summary>
    [Fact]
    public void BlankLinesAboveAnchorPreserveFingerprint()
    {
        string[] original = ["class Example", "{", "return value;", "}"];
        string[] shifted = ["", "", .. original];

        FindingIdentity before = FindingFingerprint.Create("src/Example.cs", "Correctness", original, 3);
        FindingIdentity after = FindingFingerprint.Create("src/Example.cs", " correctness ", shifted, 5);

        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal(3, before.AnchorLine);
        Assert.Equal(5, after.AnchorLine);
    }

    /// <summary>Verifies path, category, and structural source changes remain distinct findings</summary>
    [Fact]
    public void StructuralInputsChangeFingerprint()
    {
        string[] lines = ["class Example", "{", "return value;", "}"];
        FindingIdentity baseline = FindingFingerprint.Create("src/Example.cs", "correctness", lines, 3);

        Assert.NotEqual(baseline.Fingerprint, FindingFingerprint.Create("src/Other.cs", "correctness", lines, 3).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, FindingFingerprint.Create("src/Example.cs", "security", lines, 3).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, FindingFingerprint.Create("src/Example.cs", "correctness", ["class Example", "{", "return other;", "}"], 3).Fingerprint);
    }

    /// <summary>Verifies inconsequential source whitespace is normalized before hashing</summary>
    [Fact]
    public void SourceWhitespaceDoesNotChangeFingerprint()
    {
        FindingIdentity compact = FindingFingerprint.Create("src/Example.cs", "general", ["if (ready)", "return value;", "}"], 2);
        FindingIdentity spaced = FindingFingerprint.Create("src/Example.cs", "general", ["  if   (ready) ", " return   value; ", "  }"], 2);

        Assert.Equal(compact.Fingerprint, spaced.Fingerprint);
    }

    /// <summary>Verifies justified markers cover their own line and the next significant line</summary>
    [Fact]
    public void JustifiedMarkerAppliesToDocumentedAnchors()
    {
        string[] lines = ["// bugswatter-ignore: legacy protocol requires this", "", "DangerousCall();"];

        FindingSuppressionMarker marker = Assert.Single(FindingFingerprint.ScanSuppressions(lines).Markers);

        Assert.True(marker.AppliesTo(1));
        Assert.True(marker.AppliesTo(3));
        Assert.False(marker.AppliesTo(2));
        Assert.Equal("legacy protocol requires this", marker.Justification);
    }

    /// <summary>Verifies an empty suppression justification is reported but never applied</summary>
    [Fact]
    public void EmptySuppressionJustificationIsInvalid()
    {
        FindingSuppressionScan scan = FindingFingerprint.ScanSuppressions(["// bugswatter-ignore:   ", "DangerousCall();"]);

        Assert.Empty(scan.Markers);
        Assert.Equal([1], scan.InvalidMarkerLines);
    }

    /// <summary>Verifies marker-like text inside ordinary source is not treated as an operator suppression</summary>
    [Fact]
    public void MarkerRequiresARecognizedCommentPrefix()
    {
        FindingSuppressionScan scan = FindingFingerprint.ScanSuppressions(["string value = \"bugswatter-ignore: not an instruction\";"]);

        Assert.Empty(scan.Markers);
        Assert.Empty(scan.InvalidMarkerLines);
    }
}
