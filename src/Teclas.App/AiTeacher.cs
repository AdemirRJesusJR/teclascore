using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace Teclas.App;

/// <summary>The user's Anthropic API key, kept in the Windows Credential Manager (never in settings, backups or sync).</summary>
internal static class TeacherKeyStore
{
    public const string Target = "TeclaScore/Anthropic API";
    private const uint Generic = 1, LocalMachine = 2;

    public static string? Read(string target = Target)
    {
        if (!CredReadW(target, Generic, 0, out IntPtr pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.BlobSize == 0 ? null : Marshal.PtrToStringUni(credential.Blob, (int)credential.BlobSize / 2);
        }
        finally { CredFree(pointer); }
    }

    public static void Write(string key, string target = Target)
    {
        byte[] blob = Encoding.Unicode.GetBytes(key);
        IntPtr name = Marshal.StringToHGlobalUni(target), user = Marshal.StringToHGlobalUni("TeclaScore"), data = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, data, blob.Length);
            var credential = new Credential { Type = Generic, TargetName = name, BlobSize = (uint)blob.Length, Blob = data, Persist = LocalMachine, UserName = user };
            if (!CredWriteW(ref credential, 0)) throw new InvalidOperationException($"O Windows não guardou a chave (erro {Marshal.GetLastWin32Error()}).");
        }
        finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(user); Marshal.FreeHGlobal(data); }
    }

    public static void Delete(string target = Target) => CredDeleteW(target, Generic, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags; public uint Type; public IntPtr TargetName; public IntPtr Comment; public long LastWritten;
        public uint BlobSize; public IntPtr Blob; public uint Persist; public uint AttributeCount; public IntPtr Attributes; public IntPtr TargetAlias; public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWriteW(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);
}

/// <summary>
/// A keyboard teacher answering about the current song through the Claude API, with the user's own key. The song's
/// facts (chords and notes by measure, lyrics, practice results) go as text in the system prompt; the conversation
/// continues across questions. Answers stream in as they are written. Exercises come back as structured JSON.
/// </summary>
internal sealed class AiTeacher(string apiKey, string songContext)
{
    public const string ModelId = "claude-opus-5-5";
    private readonly AnthropicClient client = new() { ApiKey = apiKey };
    private readonly List<MessageParam> history = [];

    private string SystemPrompt => """
        Você é um professor de teclado paciente e prático, falando português do Brasil com um aluno que toca músicas populares
        e de igreja a partir de partituras com melodia, cifras e diagramas de teclado. Responda de forma curta e concreta,
        citando compassos, acordes e notas da música abaixo. Quando sugerir dedilhados, posições de acorde ou simplificações,
        diga que são sugestões. Não invente informações que não estão nos dados; se faltar algo, diga o que precisaria ver.
        Escreva em texto simples, sem tabelas nem marcação Markdown.

        Dados da música aberta no TeclaScore:
        """ + "\n" + songContext;

    public IReadOnlyList<MessageParam> History => history;

    /// <summary>Asks a question; <paramref name="onText"/> receives the answer as it streams. Returns the whole answer.</summary>
    public async Task<string> AskAsync(string question, Action<string> onText, CancellationToken token)
    {
        history.Add(new MessageParam { Role = Role.User, Content = question });
        var answer = new StringBuilder(); string? stop = null;
        try
        {
            var parameters = new MessageCreateParams
            {
                Model = ModelId,
                MaxTokens = 16000,
                System = SystemPrompt,
                Thinking = new ThinkingConfigAdaptive(),
                OutputConfig = new OutputConfig { Effort = Effort.Medium },
                Messages = history,
            };
            await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, token))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                { answer.Append(text.Text); onText(text.Text); }
                else if (streamEvent.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason is { } reason) stop = reason.ToString();
            }
        }
        catch (Exception error) when (Friendly(error) is { } message) { history.RemoveAt(history.Count - 1); throw new InvalidOperationException(message); }
        if (stop?.Contains("refusal", StringComparison.OrdinalIgnoreCase) == true)
        {
            history.RemoveAt(history.Count - 1);
            return "O modelo preferiu não responder a esta pergunta. Tente reformular, falando da música e do estudo.";
        }
        string result = answer.ToString().Trim();
        history.Add(new MessageParam { Role = Role.Assistant, Content = result.Length > 0 ? result : "(sem resposta)" });
        return result;
    }

    private static readonly Dictionary<string, JsonElement> ExerciseSchema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            title = new { type = "string" },
            explanation = new { type = "string" },
            tempo = new { type = "integer" },
            beatsPerBar = new { type = "integer" },
            steps = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new { chord = new { type = "string" }, notes = new { type = "array", items = new { type = "integer" } }, beats = new { type = "number" } },
                    required = new[] { "chord", "notes", "beats" },
                    additionalProperties = false,
                },
            },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "title", "explanation", "tempo", "beatsPerBar", "steps" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    /// <summary>Asks for a short practice exercise for this song (scale, arpeggio or chord changes) as structured JSON.</summary>
    public async Task<string> ExerciseJsonAsync(string kind, CancellationToken token)
    {
        string request = $"""
            Monte um exercício curto de teclado para o aluno estudar antes desta música: {kind}.
            Use as tonalidades e os acordes da música. Regras: de 4 a 16 compassos; "steps" em ordem, cada passo com a cifra
            em vigor ("chord", vazia se não houver acorde), as notas da mão direita tocadas juntas nesse passo ("notes",
            números MIDI entre 55 e 84; uma nota para escalas e melodias, várias para acordes) e a duração em tempos ("beats",
            0.5, 1, 1.5, 2, 3 ou 4). A soma dos tempos deve fechar compassos inteiros de "beatsPerBar" (3 ou 4).
            "tempo" é o andamento sugerido (40 a 120). "title" curto; "explanation" com até 3 frases sobre como praticar.
            """;
        try
        {
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = ModelId,
                MaxTokens = 16000,
                System = SystemPrompt,
                Thinking = new ThinkingConfigAdaptive(),
                OutputConfig = new OutputConfig { Effort = Effort.Medium, Format = new JsonOutputFormat { Schema = ExerciseSchema } },
                Messages = [new() { Role = Role.User, Content = request }],
            }, token);
            if (response.StopReason == "refusal") throw new InvalidOperationException("O modelo preferiu não montar este exercício. Tente outro tipo.");
            return string.Join("", response.Content.Select(block => block.TryPickText(out TextBlock? text) ? text.Text : null).OfType<string>());
        }
        catch (Exception error) when (Friendly(error) is { } message) { throw new InvalidOperationException(message); }
    }

    private static string? Friendly(Exception error) => error switch
    {
        AnthropicUnauthorizedException => "A chave da Anthropic foi recusada. Confira a chave em Configurações.",
        AnthropicRateLimitException => "Muitas perguntas seguidas ou limite da conta atingido. Espere um pouco e tente de novo.",
        AnthropicApiException api => "A Anthropic respondeu com erro: " + api.Message,
        System.Net.Http.HttpRequestException or AnthropicIOException => "Sem conexão com a Anthropic. Confira a internet e tente de novo.",
        _ => null,
    };
}
