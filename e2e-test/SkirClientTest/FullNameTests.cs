using Skirout.FullName;

namespace SkirClientTest;

/// <summary>
/// Smoke test for the generated <see cref="FullName"/> struct.
/// Ensures struct-init syntax and the Serializer property compile and work.
/// </summary>
public sealed class FullNameTests
{
    [Fact]
    public void StructInitSyntax()
    {
        FullName name = new() { FirstName = "John", LastName = "Doe" };
        Assert.Equal("John", name.FirstName);
        Assert.Equal("Doe", name.LastName);
    }

    [Fact]
    public void RoundTrip_Json()
    {
        FullName name = new() { FirstName = "John", LastName = "Doe" };
        string json = FullName.Serializer.ToJson(name);
        FullName decoded = FullName.Serializer.FromJson(json);
        Assert.Equal(name, decoded);
    }
}
