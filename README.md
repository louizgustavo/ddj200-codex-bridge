# DDJ-200 Codex Bridge

Ponte comunitária para usar controles e LEDs de uma Pioneer DJ / AlphaTheta DDJ-200 com o recurso Codex Micro no aplicativo Codex para Windows. O projeto recebe MIDI da controladora, apresenta um dispositivo Micro virtual apenas no computador local e devolve o estado visual às luzes da DDJ-200.

> **Projeto comunitário e não oficial.** Não é afiliado, aprovado ou suportado pela OpenAI, Pioneer DJ, AlphaTheta ou pelos mantenedores do USBip. “Codex”, “DDJ-200” e demais marcas pertencem a seus respectivos titulares.

**English summary:** Community-built Windows bridge that translates Pioneer DJ DDJ-200 MIDI controls into the native Codex Micro device protocol and mirrors supported lighting feedback. USB and the integrated native Bluetooth path were validated on the original development machine; exhaustive per-control Bluetooth coverage is not claimed.

## Estado da versão 1.0

- USB físico validado em Windows com uma DDJ-200 real.
- Seis pads esquerdos seguem as seis tarefas em **Chats mais recentes** do Codex.
- Seis controles direitos enviam as identidades nativas `ACT06` a `ACT12`; a função escolhida no próprio Codex pode ser remapeada sem a ponte fixar a ação antiga.
- LEDs representam o retorno disponível no protocolo Micro. A entrega de um evento HID não garante, sozinha, que uma ação terminou no aplicativo.
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
- Aplicativo Codex para Windows com Codex Micro disponível e configurado.
- Para USB: DDJ-200 conectada por cabo e reconhecida pelo Windows como porta MIDI `DDJ-200`.
- Para Bluetooth: Bluetooth LE disponível no computador e DDJ-200 anunciando o nome do produto.
- [USBip-win2](https://github.com/vadimgrn/usbip-win2) com driver UDE devidamente assinado/aceito pelo Windows.

O instalador da ponte **não instala nem reinstala driver**. Driver em modo kernel pode interromper dispositivos USB e exige confiança explícita; obtenha o USBip apenas na publicação oficial, confira o publisher/assinatura e siga a documentação do projeto. A ponte não desativa antivírus, integridade de código ou assinatura de teste.

## Instalação

1. Baixe `DDJ200-Codex-Bridge-1.0.0-Setup.exe` na release e confira o SHA-256 publicado.
2. Execute o instalador. Ele é por usuário e não precisa elevar privilégios para copiar a ponte.
3. A opção **Iniciar com o Windows** é visível e reversível; ela vem desmarcada.
4. Ao abrir, o ícone aparece na bandeja. Em **Conexão**, escolha USB ou procure sua DDJ-200 por Bluetooth dentro do próprio aplicativo.
5. Use **Configurar controles e luzes** para personalizar sem editar JSON ou usar terminal. A função final de cada tecla Micro continua sendo escolhida no Codex.

Ao ser aberto no modo USB, o aplicativo da bandeja tenta iniciar a ponte automaticamente. No modo Bluetooth salvo, ele aguarda uma nova escolha manual da DDJ porque o endereço do dispositivo não é persistido. Se algum requisito estiver ausente, ele permanece na bandeja, informa o erro e permite tentar novamente pelo menu.

O executável inicial não possui assinatura Authenticode comercial. Por isso, o Windows pode mostrar aviso de publisher desconhecido ou reputação do SmartScreen; o projeto não finge uma assinatura ou reputação que não possui.

## Atualização e desinstalação

Instale uma versão nova por cima da anterior para atualizar os arquivos do aplicativo. A ponte é parada de forma limpa antes da substituição. Desinstalar remove aplicativo, atalhos e entrada de inicialização, mas preserva `%LOCALAPPDATA%\DDJ200CodexBridge` para não apagar sua seleção de configuração e seus diagnósticos. Essa pasta pode ser removida manualmente depois, se desejado.

## Solução de problemas

- **USBip não encontrado:** instale a versão oficial adequada e reinicie somente se o instalador oficial pedir.
- **Codex Micro não confirmou em 30 s:** confirme que o Codex está aberto, a fonte está em Chats mais recentes e o Micro está habilitado.
- **DDJ-200 indisponível:** feche outro software de DJ que tenha aberto a mesma porta MIDI, reconecte a controladora e tente novamente.
- **A ponte para após remapear:** somente mudanças nativas com estrutura compatível são aceitas; mudanças de layout, modo de microfone ou fonte de tarefas continuam falhando de forma fechada.
- **Diagnóstico:** use o menu da bandeja para abrir a pasta local. Não publique esses arquivos; eles podem conter caminhos da sua conta e detalhes do ambiente.

Detalhes adicionais: [arquitetura e limites](docs/architecture.md), [segurança e privacidade](SECURITY.md) e [avisos de terceiros](THIRD_PARTY_NOTICES.md).

## Compilar

Requer o SDK .NET 8.0.424 para Windows e o compilador oficial Inno Setup 6.7.3 (`ISCC.exe`) com o SHA-256 verificado pelo script. `./build.ps1 -Action Test` compila e executa 265 verificações determinísticas. `./build.ps1 -Action Publish` limpa o stage, publica os binários self-contained `win-x64`, gera manifesto e hashes e compila o instalador. A árvore local de diagnóstico nunca deve ser publicada em bloco.

Este repositório não inclui uma licença de uso das fontes. A publicação do código não concede, por si só, permissão para copiar, modificar ou redistribuir além do permitido por lei. Dependências de terceiros mantêm suas próprias licenças.
