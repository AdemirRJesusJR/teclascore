# Histórico de versões

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
