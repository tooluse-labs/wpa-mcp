using WpaMcp;

namespace WpaMcp.Tests;

public class BrandingTests
{
    [Fact]
    public async Task VersionCommand_UsesWpaMcpBrand()
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();

        try
        {
            Console.SetOut(output);

            var exitCode = await Program.Main(new[] { "--version" });

            Assert.Equal(0, exitCode);
            Assert.StartsWith("WpaMcp ", output.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }
}
