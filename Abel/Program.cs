using Abel.Core;

namespace Abel;

internal sealed class Program()
{
    static async Task Main()
    {
        // await ToolChecker.CheckAll();

        foreach (var exampleProject in Directory.GetDirectories("C:\\Users\\ayanami\\Abel\\Example"))
        {
            AbelRunner abel = new();
            abel.ParseFolder(exampleProject);
            await abel.Run().ConfigureAwait(false);
        }
    }
}


