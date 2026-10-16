# Histórico de versões

## 0.20.3 — 2026-10-15

- Corrige a pasta inicial ao salvar PDF: usa a pasta padrão salva em Configurações, em vez da pasta da partitura antiga ou do último destino lembrado pelo Windows.
- Exportações de PDFs em lote e ZIP também começam na pasta configurada. Diretórios novos são preparados ao exportar; falhas de acesso são informadas sem escolher outra pasta silenciosamente.
- Testes da interface verificam os destinos, troca de pasta, caminhos Unicode, preservação dos arquivos anteriores, fila existente e ausência de redirecionamento após falhas.

## 0.20.2 — 2026-10-15

- Arquivos PDF e MuseScore usam o padrão artista/intérprete - título das propriedades da partitura. Vale para geração MIDI, PDF individual e em lote, arquivos do ZIP e novas versões de partituras MuseScore importadas ou editadas.
- Sem intérprete, o nome usa somente o título. Caracteres incompatíveis com arquivos do Windows são substituídos; nomes muito longos são limitados sem dividir caracteres Unicode.
- Exportações com nomes iguais recebem numeração. Versões nativas continuam em pastas próprias e arquivos anteriores permanecem intactos. O PDF individual mantém a escolha de nome e a confirmação de substituição do Windows.

## 0.20.1 — 2026-10-15

- Preparação de áudio no rodapé, com progresso e cancelamento, sem abrir o painel central nem trocar o foco a cada reprodução. Análise e conversão mantêm o painel de etapas.
- Reutilização limitada em memória das sequências de MIDI completo, pistas isoladas e mapas de acompanhamento. A reprodução deixa de recarregar a prévia e regravar toda a biblioteca ao terminar a preparação.
- Mapas de acompanhamento conferem conteúdo da partitura, velocidade, renderizador e disponibilidade das páginas antes da reutilização. Desligar o acompanhamento evita preparar o mapa automaticamente.
- Configurações com pasta de saída padrão, rascunho separado e ação explícita para salvar. Validação de destino antes de gravar, preferência persistente e preservação dos locais das partituras e trabalhos já enfileirados.
- Testes de cache, cancelamento, falhas e resultados atrasados, interface discreta, persistência de pasta e medição com mapas sintéticos renderizados pelo MuseScore.

## 0.20.0 — 2026-10-15

- Posições dos diagramas agrupadas por cifra, com contagem das ocorrências. Escolher uma inversão aplica a mesma posição a todas as repetições do acorde; a prévia continua imediata. Variações antigas ficam preservadas até a escolha explícita de uma posição comum.
- Sugestões de posições confortáveis consideram as transições da música mantendo uma posição por cifra no diálogo agrupado. Escolhas manuais e variações anteriores permanecem protegidas.
- Reprodução do MIDI completo diretamente no card da Biblioteca, com todas as pistas, pausa, continuação, parada e indicação de tempo. Preparação cancelável e integração com a escuta de pistas e partituras para evitar sobreposição de áudios.

## 0.19.2 — 2026-10-15

- Prévia de teclado ao lado de cada cifra em Posições dos diagramas, atualizada imediatamente ao escolher a inversão, restaurar o padrão ou aplicar posições em lote. As teclas correspondem às mesmas notas e oitavas do diagrama na partitura, incluindo a transposição.
- A abertura do aplicativo sempre apresenta Visão geral. Biblioteca, filtros e demais preferências continuam salvos.

## 0.19.1 — 2026-10-15

- Instalador EXE Windows x64 com assistente em português, instalação por usuário, atalhos e desinstalador. Biblioteca, configurações e partituras permanecem preservadas.
- EXE e ZIP incluem o mesmo runtime .NET Desktop 10, sem instalação global desse componente. Python/Pillow e MuseScore continuam configurados pelo assistente do aplicativo.
- Script de compilação do instalador, conferência de manifesto e hashes dos dois pacotes publicados. Atualização pelo ZIP permanece compatível.
- Modo compacto do player e das ferramentas na Prévia, com controles essenciais visíveis, expansão reversível e preferência salva para liberar espaço para a partitura.

## 0.19.0 — 2026-10-15

