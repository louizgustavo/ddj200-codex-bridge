# Arquitetura e limites da versão 1.0

## Entrada física

No USB, o núcleo abre por nome as portas WinMM `DDJ-200`. No Bluetooth, a tela nativa procura anúncios cujo nome começa com `DDJ-200`, mostra nome, endereço e intensidade do sinal, exige escolha manual e só conecta se encontrar o serviço e a característica BLE-MIDI esperados com notificação e escrita. Não há navegador, PIN genérico, fallback automático ou uso simultâneo dos transportes. Nome, endereço e UUIDs reduzem escolhas acidentais, mas não constituem autenticação criptográfica; confirme o endereço da sua controladora e evite conectar em ambiente com dispositivo desconhecido imitando o anúncio.

Botões, pads e jogs são interpretados conforme um perfil declarativo. A cópia do usuário é validada antes da ativação e salva atomicamente; colisões mantêm o perfil anterior. O perfil não contém endereço Bluetooth, capturas, identificadores de tarefas ou timestamps.

## Dispositivo Micro virtual

O processo publica uma coleção HID vendor-specific por USB/IP apenas no loopback. O cliente USBip materializa o dispositivo virtual no Windows; o instalador 1.1 incorpora o pacote oficial da dependência, mantendo sua instalação separada e elevada. O protocolo transporta identidades de teclas (`AG00..AG05`, `ACT06..ACT12`), encoder/joystick e retorno de iluminação compatível.

O comando `discover-micro` usa o mesmo protocolo sem abrir MIDI, ler layout ou enviar entradas. Ele compartilha o mutex do engine, escuta somente em loopback e espera um snapshot válido `v.oai.thstatus` com seis slots. O assistente anexa uma única conexão, verifica o retorno e remove somente essa conexão antes de iniciar a ponte normal. `setup-config` valida TOML completo com Tomlyn, acrescenta apenas fonte/layout ausentes, valida o contrato da ponte e salva com backup; `--dry-run` não escreve. Um layout existente incompatível é preservado. Não existe API oficial de provisionamento do Micro implementada aqui: a descoberta depende da enumeração HID pelo aplicativo e do feedback observado no protocolo já usado pela ponte.

O dispositivo não se anuncia como teclado ou mouse genérico. A ponte não injeta teclas, não controla janelas e não extrai código ou binários do Codex.

## Falha fechada

A ponte para e libera controles quando perde o transporte, deixa de receber feedback, detecta perda de eventos, encontra configuração Micro incompatível ou não consegue registrar seu estado. Gestos antigos não são reproduzidos após reinício.

Remapear uma tecla nativa para outra ação suportada pelo Codex não muda a identidade física enviada pela ponte e é aceito. Mudanças estruturais no layout ou modo de microfone continuam bloqueadas. As fontes `recent` e `pinned` podem ser selecionadas e alternadas sem reiniciar: a ponte envia `AG00..AG05`, e o Codex resolve a posição na fonte nativa selecionada. O retorno `v.oai.thstatus` atualiza as luzes pelos IDs 0..5, inclusive posições vazias, sem a ponte armazenar títulos ou reordenar tarefas. Uma troca de fonte cancela gestos de seleção de tarefa em andamento, preservando liberações dos demais comandos. O status local informa `taskSource` e `taskSlots`; o log registra a troca. O protocolo de iluminação não informa títulos nem a fonte, por isso a confirmação visual da identidade selecionada pertence ao aplicativo.

## Limites de evidência

USB, entrada MIDI e LEDs foram validados no hardware original. Isso não garante compatibilidade com toda versão futura do Codex, todo firmware, toda máquina Windows ou todo pacote USBip. A confirmação HID prova entrega ao transporte; não prova que a ação solicitada concluiu dentro do aplicativo.

A prova física BLE direta de 2026-09-09 cobriu a entrada e o LED do PLAY esquerdo. Depois, o fluxo nativo integrado confirmou descoberta, conexão pronta, ações físicas chegando ao Codex e retorno visual derivado do estado real do Micro. A evidência aprova o uso funcional observado, sem alegar cobertura exaustiva de cada controle, firmware ou computador Windows.
