namespace WebHop.Tests.Infrastructure
{
    /// <summary>
    /// Captures Console.Out for the duration of a test and restores it afterwards. Console.Out is
    /// process-wide, so tests that use it must be [NonParallelizable].
    /// </summary>
    internal sealed class ConsoleOutput : IDisposable
    {
        private readonly TextWriter original = Console.Out;
        private readonly StringWriter captured = new();

        public ConsoleOutput()
        {
            Console.SetOut(captured);
        }

        public string Text => captured.ToString();

        public void Dispose()
        {
            Console.SetOut(original);
        }
    }
}