- Transposição de MSCZ importados e cópias editadas, com intervalo relativo à versão aberta, comparação, reprodução e publicação de uma nova versão. Notas, armaduras, cifras e diagramas reconhecidos são conferidos; desenhos externos exigem escolha explícita e estruturas incompatíveis não são alteradas parcialmente.
- Assistente Preparar este computador em Configurações e Instalação e atualizações: detecção e teste funcional de Python/Pillow e MuseScore, caminhos validados e preparo opcional de um ambiente Python próprio. O instalador orienta o .NET Desktop Runtime correto para a arquitetura.
- Refinamento rítmico local adicional para passagens rápidas, preservando ataques. Falhas de geração agora encaminham os pontos localizados à Revisão guiada, com ação para escolher a pista do compasso.
- Ícone vermelho Remover no próprio card da Biblioteca, substituindo o botão inferior; desfazer e preservação dos arquivos mantidos.

## 0.18.0 — 2026-10-15

- Testes automáticos no GitHub para cada atualização da branch principal e pull request: compilação Windows, suítes C# executáveis, testes Python, paridade dos diagramas e verificação WPF com biblioteca sintética isolada. Casos que exigem MuseScore aparecem como opcionais e são executáveis localmente.
- Medições locais de duração, CPU e pico observado de memória dos processos. Em Sobre → Desempenho, consulte, exporte ou limpe o histórico; arquivos musicais e dados de identificação não entram no relatório e não há envio automático.
- Benchmark reproduzível com MIDIs sintéticos, sugestões de melodia, metadados e conversão de áudio. Relatórios registram amostras e ambiente; tempos são informativos, sem limites absolutos em máquinas compartilhadas.
- Identificação de título e intérprete nos dois sentidos do nome do MIDI, priorizando metadados explícitos. Nomes ambíguos permanecem inteiros para revisão. Propriedades oferece sugestões e troca dos campos; ajustes já salvos são preservados até a aplicação pelo usuário.
- Botão Remover em cada partitura da tela Produção, com Desfazer remoção. A biblioteca e seus totais são atualizados, preservando os arquivos MIDI, MuseScore e PDF nas pastas.

## 0.17.0 — 2026-10-15

- Exportação de PDFs em massa na tela Produção, com seleção de partituras, escolha de pasta e um PDF completo por música. Inclui partituras geradas, importadas do MuseScore e editadas.
- Progresso por arquivo, cancelamento e resumo dos resultados; uma falha não interrompe as demais exportações. Cada lote usa uma pasta exclusiva, preservando PDFs anteriores e as partituras de origem.
- Edições de cifras e armadura passam a indicar a necessidade de gerar novamente, evitando exportar ou reproduzir uma versão anterior como se contivesse os novos ajustes.
- Espaçamento vertical ajustado por sistema em melodias com trocas de instrumento. A compactação mantém o tamanho das notas e diagramas, todos os nomes completos e o conteúdo musical; o resultado anterior é mantido quando a verificação não aprova a redução.

## 0.16.0 — 2026-10-14

- Rascunhos do Editor musical protegidos automaticamente, com cópia imutável da partitura base, gravação atômica e recuperação após interrupção. O editor permite manter ou descartar as alterações ao fechar; a barra lateral mostra os rascunhos disponíveis.
- Inserção de notas em pausas, exclusão com preservação da duração, criação de pausas e cópia de voz entre compassos compatíveis. Desfazer/refazer, prévia, reprodução e publicação conferem o conteúdo; estruturas especiais indicam suas limitações.
- Sugestões de inversões que reduzem deslocamentos entre acordes, com alcance configurável, comparação das notas e preservação das escolhas manuais. Disponíveis para MIDI e diagramas reconhecidos no editor nativo, sem alterar o áudio ou as notas da melodia.
- Modo apresentação com repertório ordenado, anotações de ensaio, posição de leitura persistente e navegação por teclado ou pedal que emula teclas.
- Trocas de instrumento muito próximas recebem faixas de texto e espaço entre sistemas. Todos os nomes e suas posições musicais são preservados; a paginação pode aumentar para evitar sobreposições.

## 0.15.0 — 2026-10-14

- Fontes de melodia por faixa de compassos, com mapa real de fórmulas/anacruse, prioridade geral fora das faixas e preservação na sessão, fila e Histórico.
- Acompanhamento da pauta e das páginas baseado nas posições e nos tempos do MuseScore; clique para iniciar em um compasso/ataque, incluindo repetições.
- Editor musical seleciona ataques na página e reproduz o compasso ou intervalo selecionado. Associação ambígua de vozes permanece explícita; estruturas não suportadas mantêm a tabela.
- Revisão visual de MSCZ gerados/importados: achados localizados, proposta conservadora, comparação e nova versão com conteúdo musical conferido.
- Backup portátil da biblioteca, arquivos, páginas e Histórico, com verificação de integridade e restauração que adiciona itens e preserva preferências locais.
- Originais e versões anteriores preservados; nenhum destaque ou correção é apresentado como seguro quando não foi possível validar a estrutura.


