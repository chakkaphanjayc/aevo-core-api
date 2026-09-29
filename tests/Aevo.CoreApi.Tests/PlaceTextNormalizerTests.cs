using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class PlaceTextNormalizerTests
{
    private static readonly string[] AliasInput = ["Ari Cafe", " ari café ", "ร้านอารี", "ร้านอารี"];
    private static readonly string[] AliasExpected = ["ari cafe", "ari café", "ร้านอารี"];

    [Fact]
    public void NormalizesThaiEnglishAndPunctuationWithoutInventingTransliteration()
    {
        Assert.Equal("ร้านกาแฟ ari", PlaceTextNormalizer.Normalize("  ร้านกาแฟ — ARI "));
        Assert.Equal("สุขุมวิท 11", PlaceTextNormalizer.Normalize("สุขุมวิท  11"));
    }

    [Fact]
    public void AliasNormalizationIsDeterministicAndDeduplicated()
    {
        var aliases = PlaceTextNormalizer.NormalizeAliases(AliasInput);

        Assert.Equal(AliasExpected, aliases);
    }
}
