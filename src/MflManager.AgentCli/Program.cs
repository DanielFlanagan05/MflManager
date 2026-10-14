using MflManager.AgentCli;

return await new AgentCliRunner(Console.In, Console.Out, Console.Error).RunAsync(args, CancellationToken.None);
