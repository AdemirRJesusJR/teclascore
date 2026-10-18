using System.Runtime.InteropServices;
using System.Text;
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
/// facts (chords by measure, key, tempo, melody range, lyrics, practice results) go as text in the system prompt; the
/// conversation continues across questions. Answers are kept short, so a plain (non-streaming) request is enough.
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

        Dados da música aberta no TeclaScore:
        """ + "\n" + songContext;

    public IReadOnlyList<MessageParam> History => history;

    /// <summary>Asks a question and returns the answer text (the conversation is kept for follow-up questions).</summary>
    public async Task<string> AskAsync(string question, CancellationToken token)
    {
        history.Add(new MessageParam { Role = Role.User, Content = question });
        try
        {
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = ModelId,
                MaxTokens = 8000,
                System = SystemPrompt,
                Thinking = new ThinkingConfigAdaptive(),
                OutputConfig = new OutputConfig { Effort = Effort.Medium },
                Messages = history,
            }, token);
            if (response.StopReason == "refusal")
            {
                history.RemoveAt(history.Count - 1);
                return "O modelo preferiu não responder a esta pergunta. Tente reformular, falando da música e do estudo.";
            }
            string answer = string.Join("\n", response.Content.Select(block => block.TryPickText(out TextBlock? text) ? text.Text : null).OfType<string>()).Trim();
            history.Add(new MessageParam { Role = Role.Assistant, Content = answer.Length > 0 ? answer : "(sem resposta)" });
            return answer;
        }
        catch (AnthropicUnauthorizedException) { history.RemoveAt(history.Count - 1); throw new InvalidOperationException("A chave da Anthropic foi recusada. Confira a chave em Configurações."); }
        catch (AnthropicRateLimitException) { history.RemoveAt(history.Count - 1); throw new InvalidOperationException("Muitas perguntas seguidas ou limite da conta atingido. Espere um pouco e tente de novo."); }
        catch (AnthropicApiException error) { history.RemoveAt(history.Count - 1); throw new InvalidOperationException("A Anthropic respondeu com erro: " + error.Message); }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or AnthropicIOException) { history.RemoveAt(history.Count - 1); throw new InvalidOperationException("Sem conexão com a Anthropic. Confira a internet e tente de novo."); }
    }
}
