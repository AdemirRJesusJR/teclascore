using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Teclas.Core;

public sealed class EngineOperationException(string message, JsonObject details) : InvalidOperationException(message)
{
    public JsonObject Details { get; } = (JsonObject)details.DeepClone();
}

public sealed class EngineClient(string python,string engineDirectory)
{
    private readonly LocalPerformanceRecorder? recorder;
    public EngineClient(string python, string engineDirectory, LocalPerformanceRecorder recorder) : this(python, engineDirectory)
    { this.recorder = recorder; }

    public async Task<JsonObject> RunAsync(JsonObject request,string workspace,IProgress<string>? progress,CancellationToken token)
    {
        var operation = request["mode"] is JsonValue mode && mode.TryGetValue<string>(out var name) ? name switch
        {
            "analyze" => PerformanceOperation.EngineAnalyze, "generate" => PerformanceOperation.EngineGenerate,
            "measureMap" => PerformanceOperation.EngineMeasureMap, _ => PerformanceOperation.EngineOther
        } : PerformanceOperation.EngineOther;
        using var measurement = (recorder ?? PerformanceDiagnostics.Recorder)?.Begin(operation, PerformanceProcessKind.Worker, PerformancePhase.Process)
            ?? PerformanceMeasurement.Disabled;
        Process? process = null;
        try
        {
        Directory.CreateDirectory(workspace);
        var requestPath=Path.Combine(workspace,"request-"+Guid.NewGuid().ToString("N")+".json");
        await File.WriteAllTextAsync(requestPath,request.ToJsonString(new(){WriteIndented=true}),Encoding.UTF8,token);
        var start=new ProcessStartInfo(python){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=workspace};
        start.ArgumentList.Add("-u");start.ArgumentList.Add(Path.Combine(engineDirectory,"worker.py"));start.ArgumentList.Add(requestPath);
        start.Environment["PYTHONIOENCODING"]="utf-8";start.Environment["PARTITURAS_JOB"]=Path.Combine(workspace,"work");
        process=Process.Start(start)??throw new InvalidOperationException("Não foi possível iniciar o motor musical.");
        measurement.TrackProcess(process);
        using var registration=token.Register(()=>{try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}});
        var errorTask=process.StandardError.ReadToEndAsync(token);JsonObject? result=null;string? failure=null;JsonObject? failureDetails=null;
        while(await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            if(string.IsNullOrWhiteSpace(line))continue;
            try
            {
                var message=JsonNode.Parse(line)?.AsObject();
                switch(message?["kind"]?.GetValue<string>())
                {
                    case "progress":progress?.Report(message["message"]?.GetValue<string>()??"");break;
                    case "result":result=message["data"]!.AsObject();break;
                    case "error":failure=message["message"]?.GetValue<string>();failureDetails=message;break;
                }
            }
            catch(JsonException){progress?.Report(line);}
        }
        await process.WaitForExitAsync(token);var error=await errorTask;
        if(token.IsCancellationRequested)throw new OperationCanceledException(token);
        if(process.ExitCode!=0 || result is null)
        {
            string failureMessage=failure??($"Falha no motor musical. {error}".Trim());
            if(failureDetails is not null)throw new EngineOperationException(failureMessage,failureDetails);
            throw new InvalidOperationException(failureMessage);
        }
        if (result["pages"] is JsonValue pages && pages.TryGetValue<int>(out var pageCount)) measurement.SetCount(PerformanceCount.Pages, pageCount);
        if (result["chords"] is JsonArray chords) measurement.SetCount(PerformanceCount.Chords, chords.Count);
        measurement.Success();
        return result;
        }
        catch (OperationCanceledException) { measurement.Cancelled(); throw; }
        catch (TimeoutException) { measurement.TimedOut(); throw; }
        finally { measurement.Dispose(); process?.Dispose(); }
    }
}
