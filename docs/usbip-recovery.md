# Backup do USBip para recuperação

O instalador original **USBip-win2 0.9.8.0 x64** está preservado na [release de dependência](https://github.com/louizgustavo/ddj200-codex-bridge/releases/tag/dependency-usbip-0.9.8.0).
A publicação preserva o executável oficial sem modificações, licença e registro de procedência/verificação.

- Origem: https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.8.0
- Arquivo: `USBip-0.9.8.0-x64.exe` (26.390.744 bytes)
- SHA-256: `81f426741f7ee2ed991febe24a22daca8400b6ae2f171054e3fb404897e15d39`
- Assinatura Authenticode do instalador verificada como válida no Windows em 21/09/2026.
- Assinante: Cloudyne Systems (Scheibling Consulting AB).
- Impressão digital do certificado: `9AC56B6C76141395D74FFF6652818376E80B9C95`.
- Licença de redistribuição: [BSD 2-Clause original](usbip-LICENSE.txt), com copyright e condições preservados. Dependências incorporadas mantêm seus próprios termos e avisos no pacote original.
- A documentação oficial declara drivers WHLK/attestation assinados. A assinatura do instalador não substitui a validação dos drivers pelo Windows durante a instalação.

## Recuperar depois de formatar

Baixe o executável, a licença e o registro de verificação na release acima. Confira o SHA-256 e a assinatura digital do executável antes de executar. O pacote atende aos requisitos documentados da ponte (Windows x64 e UDE USB/IP), mas o repositório não registra qual versão estava instalada na máquina anterior. Isso não constitui uma nova validação funcional da DDJ-200.

O candidato 1.1 da ponte incorpora esse mesmo instalador oficial e o executa, se ausente, em uma etapa separada com privilégios administrativos. O pacote oficial 0.9.8.0 declara `AlwaysRestart=yes`: o instalador completo impede reinício automático da dependência, copia a ponte e prepara a retomada no próximo login. Dispositivos USB podem reiniciar durante a instalação. Não desative verificações de assinatura ou proteções do Windows. A dependência preservada na release também continua disponível para recuperação manual.

A release oficial 0.9.8.0 registra um problema conhecido em `vhci::stop_attach_attempts` quando uma localização é especificada. O backup é da versão publicada, sem patches.

O fluxo `Preserve USBip dependency` só publica esse pacote fixado por hash em uma release separada; não instala drivers e não altera os binários da ponte.
