# TeclaScore

Versão 0.3.1 para Windows, em C# / WPF (.NET 10). Prepara partituras com uma pauta de melodia, cifras e diagramas de teclado na segunda inversão e salva arquivos MuseScore .mscz.

Repositório: [AdemirRJesusJR/teclascore](https://github.com/AdemirRJesusJR/teclascore).

## Para testar

1. Abra Aplicativo/Teclas.Partituras.exe.
2. Confira os caminhos em Configurações e escolha a pasta de saída.
3. Importe arquivos .mid, .midi ou .kar.
4. Nos 18 MIDIs com perfil revisado, mantenha a revisão aprovada marcada. Use “Gerar lote revisado” para processar os que ainda não foram gerados.
5. Em MIDIs novos, escolha a pista de melodia, clique em “Analisar acordes”, confira/edite as cifras e clique em “Gerar .mscz”.
6. Confira a prévia ou abra o resultado no MuseScore. Na tela Produção, “Exportar ZIP” reúne os arquivos disponíveis da biblioteca.
7. Use o botão no topo da barra lateral para alternar entre menus completos e somente ícones. Passe o mouse sobre um ícone para ver o nome do menu.
8. Escolha seu tema em Configurações. Ao fechar e reabrir, o app restaura suas preferências e sua biblioteca.

A aba de cifras aceita inclusão e exclusão de linhas. A posição é medida em semínimas desde o início do MIDI, antes da remoção de compassos vazios de preparação. A armadura usa números negativos para bemóis e positivos para sustenidos. Alterar a armadura muda a notação da tonalidade inicial; não transpõe as notas. Modulações dos perfis revisados são preservadas.

## Painéis e biblioteca

- **Visão geral**: total de MIDIs importados, prontos para o lote, partituras geradas e arquivos para revisão musical. O gráfico separa também as falhas de leitura ou conversão. Clique nos indicadores para abrir os arquivos correspondentes.
- **Biblioteca & editor**: busca por nome sem distinção de maiúsculas ou acentos, filtros por situação e editor em três etapas: melodia, cifras e prévia.
- **Produção**: partituras disponíveis, páginas no resultado mais recente de cada MIDI, percentual concluído da biblioteca, abertura da prévia e exportação de ZIP.
- **Configurações**: temas Verde clássico, Oceano, Índigo, Grafite e âmbar e Azul claro; caminhos do MuseScore, do Python e da pasta de saída.
- **Sobre**: versão atual obtida do aplicativo, descrição e acesso ao repositório.

Os indicadores refletem a biblioteca restaurada. As saídas permanecem na pasta escolhida quando o app é fechado. As quatro categorias do gráfico não se sobrepõem. Um arquivo com perfil revisado só está pronto para o lote quando a revisão está marcada e não há falha pendente. A contagem de páginas considera apenas o último resultado disponível por MIDI. Busca e filtros não alteram as partituras.

## Preferências e trabalho salvo

Ao fechar o aplicativo, são salvos o tema, o modo da barra lateral, os caminhos configurados, o tamanho e a posição da janela, a tela aberta, a busca, o filtro, a aba do editor e o arquivo selecionado. A biblioteca também mantém a escolha das pistas, o uso de perfis revisados, as cifras editadas, a tonalidade e os vínculos com análises e partituras geradas.

Os dados ficam em `%LOCALAPPDATA%/TeclasPartituras/settings.json`, com gravação atômica e cópia de recuperação. Se o salvamento falhar, a janela permanece aberta e mostra o motivo. Os arquivos MIDI e .mscz continuam nos locais originais; o app salva suas referências. Um MIDI alterado precisa de nova análise para impedir a reutilização de resultados de outro conteúdo. Mantenha os arquivos e pastas de trabalho para continuar de onde parou.

## Dependências desta versão

- Windows com .NET Desktop Runtime 10.
- MuseScore Studio 4 instalado, para gravação nativa e renderização.
- Python 3.11 ou superior com Pillow, para o motor musical reaproveitado.

A interface, a leitura inicial do MIDI, a biblioteca, o controle das operações e a criação de ZIP são em C#. A preparação musical detalhada usa o motor Python validado no lote anterior. Este protótipo ainda não é uma implementação musical integralmente em C# nem um instalador que inclua todas as dependências.

No computador usado para preparar o projeto, as dependências já existem e o programa procura seus caminhos automaticamente. Em outro computador, configure os executáveis na interface. O aplicativo não instala programas, não baixa dependências e não envia os MIDIs pela internet.

## Cuidados de edição e limites

- A análise automática de um MIDI novo é uma proposta para revisão. O programa não recria uma melodia ausente, nem distingue com certeza uma voz de um acompanhamento.
- Os perfis conhecidos são reconhecidos pelo conteúdo SHA-256, não pelo nome. Há 18 revisões completas, 79 casos sem melodia completa e 14 com melodia parcial no catálogo.
- A edição de notas e ritmos é feita no MuseScore. Nesta interface, é possível escolher fontes e editar as cifras.
- Os diagramas são imagens vetoriais incorporadas. Ao mudar uma cifra dentro do MuseScore, atualize também seu desenho.
- Arquivos de saída existentes recebem outro nome, sem sobrescrita. MIDIs originais não são modificados.
- Cada geração inclui uma pasta de revisão com imagens de todas as páginas, MusicXML e relatório de validação.
- Configurações, biblioteca e análises temporárias ficam em %LOCALAPPDATA%/TeclasPartituras.
- MIDI tipo 2 e tempo SMPTE não são suportados nesta versão.

## Código e compilação

Abra Teclas.Partituras.sln. Não há pacotes NuGet externos; NuGet.Config mantém as fontes vazias.

    dotnet build Teclas.Partituras.sln
    dotnet publish src/Teclas.App/Teclas.App.csproj -c Release --no-self-contained -o Aplicativo
    dotnet run --project tests/Teclas.Tests/Teclas.Tests.csproj
    dotnet run --project tests/Teclas.Dashboard.Tests/Teclas.Dashboard.Tests.csproj
    dotnet run --project tests/Teclas.Settings.Tests/Teclas.Settings.Tests.csproj
    python tools/qa_ui.py --midis "C:\caminho\para\midis"
    python tools/qa_preferences.py --project "C:\caminho\para\Partituras de Teclado"

Projetos: Teclas.App (WPF), Teclas.Core (leitura MIDI, protocolo e métricas), Teclas.Tests, Teclas.Dashboard.Tests e Teclas.Settings.Tests (testes executáveis). A pasta engine contém o motor musical e seus perfis. O executável precisa permanecer junto das DLLs e dessa pasta.

Os testes de configurações e de painéis usam dados sintéticos. Os testes de interface e integração musical exigem a pasta do acervo local, que não faz parte do repositório; informe seu caminho nos argumentos acima. Para a integração completa, execute `python tools/qa_engine.py "C:\caminho\para\Partituras de Teclado"`.

O script tools/prepare_engine.py regenera a cópia do motor a partir das ferramentas externas do projeto musical; seu uso é opcional. tools/qa_engine.py verifica os 18 perfis contra o lote original e exercita a conversão de um MIDI novo. A pasta engine já inclui o motor necessário para o aplicativo, sem exigir a regeneração. A ferramenta histórica engine/build_keyboard_diagrams.py é preservada como origem das funções vetoriais reutilizadas pelo motor; executá-la isoladamente requer a partitura piloto na pasta externa partituras.

O número da versão é definido em `src/Teclas.App/Teclas.App.csproj` e aparece automaticamente na tela Sobre e nas propriedades do executável. As versões publicadas são marcadas com tags Git, como `v0.3.1`. O executável mantém o nome `Teclas.Partituras.exe` para preservar os caminhos já usados, e as preferências continuam na pasta `TeclasPartituras`.

Veja VALIDACAO.txt para os testes realizados. Esta é a prévia 0.3.1, destinada a testes no computador do usuário.
