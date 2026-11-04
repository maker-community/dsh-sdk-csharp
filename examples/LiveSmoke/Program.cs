using DeepSeek.Harness.Sdk;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine(
        "Usage: LiveSmoke <dsh-executable-or-dsh.cmd> <dsh-home> [profile]");
    return 2;
}

var dshExecutable = Path.GetFullPath(args[0]);
var dshHome = Path.GetFullPath(args[1]);
var profile = args.Length > 2 ? args[2] : "sdk";
var workspace = Directory.CreateTempSubdirectory("dsh-sdk-csharp-smoke-");

var options = new DeepSeekHarnessOptions
{
    DshExecutable = dshExecutable,
    DshHome = dshHome,
    Profile = profile,
    ProcessWorkingDirectory = workspace.FullName,
    WorkingDirectory = workspace.FullName,
};

try
{
    await using var harness = new DeepSeekHarnessClient(options);
    var sessionId = $"dsh-sdk-csharp-smoke-{Guid.NewGuid():N}";
    var result = await harness.RunAsync(
        "Do not use tools or change files. Reply with exactly: DSH C# SDK live smoke test passed.",
        sessionId);

    Console.WriteLine($"Session: {result.SessionId}");
    Console.WriteLine($"Finish reason: {result.FinishReason ?? "(not reported)"}");
    Console.WriteLine(result.FinalResponse);
    return result.FinishReason is "error" or "aborted" ? 1 : 0;
}
finally
{
    workspace.Delete(recursive: true);
}