## 0.14.0 — 13/10/2026

- Arranjos por nível para partituras geradas de MIDI e MSCZ importados, com seleção explícita de pauta e voz.
- Propostas de simplificação explicadas por compasso, preservando trechos não suportados e sinalizando limitações.
- Comparação original/arranjo, prévia paginada e reprodução com controle de velocidade; salvar exige prévia atual.
- Nova cópia com identificação do nível, pauta e voz no arquivo, na Biblioteca e no Histórico, preservada ao reabrir.
- Roteiro consolidado para testar todas as fases entregues.

## 0.13.1 — 13/10/2026

- Nome do instrumento na entrada e nas trocas de fonte da melodia combinada, seguindo a prioridade e as notas efetivamente incluídas.
- Indicações preservadas no arquivo MuseScore e nas páginas geradas, sem alterar as notas ou o instrumento de reprodução da pauta.

## 0.13.0 — 13/10/2026

- Fila persistente de conversões MIDI, com cópia da fonte e opções congeladas por item, execução sequencial, pausa e retomada explícita.
- Falhas isoladas por item; itens em execução voltam pausados após reiniciar. Checkpoints de análise e comprovantes de saída recuperam trabalho concluído sem duplicar publicações.
- Publicação da partitura e suas páginas em uma pasta exclusiva, conferência de hashes e histórico que preserva ajustes mais recentes.
- Seleção de várias fontes de introdução, solo e melodia em uma pauta, com prioridade ajustável e avisos por compasso nas sobreposições.
- Ordem preservada na sessão e no Histórico; análises antigas de múltiplas fontes são refeitas com o novo critério, mantendo cifras editadas.
- Perfis musicais previamente aprovados e fluxo de edição nativa preservados.

## 0.12.0 — 13/10/2026

- Editor musical por pauta, compasso e voz, disponível para partituras geradas e arquivos MuseScore 4 importados.
- Edição de alturas e grafias, figuras rítmicas, pontos de aumento e cifras; desfazer/refazer e atualização da prévia antes de salvar.
- Alterar cifras reconhecidas atualiza somente o diagrama de teclado daquela ocorrência, preservando imagens compartilhadas.
- Reprodução independente do rascunho inteiro, com pausa, posição e velocidades de 50%, 75% e 100%; cancelamento sem áudio tardio.
- Salvamento em nova versão, com conferência do conteúdo modificado, da origem e das páginas; cópias anteriores permanecem no Histórico.
- Partituras editadas continuam pelo fluxo nativo, sem regeneração acidental a partir do MIDI; restauração de uma versão anterior permite voltar à preparação do MIDI.
- Estruturas que exigem sincronização especial indicam sua limitação no editor. Durações devem conservar o total da voz e as posições de elementos não editados.

## 0.11.0 — 13/10/2026

- Comparação de pistas candidatas à melodia, com ordem de escuta explicada e trechos em velocidade reduzida; escolha sempre explícita.
- Confirmar a mesma melodia preserva as cifras editadas; troca efetiva mantém a partitura anterior no Histórico e prepara uma nova análise.
- Contexto harmônico para novas análises apresenta justificativas e alternativas com as mesmas notas comprovadas, preservando cromatismos e os 18 perfis revisados.
- Revisão guiada localiza dúvidas de cifras, evidência insuficiente, lacunas de harmonia e tonalidade; avisos não bloqueiam a geração.
- Diagnóstico de MuseScore por pauta, compasso e voz, disponível na Prévia e automático na importação e melhoria de apresentação. Originais e cópias já salvas são preservados mesmo quando o diagnóstico falha ou é cancelado.
- Justificativas e alternativas de cifras permanecem salvas na biblioteca e no Histórico; editar a cifra remove a orientação antiga.

## 0.10.0 — 13/10/2026

