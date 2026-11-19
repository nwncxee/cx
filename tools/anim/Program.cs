using CX.Services.Anim;

// replay <recording>: print each body's grades. replay --fixture <out>: write the hand-checked recording the approved example replays.
if (args.Length == 1 && args[0] != "--fixture") return LegReplay.Run(args[0], Console.Out, Console.Error) ? 0 : 1;
if (args.Length == 2 && args[0] == "--fixture")
{
    try
    {
        using var file = new FileStream(args[1], FileMode.Create, FileAccess.Write);
        LegReplayFixture.Write(file);
        return 0;
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"replay: {e.Message}");
        return 1;
    }
}
Console.Error.WriteLine("usage: replay.sh <recording> | replay.sh --fixture <out>");
return 2;
