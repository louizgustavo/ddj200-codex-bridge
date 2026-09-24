# DDJ-200 Codex Bridge

Ponte comunitária para usar controles e LEDs de uma Pioneer DJ / AlphaTheta DDJ-200 com o recurso Codex Micro no aplicativo Codex para Windows. O projeto recebe MIDI da controladora, apresenta um dispositivo Micro virtual apenas no computador local e devolve o estado visual às luzes da DDJ-200.

> **Projeto comunitário e não oficial.** Não é afiliado, aprovado ou suportado pela OpenAI, Pioneer DJ, AlphaTheta ou pelos mantenedores do USBip. “Codex”, “DDJ-200” e demais marcas pertencem a seus respectivos titulares.

**English summary:** Community-built Windows bridge that translates Pioneer DJ DDJ-200 MIDI controls into the native Codex Micro device protocol and mirrors supported lighting feedback. USB and the integrated native Bluetooth path were validated on the original development machine; exhaustive per-control Bluetooth coverage is not claimed.

## Estado da versão 1.1 (pré-release)

- USB físico validado em Windows com uma DDJ-200 real.
- Seis pads esquerdos seguem os seis slots nativos do Micro, usando **Chats mais recentes** ou **Chats fixados**, conforme a fonte escolhida no Codex. Trocar entre essas fontes mantém a ponte conectada; posições vazias continuam sem seleção.
- Seis controles direitos enviam as identidades nativas `ACT06` a `ACT12`; a função escolhida no próprio Codex pode ser remapeada sem a ponte fixar a ação antiga.
- LEDs representam o retorno disponível no protocolo Micro. A entrega de um evento HID não garante, sozinha, que uma ação terminou no aplicativo.
- PLAY esquerdo envia a direção para baixo do Micro; CUE esquerdo envia para cima. As alavancas de tempo não enviam mais navegação vertical. Ambos podem ser remapeados em **Controles** e compartilham os estados de luz dos comandos direitos: aceso, apagado ou piscar; pressionado vem em 500 ms aceso / 500 ms apagado.
- Jog esquerdo: escala 0,4 e intervalo de 50 ms. Jog direito: escala 0,1. Tocar o topo de um jog suprime o giro daquele mesmo deck.
- USB ou Bluetooth são escolhidos explicitamente; os dois nunca operam juntos. O backend Bluetooth é GATT nativo, sem navegador e sem PIN genérico do Windows.
- O fluxo Bluetooth integrado foi confirmado em uso real com conexão pronta, ações físicas chegando ao Codex e LEDs acompanhando o estado real do Micro. Isso não equivale a uma certificação exaustiva de cada controle.
- **Configurar controles e luzes** oferece mapeamento assistido, tempos de LED, prévia visual, sensibilidade dos jogs e restauração do padrão. A aprendizagem abre somente entrada e não envia comandos ao Codex.

## Arquitetura

```text
DDJ-200 -- USB MIDI/WinMM ou BLE-MIDI/GATT --> ddj200.exe -- USB/IP local --> USBip UDE --> Codex Micro
   ^                         |                                      |
   +--------- LEDs MIDI <----+<---------- retorno de luz -----------+

DDJ200.CodexBridge.exe: bandeja, iniciar/parar/status e ciclo de vida
```

A ponte escuta somente em `127.0.0.1:3240`, publica o bus local `1-1` e não usa automação de interface, macros, API paga ou acesso remoto. O aplicativo da bandeja guarda configurações e logs sob `%LOCALAPPDATA%\DDJ200CodexBridge`.

## Requisitos

