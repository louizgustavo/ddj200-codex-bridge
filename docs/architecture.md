# Arquitetura e limites da versão 1.0

## Entrada física

No USB, o núcleo abre por nome as portas WinMM `DDJ-200`. No Bluetooth, a tela nativa procura anúncios cujo nome começa com `DDJ-200`, mostra nome, endereço e intensidade do sinal, exige escolha manual e só conecta se encontrar o serviço e a característica BLE-MIDI esperados com notificação e escrita. Não há navegador, PIN genérico, fallback automático ou uso simultâneo dos transportes. Nome, endereço e UUIDs reduzem escolhas acidentais, mas não constituem autenticação criptográfica; confirme o endereço da sua controladora e evite conectar em ambiente com dispositivo desconhecido imitando o anúncio.

Botões, pads, jogs e TEMPO são interpretados conforme um perfil declarativo. A cópia do usuário é validada antes da ativação e salva atomicamente; colisões mantêm o perfil anterior. O perfil não contém endereço Bluetooth, capturas, identificadores de tarefas ou timestamps.

## Dispositivo Micro virtual

O processo publica uma coleção HID vendor-specific por USB/IP apenas no loopback. O cliente USBip instalado separadamente materializa o dispositivo virtual no Windows. O protocolo transporta identidades de teclas (`AG00..AG05`, `ACT06..ACT12`), encoder/joystick e retorno de iluminação compatível.

O dispositivo não se anuncia como teclado ou mouse genérico. A ponte não injeta teclas, não controla janelas e não extrai código ou binários do Codex.

## Falha fechada

A ponte para e libera controles quando perde o transporte, deixa de receber feedback, detecta perda de eventos, encontra configuração Micro incompatível ou não consegue registrar seu estado. Gestos antigos não são reproduzidos após reinício.

Remapear uma tecla nativa para outra ação suportada pelo Codex não muda a identidade física enviada pela ponte e é aceito. Mudanças estruturais no layout, modo de microfone ou origem das tarefas continuam bloqueadas.

## Limites de evidência

USB, entrada MIDI e LEDs foram validados no hardware original. Isso não garante compatibilidade com toda versão futura do Codex, todo firmware, toda máquina Windows ou todo pacote USBip. A confirmação HID prova entrega ao transporte; não prova que a ação solicitada concluiu dentro do aplicativo.

A prova física BLE direta de 2026-09-09 cobriu a entrada e o LED do PLAY esquerdo. Depois, o fluxo nativo integrado confirmou descoberta, conexão pronta, ações físicas chegando ao Codex e retorno visual derivado do estado real do Micro. A evidência aprova o uso funcional observado, sem alegar cobertura exaustiva de cada controle, firmware ou computador Windows.
