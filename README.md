# TeclaScore

Versão 0.32.0 para Windows x64, em C# / WPF (.NET 10). Organiza título e artista ao importar MIDI, permite ouvir as pistas antes de escolher a melodia, prepara partituras com cifras e diagramas de teclado com inversão configurável por acorde, salva arquivos MuseScore .mscz, reproduz a partitura e exporta PDFs individuais ou em massa.

**Pasta de MIDIs (Ctrl+L):** em vez de importar tudo, escolha uma pasta e marque, dentro do app, os MIDIs que entram na biblioteca. A lista mostra subpasta, tamanho, data e se o arquivo já está na biblioteca; filtre por nome (sem diferenciar acentos) e use ▶ para ouvir antes. Nada é copiado: os cards apontam para os arquivos na própria pasta, e a pasta escolhida fica lembrada. É o botão principal do cabeçalho; para arquivos avulsos use Escolher arquivos… na mesma janela, arraste-os para o app ou Ctrl+O.

**Posições na biblioteca inteira:** em Posições dos diagramas, deixe marcada a opção Aplicar também às outras músicas. As cifras alteradas passam a valer nas músicas com o mesmo acorde tocado (nota e tipo, já com a transposição), e as partituras já geradas são atualizadas em seguida, com a versão anterior no Histórico. As outras cifras de cada música não mudam.

**Instalador completo:** cada entrega gera `debug/TeclaScore-Completo.iss` com a versão atual e o compila em `debug/saida/TeclaScore-X-Completo-Setup-x64.exe`, que instala o aplicativo e, se faltar, o MuseScore Studio 4 do kit. O script é gerenciado e refeito a cada versão; para personalizar, copie-o com outro nome.

**Palco e repertórios:** na Apresentação, mantenha várias listas (Festa junina, Missa…), gere o PDF de cada uma, vire a página com o pedal do teclado MIDI e use a virada de meia página. Na aba Cifras, ouça as alternativas sugeridas e troque com um clique.

**Praticar com a música e por trechos:** a partitura acompanha a prática; no tempo, ligue Tocar junto com a música (50%, 75% ou 100%). Escolha compassos de/até e Repetir até acertar para treinar uma passagem. O histórico de estudo mostra a evolução em gráfico.

**Repertório, tablet e vários computadores:** na Biblioteca, Repertório em PDF junta as partituras escolhidas em um arquivo com índice e marcadores; em Exportar há o PDF para tablet (margens estreitas, página 3:4, mesmo layout). Em Configurações, escolha uma pasta sincronizada para levar a biblioteca a outro computador.

**Estudo no tempo, acordes e histórico:** em Praticar, ligue No tempo para tocar com metrônomo no andamento escolhido e ganhar pontos pela precisão, ou escolha Acordes da cifra para treinar a mão esquerda com as posições dos diagramas. O botão 📈 mostra o histórico de estudo por música.

**Letra, cifras simples, tom e lote:** MIDIs de karaokê trazem a letra para a partitura. Cifras simplificadas geram tríades para iniciantes. Ao transpor, escolha o tom desejado. Melodias em lote propõe a pista de cada MIDI novo e manda os casos claros para a Fila.

**Praticar e ajustar a contagem:** na Prévia, Praticar abre o modo de espera: conecte um teclado MIDI (ou clique nas teclas da tela) e toque a nota destacada; a música avança a cada acerto. Em Configurações → Estudo, ajuste quantos milissegundos a música começa antes do fim da contagem.

