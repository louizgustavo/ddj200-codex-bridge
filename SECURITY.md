# Segurança e privacidade

## Modelo local

A versão 1.0 abre um servidor USB/IP exclusivamente em `127.0.0.1:3240`. Não há telemetria, conta própria, upload de logs nem API remota. O caminho do `config.toml` é lido localmente para validar somente a estrutura necessária ao Micro; a ponte não deve publicar nem copiar esse arquivo.

## Driver USB/IP

USBip-win2 é um projeto independente e inclui componentes em modo kernel. A instalação, atualização ou remoção do driver pode afetar dispositivos USB. Este instalador não incorpora e não instala o driver. Obtenha-o da [origem oficial](https://github.com/vadimgrn/usbip-win2), valide a assinatura apresentada pelo Windows e revise as notas da versão antes de consentir.

Não desative antivírus, Secure Boot, integridade de memória nem exigência de assinatura para executar esta ponte. Não use builds de driver que exijam Test Signing em uma máquina de uso normal.

## Dados locais

Configuração e diagnósticos ficam em `%LOCALAPPDATA%\DDJ200CodexBridge`. Eles são preservados durante atualização/desinstalação para evitar perda inesperada e podem ser apagados manualmente. Antes de anexar logs a um issue, remova nomes de usuário, caminhos pessoais e qualquer conteúdo que identifique tarefas.

O endereço Bluetooth escolhido existe apenas na sessão atual e não é gravado. A descoberta não persiste inventário de dispositivos. O serviço e a característica BLE-MIDI são validados antes de qualquer entrada ou LED, e somente mensagens de LED documentadas com valor ligado/desligado são aceitas.

## Relato responsável

Evite publicar segredos ou dados pessoais em issues. Para uma vulnerabilidade, use o mecanismo privado de security advisory do GitHub quando ele estiver habilitado no repositório.