- Painel central animado mostra operação, arquivo, etapa atual, tempo decorrido e cancelamento durante importação, análise, geração, atualização de propriedades/diagramas/transposição e exportação PDF/ZIP.
- Importações múltiplas e geração em lote mostram avanço por arquivos efetivamente processados. Etapas sem medição usam animação, sem porcentagens ou prazos artificiais.
- Preparação de pistas e de compassos na Revisão guiada ocorre em segundo plano, mantendo a interface responsiva e permitindo cancelar antes da reprodução.
- Preparação de áudio da partitura permanece visível até o reprodutor confirmar a abertura; falhas, timeout e cancelamento liberam a tela, sem reproduções tardias.
- Importação e melhoria de arquivos MuseScore informam etapas; leitura, conferência musical e gravação da cópia são executadas em segundo plano, preservando originais e Histórico.
- Cliques repetidos não iniciam processos simultâneos; mensagens atrasadas de uma operação encerrada não substituem o resultado nem o progresso seguinte.

## 0.9.2 — 13/10/2026

- Gerar partitura analisa automaticamente a seleção de melodia atual quando a pista mudou ou a análise temporária não está disponível.
- A prévia anterior fica identificada como pendente até a nova geração terminar; falhas preservam o Histórico e não liberam áudio/PDF desatualizados.
- Corrigido o bloqueio rítmico da pista de piano de Luz divina: ataques próximos recebem ajuste local limitado, preservando cada ataque da linha melódica e sua ordem.
- Quebras de página planejadas são flexibilizadas quando criam uma página interna quase vazia, preservando as divisões de sistema e o conteúdo musical.
- Removido o subtítulo automático “Melodia, cifras e diagramas de teclado” e a indicação de inversão no cabeçalho das partituras geradas.
- Subtítulo permanece opcional nas Propriedades: textos personalizados são preservados e não mudam ao selecionar outras inversões.
- Partituras antigas que usavam o subtítulo automático recebem o cabeçalho limpo ao gerar novamente; arquivos anteriores permanecem intactos.

## 0.9.1 — 13/10/2026

- Corrigido o espaçamento de linhas com quatro ou mais diagramas: a folga entre cifra e teclado é ajustada à largura real do texto quando há colisão, mantendo o tamanho dos desenhos.
- Resolvida a falha em Luz divina, em que um compasso com cinco acordes alternava entre sobreposição e extravasamento da margem durante a conversão.
- A verificação geométrica passa a rejeitar também cifras e diagramas cortados nas demais bordas da página.
- Erros de diagramação mostram uma mensagem curta no app; o histórico técnico é preservado em diagnóstico exclusivo por tentativa, sem substituir registros anteriores.

## 0.9.0 — 13/10/2026

- Importação de MIDI organiza automaticamente título, artista e autoria declarada a partir do nome do arquivo e dos metadados internos.
- Limpeza de domínios como karaoplay.com, espaços, underscores e etiquetas técnicas; versões musicais e números significativos são preservados.
- Artista/intérprete separado de autor/compositor na interface, na partitura e nos metadados MuseScore. Um artista identificado não é considerado compositor automaticamente.
- Propriedades oferecem sugestões para itens antigos; alterações manuais são preservadas ao reimportar, reabrir e restaurar o Histórico.
- Créditos longos recebem espaço no cabeçalho; leitura e melhoria de arquivos MuseScore preservam artista e compositor separadamente.

## 0.8.0 — 13/10/2026

- Janela Posições dos diagramas para escolher posição fundamental, 1ª ou 2ª inversão por ocorrência de acorde, com atualização automática da partitura.
- Aplicar à mesma cifra permite repetir a escolha; Padrão da partitura mantém o vínculo com a inversão geral.
- Escolhas preservadas ao fechar, restaurar o Histórico e desconectar/reconectar o MIDI; transposição e lembretes de cifra acompanham a posição escolhida.
- Subtítulo automático identifica posições personalizadas, preservando subtítulos escritos pelo usuário.
- Botão Importar MuseScore no topo facilita abrir .mscz para melhorar a apresentação em uma nova cópia.

## 0.7.0 — 13/10/2026

- Visualizador de todas as páginas, com miniaturas, zoom, ajuste à página/largura e tela cheia.
- Importação de partituras MuseScore 4 (.mscz) e melhoria de apresentação e cabeçalho em uma nova cópia, com verificação da preservação musical.
- Revisão guiada de acordes incertos e ocorrências rítmicas, com compasso, tempo e escuta isolada do trecho antes de corrigir a cifra.
- Transposição de partituras geradas de MIDI entre −12 e +12 semitons: melodia, armadura, cifras e diagramas atualizados juntos.
- Modo de estudo com velocidades de 25% a 150%, sem mudar a afinação, e repetição A–B.
- Histórico com comparação das páginas e restauração da versão ativa, preservando arquivos anteriores e a preparação musical.
- Favoritos, coleção/repertório, gênero e busca por nome, título, autor ou gênero; organização salva entre sessões.
- Instalador por usuário, diagnóstico das dependências e consulta de versões no GitHub pela tela Sobre; instalação de pacotes verificados por SHA-256.