**Biblioteca mais esperta:** o card avisa quando o MIDI não traz a melodia completa. A busca aceita cifras (F#dim) e tons (Dm, Ré menor). A Pasta de MIDIs mostra o que já foi gerado e o que chegou desde a última visita. Depois de gerar, o áudio é preparado em segundo plano.

**Acordes da música e exportação:** na Prévia, Acordes mostra todos os acordes da música com o teclado na posição escolhida e no tom final, para salvar em PNG ou imprimir. Exportar… salva MusicXML ou imagens PNG por página. Os nomes de instrumento aparecem em português.

**Pautas extras só quando necessário:** a melodia fica em uma pauta. Se as trocas de instrumento estiverem próximas demais para os nomes caberem sem sobreposição, aquela música é gerada com uma pauta por instrumento (nome na margem), em vez de parar em "Precisa de revisão". Cifras e diagramas ficam na pauta de cima.

**Posições lembradas:** toda posição aplicada em Posições dos diagramas fica guardada para aquele acorde e é usada nas cifras das próximas músicas analisadas; a janela já abre com elas.

**Várias pistas juntas:** na escuta das pistas, deixe Somar pistas ligado e use ▶ para acrescentar pistas à reprodução (■ tira uma delas), ou Ouvir marcadas para escutar juntas as pistas de melodia.

**PDFs em massa:** na Biblioteca, PDFs em massa lista os cards com partitura gerada, já marcados. Escolha a pasta e cada música vira um PDF direto nela, sem substituir arquivos existentes.

**Metrônomo e contagem:** na Prévia, o grupo Estudo liga o metrônomo (cliques nos tempos, com acento no início do compasso) e a contagem de um compasso antes de tocar. Os tempos vêm do MIDI de reprodução do próprio MuseScore, então respeitam anacruse, repetições, mudanças de andamento e a velocidade de estudo. O áudio com cliques é uma cópia em cache; a partitura não muda.

**Atalhos de teclado:** Espaço toca ou pausa, Esc para, Ctrl+←/→ avança ou volta 5 s, Ctrl+M liga o metrônomo e Ctrl+Shift+M a contagem. Ctrl+O importa, Ctrl+L abre a pasta de MIDIs, Ctrl+P salva PDF, Ctrl+F busca e Ctrl+1/2/3 e Ctrl+, navegam. F1 mostra a lista. Em campos de texto, Espaço, Esc e Ctrl+setas continuam editando.

**Mover a janela:** arraste o título da página ou os espaços vazios do cabeçalho, além da barra de título do Windows. Os botões de PDF e importação continuam clicáveis.

**Divisor da biblioteca:** arraste a linha entre os filtros e os cards para ajustar a altura. Também é possível usar Tab e as setas para cima/baixo. A posição fica salva, com espaço mínimo para acessar filtros e músicas. Salvar PDF usa o tamanho padrão e fica ao lado de Importar MuseScore.

**Acesso rápido:** Salvar PDF fica no cabeçalho fixo da Biblioteca, inclusive com controles compactos. Limpar biblioteca remove todos os cards, inclusive os filtrados, com confirmação e Desfazer durante a sessão; os arquivos são preservados. Subir oitava e Descer oitava atualizam todas as notas da partitura atual e sua reprodução, mantendo as cifras no mesmo tom e a versão anterior no Histórico. Valem também para MuseScore importado; estruturas não suportadas ou notas fora do intervalo válido são informadas sem alterar o original. Após essa edição, continue pelo Editor musical.

**Kit Inno Setup:** a pasta `debug` é atualizada automaticamente ao preparar e empacotar cada versão. `debug/app` contém TeclaScore, .NET e Python 3.13.13 com Pillow 12.3.0; `debug/prerequisitos` contém o MSI oficial do MuseScore Studio 4.6.5. Consulte `debug/LEIA-ME-INNO.txt`. Scripts `.iss` próprios na raiz de debug são preservados. Os binários só mudam após recompilar e preparar uma entrega.

**Nomes ao salvar:** PDFs e arquivos MuseScore usam **artista/intérprete - título**, conforme as propriedades da partitura. Sem intérprete, usam apenas o título. A regra vale para PDF individual e em lote, geração MIDI, novas versões editadas e arquivos do ZIP. Caracteres inválidos são substituídos e nomes repetidos recebem numeração; as versões anteriores permanecem intactas.

A preparação de áudio aparece discretamente no rodapé, com progresso e cancelamento, sem abrir a caixa central nem recarregar a imagem da partitura. MIDIs completos, pistas e mapas de acompanhamento já preparados são reutilizados durante a sessão, dentro de limites de memória. A primeira preparação de uma partitura ainda depende da renderização do MuseScore. Desmarcar **Acompanhar páginas durante a reprodução** evita preparar automaticamente o mapa visual.

Em **Configurações → Pasta de saída padrão**, escolha ou digite uma pasta e clique em **Salvar pasta padrão**. A escolha é restaurada nas próximas aberturas e vale para novas gerações e versões, PDF individual, PDFs em lote e ZIP. As janelas de PDF, ZIP e PDFs em lote começam na última pasta usada numa exportação concluída, inclusive após reabrir o app. Sem exportação anterior, usam a pasta padrão. Cancelar ou falhar não altera a lembrança; um lote com PDFs salvos registra a pasta mesmo se outros itens falharem. Salvar um novo padrão em Configurações redefine também o destino da próxima exportação. Se a pasta estiver indisponível, o app informa o erro sem trocar silenciosamente de destino. Partituras existentes permanecem nos seus locais; trabalhos já enfileirados conservam o destino registrado. O app confere a disponibilidade da pasta quando precisa gravar o resultado.

O aplicativo sempre inicia em **Visão geral**, mantendo biblioteca e demais preferências salvas. No card da Biblioteca, reproduza o **MIDI completo**, com todas as pistas, antes de escolher a melodia ou gerar a partitura. Os controles permitem pausar, continuar e parar; somente uma reprodução permanece ativa. A sonorização usa o sintetizador MIDI do Windows, respeitando os canais, programas e andamento do arquivo.

Em **Posições dos diagramas**, cada cifra aparece apenas uma vez, com a quantidade de ocorrências. A posição selecionada vale para todas as repetições e a imagem muda imediatamente. Passe o mouse para conferir as notas do grave ao agudo. Variações antigas permanecem salvas até você escolher uma posição comum; **Cancelar** descarta as alterações da janela.

Na **Prévia**, use **Compactar controles** para reduzir o player e recolher as opções avançadas e os botões de ferramentas. Reproduzir, pausar, parar e a posição continuam acessíveis. **Expandir controles** recupera as opções; a preferência fica salva ao fechar o app.

A versão **0.19.0** permite transpor partituras MuseScore importadas e versões editadas: em **Prévia → Transpor**, escolha o intervalo em relação à versão aberta, prepare a prévia, compare **Original / Proposta transposta**, ouça e use **Salvar nova versão**. O original e as versões anteriores ficam preservados no Histórico. Abrir novamente Transpor inicia outro intervalo relativo à cópia então selecionada; não reutiliza o deslocamento do MIDI original.

Notas, grafia, armaduras e cifras compatíveis mudam juntas. Diagramas TeclaScore reconhecidos são regenerados com sua inversão. Imagens e diagramas não reconhecidos dentro dos compassos bloqueiam a proposta até você escolher explicitamente removê-los apenas da cópia; as observações identificam os trechos afetados. Estruturas não suportadas são explicadas e não são transpostas parcialmente. Compare todas as páginas antes de salvar.

Em **Configurações → Preparar este computador**, use **Detectar e verificar** para localizar e testar Python 3.11+ com Pillow e MuseScore Studio 4. Os testes usam uma imagem em memória e uma partitura sintética. Se faltar Pillow, **Preparar Python do TeclaScore** cria um ambiente próprio e baixa a versão validada do PyPI; exige internet e não modifica o Python global. Os botões de download abrem os sites oficiais. **Usar estas configurações** aplica apenas caminhos que passaram nos testes; fechar ou cancelar não altera os caminhos salvos. O instalador e o ZIP oficiais incluem o .NET Desktop Runtime 10 necessário para abrir o aplicativo.

A Biblioteca agora tem uma **lixeira vermelha no próprio card**, com descrição **Remover** e acesso por teclado. O rodapé oferece desfazer a última remoção da sessão, preservando os arquivos nas pastas.

Passagens MIDI muito rápidas recebem refinamento rítmico local adicional quando necessário, sem remover ataques para fazê-los caber. Se ainda houver um bloqueio, a **Revisão guiada** recebe o compasso, a posição e os detalhes enviados pelo motor, com acesso à escolha de pista para aquele trecho. O ajuste automático não certifica a intenção musical: ouça e confira a notação.

Em **Produção**, cada partitura tem **Remover**, com **Desfazer remoção** no rodapé. A remoção fica salva na biblioteca; os arquivos MIDI, MuseScore e PDF continuam nas pastas. O desfazer recupera o último item removido durante a sessão.

O importador considera nomes tanto em **título – intérprete** quanto em **intérprete – título**, priorizando os campos declarados no MIDI. A inferência pelo texto é uma sugestão; nomes ambíguos ficam inteiros no título, com artista vazio, para conferência. Em arquivos já importados, abra **Propriedades da partitura → Usar sugestões**, ou **Trocar título e intérprete**, e confira antes de **Aplicar e atualizar**. A autoria preenchida é preservada; não se presume que o intérprete seja o compositor.

Em **Sobre → Desempenho**, confira as operações recentes, exporte o relatório JSON ou limpe os registros. O histórico é local, rotativo e limitado a 512 registros; não guarda títulos, nomes de arquivos, caminhos ou mensagens de erro. Duração é tempo decorrido. CPU é a diferença observada no processo, pode ser parcial e inclui outras tarefas simultâneas do aplicativo; processos filhos ficam separados. Memória mostra o maior pico observado desde o início do processo, não a alocação exclusiva de uma operação. Não some durações sobrepostas.

## Testes automáticos e medições

O workflow **TeclaScore CI** executa no Windows a cada push em `main`, pull request ou disparo manual. Compila a solução, executa os projetos de teste console (não apenas `dotnet test`), testes Python, paridade de diagramas, verificação WPF com sessão isolada e benchmarks sintéticos. Os relatórios e logs ficam nos artefatos da execução por 14 dias. Nenhum MIDI ou configuração pessoal é necessário.

Para repetir localmente em PowerShell 7.2+, com .NET SDK 10, Python 3.13 e Pillow instalado a partir de `tools/ci/requirements.txt`:

```powershell
./tools/verify_ci.ps1 -PythonPath python -OutputDirectory C:/Temp/teclas-ci-nova
```

Use uma pasta nova/vazia. Com `-MuseScorePath "C:/Program Files/MuseScore 4/bin/MuseScore4.exe"`, o runner também executa as integrações nativas disponíveis, usando partituras sintéticas. Sem esse argumento, esses casos são listados como **skipped**, não como aprovados. Os testes automatizados não substituem a revisão visual e musical de um repertório real.

O projeto `tools/Teclas.Benchmarks` mede leitura de MIDIs sintéticos pequenos/médios/grandes, sugestão de melodia, metadados e conversão WAV. Use `--output relatorio.json --iterations 5` ao executá-lo. O JSON inclui aquecimento, amostras, mediana, p95, CPU e bytes alocados no código gerenciado. Invariantes funcionais são obrigatórias; os tempos são informativos e só devem ser comparados sob ambiente e carga equivalentes. Esta versão cria a base de medição, sem prometer aceleração por si só.

Em **Produção → Exportar PDFs**, selecione as partituras desejadas ou use **Selecionar todas**, escolha uma pasta e inicie a exportação. Cada música recebe um PDF com todas as páginas, dentro de uma nova pasta do lote. Partituras MuseScore importadas e versões editadas também podem ser exportadas. Itens ainda não gerados, ausentes ou com alterações pendentes mostram o motivo e precisam ser preparados antes. O painel acompanha cada arquivo; cancelar preserva os PDFs já concluídos, e uma falha permite continuar os demais. Ao final, confira o resumo e abra a pasta de resultados.

A versão 0.17.0 também reduz espaços excessivos entre sistemas quando há trocas de instrumento, sem diminuir notas, diagramas ou nomes. A compactação só é aplicada se reduzir páginas e passar pela conferência de conteúdo e geometria. Gere novamente um MIDI existente para receber o novo espaçamento; a versão anterior permanece no Histórico. Partituras importadas ou editadas continuam com sua diagramação salva ao exportar PDF.

A versão 0.16.0 acrescenta recuperação automática de rascunhos, inserção e exclusão de notas, pausas e cópia de vozes entre compassos compatíveis, sugestões de inversões com menor deslocamento e modo apresentação com repertório e anotações. Também corrige a sobreposição de nomes de instrumentos em melodias combinadas, ampliando o espaço e a paginação quando necessário. As quatro melhorias preservam os originais e passam pela prévia antes de salvar mudanças musicais.

A versão 0.15.0 acrescenta pistas por faixa de compassos, acompanhamento da pauta durante a reprodução, seleção de ataques na página, revisão visual com comparação e backup portátil da biblioteca. A versão 0.14.0 acrescenta arranjos por nível para partituras geradas de MIDI e arquivos MuseScore importados, com mudanças explicadas, comparação e salvamento em nova versão. A versão 0.13.1 identifica automaticamente o instrumento na entrada e nas trocas de fonte da melodia combinada. A versão 0.13.0 acrescenta uma fila persistente de conversões MIDI, com retomada após interrupção ou falha, e combina várias pistas em uma única pauta com prioridade ajustável. A versão 0.12.0 acrescentou um editor musical com rascunho, desfazer/refazer, prévia, reprodução e salvamento em nova versão. A versão anterior trouxe sugestões de melodia com trechos para comparar, justificativas e alternativas de cifras, e diagnóstico estrutural de partituras MuseScore por pauta, compasso e voz. O painel de progresso continua acompanhando a preparação, análise e geração.

Em **Comparar pistas**, a ordem de escuta considera notas simultâneas, legato, registro, repetição e duração. Ouça trechos a 25%, 50%, 75% ou 100%; somente **Usar esta pista como melodia** muda a seleção. Confirmar a mesma pista preserva as cifras já ajustadas. A ordem é uma sugestão heurística, sem garantia de identificar a melodia; compare especialmente fontes duplicadas ou com várias vozes.

Na aba **Cifras**, selecione uma linha para ver a justificativa e eventuais nomes alternativos. O contexto tonal, o baixo e os acordes vizinhos ajudam a distinguir nomes que usam as mesmas notas; cromatismos explícitos são preservados. A Revisão guiada localiza ambiguidades, pouca evidência, lacunas de acompanhamento e conflitos de tonalidade. Esses avisos não bloqueiam a geração. As revisões musicais já aprovadas mantêm seus resultados anteriores.

Na **Prévia**, **Diagnóstico da partitura** confere o arquivo salvo sem alterá-lo. Partituras .mscz importadas e cópias com apresentação melhorada recebem o diagnóstico automaticamente. Ele identifica notas inválidas/duplicadas, cifras repetidas na mesma posição, duração excessiva quando interpretável, imagens ausentes e problemas básicos de cabeçalho/página. Compassos são localizados pela ordem do arquivo, que pode diferir da numeração impressa. Quiálteras, posições especiais e estruturas desconhecidas têm limitações explícitas; a análise não decide a intenção musical nem certifica a aparência visual. Uma falha ou cancelamento dessa conferência preserva a cópia já salva e seu Histórico.

Repositório: [AdemirRJesusJR/teclascore](https://github.com/AdemirRJesusJR/teclascore).

## Editor musical

Na **Prévia**, abra **Editor musical**. Escolha a pauta e o compasso; nas abas **Notas**, **Ritmo** e **Cifras**, ajuste os elementos existentes. Use notas como C4 (dó central), F#4 e Bb3. A posição indicada conta semínimas dentro do compasso; a numeração usa a ordem do arquivo, podendo diferir da impressa. O app prepara um mapa da página: clique em um ataque para selecionar suas notas na tabela. Acordes e vozes simultâneas mostram opções para escolha; o clique não escolhe uma voz arbitrariamente.

**Desfazer** e **Refazer** atuam sobre o rascunho. **Atualizar prévia** verifica as mudanças e renderiza todas as páginas. **Ouvir partitura** reproduz o rascunho inteiro em 50%, 75% ou 100%, com pausa e controle de posição. Use **Ouvir compasso** para ouvir o compasso selecionado ou **Ouvir seleção** para ouvir o ataque selecionado; os trechos incluem todas as pautas e vozes que soam naquele intervalo. Confira as páginas e o som antes de **Salvar nova versão**; qualquer nova alteração exige outra atualização da prévia. Fechar com mudanças pede confirmação de descarte.

Mudar uma duração exige compensar outra nota ou pausa da mesma voz e manter as posições de cifras, imagens, marcações e elementos não editados. Também é possível inserir notas em pausas, excluir notas preservando a duração e copiar vozes entre compassos compatíveis. Arraste livre na pauta e edição de cadeias de ligaduras ou quiálteras continuam restritos. Pautas transpositoras, tablaturas, percussão, partes vinculadas e estruturas especiais indicam restrições locais ou globais.

Cifras simples reconhecidas podem ser alteradas. Quando a cifra tem um diagrama TeclaScore associado de forma inequívoca, o desenho e sua inversão acompanham apenas aquela ocorrência. Diagramas desconhecidos e associações ambíguas são preservados e orientam edição no MuseScore. A conferência estrutural não certifica espaçamento visual: revise todas as páginas renderizadas, principalmente depois de aumentar o texto de uma cifra.

Ao salvar, o app cria uma nova cópia MSCZ e registra original e edição no Histórico. A partitura editada passa a ser a fonte para reprodução, PDF, propriedades e apresentação; as abas de preparação a partir do MIDI ficam desabilitadas para evitar perda das correções. Para voltar ao MIDI, restaure no Histórico uma versão anterior à edição. A cópia editada e seu estado continuam disponíveis mesmo se o MIDI original mudar ou ficar ausente.

## Pistas por trecho

Na aba **Melodia**, abra **Pistas por trecho**. Defina faixas sem sobreposição, como compassos 1–8 para guitarra e 9–32 para piano. A janela mostra o mapa de compassos, incluindo anacruse e mudanças de fórmula; compasso 1 é o primeiro compasso da partitura, após o corte do silêncio inicial indicado na janela.

Dentro de uma faixa, a fonte escolhida prevalece mesmo se não estiver marcada na seleção geral; seus silêncios permanecem silenciosos. Fora das faixas vale a prioridade geral das pistas. Não se reinicia uma nota que começou fora do trecho permitido. As indicações de instrumento acompanham as mudanças reais de fonte. Aplicar uma mudança invalida a análise anterior e exige nova geração; a cópia anterior fica no Histórico. A fila congela as faixas junto com a receita. Perfis revisados continuam usando a revisão aprovada até você desmarcar essa opção.

## Acompanhamento da partitura

Na **Prévia**, **Preparar acompanhamento** permite clicar em compassos e ataques para iniciar a reprodução naquele ponto. **Reproduzir** também prepara esse mapa quando **Acompanhar páginas durante a reprodução** está marcado. O destaque usa posições e tempos exportados pelo MuseScore, incluindo repetições e mudanças de andamento; a opção controla a troca automática de páginas e fica salva nas preferências. Mapas válidos são reutilizados durante a sessão; alterações na partitura, velocidade ou páginas exigem nova preparação.

No Editor musical, clicar na pauta seleciona os elementos existentes para corrigir notas e durações na tabela. Depois de editar, atualize a prévia para reconstruir as posições antes de clicar ou ouvir trechos. A inserção em pausas é feita pelos controles do editor; o clique na página não oferece arraste livre. Quando não há associação segura com os eventos, o app informa a limitação e mantém a edição pela tabela. Se o mapa de uma estrutura não puder ser validado, a reprodução inteira continua disponível sem um destaque estimado.

## Revisão visual

Na **Prévia**, abra **Revisão visual** em uma partitura MIDI já gerada ou MSCZ importada. A lista localiza colisões entre cifras, diagramas e textos reconhecidos, elementos além das margens e alguns problemas de paginação. Escolha um achado para conferir a página e o detalhe ampliado.

Prepare a proposta, compare **Original / Proposta corrigida** e salve uma nova versão. Os ajustes são conservadores: espaçamento local, reposicionamento automático e paginação quando seguros. Notas, durações, cifras e imagens são conferidas novamente; propostas sem melhoria validada não são publicadas. A revisão não cobre toda a tipografia musical, colisões entre todas as notas/articulações nem a intenção estética. Leia as limitações e confira as páginas.

## Backup portátil da biblioteca

Em **Configurações**, use **Salvar backup** para criar um arquivo .teclasbackup com MIDIs, MSCZ, páginas de prévia, versões do Histórico, cabeçalhos, coleções e escolhas musicais. Arquivos indisponíveis são listados para você decidir se deseja um backup parcial. As análises e os áudios temporários não são incluídos; são refeitos quando necessários.

**Restaurar backup** confere a integridade, pede uma pasta de destino e adiciona as músicas à biblioteca atual. Mantém as músicas e preferências existentes; as configurações de Python/MuseScore permanecem próprias de cada computador. Restaurar o mesmo backup na mesma pasta não duplica as músicas; arquivos restaurados que foram alterados são preservados e exigem outro destino. A fila de conversões não faz parte do backup. No outro computador, instale os componentes necessários para gerar e reproduzir partituras.

## Arranjos por nível

Na **Prévia**, abra **Arranjo por nível**. Escolha a pauta e a voz, selecione **Intermediário** ou **Iniciante** e use **Recalcular mudanças**. A tabela mostra o compasso, antes/depois e o motivo de cada proposta; as observações identificam os trechos preservados. **Original** mostra a versão que estava aberta ao entrar nessa janela e serve para comparação.

Os perfis aplicam regras conservadoras à voz selecionada: redução de notas simultâneas, ornamentos simples e repetições rítmicas quando seguras, além de ajustes de oitava do conjunto quando apropriados. Quando há imagens ou diagramas, o ritmo e os ornamentos são preservados para proteger o espaçamento. Não medem a habilidade de uma pessoa nem garantem que toda a música se torne fácil. Pautas e vozes restantes são preservadas. Estruturas complexas recebem limitações explícitas.

Use **Preparar prévia**, compare **Original / Arranjo preparado**, passe por todas as páginas e ouça os resultados. A reprodução inclui a partitura inteira. Mudar as opções invalida a prévia anterior. **Salvar nova versão** exige uma proposta atual com alterações; quando não há adaptação útil, o app informa e não salva uma cópia vazia.

O fluxo funciona com partituras já geradas de MIDI e **MSCZ do MuseScore 4 importados**. A nova versão mantém o original no Histórico e identifica o nível, a pauta e a voz escolhidos. Cifras, diagramas, nomes de instrumentos e cabeçalho são preservados; a cópia adaptada passa a usar o fluxo do Editor musical. Para voltar à geração pelo MIDI, restaure uma versão anterior pelo Histórico.

Veja [ROTEIRO-DE-TESTES.txt](ROTEIRO-DE-TESTES.txt) para conferir as fases entregues: importação, melodia/harmonia, progresso, diagnóstico, editor, fila, arranjos e persistência.

## Para testar

1. Abra Aplicativo/Teclas.Partituras.exe.
2. Confira os caminhos em Configurações e escolha a pasta de saída.
3. Importe arquivos .mid, .midi, .kar ou .mscz pelo botão ou arrastando-os para o app. A Biblioteca abre automaticamente com o primeiro MIDI novo selecionado, sem a busca ou os filtros anteriores. Um arquivo já importado é selecionado sem duplicação.
4. Nos 18 MIDIs com perfil revisado, mantenha a revisão aprovada marcada. Use “Gerar lote revisado” para processar os que ainda não foram gerados.
5. Na aba Melodia, use ▶ na coluna Ouvir para escutar cada fonte isoladamente. Compare as pistas, marque a melodia, clique em “Analisar acordes”, confira/edite as cifras e clique em “Gerar partitura”. Com a revisão aprovada habilitada, é possível ouvir; desmarque essa opção para mudar a seleção manualmente.
6. Na aba Prévia, clique em “Reproduzir” para ouvir, “Pausar” para continuar depois e “Parar” para voltar ao início. A barra de posição permite avançar ou voltar. “Salvar PDF” exporta todas as páginas. “Gerar partitura” já salva o arquivo .mscz automaticamente; use “Pasta de saída” para acessá-lo.
7. Use o botão na parte inferior da barra lateral para alternar entre menus completos e somente ícones. Passe o mouse sobre um ícone para ver o nome do menu.
8. Escolha seu tema em Configurações. Ao fechar e reabrir, o app restaura suas preferências e sua biblioteca.
9. Para retirar um item da lista, use o ícone vermelho de lixeira no próprio card da Biblioteca. A última remoção pode ser desfeita enquanto o app estiver aberto. Seus arquivos MIDI e .mscz permanecem nas suas pastas.

A escuta de pistas funciona antes da análise, pelo sintetizador MIDI do Windows, sem abrir o MuseScore. Cada linha identifica uma combinação de pista, canal e instrumento, importante para MIDIs que concentram vários instrumentos na mesma pista. A escuta começa na primeira nota dessa fonte, oferece pausa, controle de posição e velocidades de 25%, 50%, 75%, 100%, 125% e 150% e não marca Melodia automaticamente. Ao trocar de fonte, MIDI, aba ou tela, o áudio anterior para. Timbres personalizados por SysEx podem soar diferentes no sintetizador General MIDI.

O controle Velocidade fica ao lado de Pausar/Parar na escuta de pistas. A mudança vale imediatamente, preserva a altura das notas e fica salva ao fechar. 100% retoma a velocidade original; a posição e a duração indicam o tempo original do MIDI. Essa preferência não muda o andamento da partitura gerada.

Para ouvir a partitura gerada na aba Prévia, o MuseScore instalado prepara o áudio em segundo plano na primeira reprodução. O editor do MuseScore não precisa ser aberto. Nas próximas reproduções, o áudio é reutilizado enquanto a partitura permanecer igual. Essa reprodução corresponde ao arquivo .mscz salvo: depois de editar as cifras, gere a partitura novamente para ouvir o resultado atualizado. “Exportar ZIP”, na tela Produção, reúne as partituras disponíveis da biblioteca.

A aba de cifras aceita inclusão e exclusão de linhas. A posição é medida em semínimas desde o início do MIDI, antes da remoção de compassos vazios de preparação. A armadura usa números negativos para bemóis e positivos para sustenidos. Alterar a armadura muda a notação da tonalidade inicial; não transpõe as notas. Modulações dos perfis revisados são preservadas.

Ao trocar a pista de melodia, **Gerar partitura** refaz a análise e cria a partitura da seleção atual. Você também pode usar **Analisar acordes** antes para conferir as sugestões. Enquanto a nova geração estiver pendente, a prévia anterior fica identificada e seu áudio/PDF não são apresentados como atualizados. A cópia anterior permanece no Histórico. Se apenas a análise temporária desaparecer, o app a reconstrói preservando as cifras e a armadura editadas.

Ataques próximos que não cabem pelo arredondamento habitual recebem um ajuste local de ritmo, limitado à menor subdivisão suportada, sem eliminar ataques da linha melódica nem inverter sua ordem. Os trechos ajustados aparecem na Revisão guiada; passagens que excedem esse limite continuam exigindo revisão.

## Propriedades da partitura e diagramas

Ao importar um MIDI, o app organiza o cabeçalho automaticamente. Por exemplo, **Roberto Carlos - Luz divina (karaoplay.com).mid** preenche **Título: Luz divina**, **Artista/intérprete: Roberto Carlos** e deixa o compositor vazio quando não houver informação explícita. Nomes de sites, espaços extras, underscores e etiquetas finais de MIDI/karaokê são removidos; edições como “Ao vivo”, “Acústico” e números musicais são mantidos.

O organizador funciona localmente, sem consultas à internet. Usa campos explícitos do MIDI quando disponíveis e padrões no nome do arquivo ou cabeçalho karaoke. Ignora nomes de instrumentos, copyright, programadores de MIDI e letras como fontes de autoria. Uma sugestão baseada em convenções de nomes pode precisar de revisão; a janela informa sua origem. Compositor é preenchido somente quando identificado em um campo declarado, nunca apenas por ser o artista. Textos excessivamente longos recebem uma sugestão abreviada, mantendo o arquivo original intacto.

Na aba Prévia, abra “Propriedades da partitura” para editar título, artista, subtítulo, autor e inversão. “Aplicar e atualizar” salva as propriedades, gera um novo arquivo e recarrega a prévia automaticamente. O MIDI deve estar disponível e a melodia precisa estar escolhida ou ter uma revisão aprovada. Se ainda faltar essa preparação, as propriedades ficam salvas para a próxima geração. Cancelar descarta as alterações da janela.

Partituras já presentes na biblioteca mantêm os dados salvos. Nas Propriedades, **Usar sugestões** apresenta uma proposta para organizar itens antigos, preservando o autor/compositor preenchido e o subtítulo manual. As correções do usuário não são refeitas ao reabrir ou reimportar o mesmo arquivo. O Histórico guarda artista e compositor separadamente; a busca também encontra o artista. O nome físico do MIDI permanece intacto.

O título vazio usa a sugestão organizada para o MIDI. Artista, subtítulo e autor podem ficar vazios. O subtítulo é opcional e fica vazio por padrão; somente o texto preenchido pelo usuário aparece na partitura. Alterar a inversão dos diagramas não modifica esse texto. Cabeçalhos longos recebem espaço adicional e podem aumentar o número de páginas. Os limites são 160 caracteres para título, 200 para subtítulo e 120 para artista ou autor. Quando informado, o artista aparece como “Intérprete” no cabeçalho, separado da autoria; ambos são guardados em campos distintos no MuseScore.

Em Configurações, escolha posição fundamental, 1ª inversão ou 2ª inversão como padrão. O padrão inicial continua sendo a 2ª inversão. A escolha vale para os MIDIs importados depois da mudança; os itens existentes mantêm o padrão adotado quando entraram na biblioteca. Nas propriedades, “Padrão adotado” usa essa escolha original, e uma inversão explícita altera somente a partitura selecionada. A mudança afeta os diagramas, preservando as notas da melodia e as cifras.

Uma atualização automática mantém os arquivos gerados anteriormente. Se houver erro, o app informa a pendência e desabilita a reprodução e o botão Salvar PDF até concluir a atualização. O ZIP também pede que as partituras pendentes sejam atualizadas antes de exportar o lote. O arquivo .mscz gerado, com os diagramas incorporados, fica na pasta de saída.

Após analisar um MIDI, abra **Posições dos diagramas** na aba Cifras ou Prévia. Cada linha reúne todas as ocorrências da mesma cifra. Escolher posição fundamental, 1ª inversão, 2ª inversão ou Padrão da partitura altera todas elas, sem botão adicional. C, Cm e C7 continuam sendo acordes separados. Cifras com variações antigas mostram **Variações salvas**: escolha uma posição para unificá-las. Abrir a janela ou editar outra cifra não modifica essas variações. **Usar padrão em todos** remove as escolhas personalizadas. As sugestões confortáveis usam a sequência completa da música e uma única posição por cifra; o cálculo é uma aproximação e deve ser conferido no teclado.

**Aplicar e atualizar** salva as escolhas e gera uma nova partitura automaticamente. O MIDI precisa estar disponível; caso contrário, as escolhas ficam salvas até reconectá-lo e gerar. Cancelar descarta as edições da janela. O Histórico recupera as posições daquela versão. Alterar a inversão geral mantém as escolhas individuais; Padrão da partitura acompanha a configuração geral da música. A transposição mantém a posição escolhida para cada acorde, e a tabela mostra as cifras no tom original. As informações de inversão ficam disponíveis no app, sem acrescentar um subtítulo à partitura.

Para C, posição fundamental é Dó–Mi–Sol; 1ª inversão é Mi–Sol–Dó; 2ª inversão é Sol–Dó–Mi. Os diagramas podem deslocar o conjunto inteiro em uma oitava para caber no teclado desenhado, preservando a inversão. A personalização vale para os diagramas gerados a partir de MIDI; imagens de diagramas em partituras MuseScore importadas são preservadas.

Trocar a pista de melodia ou alternar o perfil revisado descarta a tabela de cifras para uma nova análise, incluindo as posições individuais. Uma partitura já gerada continua disponível no Histórico com suas escolhas anteriores.

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

## Leitura, estudo e organização — 0.7.0

A Prévia mostra todas as páginas. Use miniaturas, anterior/próxima, zoom, Largura, Página ou Tela cheia; Esc sai da tela cheia. Use o Editor musical para alterar notas, durações e cifras dos elementos suportados; o visualizador mostra as páginas da versão salva.

Em **Organizar**, marque favoritos e defina coleção/repertório e gênero. A busca considera nome, título, autor, coleção e gênero, sem diferenciar acentos. A biblioteca oferece filtros de favoritos e coleção. Importar limpa os filtros para exibir o novo arquivo.

**Histórico** compara a versão escolhida com a atual e permite restaurá-la. Restaurar preserva as outras cópias e recupera metadados, transposição, pistas e cifras daquela geração. Uma análise temporária ausente pode precisar ser refeita. O histórico passa a registrar a versão disponível na biblioteca ao abrir esta versão do app; arquivos antigos fora desse registro não são descobertos automaticamente. Mantenha as pastas de saída para acessar todas as cópias.

No modo de estudo, marque **A** na posição inicial e **B** na final para repetir um trecho. Limpar A–B remove os marcadores. A velocidade de 25% a 150% é independente da escuta de pistas e permanece salva. O MuseScore prepara uma cópia de áudio por velocidade, preservando afinação, repetições e mudanças de andamento; a primeira reprodução pode levar alguns instantes. A posição usa o tempo do andamento original. Trocar de partitura limpa os marcadores.

## Revisão guiada e transposição

Após Analisar acordes, **Revisão guiada** lista os pontos identificados, com compasso e tempo. Escolha uma pista e ouça apenas aquele compasso, inclusive mais devagar. Editar cifra abre o acorde correspondente na tabela. A revisão aponta incertezas da análise e refinamentos rítmicos; não garante a identificação de todos os erros musicais. Problemas rítmicos que não possam ser resolvidos automaticamente exigem rever a pista ou editar a cópia no MuseScore.

**Transpor**, na Prévia de um MIDI, permite deslocar de −12 a +12 semitons. O intervalo é relativo ao MIDI original, sem acumular alterações. A nova partitura atualiza notas, armadura, modulações, cifras e diagramas. A tabela de cifras continua no tom original. O arquivo MIDI e as versões anteriores permanecem intactos. Para arquivos .mscz importados e versões editadas, Transpor oferece um fluxo separado de comparação e salvamento; o intervalo parte da versão aberta e diagramas externos exigem tratamento explícito.

## Melhorar partituras MuseScore

Use **Importar MuseScore**, no topo do app, para abrir um **.mscz do MuseScore 4** e clique em **Melhorar apresentação** ou Propriedades. Edite título, subtítulo e autor e escolha apresentação padrão, confortável ou compacta. O app ajusta margens, tamanho de pauta e cabeçalho e salva uma nova cópia. Notas, ritmos, vozes, cifras, repetições e imagens são conferidos antes da entrega. O Histórico permite comparar antes/depois. Se a preservação não puder ser confirmada, a operação informa a falha e mantém o original.

Arquivos de versões antigas devem ser abertos e salvos em uma nova cópia pelo MuseScore 4 antes da importação. Quando há imagens ou diagramas, as quebras existentes são preservadas para evitar corte dos desenhos; confirme visualmente o resultado, sobretudo em partituras com posicionamentos manuais. A melhoria não inventa cifras nem troca automaticamente a inversão de diagramas externos.

## Instalação e atualizações

Baixe **TeclaScore-0.20.4-Setup-x64.exe** na página da versão e siga o assistente em português. Escolha a pasta e os atalhos. A instalação é para o usuário atual e não exige permissões de administrador. O desinstalador fica disponível nos Aplicativos instalados do Windows; ele preserva biblioteca, configurações e partituras. Também é possível extrair o ZIP completo e executar **Instalar.cmd**, ou abrir Teclas.Partituras.exe na pasta extraída. A instalação por usuário cria atalhos e mantém cada versão separada em `%LOCALAPPDATA%/Programs/TeclaScore/versions`; as preferências continuam em `%LOCALAPPDATA%/TeclasPartituras`. A versão anterior é preservada.

Na tela **Sobre → Instalação e atualizações**, confira dependências e consulte a última versão publicada no GitHub. A consulta só ocorre ao clicar. Baixe o ZIP pela página da versão e selecione-o no app; o instalador confere SHA-256 e conteúdo antes de instalar. Se a consulta não estiver disponível, selecione também o arquivo SHA256SUMS da mesma versão. A nova instalação não fecha o app sem sua escolha. O assistente localiza e valida Python/MuseScore e oferece o preparo isolado de Pillow; o Python privado já acompanha a distribuição; instalar MuseScore continua pelo instalador oficial.

## Dependências desta versão

- Windows 10 22H2 ou Windows 11 de 64 bits. O runtime .NET Desktop 10 está incluído nos pacotes oficiais.
- MuseScore Studio 4 instalado, para gravação nativa, renderização, preparação do áudio e exportação PDF em segundo plano.
- Python com Pillow incluído no pacote, para o motor musical reaproveitado.

A interface, a leitura inicial do MIDI, a biblioteca, o controle das operações e a criação de ZIP são em C#. A preparação musical detalhada usa o motor Python validado no lote anterior. Este protótipo ainda não é uma implementação musical integralmente em C# e Python/Pillow agora são incluídos no pacote; MuseScore permanece uma instalação separada, cujo MSI está no kit debug.

Em uma instalação nova, o app localiza automaticamente seu Python privado. O MuseScore deve ser instalado pelo MSI oficial incluído no kit debug ou pelo site oficial. Em outro computador, use Preparar este computador. Downloads oficiais e preparo do ambiente Python exigem uma ação explícita; nada é instalado ao abrir o assistente. Os MIDIs não são enviados pela internet.

A barra de título nativa acompanha as cores do tema no Windows 11. Em versões anteriores do Windows, usa os atributos de aparência suportados pelo sistema, mantendo os botões e o redimensionamento nativos.

## Cuidados de edição e limites

- A análise automática de um MIDI novo é uma proposta para revisão. O programa não recria uma melodia ausente, nem distingue com certeza uma voz de um acompanhamento.
- Os perfis conhecidos são reconhecidos pelo conteúdo SHA-256, não pelo nome. Há 18 revisões completas, 79 casos sem melodia completa e 14 com melodia parcial no catálogo.
- O Editor musical altera notas, durações e cifras, permite inserir notas em pausas e excluir preservando a duração. Estruturas complexas e arraste livre continuam pelo MuseScore.
- Os diagramas são imagens vetoriais incorporadas. Ao mudar uma cifra dentro do MuseScore, atualize também seu desenho.
- Arquivos de saída existentes recebem outro nome, sem sobrescrita. MIDIs originais não são modificados.
- Cada geração inclui uma pasta de revisão com imagens de todas as páginas, MusicXML e relatório de validação.
- Configurações, biblioteca e análises temporárias ficam em %LOCALAPPDATA%/TeclasPartituras.
- MIDI tipo 2 e tempo SMPTE não são suportados nesta versão.

## Código e compilação

Abra Teclas.Partituras.sln. A compilação normal não usa bibliotecas NuGet externas; NuGet.Config mantém as fontes vazias. Para publicar com runtime incluído, use explicitamente o NuGet oficial para obter os packs Microsoft. O instalador é compilado com Inno Setup 7.1.0; informe o caminho de ISCC.exe.

    dotnet build Teclas.Partituras.sln
    # Teste do instalador em um Windows limpo (requer o Windows Sandbox ativado):
    pwsh -File tools/test_installer_sandbox.ps1
    dotnet publish src/Teclas.App/Teclas.App.csproj -c Release -r win-x64 --self-contained true --source https://api.nuget.org/v3/index.json -o Aplicativo
    python tools/prepare_distribution.py
    pwsh -File tools/build_installer.ps1 -CompilerPath "C:\ferramentas\Inno Setup 7\ISCC.exe"
    python tools/package_app.py --installer
    dotnet run --project tests/Teclas.Tests/Teclas.Tests.csproj
    dotnet run --project tests/Teclas.Dashboard.Tests/Teclas.Dashboard.Tests.csproj
    dotnet run --project tests/Teclas.Settings.Tests/Teclas.Settings.Tests.csproj
    dotnet run --project tests/Teclas.Audition.Tests/Teclas.Audition.Tests.csproj
    dotnet run --project tests/Teclas.Export.Tests/Teclas.Export.Tests.csproj -- "C:\caminho\para\teste" "C:\caminho\para\partitura.mscz" "C:\Program Files\MuseScore 4\bin\MuseScore4.exe"
    python tools/qa_ui.py --midis "C:\caminho\para\midis"
    python tests/test_quantization.py
    python tests/test_score_properties.py
    python tests/test_chord_diagrams.py
    python tests/quantization_regression.py --project "C:\caminho\para\Partituras de Teclado"
    python tools/qa_preferences.py --project "C:\caminho\para\Partituras de Teclado"

Projetos: Teclas.App (WPF), Teclas.Core (leitura MIDI, escuta por fonte, protocolo, métricas e exportação), Teclas.Tests, Teclas.Dashboard.Tests, Teclas.Settings.Tests, Teclas.Export.Tests e Teclas.Audition.Tests (testes executáveis). A pasta engine contém o motor musical e seus perfis. O executável precisa permanecer junto das DLLs e dessa pasta.

Os testes de configurações e de painéis usam dados sintéticos. Os testes de interface e integração musical exigem a pasta do acervo local, que não faz parte do repositório; informe seu caminho nos argumentos acima. Para a integração completa, execute `python tools/qa_engine.py "C:\caminho\para\Partituras de Teclado"`.

Os testes de escuta por fonte usam MIDIs sintéticos e um transporte MIDI simulado, sem emitir som. Para verificar a interface real, execute `Teclas.Partituras.exe --verify-track-audition "C:\pasta\report.json" "C:\pasta\Norte_ForroAbertura_Show.mid"` usando o MIDI revisado do acervo. Essa verificação utiliza sessões isoladas e confere separadamente a abertura do driver WinMM sem emitir notas.

Para verificar a edição e a persistência das propriedades em janelas WPF, execute `Teclas.Partituras.exe --verify-score-metadata "C:\pasta\report.json" "C:\pasta\Norte_ForroAbertura_Show.mid"`. O teste usa cópias do MIDI e configurações isoladas; não gera áudio nem partituras.

O script histórico tools/prepare_engine.py copia o motor original das ferramentas externas do projeto musical. Ele sobrescreve adaptações posteriores em engine, incluindo a correção de quantização; não o execute para compilar ou atualizar o aplicativo. tools/qa_engine.py verifica os 18 perfis contra o lote original e exercita a conversão de um MIDI novo. A pasta engine já inclui o motor necessário para o aplicativo, sem exigir a regeneração. A ferramenta histórica engine/build_keyboard_diagrams.py é preservada como origem das funções vetoriais reutilizadas pelo motor; executá-la isoladamente requer a partitura piloto na pasta externa partituras.

O número da versão é definido em `src/Teclas.App/Teclas.App.csproj` e aparece automaticamente na tela Sobre e nas propriedades do executável. As versões publicadas são marcadas com tags Git, como `v0.14.0`. O executável mantém o nome `Teclas.Partituras.exe` para preservar os caminhos já usados, e as preferências continuam na pasta `TeclasPartituras`.

Veja VALIDACAO.txt para os testes realizados. Esta é a prévia 0.19.0, destinada a testes no computador do usuário.

## Fila de conversões e várias pistas

Na Biblioteca, marque as fontes de introdução, solo e melodia. Em **Prioridade das pistas**, coloque no topo a fonte que deve prevalecer quando tocarem juntas. As seguintes completam os trechos livres. Não são criadas notas novas para retomar ataques que ficaram encobertos; sobreposições relevantes aparecem na Revisão guiada. A seleção e a prioridade acompanham a sessão e o Histórico. Perfis já revisados mantêm seu arranjo aprovado.

Use **Adicionar à fila**, depois **Fila de conversões → Iniciar pendentes**. A fila guarda uma cópia do MIDI e das opções de cada item: seleção, prioridade, cifras já revisadas, metadados, inversões e transposição. **Gerar partitura** continua disponível para uma conversão imediata. O lote revisado agora entra na fila.

Uma falha não impede os próximos itens. **Pausar** interrompe o item ativo; **Retomar** o devolve à espera. Após reabrir o app, abra a fila e inicie os pendentes explicitamente. A retomada usa a análise já concluída e recupera resultados com comprovante válido; renderizações incompletas são refeitas. Concluídos não são gerados novamente.

Resultados aparecem na Biblioteca/Histórico. Se a música tiver recebido ajustes depois de enfileirada, o resultado entra apenas no Histórico. Remover da fila retira a entrada, preservando cópias e arquivos gerados. Problemas musicais na receita exigem corrigir a Biblioteca e adicionar um novo item; a receita já enfileirada não muda junto com as configurações. Nesta versão, a fila é para MIDI; arquivos MuseScore continuam pelo editor e melhoria de apresentação.

Testes da fila: `dotnet run --project tests/Teclas.Queue.Tests -c Release`. Usam motor simulado e pastas isoladas, incluindo falhas, cancelamento, backup e retomada por recibos.

Testes de PDF em massa: `dotnet run --project tests/Teclas.BatchPdf.Tests -c Release -- "C:\pasta\qa-isolada"`. Para incluir dois PDFs reais, acrescente o caminho de uma partitura MSCZ e do MuseScore. As verificações cobrem nomes, falhas isoladas, cancelamento, progresso e preservação das origens.

Ao combinar duas ou mais fontes manualmente, a partitura mostra o nome General MIDI do instrumento na primeira entrada e em cada troca de pista, inclusive quando uma fonte retorna. Os textos identificam a fonte efetivamente usada após aplicar a prioridade; notas descartadas não recebem indicação. Gere novamente uma partitura existente para incluir as indicações em uma nova cópia.

O espaçamento dos nomes é conferido nas páginas renderizadas. Trocas muito próximas que ainda provoquem sobreposição pedem revisão por compasso antes de concluir a geração.

Quando o espaçamento horizontal não basta, os diagramas de compassos densos são distribuídos automaticamente em alturas alternadas, mantendo o tamanho das teclas. A alternativa só é aceita após conferir margens, colisões e preservação musical no MuseScore.