- Windows 10 x64 1903 ou mais recente, ou Windows 11 x64.
- Aplicativo Codex/ChatGPT para Windows, pacote `OpenAI.Codex`, instalado para o usuário e com sessão iniciada. O assistente prepara os campos Micro ausentes e verifica a descoberta.
- Para USB: DDJ-200 conectada por cabo e reconhecida pelo Windows como porta MIDI `DDJ-200`.
- Para Bluetooth: Bluetooth LE disponível no computador e DDJ-200 anunciando o nome do produto.
- [USBip-win2](https://github.com/vadimgrn/usbip-win2) com driver UDE devidamente assinado/aceito pelo Windows.

O instalador completo inclui **USBip-win2 0.9.8.0 x64 oficial**, verificado por hash e assinatura no build. Se ausente, instala o cliente, drivers UDE/filtro e runtime Visual C++ com elevação do Windows. O pacote oficial exige reinício e pode interromper dispositivos USB temporariamente. USBip funcional já instalado é preservado. A ponte não desativa antivírus, integridade de código ou assinatura de teste. ARM64 não é aceito por este pacote de drivers x64.

Há também uma [cópia preservada do instalador oficial USBip, com licença, SHA-256 e assinatura documentados](docs/usbip-recovery.md). A versão 1.1 incorpora esse mesmo executável, sem modificações.

## Instalação

1. Baixe `DDJ200-Codex-Bridge-1.1.0-Setup.exe` na [pré-release 1.1.0](https://github.com/louizgustavo/ddj200-codex-bridge/releases/tag/v1.1.0) e confira o arquivo `SHA256SUMS.txt` anexado. O build local fica em `artifacts/release`.
2. Execute normalmente, pela conta que usa o Codex. A ponte é instalada por usuário; somente a instalação do USBip pede administrador.
3. Se USBip foi instalado, reinicie quando for conveniente. A preparação do Micro será retomada no próximo login. **Iniciar com o Windows** continua opcional e desmarcado; a retomada usa uma entrada separada, de execução única.
4. O assistente verifica e abre o Codex, acrescenta somente campos ausentes, com backup do arquivo existente, e apresenta o Micro virtual sem enviar ações. Fontes recentes/fixados e remapeamentos existentes são preservados. Se o Codex já aberto não recarregar a configuração, feche e reabra o aplicativo e use **Tentar novamente**.
5. Quando o retorno real do Micro for confirmado, feche o assistente e conecte a DDJ-200. USB é o padrão; para Bluetooth, use **Conexão** na bandeja. A confirmação do Micro é separada do estado **Ativa**, que exige também a DDJ-200 conectada.
6. Use **Configurar controles e luzes** para personalizar. Para repetir a preparação, pare a ponte e use **Preparar / verificar Codex Micro**. Não é necessário pedir detecção pelo chat.

Layouts existentes incompatíveis e formas de TOML que o assistente não consegue complementar com segurança são preservados e geram uma orientação. Não há promessa de compatibilidade com toda versão futura do Codex. A instalação completa, incluindo UAC/reinício/primeira detecção, ainda precisa ser validada em Windows limpo; veja [validação da versão 1.1](docs/installer-validation.md).

Ao ser aberto no modo USB, o aplicativo da bandeja tenta iniciar a ponte automaticamente. No modo Bluetooth salvo, ele aguarda uma nova escolha manual da DDJ porque o endereço do dispositivo não é persistido. Se algum requisito estiver ausente, ele permanece na bandeja, informa o erro e permite tentar novamente pelo menu.

O executável inicial não possui assinatura Authenticode comercial. Por isso, o Windows pode mostrar aviso de publisher desconhecido ou reputação do SmartScreen; o projeto não finge uma assinatura ou reputação que não possui.

## Atualização e desinstalação

Instale uma versão nova por cima da anterior para atualizar os arquivos do aplicativo. A ponte é parada de forma limpa antes da substituição. Desinstalar remove aplicativo, atalhos e entradas de inicialização/retomada, mas preserva `%LOCALAPPDATA%\DDJ200CodexBridge`, a configuração do Codex e seus backups. O USBip é uma dependência separada no Windows e não é removido junto com a ponte.

## Solução de problemas

- **USBip não encontrado:** execute o instalador completo; se há driver instalado sem cliente ou falha na verificação, repare o USBip oficial. Uma instalação inconsistente não é substituída automaticamente.
- **Codex Micro não confirmou em 30 s:** confirme que o Codex está aberto, a fonte está em Chats mais recentes ou Chats fixados e o Micro está habilitado.
- **DDJ-200 indisponível:** feche outro software de DJ que tenha aberto a mesma porta MIDI, reconecte a controladora e tente novamente.
- **A ponte para após remapear:** somente mudanças nativas com estrutura compatível são aceitas; mudanças incompatíveis de layout ou modo de microfone continuam falhando de forma fechada. As fontes recentes e fixados são suportadas.
- **Diagnóstico:** use o menu da bandeja para abrir a pasta local. Não publique esses arquivos; eles podem conter caminhos da sua conta e detalhes do ambiente.

Detalhes adicionais: [arquitetura e limites](docs/architecture.md), [segurança e privacidade](SECURITY.md) e [avisos de terceiros](THIRD_PARTY_NOTICES.md).

## Compilar

Requer o SDK .NET 8.0.424 para Windows e o compilador oficial Inno Setup 6.7.3 (`ISCC.exe`) com SHA-256 verificado pelo script. `./build.ps1 -Action Test` compila e executa os testes isolados, incluindo primeira configuração. Antes de publicar, execute `./installer/prepare-dependencies.ps1` (ou forneça `-UsbIpInstaller` com uma cópia local); ele verifica e guarda o pacote oficial em `.deps`, sem instalá-lo. `./build.ps1 -Action Publish` publica binários self-contained `win-x64`, manifesto e hashes em `artifacts/release`. O script preserva `artifacts/sdk` e diagnósticos. `ISCC_EXE` pode apontar para o compilador; `.tools/inno/ISCC.exe` também é reconhecido. A árvore local de diagnóstico nunca deve ser publicada em bloco.

Este repositório não inclui uma licença de uso das fontes. A publicação do código não concede, por si só, permissão para copiar, modificar ou redistribuir além do permitido por lei. Dependências de terceiros mantêm suas próprias licenças.
