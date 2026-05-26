using OpenIsland.Core;

namespace OpenIsland.Tests;

public class SessionIdValidatorTests
{
    [Theory]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890")] // a real Claude GUID session id
    [InlineData("claude_1a2b3c4d5e6f")]                  // synthetic fallback id
    [InlineData("ABCdef0123456789")]
    public void IsValid_AcceptsSafeIds(string id)
    {
        Assert.True(SessionIdValidator.IsValid(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x\"; Remove-Item -Recurse C:\\Users; \"")] // the injection payload
    [InlineData("$(whoami)")]                                // command substitution
    [InlineData("a`b")]                                      // backtick
    [InlineData("a;b")]                                      // statement separator
    [InlineData("a b")]                                      // whitespace
    [InlineData("a&b")]
    [InlineData("../etc")]                                   // slashes / path chars
    public void IsValid_RejectsUnsafeIds(string? id)
    {
        Assert.False(SessionIdValidator.IsValid(id));
    }
}
