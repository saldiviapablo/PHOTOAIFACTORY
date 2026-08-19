namespace PhotoAIFactory.Rec01;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        var values = Cli.Parse(args);
        var mode = values.Optional("mode", "controller");
        try
        {
            return mode.ToLowerInvariant() switch
            {
                "controller" => await RecoveryController.RunAsync(values),
                "worker" => await RecoveryWorker.RunAsync(new WorkerOptions(
                    values.Required("db"), values.Required("scenario"), values.Required("work"),
                    values.Required("log"), values.Required("fixture"), values.Optional("crash"),
                    values.Optional("target"), values.Optional("barrier"), values.Optional("helper-barrier"),
                    int.Parse(values.Optional("jobs", "1"), System.Globalization.CultureInfo.InvariantCulture))),
                "stage-helper" => await RecoveryWorker.RunStageHelperAsync(
                    values.Required("output"), values.Required("barrier")),
                "self-test" => RecoveryController.SelfTest(),
                _ => throw new ArgumentException($"Unknown mode: {mode}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
