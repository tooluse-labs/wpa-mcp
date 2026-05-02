namespace WprMcp;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine("WprMcp 0.1.0-poc");
            return 0;
        }
        Console.Error.WriteLine("WprMcp PoC: MCP host not wired up yet (Task 4).");
        return 1;
    }
}
