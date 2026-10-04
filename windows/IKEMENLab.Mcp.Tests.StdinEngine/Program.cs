// Behaves like Ikemen GO in one respect (src/system.go starts a goroutine that scans os.Stdin and runs each line as a command): it reads stdin line by
// line as soon as it starts, whatever its arguments, and echoes each line to stdout. If it ever shared the MCP server's stdin it would swallow a client's
// request and print into the protocol stream. With its own empty pipe it simply waits, like a game, until it is killed.
while (Console.ReadLine() is { } line) Console.WriteLine("engine read: " + line);
Thread.Sleep(Timeout.Infinite);
