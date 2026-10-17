# TeclaScore

## Versionamento autorizado pelo usuário

O usuário autorizou em 12/10/2026 fazer commit, criar uma nova tag e enviar as atualizações ao GitHub automaticamente, sempre que necessário, sem pedir nova confirmação.

- Após concluir e validar uma atualização, atualize a versão do aplicativo e o histórico, faça commit das alterações pertinentes, crie uma tag anotada `vX.Y.Z` e envie o commit e a tag ao repositório configurado.
- Mantenha o número da versão, a tag, o histórico, a tela Sobre e o pacote entregue coerentes.
- Preserve alterações alheias à tarefa. Não inclua MIDIs, partituras pessoais, configurações do usuário, credenciais ou resultados temporários de testes no Git.
- Confira o destino e o estado do repositório antes de publicar. Não reescreva tags publicadas nem use push forçado para contornar conflitos.
- Informe a versão entregue e qualquer falha real de validação ou publicação.

## Kit de instalação debug

- A cada entrega, publique o aplicativo, execute tools/prepare_distribution.py e finalize com tools/package_app.py. Esses comandos atualizam automaticamente debug com aplicativo, Python/Pillow privados e instalador oficial do MuseScore.
- Em um checkout novo, use tools/prepare_offline_dependencies.py --download para obter os pacotes oficiais fixados por SHA-256. Não copie ambientes Python pessoais, configurações ou partituras para debug.
- Preserve scripts .iss e outros arquivos do usuário fora dos caminhos gerenciados de kit-manifest.json. Não envie debug, Aplicativo ou os downloads ao Git.

## Instaladores a cada entrega (pedido do usuário em 17/10/2026)

Sempre e automaticamente, junto com commit, push e nova tag: gere o instalador oficial (tools/build_installer.ps1, Inno Setup 7) e o instalador completo. tools/build_debug.py recria debug/TeclaScore-Completo.iss com a versão atual, e build_installer.ps1 chama tools/build_debug_installer.ps1, que compila debug/saida/TeclaScore-X-Completo-Setup-x64.exe. Confira ambos antes de publicar e mantenha em software/ e debug/saida apenas os pacotes da versão atual.
