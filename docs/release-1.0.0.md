# Release 1.0.0

Primeira release pública dos caminhos USB e Bluetooth nativo da DDJ-200 para Codex Micro no Windows.

## Artefato

- `DDJ200-Codex-Bridge-1.0.0-Setup.exe`
- Tamanho do candidato atual: 82.180.547 bytes
- SHA-256 do candidato atual: `73920f872a964286cd12d608aa1f37b677964b86e7babf75c601c1ef47c9c373`
- Arquitetura: Windows x64
- Versão do arquivo: 1.0.0.0
- Assinatura Authenticode: **não assinado** (`NotSigned`)

## Verificação executada

- Build Release do núcleo e do aplicativo de bandeja: sem avisos e sem erros.
- 61 testes da superfície de 12 controles e perfis de LED.
- 106 testes de analógicos, toque e sensibilidade dos jogs.
- 18 testes de limite/rotação de logs.
- 80 testes do transporte Micro e framing BLE em loopback/sintético.
- Compilador Inno verificou o setup e o stage foi registrado por manifesto com hashes por arquivo.
- As mesmas 265 verificações passaram usando o `ddj200.exe` do stage self-contained incluído no instalador.
- Auditoria textual da árvore pública para caminhos pessoais, IDs de tarefa, e-mails, tokens e chaves.

Os testes automatizados desta etapa não abriram MIDI, não enviaram ações ao Codex, não instalaram driver e não alteraram inicialização do Windows. A prova física USB pertence ao baseline validado anteriormente. No Bluetooth nativo, a sonda provou conexão, entrada PLAY e saída física de LED; depois, o fluxo integrado foi confirmado em uso real com estado `ready`, ações físicas entregues ao Codex e LEDs derivados do estado real do Micro. Essa aprovação funcional não afirma cobertura exaustiva de cada controle.
