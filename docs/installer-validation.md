# Instalador 1.1 — validação da pré-release

## Implementado

- Um EXE distribui a ponte self-contained e o instalador oficial USBip-win2 0.9.8.0 x64, intacto e fixado por SHA-256. Licenças USBip, .NET e Tomlyn acompanham a instalação.
- Somente Windows x64. A ponte permanece por usuário. USBip ausente requer confirmação informada e UAC do instalador oficial; USBip existente é verificado por `usbip port`, que abre o driver e consulta dispositivos, e é preservado quando funcional. Instalação parcial não é substituída automaticamente.
- Dependência usa `/TYPE=compact /TASKS=vcredist /NORESTART /RESTARTEXITCODE=3010`. O fonte oficial declara `AlwaysRestart=yes`. Após instalação, a ponte é copiada e a configuração é retomada via HKCU RunOnce no login. A opção permanente de iniciar com Windows é independente.
- Assistente resolve o pacote `OpenAI.Codex` do usuário, prepara somente campos Micro ausentes, abre o aplicativo e conecta o Micro virtual sem MIDI. Confirmação exige feedback real `v.oai.thstatus` com seis slots válidos; anexar USBip não basta. Preferências e remapeamentos são preservados; fonte ausente recebe recent, fonte existente recent/pinned permanece intacta.
- Layout incompatível, TOML inválido/ambíguo, driver indisponível, porta ocupada, conexão residual, Codex ausente, timeout e cancelamento geram erro recuperável. O assistente limpa somente sua própria conexão e mantém marcador de limpeza se houver falha. Ele não envia aprovações, mensagens ou outras ações para testar.
- A descoberta independe da DDJ. Depois dela, a ponte normal valida configuração, transporte físico e feedback antes de indicar Ativa. Bluetooth continua exigindo escolha da controladora.

## Evidência obtida nesta máquina

- Build .NET 8.0.424 de ambos os projetos, sem avisos/erros.
- 316 verificações anteriores de superfície, jogs, logs e Micro, mais 21 verificações novas de configuração inicial (337 no total). Incluem arquivos temporários, backup byte a byte, idempotência, fonte pinned e remapeamento preservados, recusa de layout incompatível e TOML inválido. Testes de transporte existentes usam somente loopback sintético.
- `setup-config --dry-run` na configuração real retornou `CONFIG_PRESERVED`; hash antes/depois igual. Não houve escrita no arquivo real.
- Interface do assistente renderizada fora da tela com a execução automática desativada e inspecionada visualmente; textos e botões legíveis. Nenhuma preparação foi executada nesse teste.
- USBip existente respondeu à consulta somente leitura `port`; o Windows informou controlador UDE com código de problema 0. Não foi efetuado attach/detach de teste.
- Compilador oficial Inno Setup 6.7.3 verificado pelo SHA-256 exigido no build. Compilação do instalador e incorporação do USBip realizadas localmente, sem executar o instalador da ponte.
- A validação física de recentes/fixados e controles pertence à tarefa anterior, confirmada pelo usuário. Nenhuma instalação, reinicialização ou substituição do runtime foi feita nesta tarefa.

## Ainda obrigatório antes de considerar a release validada

1. Em Windows x64 limpo com Codex instalado/logado: instalar o pacote, aceitar UAC do USBip, confirmar cópia da ponte e reiniciar. Conferir retomada no mesmo usuário, descoberta no Codex e estado Ativa com DDJ USB, seguido de teste físico dos controles/LEDs.
2. Repetir com usuário padrão fornecendo credenciais de outro administrador no UAC: arquivos/configuração/RunOnce precisam continuar na conta original. Confirmar attach sem elevação após reinício.
3. Recusar UAC e cancelar dependência: nenhum sucesso falso; nova execução deve permitir retomar. Verificar driver bloqueado pelas proteções do Windows sem desativá-las.
4. Testar Codex ausente, fechado, já aberto com configuração ausente, e primeira detecção com configuração limpa. Se o app aberto não recarregar TOML, a UI pede fechar/reabrir; nenhuma compatibilidade automática com versões futuras é presumida. A ação histórica feita pelo chat após a formatação não foi recuperada.
5. Atualizar uma instalação com perfil personalizado, fonte pinned e ações personalizadas. Verificar preservação; alternar recent/pinned com ponte ativa, confirmar slots e LEDs, parar/iniciar e repetir após login.
6. Testar timeout/cancelamento de descoberta, falha de detach, porta 3240 ocupada, DDJ ausente/ocupada e Bluetooth. Nenhuma outra porta USBip pode ser removida. Confirmar que o assistente não interfere numa ponte já ativa.
7. Desinstalar: encerrar ponte com limpeza, remover aplicativo/atalhos/entradas de inicialização, preservar dados e configuração, e manter USBip instalado.

## Limites

Não há VM ou computador Windows limpo disponível nesta execução. UAC, instalação dos drivers, reinício, retomada e primeira enumeração real não foram exercitados de ponta a ponta. O instalador da ponte não tem assinatura Authenticode comercial; a assinatura verificada pertence ao pacote oficial USBip. A versão é distribuída como pré-release para validação, sem substituir a versão estável como Latest.

## Referências da integração

- [Fonte do instalador USBip 0.9.8.0](https://github.com/vadimgrn/usbip-win2/blob/v.0.9.8.0/userspace/innosetup/setup.iss)
- [Consulta do driver pelo comando port](https://github.com/vadimgrn/usbip-win2/blob/v.0.9.8.0/userspace/usbip/port.cpp)
- [Parâmetros Inno Setup](https://jrsoftware.org/ishelp/topic_setupcmdline.htm)
- [Descoberta do Micro documentada pela OpenAI](https://learn.chatgpt.com/docs/features/codex-micro)
