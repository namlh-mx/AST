using AST.Shell.Session;

namespace AST.Shell.Tests.Session;

public class AdminSessionTests
{
    [Fact]
    public void New_session_is_not_authenticated()
    {
        var s = new AdminSession();
        Assert.False(s.IsAuthenticated);
        Assert.Null(s.PrivateKey);
        Assert.Null(s.Passphrase);
    }

    [Fact]
    public void Authenticate_stores_key_and_flips_flag_and_raises_Changed()
    {
        var s = new AdminSession();
        var raised = 0;
        s.Changed += (_, _) => raised++;
        var key = new byte[] { 9, 9, 9 };

        s.Authenticate(key, "pp");

        Assert.True(s.IsAuthenticated);
        Assert.Same(key, s.PrivateKey);
        Assert.Equal("pp", s.Passphrase);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Authenticate_with_null_key_still_marks_authenticated_for_Debug_skip()
    {
        var s = new AdminSession();
        s.Authenticate(null, null);
        Assert.True(s.IsAuthenticated);
        Assert.Null(s.PrivateKey);
    }

    [Fact]
    public void Clear_resets_state_and_raises_Changed()
    {
        var s = new AdminSession();
        s.Authenticate(new byte[] { 1 }, "pp");
        var raised = 0;
        s.Changed += (_, _) => raised++;

        s.Clear();

        Assert.False(s.IsAuthenticated);
        Assert.Null(s.PrivateKey);
        Assert.Null(s.Passphrase);
        Assert.Equal(1, raised);
    }
}
