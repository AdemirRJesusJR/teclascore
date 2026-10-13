# TeclaScore

Versão 0.6.1 para Windows, em C# / WPF (.NET 10). Permite ouvir as pistas MIDI antes de escolher a melodia, prepara partituras com cifras e diagramas de teclado com inversão configurável, edita as propriedades do cabeçalho, salva arquivos MuseScore .mscz, reproduz a partitura e exporta PDF.

Repositório: [AdemirRJesusJR/teclascore](https://github.com/AdemirRJesusJR/teclascore).

## Para testar

1. Abra Aplicativo/Teclas.Partituras.exe.
2. Confira os caminhos em Configurações e escolha a pasta de saída.
3. Importe arquivos .mid, .midi ou .kar pelo botão ou arrastando-os para o app. A Biblioteca abre automaticamente com o primeiro MIDI novo selecionado, sem a busca ou os filtros anteriores. Um arquivo já importado é selecionado sem duplicação.
4. Nos 18 MIDIs com perfil revisado, mantenha a revisão aprovada marcada. Use “Gerar lote revisado” para processar os que ainda não foram gerados.
5. Na aba Melodia, use ▶ na coluna Ouvir para escutar cada fonte isoladamente. Compare as pistas, marque a melodia, clique em “Analisar acordes”, confira/edite as cifras e clique em “Gerar partitura”. Com a revisão aprovada habilitada, é possível ouvir; desmarque essa opção para mudar a seleção manualmente.
6. Na aba Prévia, clique em “Reproduzir” para ouvir, “Pausar” para continuar depois e “Parar” para voltar ao início. A barra de posição permite avançar ou voltar. “Salvar PDF” exporta todas as páginas. “Gerar partitura” já salva o arquivo .mscz automaticamente; use “Pasta de saída” para acessá-lo.
7. Use o botão na parte inferior da barra lateral para alternar entre menus completos e somente ícones. Passe o mouse sobre um ícone para ver o nome do menu.
8. Escolha seu tema em Configurações. Ao fechar e reabrir, o app restaura suas preferências e sua biblioteca.
9. Para retirar um item da lista, selecione-o e clique em “Remover da biblioteca”. A última remoção pode ser desfeita enquanto o app estiver aberto. Seus arquivos MIDI e .mscz permanecem nas suas pastas.

A escuta de pistas funciona antes da análise, pelo sintetizador MIDI do Windows, sem abrir o MuseScore. Cada linha identifica uma combinação de pista, canal e instrumento, importante para MIDIs que concentram vários instrumentos na mesma pista. A escuta começa na primeira nota dessa fonte, oferece pausa, controle de posição e velocidades de 25%, 50%, 75%, 100%, 125% e 150% e não marca Melodia automaticamente. Ao trocar de fonte, MIDI, aba ou tela, o áudio anterior para. Timbres personalizados por SysEx podem soar diferentes no sintetizador General MIDI.

O controle Velocidade fica ao lado de Pausar/Parar na escuta de pistas. A mudança vale imediatamente, preserva a altura das notas e fica salva ao fechar. 100% retoma a velocidade original; a posição e a duração indicam o tempo original do MIDI. Essa preferência não muda o andamento da partitura gerada.

Para ouvir a partitura gerada na aba Prévia, o MuseScore instalado prepara o áudio em segundo plano na primeira reprodução. O editor do MuseScore não precisa ser aberto. Nas próximas reproduções, o áudio é reutilizado enquanto a partitura permanecer igual. Essa reprodução corresponde ao arquivo .mscz salvo: depois de editar as cifras, gere a partitura novamente para ouvir o resultado atualizado. “Exportar ZIP”, na tela Produção, reúne as partituras disponíveis da biblioteca.

A aba de cifras aceita inclusão e exclusão de linhas. A posição é medida em semínimas desde o início do MIDI, antes da remoção de compassos vazios de preparação. A armadura usa números negativos para bemóis e positivos para sustenidos. Alterar a armadura muda a notação da tonalidade inicial; não transpõe as notas. Modulações dos perfis revisados são preservadas.

## Propriedades da partitura e diagramas

Na aba Prévia, abra “Propriedades da partitura” para editar título, subtítulo, autor e inversão. “Aplicar e atualizar” salva as propriedades, gera um novo arquivo e recarrega a prévia automaticamente. O MIDI deve estar disponível e a melodia precisa estar escolhida ou ter uma revisão aprovada. Se ainda faltar essa preparação, as propriedades ficam salvas para a próxima geração. Cancelar descarta as alterações da janela.

O título vazio usa o nome do MIDI. Subtítulo e autor podem ficar vazios. Com o subtítulo automático habilitado, seu texto acompanha a inversão; um texto personalizado permanece como você escreveu. Cabeçalhos longos recebem espaço adicional e podem aumentar o número de páginas. Os limites são 160 caracteres para título, 200 para subtítulo e 120 para autor.

Em Configurações, escolha posição fundamental, 1ª inversão ou 2ª inversão como padrão. O padrão inicial continua sendo a 2ª inversão. A escolha vale para os MIDIs importados depois da mudança; os itens existentes mantêm o padrão adotado quando entraram na biblioteca. Nas propriedades, “Padrão adotado” usa essa escolha original, e uma inversão explícita altera somente a partitura selecionada. A mudança afeta os diagramas, preservando as notas da melodia e as cifras.

Uma atualização automática mantém os arquivos gerados anteriormente. Se houver erro, o app informa a pendência e desabilita a reprodução e o botão Salvar PDF até concluir a atualização. O ZIP também pede que as partituras pendentes sejam atualizadas antes de exportar o lote. O arquivo .mscz gerado, com os diagramas incorporados, fica na pasta de saída.

## Painéis e biblioteca

- **Visão geral**: total de MIDIs importados, prontos para o lote, partituras geradas e arquivos para revisão musical. O gráfico separa também as falhas de leitura ou conversão. Clique nos indicadores para abrir os arquivos correspondentes.
- **Biblioteca & editor**: busca por nome sem distinção de maiúsculas ou acentos, filtros por situação, remoção com opção de desfazer e editor em três etapas: melodia, cifras e prévia com reprodução e PDF.
- **Produção**: partituras disponíveis, páginas no resultado mais recente de cada MIDI, percentual concluído da biblioteca, abertura da prévia e exportação de ZIP.
- **Configurações**: temas Verde clássico, Oceano, Índigo, Grafite e âmbar e Azul claro; inversão padrão dos diagramas para novos MIDIs; caminhos do MuseScore, do Python e da pasta de saída.
- **Sobre**: versão atual obtida do aplicativo, descrição e acesso ao repositório.

Os indicadores refletem a biblioteca restaurada. As saídas permanecem na pasta escolhida quando o app é fechado. As quatro categorias do gráfico não se sobrepõem. Um arquivo com perfil revisado só está pronto para o lote quando a revisão está marcada e não há falha pendente. A contagem de páginas considera apenas o último resultado disponível por MIDI. Busca e filtros não alteram as partituras.

## Preferências e trabalho salvo

Ao fechar o aplicativo, são salvos o tema, o modo da barra lateral, os caminhos configurados, o tamanho e a posição da janela, a tela aberta, a busca, o filtro, a aba do editor e o arquivo selecionado. A biblioteca também mantém a escolha das pistas, o uso de perfis revisados, as cifras editadas, a tonalidade, as propriedades do cabeçalho e dos diagramas e os vínculos com análises e partituras geradas.

Os dados ficam em `%LOCALAPPDATA%/TeclasPartituras/settings.json`, com gravação atômica e cópia de recuperação. Se o salvamento falhar, a janela permanece aberta e mostra o motivo. Os arquivos MIDI e .mscz continuam nos locais originais; o app salva suas referências. Um MIDI alterado precisa de nova análise para impedir a reutilização de resultados de outro conteúdo. Mantenha os arquivos e pastas de trabalho para continuar de onde parou.

## Dependências desta versão

- Windows com .NET Desktop Runtime 10.
- MuseScore Studio 4 instalado, para gravação nativa, renderização, preparação do áudio e exportação PDF em segundo plano.
- Python 3.11 ou superior com Pillow, para o motor musical reaproveitado.

A interface, a leitura inicial do MIDI, a biblioteca, o controle das operações e a criação de ZIP são em C#. A preparação musical detalhada usa o motor Python validado no lote anterior. Este protótipo ainda não é uma implementação musical integralmente em C# nem um instalador que inclua todas as dependências.

No computador usado para preparar o projeto, as dependências já existem e o programa procura seus caminhos automaticamente. Em outro computador, configure os executáveis na interface. O aplicativo não instala programas, não baixa dependências e não envia os MIDIs pela internet.

A barra de título nativa acompanha as cores do tema no Windows 11. Em versões anteriores do Windows, usa os atributos de aparência suportados pelo sistema, mantendo os botões e o redimensionamento nativos.

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
    dotnet run --project tests/Teclas.Audition.Tests/Teclas.Audition.Tests.csproj
    dotnet run --project tests/Teclas.Export.Tests/Teclas.Export.Tests.csproj -- "C:\caminho\para\teste" "C:\caminho\para\partitura.mscz" "C:\Program Files\MuseScore 4\bin\MuseScore4.exe"
    python tools/qa_ui.py --midis "C:\caminho\para\midis"
    python tests/test_quantization.py
    python tests/test_score_properties.py
    python tests/quantization_regression.py --project "C:\caminho\para\Partituras de Teclado"
    python tools/qa_preferences.py --project "C:\caminho\para\Partituras de Teclado"

Projetos: Teclas.App (WPF), Teclas.Core (leitura MIDI, escuta por fonte, protocolo, métricas e exportação), Teclas.Tests, Teclas.Dashboard.Tests, Teclas.Settings.Tests, Teclas.Export.Tests e Teclas.Audition.Tests (testes executáveis). A pasta engine contém o motor musical e seus perfis. O executável precisa permanecer junto das DLLs e dessa pasta.

Os testes de configurações e de painéis usam dados sintéticos. Os testes de interface e integração musical exigem a pasta do acervo local, que não faz parte do repositório; informe seu caminho nos argumentos acima. Para a integração completa, execute `python tools/qa_engine.py "C:\caminho\para\Partituras de Teclado"`.

Os testes de escuta por fonte usam MIDIs sintéticos e um transporte MIDI simulado, sem emitir som. Para verificar a interface real, execute `Teclas.Partituras.exe --verify-track-audition "C:\pasta\report.json" "C:\pasta\Norte_ForroAbertura_Show.mid"` usando o MIDI revisado do acervo. Essa verificação utiliza sessões isoladas e confere separadamente a abertura do driver WinMM sem emitir notas.

Para verificar a edição e a persistência das propriedades em janelas WPF, execute `Teclas.Partituras.exe --verify-score-metadata "C:\pasta\report.json" "C:\pasta\Norte_ForroAbertura_Show.mid"`. O teste usa cópias do MIDI e configurações isoladas; não gera áudio nem partituras.

O script histórico tools/prepare_engine.py copia o motor original das ferramentas externas do projeto musical. Ele sobrescreve adaptações posteriores em engine, incluindo a correção de quantização; não o execute para compilar ou atualizar o aplicativo. tools/qa_engine.py verifica os 18 perfis contra o lote original e exercita a conversão de um MIDI novo. A pasta engine já inclui o motor necessário para o aplicativo, sem exigir a regeneração. A ferramenta histórica engine/build_keyboard_diagrams.py é preservada como origem das funções vetoriais reutilizadas pelo motor; executá-la isoladamente requer a partitura piloto na pasta externa partituras.

O número da versão é definido em `src/Teclas.App/Teclas.App.csproj` e aparece automaticamente na tela Sobre e nas propriedades do executável. As versões publicadas são marcadas com tags Git, como `v0.6.1`. O executável mantém o nome `Teclas.Partituras.exe` para preservar os caminhos já usados, e as preferências continuam na pasta `TeclasPartituras`.

Veja VALIDACAO.txt para os testes realizados. Esta é a prévia 0.6.1, destinada a testes no computador do usuário.
