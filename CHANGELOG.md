# Histórico de versões

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
