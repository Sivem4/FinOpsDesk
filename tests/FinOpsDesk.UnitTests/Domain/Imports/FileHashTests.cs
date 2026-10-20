using FinOpsDesk.Modules.Reconciliation.Domain.Imports;

namespace FinOpsDesk.UnitTests.Reconciliation.Domain.Imports;

public sealed class FileHashTests
{
    private const string Digest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Constructor_should_accept_a_sha256_digest()
    {
        var fileHash = new FileHash(Digest);

        Assert.Equal(Digest, fileHash.Value);
        Assert.Equal(FileHash.HexLength, fileHash.Value.Length);
    }

    [Fact]
    public void Constructor_should_canonicalize_to_lowercase()
    {
        var lower = new FileHash(Digest);
        var upper = new FileHash(Digest.ToUpperInvariant());
        var padded = new FileHash($"  {Digest.ToUpperInvariant()}  ");

        Assert.Equal(lower, upper);
        Assert.Equal(lower, padded);
        Assert.Equal(Digest, upper.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefg")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Constructor_should_reject_invalid_digests(string value)
    {
        Assert.Throws<ArgumentException>(
            () => new FileHash(value));
    }
}