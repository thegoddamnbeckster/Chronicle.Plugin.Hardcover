using System.Reflection;
using Xunit;

namespace Chronicle.Plugin.Hardcover.Tests;

/// <summary>The checklist's K7: nothing about the token (not even its first characters) may be written to a log or an error.</summary>
public class SecretProofTests
{
    private const string Token = "eyJSECRETTOKENabcdefghijklmnopqrstuvwxyz0123456789";

    [Theory]
    [InlineData(Token)]
    [InlineData("Bearer " + Token)]
    public void The_description_of_the_token_used_in_logs_and_errors_contains_none_of_it(string pasted)
    {
        var client = new HardcoverClient(pasted);
        var preview = (string)typeof(HardcoverClient).GetField("_tokenPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        for (var i = 0; i + 4 <= Token.Length; i++)
            Assert.DoesNotContain(Token.Substring(i, 4), preview);
        Assert.Contains("characters", preview);
    }

    [Fact]
    public void An_empty_token_is_described_as_empty_not_blank()
    {
        var client = new HardcoverClient("");
        var preview = (string)typeof(HardcoverClient).GetField("_tokenPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        Assert.Contains("no token", preview);
    }
}