## 0.6.1 — 12/10/2026

- Removido o botão Salvar MuseScore da aba Prévia, conforme preferência do usuário.
- Gerar partitura continua salvando o arquivo .mscz automaticamente na pasta de saída.

## 0.6.0 — 12/10/2026

- Janela Propriedades da partitura para editar título, subtítulo, autor e inversão dos diagramas, com geração e atualização automática da prévia ao aplicar.
- Posição fundamental, 1ª e 2ª inversão; Configurações define o padrão adotado por novos MIDIs, sem alterar os já presentes na biblioteca.
- Propriedades salvas entre sessões; subtítulo automático acompanha a inversão, e subtítulo personalizado ou vazio é preservado.
- Botão Salvar MuseScore ao lado de Salvar PDF, para copiar o arquivo .mscz ao destino escolhido.
- Botão de geração renomeado para Gerar partitura.
- Cabeçalhos longos recebem espaço e paginação adequados, sem alterar notas ou cifras.
- A biblioteca mantém o fundo do tema enquanto fica desabilitada durante análises e conversões, sem o retângulo branco do visual padrão do Windows.
- Seleção, rolagem e bloqueio de interação durante o processamento preservados.

## 0.5.1 — 12/10/2026

- Controle de velocidade da escuta de pistas: 25%, 50%, 75%, 100%, 125% e 150%, sem alterar a altura das notas.
- Mudança de velocidade durante a reprodução e preferência preservada entre pistas e ao reabrir o aplicativo.
- Correção de colisões causadas pela quantização de ataques em tempos vizinhos, refinando apenas as grades necessárias.

## 0.5.0 — 12/10/2026

- Escuta individual de cada pista/canal/instrumento na aba Melodia, antes de analisar os acordes ou gerar a partitura.
- Botões Ouvir por linha, pausa, retomada, parada e controle de posição; a escuta começa na primeira nota da fonte.
- Reprodução MIDI pelo sintetizador do Windows, com andamento, intensidade e controles musicais preservados.
- Trocar de pista, MIDI, aba ou tela interrompe a escuta anterior; ouvir não altera a seleção de melodia.
- A escuta continua disponível com a revisão musical aprovada habilitada.

## 0.4.1 — 12/10/2026

- Botão de recolher/expandir menor e fixado na parte inferior da barra lateral, com o ícone espelhado para expandir.

## 0.4.0 — 12/10/2026

- Reprodução da partitura no aplicativo, com pausa, parada e navegação pelo áudio.
- Exportação de todas as páginas da partitura para PDF.
- Barra de título do Windows acompanha o tema do aplicativo.
- Controle da barra lateral usa somente o ícone de três linhas, espelhado quando o menu está recolhido.
- Remoção de itens da biblioteca com opção de desfazer, mantendo os arquivos originais nas suas pastas.
- Importar um MIDI abre automaticamente a Biblioteca, com o arquivo selecionado na aba Melodia.
- Importação pelo botão ou ao arrastar arquivos limpa a busca e os filtros anteriores para mostrar os novos MIDIs.
- Ao importar novamente um arquivo já presente, o app o seleciona sem duplicar a entrada. Em lotes, seleciona o primeiro arquivo novo.

## 0.3.1 — 12/10/2026

- Nome TeclaScore e ícone de teclado com nota musical.
- Tela Sobre com versão obtida do aplicativo e acesso ao repositório.
- Barra lateral recolhível com menus por ícones e dicas de navegação.
- Cinco temas: Verde clássico, Oceano, Índigo, Grafite e âmbar e Azul claro.
- Preferências e biblioteca restauradas ao reabrir: janela, navegação, caminhos, pistas, cifras e tonalidade.
- Salvamento atômico, recuperação por backup e preservação da última gravação quando há falha.

## 0.2.0 — 12/10/2026

- Interface renovada com painéis de biblioteca e produção.
- Busca por nome, filtros por situação e indicadores baseados nos arquivos carregados.
- Prévia das partituras e exportação em ZIP.

## 0.1.0 — 12/10/2026

- Aplicativo inicial em C# / WPF.
- Importação de MIDI, escolha da melodia, análise e edição de cifras.
- Geração nativa de MuseScore .mscz com diagramas de teclado na segunda inversão.
- Perfis musicais revisados e conversão em lote.
