using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Teclas.Core;

public sealed class EngineClient(string python,string engineDirectory)
{
    public async Task<JsonObject> RunAsync(JsonObject request,string workspace,IProgress<string>? progress,CancellationToken token)
    {
        Directory.CreateDirectory(workspace);
        var requestPath=Path.Combine(workspace,"request-"+Guid.NewGuid().ToString("N")+".json");
        await File.WriteAllTextAsync(requestPath,request.ToJsonString(new(){WriteIndented=true}),Encoding.UTF8,token);
        var start=new ProcessStartInfo(python){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=workspace};
        start.ArgumentList.Add("-u");start.ArgumentList.Add(Path.Combine(engineDirectory,"worker.py"));start.ArgumentList.Add(requestPath);
        start.Environment["PYTHONIOENCODING"]="utf-8";start.Environment["PARTITURAS_JOB"]=Path.Combine(workspace,"work");
        using var process=Process.Start(start)??throw new InvalidOperationException("Não foi possível iniciar o motor musical.");
        using var registration=token.Register(()=>{try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}});
        var errorTask=process.StandardError.ReadToEndAsync(token);JsonObject? result=null;string? failure=null;
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
                    case "error":failure=message["message"]?.GetValue<string>();break;
                }
            }
            catch(JsonException){progress?.Report(line);}
        }
        await process.WaitForExitAsync(token);var error=await errorTask;
        if(token.IsCancellationRequested)throw new OperationCanceledException(token);
        if(process.ExitCode!=0 || result is null)throw new InvalidOperationException(failure??($"Falha no motor musical. {error}".Trim()));
        return result;
    }
}
